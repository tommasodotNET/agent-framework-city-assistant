import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

const source = await readFile(new URL('../src/A2AClientWrapper.ts', import.meta.url), 'utf8');
const { outputText } = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 }
});
const resolved = outputText.replace(/from (['"])(@a2a-js\/sdk(?:\/client)?|uuid)\1/g,
    (_, _quote, specifier) => `from ${JSON.stringify(import.meta.resolve(specifier))}`);
const { A2AClientWrapper, EmptyAgentResponseError } =
    await import(`data:text/javascript;base64,${Buffer.from(resolved).toString('base64')}`);

beforeEach(t => { t.mock.method(console, 'error', () => {}); });

const part = text => ({ content: { $case: 'text', value: text } });
const message = text => ({ payload: { $case: 'message', value: { contextId: 'context', parts: [part(text)] } } });
function setup(events, failure) {
    const wrapper = new A2AClientWrapper('/unused');
    let requests = 0;
    wrapper.client = {
        async *sendMessageStream() {
            requests++;
            for (const event of events) yield event;
            if (failure) throw failure;
        }
    };
    return { wrapper, requests: () => requests };
}
const collect = async wrapper => {
    const values = [];
    for await (const value of wrapper.sendMessageStream([{ role: 'user', content: 'hello' }], 'context')) values.push(value);
    return values;
};

for (const [name, events] of [
    ['no events', []],
    ['empty assistant text', [message('')]],
    ['whitespace only', [message(' \n ')]],
    ['task without text', [{ payload: { $case: 'task', value: { contextId: 'context' } } }]],
    ['empty final status', [{ payload: { $case: 'statusUpdate', value: { contextId: 'context', status: { message: { parts: [] } } } } }]]
]) {
    test(`${name} reports an explicit empty_response without retrying`, async () => {
        const { wrapper, requests } = setup(events);
        await assert.rejects(() => collect(wrapper), error =>
            error instanceof EmptyAgentResponseError && error.code === 'empty_response' && /not retried/.test(error.message));
        assert.equal(requests(), 1);
    });
}

test('empty intermediate events followed by assistant text are valid', async () => {
    const { wrapper, requests } = setup([message(''), message('answer')]);
    assert.deepEqual(await collect(wrapper), [{ content: 'answer', contextId: 'context' }]);
    assert.equal(requests(), 1);
});

test('artifact text and context updates retain their normal behavior', async () => {
    const { wrapper } = setup([
        { payload: { $case: 'task', value: { contextId: 'context' } } },
        { payload: { $case: 'artifactUpdate', value: { contextId: 'context', artifact: { parts: [part('answer')] } } } }
    ]);
    assert.deepEqual(await collect(wrapper), [{ contextId: 'context' }, { content: 'answer', contextId: 'context' }]);
});

test('actual transport errors are not mislabeled as empty responses', async () => {
    const expected = new Error('network failure');
    const { wrapper } = setup([], expected);
    await assert.rejects(() => collect(wrapper), error => error === expected);
});

test('nonstreaming empty result is also explicit', async () => {
    const wrapper = new A2AClientWrapper('/unused');
    wrapper.client = { async sendMessage() { return { messageId: 'message', contextId: 'context', parts: [part('')] }; } };
    await assert.rejects(() => wrapper.sendMessage([{ role: 'user', content: 'hello' }]), EmptyAgentResponseError);
});
