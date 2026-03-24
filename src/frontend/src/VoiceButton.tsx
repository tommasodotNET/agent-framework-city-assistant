// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { useCallback, useRef, useState } from "react";
import { VoiceSession, VoiceStatus, VoiceTranscript } from "./VoiceSession";
import styles from "./Chat.module.css";

interface VoiceButtonProps {
    onTranscript: (transcript: VoiceTranscript) => void;
    disabled?: boolean;
}

const STATUS_LABELS: Record<VoiceStatus, string> = {
    disconnected: '',
    connecting: 'Connecting...',
    ready: 'Voice active',
    listening: '🎤 Listening...',
    processing: '🤔 Processing...',
    function_calling: '🔧 Searching...',
};

export default function VoiceButton({ onTranscript, disabled }: VoiceButtonProps) {
    const [status, setStatus] = useState<VoiceStatus>('disconnected');
    const [error, setError] = useState<string | null>(null);
    const sessionRef = useRef<VoiceSession | null>(null);

    const isActive = status !== 'disconnected';

    const toggleVoice = useCallback(async () => {
        if (isActive) {
            await sessionRef.current?.stop();
            sessionRef.current = null;
            return;
        }

        setError(null);
        const session = new VoiceSession({
            onTranscript,
            onStatus: (newStatus) => setStatus(newStatus),
            onError: (msg) => {
                setError(msg);
                console.error('Voice error:', msg);
            },
        });
        sessionRef.current = session;
        await session.start();
    }, [isActive, onTranscript]);

    const statusLabel = STATUS_LABELS[status];

    return (
        <div className={styles.voiceContainer}>
            <button
                className={`${styles.voiceButton} ${isActive ? styles.voiceActive : ''}`}
                onClick={toggleVoice}
                disabled={disabled}
                title={isActive ? 'Stop voice session' : 'Start voice session'}
            >
                {isActive ? '🔊' : '🎙️'}
            </button>
            {statusLabel && (
                <span className={styles.voiceStatus}>{statusLabel}</span>
            )}
            {error && (
                <span className={styles.voiceError} title={error}>⚠️</span>
            )}
        </div>
    );
}
