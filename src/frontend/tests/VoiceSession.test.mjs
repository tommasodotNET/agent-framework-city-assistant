import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

const source = await readFile(new URL('../src/VoiceSession.ts', import.meta.url), 'utf8');
const { outputText } = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 }
});
const { VoiceSession } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`);

function setup(t) {
    const descriptors = new Map();
    function replace(name, value) {
        descriptors.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
        Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
    }
    t.after(() => {
        for (const [name, descriptor] of descriptors) {
            if (descriptor) Object.defineProperty(globalThis, name, descriptor);
            else delete globalThis[name];
        }
    });
    const timers = new Map();
    let nextTimer = 0;
    replace('setTimeout', (callback, delay) => { timers.set(++nextTimer, { callback, delay }); return nextTimer; });
    replace('clearTimeout', id => timers.delete(id));
    const sockets = [];
    const tracks = [];
    let getMedia;
    const createStream = () => {
        const track = { stopped: false, stop() { this.stopped = true; } };
        tracks.push(track);
        return { getTracks: () => [track] };
    };
    getMedia = async () => createStream();
    class Socket {
        static OPEN = 1;
        static CLOSED = 3;
        readyState = 0;
        sent = [];
        closeCount = 0;
        constructor() {
            sockets.push(this);
            queueMicrotask(() => {
                if (this.readyState === 0) {
                    this.readyState = Socket.OPEN;
                    this.onopen?.();
                }
            });
        }
        send(value) {
            if (this.throwOnSend) throw new Error('send failed');
            this.sent.push(JSON.parse(value));
        }
        receive(value) { this.onmessage?.({ data: JSON.stringify(value) }); }
        close() { this.closeCount++; this.readyState = Socket.CLOSED; this.onclose?.(); }
    }
    class Audio {
        audioWorklet = { addModule: async () => {} };
        destination = {};
        createMediaStreamSource() { return { connect() {}, disconnect() {} }; }
        async close() { this.closed = true; }
    }
    replace('window', { location: { protocol: 'http:', host: 'localhost' } });
    replace('WebSocket', Socket);
    replace('AudioContext', Audio);
    replace('AudioWorkletNode', class { port = {}; connect() {} disconnect() {} });
    replace('navigator', { mediaDevices: { getUserMedia: () => getMedia() } });
    const errors = [];
    const statuses = [];
    const transcripts = [];
    const session = new VoiceSession({
        onError: error => errors.push(error),
        onStatus: status => statuses.push(status),
        onTranscript: transcript => transcripts.push(transcript)
    }, 'context');
    return {
        session, errors, statuses, transcripts, sockets, tracks, timers,
        createStream,
        setGetMedia(value) { getMedia = value; },
        async start() {
            await session.start();
            sockets.at(-1).receive({ type: 'ready' });
            return sockets.at(-1);
        }
    };
}

test('stop releases the microphone immediately but waits for persisted before closing', async t => {
    const f = setup(t);
    const socket = await f.start();
    let completed = false;
    const stop = f.session.stop().then(() => { completed = true; });
    await Promise.resolve();
    assert.equal(f.tracks[0].stopped, true);
    assert.equal(f.session.status, 'stopping');
    assert.deepEqual(socket.sent, [{ type: 'stop' }]);
    assert.equal(socket.closeCount, 0);
    assert.equal(completed, false);
    socket.receive({ type: 'persisted' });
    await stop;
    assert.equal(socket.closeCount, 1);
    assert.equal(f.session.status, 'disconnected');
    assert.deepEqual(f.errors, []);
    assert.equal(f.timers.size, 0);
});

test('persistence_error is shown before stop closes the socket', async t => {
    const f = setup(t);
    const socket = await f.start();
    const stop = f.session.stop();
    socket.receive({ type: 'persistence_error', message: 'save failed' });
    assert.equal(socket.closeCount, 0);
    assert.deepEqual(f.errors, ['save failed']);
    await stop;
    assert.equal(socket.closeCount, 1);
});

test('missing acknowledgement times out without pretending save succeeded', async t => {
    const f = setup(t);
    const socket = await f.start();
    const stop = f.session.stop();
    const timer = [...f.timers.values()].find(timer => timer.delay === 25000);
    assert.ok(timer);
    timer.callback();
    await stop;
    assert.match(f.errors[0], /Timed out.*persistence/i);
    assert.equal(socket.closeCount, 1);
    assert.equal(f.timers.size, 0);
});

test('disconnect while waiting reports unconfirmed persistence and releases the wait', async t => {
    const f = setup(t);
    const socket = await f.start();
    const stop = f.session.stop();
    socket.close();
    await stop;
    assert.match(f.errors[0], /closed before.*confirmed/i);
    assert.equal(f.session.status, 'disconnected');
    assert.equal(f.timers.size, 0);
});

test('repeated stop calls share one operation and do not send duplicate stop messages', async t => {
    const f = setup(t);
    const socket = await f.start();
    const first = f.session.stop();
    const second = f.session.stop();
    assert.equal(first, second);
    assert.equal(socket.sent.length, 1);
    socket.receive({ type: 'persisted' });
    await first;
    await f.session.stop();
    assert.equal(socket.closeCount, 1);
});

test('status and queued audio do not reactivate a stopping session; final transcripts still arrive', async t => {
    const f = setup(t);
    const socket = await f.start();
    const stop = f.session.stop();
    socket.receive({ type: 'status', status: 'ready' });
    socket.receive({ type: 'ready' });
    socket.receive({ type: 'audio', data: 'ignored' });
    socket.receive({ type: 'transcript', role: 'assistant', text: 'final', final_: true });
    assert.equal(f.session.status, 'stopping');
    assert.equal(f.transcripts[0].text, 'final');
    socket.receive({ type: 'persisted' });
    await stop;
    assert.deepEqual(f.errors, []);
});

test('nonpersistent voice connection does not wait for an acknowledgement that cannot arrive', async t => {
    const f = setup(t);
    f.session.conversationId = undefined;
    const socket = await f.start();
    await f.session.stop();
    assert.equal(socket.closeCount, 1);
    assert.equal(f.timers.size, 0);
});

test('stopping before server readiness does not wait for persistence', async t => {
    const f = setup(t);
    await f.session.start();
    await f.session.stop();
    assert.equal(f.sockets[0].closeCount, 1);
    assert.equal(f.timers.size, 0);
});

test('microphone permission resolving after stop cannot restart capture or leak tracks', async t => {
    const f = setup(t);
    let resolveMedia;
    let entered;
    const pending = new Promise(resolve => { resolveMedia = resolve; });
    const mediaRequested = new Promise(resolve => { entered = resolve; });
    f.setGetMedia(() => { entered(); return pending; });
    const start = f.session.start();
    await mediaRequested;
    await f.session.stop();
    resolveMedia(f.createStream());
    await start;
    assert.equal(f.tracks[0].stopped, true);
    assert.equal(f.session.status, 'disconnected');
    assert.equal(f.timers.size, 0);
});

test('send failure cleans up the acknowledgement timer and reports the error once', async t => {
    const f = setup(t);
    const socket = await f.start();
    socket.throwOnSend = true;
    await f.session.stop();
    assert.deepEqual(f.errors, ['send failed']);
    assert.equal(f.timers.size, 0);
    assert.equal(socket.closeCount, 1);
});

test('a new connection can be started after a completed stop', async t => {
    const f = setup(t);
    const first = await f.start();
    const stopped = f.session.stop();
    first.receive({ type: 'persisted' });
    await stopped;
    const second = await f.start();
    assert.notEqual(first, second);
    const stoppedAgain = f.session.stop();
    second.receive({ type: 'persisted' });
    await stoppedAgain;
    assert.deepEqual(f.errors, []);
});

test('late callbacks from a previous socket cannot stop a newly started session', async t => {
    const f = setup(t);
    const first = await f.start();
    const stopped = f.session.stop();
    first.receive({ type: 'persisted' });
    await stopped;
    const second = await f.start();
    first.onclose();
    first.onerror();
    first.receive({ type: 'error', message: 'old error' });
    assert.equal(f.session.status, 'ready');
    assert.equal(second.closeCount, 0);
    assert.deepEqual(f.errors, []);
    const stop = f.session.stop();
    second.receive({ type: 'persisted' });
    await stop;
});

test('stop while the WebSocket is connecting settles startup and cleans its timeout', async t => {
    const f = setup(t);
    const start = f.session.start();
    const stop = f.session.stop();
    await Promise.all([start, stop]);
    assert.equal(f.session.status, 'disconnected');
    assert.equal(f.timers.size, 0);
    assert.equal(f.tracks.length, 0);
});
