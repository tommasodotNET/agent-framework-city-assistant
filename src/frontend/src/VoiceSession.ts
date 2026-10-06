// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

export interface VoiceTranscript {
    role: 'user' | 'assistant';
    text: string;
    isFinal: boolean;
}

export type VoiceStatus = 'disconnected' | 'connecting' | 'ready' | 'listening' | 'processing' | 'function_calling' | 'stopping';

export interface VoiceSessionCallbacks {
    onTranscript: (transcript: VoiceTranscript) => void;
    onStatus: (status: VoiceStatus, detail?: string) => void;
    onError: (message: string) => void;
}

const PCM_SAMPLE_RATE = 24000;
const PCM_CHUNK_MS = 50;
const PCM_CHUNK_SAMPLES = (PCM_SAMPLE_RATE * PCM_CHUNK_MS) / 1000; // 1200 samples per chunk
// Server shutdown can spend 10s draining producers and another 10s persisting.
const PERSISTENCE_ACK_TIMEOUT_MS = 25000;

// AudioWorklet processor code (inline to avoid separate file)
const WORKLET_CODE = `
class PCMCaptureProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this.buffer = new Float32Array(0);
    }

    process(inputs) {
        const input = inputs[0];
        if (!input || !input[0]) return true;
        const channelData = input[0];

        // Accumulate samples
        const newBuffer = new Float32Array(this.buffer.length + channelData.length);
        newBuffer.set(this.buffer);
        newBuffer.set(channelData, this.buffer.length);
        this.buffer = newBuffer;

        // Send chunks of ${PCM_CHUNK_SAMPLES} samples
        while (this.buffer.length >= ${PCM_CHUNK_SAMPLES}) {
            const chunk = this.buffer.slice(0, ${PCM_CHUNK_SAMPLES});
            this.buffer = this.buffer.slice(${PCM_CHUNK_SAMPLES});

            // Convert Float32 to Int16
            const int16 = new Int16Array(chunk.length);
            for (let i = 0; i < chunk.length; i++) {
                const s = Math.max(-1, Math.min(1, chunk[i]));
                int16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
            }
            this.port.postMessage(int16.buffer, [int16.buffer]);
        }
        return true;
    }
}
registerProcessor('pcm-capture-processor', PCMCaptureProcessor);
`;

export class VoiceSession {
    private ws: WebSocket | null = null;
    private audioContext: AudioContext | null = null;
    private mediaStream: MediaStream | null = null;
    private workletNode: AudioWorkletNode | null = null;
    private sourceNode: MediaStreamAudioSourceNode | null = null;
    private callbacks: VoiceSessionCallbacks;
    private _status: VoiceStatus = 'disconnected';
    private stopPromise: Promise<void> | null = null;
    private completePersistence: ((error?: string) => void) | null = null;
    private readyReceived = false;

    // Playback
    private playbackContext: AudioContext | null = null;
    private nextPlaybackTime = 0;
    private scheduledSources: AudioBufferSourceNode[] = [];

    private conversationId?: string;

    constructor(callbacks: VoiceSessionCallbacks, conversationId?: string) {
        this.callbacks = callbacks;
        this.conversationId = conversationId;
    }

    get status(): VoiceStatus {
        return this._status;
    }

    get isActive(): boolean {
        return this._status !== 'disconnected';
    }

    async start(): Promise<void> {
        if (this.isActive || this.stopPromise) return;
        this.readyReceived = false;
        this.setStatus('connecting');

        try {
            // Connect WebSocket to voice orchestrator
            const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            let wsUrl = `${wsProtocol}//${window.location.host}/ws/voice`;
            if (this.conversationId) {
                wsUrl += `?conversationId=${encodeURIComponent(this.conversationId)}`;
            }
            const socket = new WebSocket(wsUrl);
            this.ws = socket;

            await new Promise<void>((resolve, reject) => {
                let opened = false;
                const timeout = setTimeout(() => reject(new Error('WebSocket connection timeout')), 10000);
                socket.onopen = () => {
                    opened = true;
                    clearTimeout(timeout);
                    resolve();
                };
                socket.onmessage = (event) => {
                    if (this.ws === socket) this.handleServerMessage(event.data);
                };
                socket.onclose = () => {
                    clearTimeout(timeout);
                    if (!opened) reject(new Error('WebSocket closed before connecting'));
                    if (this.ws === socket) this.handleDisconnect();
                };
                socket.onerror = () => {
                    clearTimeout(timeout);
                    if (!opened) reject(new Error('WebSocket connection failed'));
                    else if (this.ws === socket) {
                        if (this.completePersistence) this.completePersistence('Connection failed before voice persistence was confirmed.');
                        else this.callbacks.onError('WebSocket error');
                    }
                };
            });

            if (!this.canCapture()) return;

            // Initialize audio capture
            await this.startAudioCapture();
            if (!this.canCapture()) return;

            // Initialize audio playback
            this.playbackContext = new AudioContext({ sampleRate: PCM_SAMPLE_RATE });
            this.nextPlaybackTime = 0;

        } catch (error) {
            if (!this.stopPromise && this.isActive)
                this.callbacks.onError(error instanceof Error ? error.message : String(error));
            await this.stop();
        }
    }

    stop(): Promise<void> {
        if (this.stopPromise) return this.stopPromise;
        if (!this.isActive && !this.ws) return Promise.resolve();
        this.setStatus('stopping');
        this.stopPromise = this.stopCore();
        return this.stopPromise;
    }

    private async stopCore(): Promise<void> {
        const socket = this.ws;
        const audioReleased = this.releaseAudio();
        try {
            if (socket?.readyState === WebSocket.OPEN) {
                // A connection without a continuation id never saves a snapshot. A connection
                // stopped before server readiness has not begun a resumable voice conversation.
                const acknowledgement = this.readyReceived && !!this.conversationId
                    ? this.waitForPersistence()
                    : Promise.resolve();
                socket.send(JSON.stringify({ type: 'stop' }));
                await acknowledgement;
            }
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            if (this.completePersistence) this.completePersistence(message);
            else this.callbacks.onError(message);
        } finally {
            this.completePersistence = null;
            this.readyReceived = false;
            if (this.ws === socket) this.ws = null;
            if (socket && socket.readyState !== WebSocket.CLOSED) socket.close();
            await audioReleased;
            this.setStatus('disconnected');
            this.stopPromise = null;
        }
    }

    private waitForPersistence(): Promise<void> {
        return new Promise(resolve => {
            const timeout = setTimeout(() => finish('Timed out waiting for voice persistence confirmation.'), PERSISTENCE_ACK_TIMEOUT_MS);
            const finish = (error?: string) => {
                clearTimeout(timeout);
                this.completePersistence = null;
                if (error) this.callbacks.onError(error);
                resolve();
            };
            this.completePersistence = finish;
        });
    }

    private async releaseAudio(): Promise<void> {
        // Stop the microphone immediately, independently of the server acknowledgement.
        this.workletNode?.disconnect();
        this.workletNode = null;
        this.sourceNode?.disconnect();
        this.sourceNode = null;

        if (this.mediaStream) {
            this.mediaStream.getTracks().forEach(t => t.stop());
            this.mediaStream = null;
        }

        const audioContext = this.audioContext;
        this.audioContext = null;
        const playbackContext = this.playbackContext;
        this.playbackContext = null;
        this.clearPlaybackQueue();
        for (const context of [audioContext, playbackContext]) {
            try {
                await context?.close();
            } catch (error) {
                this.callbacks.onError(`Could not close voice audio: ${error instanceof Error ? error.message : String(error)}`);
            }
        }
    }

    private async startAudioCapture(): Promise<void> {
        // Get microphone access
        const stream = await navigator.mediaDevices.getUserMedia({
            audio: {
                echoCancellation: true,
                noiseSuppression: true,
                sampleRate: PCM_SAMPLE_RATE,
            }
        });
        if (!this.canCapture()) {
            stream.getTracks().forEach(track => track.stop());
            return;
        }
        this.mediaStream = stream;

        // Create AudioContext at 24kHz
        const audioContext = new AudioContext({ sampleRate: PCM_SAMPLE_RATE });
        this.audioContext = audioContext;

        // Load AudioWorklet from inline code
        const blob = new Blob([WORKLET_CODE], { type: 'application/javascript' });
        const workletUrl = URL.createObjectURL(blob);
        try {
            await audioContext.audioWorklet.addModule(workletUrl);
        } finally {
            URL.revokeObjectURL(workletUrl);
        }
        if (!this.canCapture()) return;

        // Connect microphone → worklet → WebSocket
        this.sourceNode = this.audioContext.createMediaStreamSource(this.mediaStream);
        this.workletNode = new AudioWorkletNode(this.audioContext, 'pcm-capture-processor');

        this.workletNode.port.onmessage = (event: MessageEvent) => {
            if (this.canCapture() && this.ws) {
                const int16Buffer = event.data as ArrayBuffer;
                const base64 = arrayBufferToBase64(int16Buffer);
                this.ws.send(JSON.stringify({ type: 'audio', data: base64 }));
            }
        };

        this.sourceNode.connect(this.workletNode);
        // Don't connect worklet to destination (we don't want to hear our own mic)
        this.workletNode.connect(this.audioContext.destination);
    }

    private canCapture(): boolean {
        return this.isActive && this.status !== 'stopping' && this.ws?.readyState === WebSocket.OPEN;
    }

    private handleServerMessage(data: string): void {
        try {
            const msg = JSON.parse(data);

            switch (msg.type) {
                case 'ready':
                    this.readyReceived = true;
                    if (this.status !== 'stopping') this.setStatus('ready');
                    break;

                case 'audio':
                    if (this.status !== 'stopping') this.playAudio(msg.data);
                    break;

                case 'clear_audio':
                    this.clearPlaybackQueue();
                    break;

                case 'transcript':
                    this.callbacks.onTranscript({
                        role: msg.role,
                        text: msg.text,
                        isFinal: msg.final_ ?? msg.final ?? false,
                    });
                    break;

                case 'status':
                    if (this.status !== 'stopping') this.setStatus(msg.status as VoiceStatus);
                    break;

                case 'persisted':
                    this.completePersistence?.();
                    break;

                case 'persistence_error':
                    if (this.completePersistence) this.completePersistence(msg.message);
                    else this.callbacks.onError(msg.message);
                    break;

                case 'error':
                    this.callbacks.onError(msg.message);
                    break;
            }
        } catch (error) {
            console.error('Error parsing server message:', error);
        }
    }

    private playAudio(base64Data: string): void {
        if (!this.playbackContext) return;

        const bytes = base64ToArrayBuffer(base64Data);
        const int16 = new Int16Array(bytes);

        // Convert Int16 to Float32 for Web Audio API
        const float32 = new Float32Array(int16.length);
        for (let i = 0; i < int16.length; i++) {
            float32[i] = int16[i] / 32768.0;
        }

        const audioBuffer = this.playbackContext.createBuffer(1, float32.length, PCM_SAMPLE_RATE);
        audioBuffer.getChannelData(0).set(float32);

        const source = this.playbackContext.createBufferSource();
        source.buffer = audioBuffer;
        source.connect(this.playbackContext.destination);
        source.onended = () => {
            const idx = this.scheduledSources.indexOf(source);
            if (idx !== -1) this.scheduledSources.splice(idx, 1);
        };
        this.scheduledSources.push(source);

        // Schedule playback to maintain continuous stream
        const currentTime = this.playbackContext.currentTime;
        const startTime = Math.max(currentTime, this.nextPlaybackTime);
        source.start(startTime);
        this.nextPlaybackTime = startTime + audioBuffer.duration;
    }

    private clearPlaybackQueue(): void {
        for (const source of this.scheduledSources) {
            try { source.stop(); } catch { /* already stopped */ }
        }
        this.scheduledSources = [];
        this.nextPlaybackTime = 0;
    }

    private handleDisconnect(): void {
        if (this.completePersistence) {
            this.completePersistence('Connection closed before voice persistence was confirmed.');
        }
        if (this.isActive && !this.stopPromise) {
            void this.stop();
        }
    }

    private setStatus(status: VoiceStatus): void {
        this._status = status;
        this.callbacks.onStatus(status);
    }
}

function arrayBufferToBase64(buffer: ArrayBuffer): string {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (let i = 0; i < bytes.length; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary);
}

function base64ToArrayBuffer(base64: string): ArrayBuffer {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes.buffer;
}
