# Architecture

## System Overview

The application runs two independent, non-voice orchestrators with the same city-assistant capabilities. They differ only in how those capabilities are composed:

- **Class Skills Orchestrator** (`class-skills-orchestrator-agent`) uses only in-process `AgentClassSkill<T>` implementations.
- **A2A Agent Tools Orchestrator** (`a2a-orchestrator-agent`) uses only remote specialist agents exposed as tools through A2A.

Both orchestrators expose A2A HTTP+JSON endpoints, stream responses, and persist sessions and chat history in Cosmos DB.

## Topology

```text
                              ┌────────────────────────────┐
                              │      React Frontend        │
                              │  Select orchestration mode │
                              └─────────────┬──────────────┘
                                            │ A2A
                         ┌──────────────────┴──────────────────┐
                         │                                     │
              ┌──────────▼───────────┐             ┌──────────▼───────────┐
              │ Class Skills         │             │ A2A Agent Tools      │
              │ Orchestrator         │             │ Orchestrator         │
              │                      │             │                      │
              │ AgentClassSkill<T>:  │             │ Remote A2A tools:    │
              │ • Restaurant         │             │ • Restaurant Agent  │
              │ • Activities         │             │ • Activities Agent  │
              │ • Accommodation      │             │ • Accommodation     │
              │ • Weather            │             │   Agent              │
              └──────────┬───────────┘             └──────────┬───────────┘
                         │                                    │ A2A
                         │                         ┌───────────┼───────────┐
                         │                         │           │           │
                         │                    Restaurant  Activities  Accommodation
                         │                         │           │           │
                         │                         └───────────┼───────────┘
                         │                                     │ MCP
                         │                           Geocoding MCP Server
                         │
                         └──────────────────┬──────────────────┘
                                            │
                                      Cosmos DB
                              sessions + conversation history
```

## Orchestrators

### Class Skills Orchestrator

Project: [`src/orchestrator-agent`](../src/orchestrator-agent)

The orchestrator uses `AgentSkillsProvider` for progressive disclosure and contains no remote A2A tools. Restaurant, activities, accommodation, geocoding, and weather behavior executes in process. Trusted skill-provider tools and scripts are auto-approved.

Agent name: `class-skills-orchestrator-agent`

### A2A Agent Tools Orchestrator

Project: [`src/a2a-orchestrator-agent`](../src/a2a-orchestrator-agent)

The orchestrator resolves the restaurant, activities, and accommodation agent cards at startup and exposes each remote agent with `AsAIFunction()`. It contains no class-based skills.

Agent name: `a2a-orchestrator-agent`

## Frontend Routes

The frontend defaults to the class-skills mode and provides a mode selector. Aspire proxies stable same-origin routes:

| Mode | Agent card | A2A service |
|---|---|---|
| Class-based skills | `/orchestrators/class-skills/.well-known/agent-card.json` | `/orchestrators/class-skills/agenta2a` |
| A2A agents as tools | `/orchestrators/a2a/.well-known/agent-card.json` | `/orchestrators/a2a/agenta2a` |

The legacy `/.well-known/agent-card.json` and `/agenta2a` routes remain aliases for the class-skills orchestrator.

## Storage and Protocols

- **A2A HTTP+JSON** is used from the frontend to both orchestrators and from the A2A orchestrator to specialist agents.
- **MCP** is used by standalone specialist agents for geocoding.
- **Cosmos DB** stores agent sessions and conversation history with agent-specific keys, so the same `contextId` cannot mix histories between orchestrators. See [Session Persistence and User Isolation](#session-persistence-and-user-isolation).
- **Aspire** starts both orchestrators, all specialist agents, the geocoding MCP server, Cosmos DB, Foundry connections, and the frontend.

## Session Persistence and User Isolation

### How sessions are stored

Three identifiers have different responsibilities:

| Identifier | Source | Responsibility |
|---|---|---|
| Isolation key | Trusted `AgentIsolationKeyProvider`, when registered | Identifies the caller's isolation boundary, not a conversation |
| Session lookup id | Hosting protocol or voice adapter | Selects a stored snapshot: A2A `contextId`, Responses conversation/response id, or voice continuation |
| History conversation id | Generated by the persistence layer | Identifies one message history, independently of its current lookup aliases |

Shared services use two fixed hierarchical partition keys in both anonymous and isolated mode:

| Container | Partition key | Document id |
|---|---|---|
| `sessions` | `[/scopeKey, /sessionId]` | Reversibly encoded stable agent id |
| `conversations` | `[/scopeKey, /conversationId]` | Internal message id or reserved history metadata id |

`StorageScope` encodes an anonymous scope as compact JSON `["anonymous","<lookup-id>"]`. With framework key partitions, it encodes `["partitions",[["isolation","<caller>"],...]]`, sorting all partition names ordinally. This distinguishes anonymous and isolated namespaces and avoids ambiguous separator concatenation. Additional partitions are honored rather than discarded.

`sessionId` retains the opaque external lookup id. It is not assumed to be a UUID, phone number, A2A context or Responses id. No HMAC or encryption is applied to these identifiers: **encoding is not anonymization**, and stored keys can contain personal data. An anonymous id identifies a continuation, not its owner's identity.

`CosmosAgentSessionStore` adapts MAF serialization to the common `CosmosSessionRepository`. Its schema-v2 envelope contains `id`, `agentId`, `scopeKey`, `sessionId`, `serializedSession`, `lastUpdated`, and `ttl`. `serializedSession` is a JSON object containing the complete agent state. There is no top-level active `conversationId`.

Every hosted agent sets a stable `ChatClientAgentOptions.Id`; otherwise stored sessions would be unreachable after a restart or on another replica. Each read deserializes an independent working copy. Missing documents return `null`; malformed state and storage failures are not treated as missing sessions.

`SessionPersistenceState` holds the sole active-history reference in `AgentSession.StateBag`: its immutable anchor scope, generated conversation id and expected history revision. The history provider does not infer this reference from the current protocol id or query a second identity provider.

### Current mode: anonymous

The application has no user authentication by design. `WithCosmosSessionStore()` wraps the store in the framework's `IsolationKeyScopedAgentSessionStore`. Without an `AgentIsolationKeyProvider`, it logs a warning and accepts unscoped continuation keys; `StorageScope` maps these to anonymous scopes.

A browser-generated UUID is difficult to guess, but the frontend's **Context** field can supply any valid key. Knowing a continuation id is enough to resume it in anonymous mode. A phone number supplied as a continuation is not a verified caller identity.

### Enabling authenticated users

Authentication is not implemented by this persistence layer. To enable it later:

1. Authenticate users or verify the originating channel, and authorize every exposed endpoint. For browser clients, configure the token/cookie flow separately.
2. Register one trusted `AgentIsolationKeyProvider`. In ASP.NET Core, `AddHttpContextAccessor()` plus `UseClaimsBasedAgentIsolation()` from `Microsoft.Agents.AI.Hosting.AspNetCore` uses an authenticated claim. The default `NameIdentifier` must be stable and unique across all callers; multi-tenant identities may require a composite tenant/subject provider.
3. Keep isolation strict when a provider is registered. A missing key must fail, not fall back to anonymous storage. Setting one session-store option to non-strict is not enough to enable optional login consistently across OpenAI resources and A2A tasks.
4. Capture the same trusted key at voice WebSocket ingress. Do not read request claims from the history provider or during detached background work.

A verified messaging channel may supply a channel-scoped person identifier through this provider. Never promote unvalidated request headers, phone numbers or continuation ids to trusted identity.

The schema does **not** change when authentication is enabled. The framework adds the isolation partition to session keys, and the persistence context carries the resulting scope into history. Existing anonymous documents are not automatically assigned to the newly authenticated user. Some existing retention policies are indefinite; those documents must not be assumed to disappear after seven days.

Authentication and identity propagation between the orchestrators and remote specialist agents remain separate work. `AsAIFunction()` and voice calls without a session start new specialist sessions, and do not automatically forward an end user's identity.

### Responses aliases and protocol-owned storage

The hosted session store treats keys as opaque, including when the same working session is saved under multiple aliases:

| Responses operation | Session read key | Session write keys |
|---|---|---|
| First turn | Generated response id | New response id, unless `store=false` |
| `previous_response_id` | Previous response id | New response id, unless `store=false` |
| Explicit `conversation` | Conversation id | New response id (if stored) and conversation id |

An anonymous response chain changes the scope of its session lookup documents. Its history keeps the original anchor scope inside the serialized session. Do not overwrite this anchor when a new response alias is saved. Authenticated aliases must retain the same trusted partition scope.

The application's external history supports **linear continuation**, not independent branches from the same old response. An expected-revision check rejects stale snapshots rather than silently reading a newer shared history.

MAF 1.23's OpenAI response/conversation resources and conversation index remain in memory; their internal storage abstractions are not replaced by our Cosmos repositories. Consequently, restarting the host can lose `GET /responses/{id}` results and explicit Conversations resources while retaining the custom session snapshots. Protocol tests characterize the installed package's behavior for `previous_response_id`, explicit conversations, streaming, `store=false` and background execution.

### Conditional writes and consistency

New session addresses use create-only writes. A loaded address is replaced only with its loaded Cosmos ETag. Tokens belong to one independent working copy and one full address; they are not serialized into snapshots or shared between runs. A response alias and its mutable conversation head have different tokens.

History metadata and appended messages share a complete history partition. Revision checks and transactional batches prevent two writers from advancing the same version without a conflict. Ordering is explicit rather than inferred from equal timestamps.

Cosmos does not provide an atomic transaction across the two containers. If history append succeeds but saving the session fails, the session can be stale: the next attempt reports the mismatch rather than silently adopting newer messages. Conflicts do not automatically replay model/tool side effects. Recovery is explicit; orphaned or partially advanced data can require intervention.

The session envelope has a conservative 2,000,000-byte UTF-8 budget. An oversized document, wrong container schema, malformed snapshot or failed batch is an error, not an empty session or successful save.

History content is polymorphic (`TextContent`, function calls/results, and other
`AIContent` types). Cosmos may reorder JSON object properties, so the history
deserializer accepts out-of-order `$type` metadata. It still rejects unknown
discriminators; it does not convert unsupported content into plain text.

Tool approval state also needs reconstruction across append-only history reads.
If a stored function result proves a call has completed, its historical approval
request/response is marked informational on the newly loaded working copy. Pending
requests without a result are not auto-approved. This preserves the framework's
in-memory consumed-approval semantics without rewriting audit history or executing
the tool again.

The model client pipeline removes only the reserved `a2a.configuration` transport
metadata from a cloned `ChatOptions` before inference. The agent's original
options remain intact, and actual model parameters are preserved. Otherwise the
Azure AI Inference adapter forwards that metadata as an unsupported model
parameter.

### Retention and future compaction

Both containers have `DefaultTimeToLive = -1`: per-document TTL is enabled without a container-wide expiration.

| Component | Session TTL | Message TTL |
|---|---|---|
| Class-skills and A2A orchestrators | 7 days | 7 days |
| Restaurant, activities and accommodation agents | No expiration | 24 hours |
| Voice | 7 days for resumable application state | 7 days |
| Legacy archived messages | According to the owning session policy | No expiration |

History revision metadata has **no expiration**. A later Responses alias can renew a snapshot's TTL without appending messages, so deriving metadata TTL from the last append would allow it to expire too early. Metadata therefore requires explicit maintenance when its histories are no longer needed. Expiring individual messages does not delete the session or reset its revision. Message TTL is measured from each message's last update, not from the last activity of the entire conversation.

The reference can later select a new conversation within the same scope without changing the external session lookup id. **New compaction, automatic rotation and branch-specific histories are not implemented in this redesign.** Existing reducer policies are not enabled by default.

Hierarchical keys allow a scope prefix to span partitions; they do not remove the 20-GB limit on each complete logical partition key or guarantee unlimited throughput for a hot scope.

### Recreating existing containers

This is an intentional schema break, with no automatic migration or deletion. Both container names remain `sessions` and `conversations`.

Before running the new schema against existing data:

1. Stop all old writers and take any required backup.
2. Manually delete **only** the confirmed `sessions` and `conversations` containers in the intended environment.
3. Recreate them through the updated AppHost/provisioning configuration and start the updated services.

Do not mix old and new application versions. Aspire does not update an existing emulator container's partition key, and Azure cannot change it in place. Data loss is **not** limited to seven days: specialist sessions and archives may have no expiration. Old ids cannot retrieve deleted data.

## Configuration

Aspire injects Foundry, Cosmos, and service-discovery settings. The A2A orchestrator requires:

- `services__restaurantagent__https__0` or `services__restaurantagent__http__0`
- `services__activitiesagent__https__0` or `services__activitiesagent__http__0`
- `services__accommodationagent__https__0` or `services__accommodationagent__http__0`

The class-skills orchestrator has no specialist-agent service dependencies.
