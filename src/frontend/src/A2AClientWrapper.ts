// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { Client, ClientFactory, DefaultAgentCardResolver } from '@a2a-js/sdk/client';
import { Role } from '@a2a-js/sdk';
import type { Message, Part, SendMessageRequest, Task } from '@a2a-js/sdk';
import { v4 as uuidv4 } from 'uuid';

export interface A2AChatMessage {
    role: 'user' | 'assistant';
    content: string;
}

export interface A2AStreamEvent {
    content?: string;
    contextId?: string;
}

export class A2AClientWrapper {
    private client: Client | null = null;
    private agentCardUrl: string;
    private servicePath: string;

    constructor(agentCardUrl: string, servicePath = '/agenta2a') {
        this.agentCardUrl = agentCardUrl;
        this.servicePath = servicePath;
    }

    private async ensureClient(): Promise<Client> {
        if (!this.client) {
            const resolver = new DefaultAgentCardResolver({ path: this.agentCardUrl });
            const agentCard = await resolver.resolve(window.location.origin);
            const serviceUrl = new URL(this.servicePath, window.location.origin).toString();

            for (const supportedInterface of agentCard.supportedInterfaces) {
                supportedInterface.url = serviceUrl;
            }

            this.client = await new ClientFactory().createFromAgentCard(agentCard);
        }
        return this.client;
    }

    async *sendMessageStream(
        messages: A2AChatMessage[],
        contextId?: string
    ): AsyncGenerator<A2AStreamEvent, void, undefined> {
        const client = await this.ensureClient();

        // Get the last user message
        const userMessage = messages[messages.length - 1];
        
        if (!userMessage || userMessage.role !== 'user') {
            throw new Error('Last message must be from user');
        }

        const params = createMessageRequest(userMessage.content, contextId);

        try {
            // Stream the response
            const stream = client.sendMessageStream(params);
            
            for await (const event of stream) {
                const payload = event.payload;
                if (!payload) {
                    continue;
                }

                if (payload.$case === 'message') {
                    const content = getText(payload.value.parts);
                    if (content) {
                        yield {
                            content,
                            contextId: payload.value.contextId || undefined,
                        };
                    }
                }

                if (payload.$case === 'task') {
                    yield { contextId: payload.value.contextId || undefined };
                }

                if (payload.$case === 'statusUpdate') {
                    const content = getText(payload.value.status?.message?.parts ?? []);
                    yield {
                        content: content || undefined,
                        contextId: payload.value.contextId || undefined,
                    };
                }

                if (payload.$case === 'artifactUpdate') {
                    const content = getText(payload.value.artifact?.parts ?? []);
                    if (content) {
                        yield {
                            content,
                            contextId: payload.value.contextId || undefined,
                        };
                    }
                }
            }
        } catch (error) {
            console.error('Error streaming message:', error);
            throw error;
        }
    }

    async sendMessage(
        messages: A2AChatMessage[],
        contextId?: string
    ): Promise<{ content: string; contextId?: string }> {
        const client = await this.ensureClient();

        // Get the last user message
        const userMessage = messages[messages.length - 1];
        
        if (!userMessage || userMessage.role !== 'user') {
            throw new Error('Last message must be from user');
        }

        const params = createMessageRequest(userMessage.content, contextId);

        const response = await client.sendMessage(params);

        // Extract text content from the response
        let content = '';
        let responseContextId: string | undefined;

        if (isMessage(response)) {
            responseContextId = response.contextId || undefined;
            content = getText(response.parts);
        } else {
            responseContextId = response.contextId || undefined;
            const lastMessage = response.history[response.history.length - 1] ?? response.status?.message;
            if (lastMessage) {
                content = getText(lastMessage.parts);
            } else {
                content = response.artifacts.map(artifact => getText(artifact.parts)).join('');
            }
        }

        return {
            content,
            contextId: responseContextId,
        };
    }
}

function createMessageRequest(content: string, contextId?: string): SendMessageRequest {
    return {
        tenant: '',
        message: {
            messageId: uuidv4(),
            contextId: contextId ?? '',
            taskId: '',
            role: Role.ROLE_USER,
            parts: [{
                content: { $case: 'text', value: content },
                metadata: undefined,
                filename: '',
                mediaType: 'text/plain',
            }],
            metadata: undefined,
            extensions: [],
            referenceTaskIds: [],
        },
        configuration: undefined,
        metadata: undefined,
    };
}

function getText(parts: Part[]): string {
    return parts
        .filter(part => part.content?.$case === 'text')
        .map(part => part.content?.value ?? '')
        .join('');
}

function isMessage(result: Message | Task): result is Message {
    return 'messageId' in result;
}
