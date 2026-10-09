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

Both complete addresses enforce a conservative **2,048-byte combined UTF-8
budget** across their two partition values, not 2,048 bytes per component.
Canonical scope encoding counts as part of that value; additional JSON escaping
in HTTP headers does not. This application guard is based on the
[documented Cosmos partition-key limit](https://learn.microsoft.com/en-us/azure/cosmos-db/concepts-limits#per-item-limits);
the documentation does not specify the internal `MultiHash` size calculation.
The tested vNext emulator accepts some oversized values, so emulator acceptance
alone is not a production boundary guarantee.

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

### Persistence composition without Aspire

Aspire is only one source of Cosmos clients/containers; `SharedServices` depends on
MAF hosting, standard DI and the Cosmos SDK, not Aspire. Existing host-wide keyed
registrations remain supported. `IHostedAgentBuilder.WithCosmosSessionStore` can also
attach an existing store or an `(IServiceProvider, agentName)` store factory directly,
allowing per-agent containers and snapshot retention without global store registration.
The factory uses MAF's singleton store lifetime and the same strict authenticated
isolation/anonymous-warning policy as the original overload.

External message history remains part of `ChatClientAgentOptions`, not a mutation of
an arbitrary hosted `AIAgent`. Its direct-container/factory overloads resolve logging
and keyed compaction during agent construction and reuse the common provider factory.
Their options are agent-local and do not inherit another global profile. Existing
direct-client, connection-string and credential overloads remain available.
Supplied clients are never disposed by per-agent helpers. The standalone extension
factory disposes a client it owns if any construction/configuration step fails,
then rethrows the original failure.
Owned-client direct constructors likewise clean up initialization failures. Borrowed
client lifetimes remain external; construction and extension configuration do not
introduce duplicate disposal paths.

Hosted protocols own their normal session load/save lifecycle. A direct `RunAsync`
caller must explicitly get/create the session and save it using the store, or initialize
the required persistence context when operating without a store. Registration alone
does not persist arbitrary direct runs.

### Retention and history compaction

Both containers have `DefaultTimeToLive = -1`: per-document TTL is enabled without a container-wide expiration.

| Component | Session TTL | Message TTL |
|---|---|---|
| Class-skills and A2A orchestrators | 7 days | 7 days |
| Restaurant, activities and accommodation agents | No expiration | 24 hours |
| Voice | 7 days for resumable application state | 7 days |

History revision metadata has **no expiration**. A later Responses alias can renew a snapshot's TTL without appending messages, so deriving metadata TTL from the last append would allow it to expire too early. Metadata therefore requires explicit maintenance when its histories are no longer needed. Expiring individual messages does not delete the session or reset its revision. Message TTL is measured from each message's last update, not from the last activity of the entire conversation.

Compaction is opt-in and disabled by default. The text history provider can apply
an injected `IHistoryCompactor` while loading a complete history. The first
implementation, `MafForegroundHistoryCompactor`, uses MAF's experimental
`CompactionStrategy` APIs; it does not access Cosmos or mutate an agent session.

| Responsibility | Owner |
|---|---|
| When and how to reduce messages | DI-configured MAF strategy, trigger and target |
| Foreground/background selection | Nullable `CosmosChatHistoryProviderOptions.Compaction` options |
| Execute reduction and return a candidate/ticket or retrieve its result | Storage-independent `IHistoryCompactor` |
| Write and activate the candidate history | History provider and its Cosmos repository |
| Save the complete agent session | Normal A2A/Responses hosting and session store |

`Foreground` awaits and validates a candidate during Load, but publishes it only
at Save. `Background` starts work during Load and polls for the result during Save.
The built-in profiles use `LocalBackgroundHistoryCompactor`, an in-process best-effort
adapter over `MafForegroundHistoryCompactor`. Custom engines use the same
`IHistoryCompactor` contract and advertise their supported mode.

Foreground Load keeps C1 and its expected revision active. A ready candidate, its
exact source reference, operation id and immutable profile are held only in
`SessionPersistenceContext.PreparedCompaction`, a transient `[JsonIgnore]` property
beside `ActiveHistory`. There is no separate StateBag entry or external store.
Every new Load clears abandoned preparation **before** reading, including after a
failed/cancelled model invocation and when compaction is disabled. Clear invalidates
it too. Serialization excludes it from snapshots without clearing the live value;
a restored session recalculates. The supported same-session contract is non-overlapping
invocations, not concurrent use of one `AgentSession`.

The simple `ProvideChatHistoryAsync`/`StoreChatHistoryAsync` hooks retain MAF's base
merging, stamping, filtering and failure behavior. The model/output-filter view is a
deep copy of the already detached canonical candidate. At Save, the provider joins
that canonical prefix with the exact newly filtered request/response messages, validates
them, and publishes against the captured source revision. It does
not append to C1 first or append the same turn twice. Only successful publication
updates `ActiveHistory` and clears preparation; hosting saves the snapshot normally.
Profile/options changes cannot apply an old preparation. A model failure skips Store
entirely and therefore leaves C1 untouched (apart from independent concurrent writers
or TTL expiry).

Before inference or compaction, one private model view omits consumed approval-only
messages whose call identity/arguments and completed result match. Consumption
proof reuses the contiguous function-exchange validator, not a scan of result ids.
Roles, call uniqueness, ordering and complete parallel results must be valid before
restoring informational flags; nested approval calls must match the direct execution
and denied decisions never supply consumption proof. Compaction's validator also rejects
`Approved=false` explicitly, regardless of an existing informational flag or matching exchange.
With incomplete/malformed exchanges,
the provider logs the reason and leaves all existing approval flags/records unchanged.
The same proof applies to background merges, with caller messages copied before mutation.
System/developer,
mixed-content, denied, pending and ambiguous records are not removed. This prevents
the inference adapter from projecting old approval audit records as empty assistant
messages. Source storage remains unchanged. It is not a generic empty-text filter:
real function calls/results and even unrelated empty messages are preserved.
Both execution modes receive a detached clone of this view.

Tool/approval middleware can call Save with an incomplete new exchange. The foreground
prefix has already passed full compaction validation, including complete tool groups;
the appended suffix is copied unchanged, not reduced. This allows a native approval
pause to be persisted without dropping its messages or retaining a foreground job.
The repository validates individual message and batch payloads before writing.
A subsequent Load with pending tools uses the existing safe unchanged-history path.

Background start returns `Unchanged` or `Pending(ticket)`, never inline `Completed`.
The ticket, exact request and source reference live only in transient `[JsonIgnore]`
state between that Load and its Save. They are never written into a hosted session
snapshot. `BackgroundSaveWaitTimeout` gives Save a bounded polling window, defaulting
to two seconds. A valid ready result is merged with the current invocation's filtered
input/output and conditionally published as C2. Pending, missing, failed, or invalid
work falls back to the normal append on C1. The provider calls `CancelAsync` using
that same ticket and discards its live state; cancellation does not await the worker.
A new Load, explicit Clear, or cancelled Save also cancels abandoned jobs through
their original compactor. Cleanup failure is logged without masking the normal
turn save or its original error. The local worker owns a `CancellationTokenSource`
per ticket, forwards its token to the strategy/client, and retains orphan terminal
results for five minutes after completion, never counting execution time as retention.
Retrieval and cancellation signal a separate result-release token when removing the
job, interrupting retention and releasing completed worker/results promptly even if
removal races delay creation. Running workers retain their own cooperative cancellation.
A later Load starts a fresh job; restart recovery and cross-turn suffix merging are intentionally
out of scope. External cancellation and uncertain storage publication still propagate.

A profile identifies a keyed compactor and execution mode, not an aggregate byte cap.
History is stored as separate message documents; its total size can legitimately exceed
Cosmos's individual item limit. Repository document/batch checks remain independent
from compaction. UTF-8 diagnostics measure the JSON array of the complete `ChatMessage`
history, including content and metadata, and independently verify real reduction.
They are **not** an exact model token count or a complete prompt budget: leave capacity
for instructions, tool schemas, new input and model output when selecting MAF triggers
and targets. Structural and source-version validation remain enabled in both modes.

The provider must validate the returned candidate independently. Fewer messages
are not required: replacing a large tool result with shorter content can change
size without changing the message count. System messages, unresolved approvals
and tool-call/result relationships must not be silently lost. Compaction never
grants approval to a pending tool.

Protected system/developer messages must form a contiguous prefix in both the
source and candidate, with identical payloads and ordering. The candidate cannot
insert conversation content before or between these instructions. A source with
interleaved protected instructions is deliberately not compacted: Load logs the
reason before starting any worker/strategy and uses the validated original history.
Normal appends and unchanged fallback remain valid. This conservative rule avoids
reconstructing instruction positions across summarized or removed conversation content;
instructions outside stored history are not part of this check.

Summary validation distinguishes provenance: explicit summary metadata is required
for canonical/fallback history, unchanged results and newly appended conversation
messages. Every new compactor-produced assistant text-only message must be useful:
blank text or the unavailable-summary sentinel is rejected even with no metadata/prefix.
Source messages retained unchanged are not new output; nontext tool/reasoning contents
do not require summary text. Flagged summaries remain validated in all paths.
Retention exemptions consume source occurrences once, using semantic message equality
and per-validation bookkeeping. One source message cannot excuse multiple invalid
candidate copies; genuine repeated source messages can retain their original count.
The same canonical/fallback validation runs on the source before strategy/job execution
and in the independent candidate validator. Explicitly flagged invalid persisted summaries
fail even if a replacement removes them; compaction is not an implicit corruption-repair path.

The summarizer, if used, is a separate `IChatClient` dependency without the main
agent's tools/history/compaction pipeline. Otherwise summarization could recurse
or execute application tools. Strategies use per-invocation copies and indexes;
no full compaction index is placed in `AgentSession.StateBag`.

All five text hosts accept a built-in opt-in `summary` profile via
`AddHistoryCompactionProfile`. It requires an explicit `HistoryCompaction:Model`
deployment, positive `TriggerTokens` and positive `MinimumPreservedGroups`.
`Background` wraps the same strategy in the local best-effort adapter. No default
activation, implicit model reuse or fixed threshold is supplied.
Each host provides a lazy factory that calls `AsIChatClient(model)` on the already
registered Azure Inference `ChatCompletionsClient`, reusing the `foundry`
endpoint/credentials but not the agent's wrapped `IChatClient`. The separate adapter
is DI-owned and created only when the enabled compactor is resolved. The shared
profile constructs the public MAF `SummarizationCompactionStrategy` and wraps it
in the existing `MafForegroundHistoryCompactor`, retaining all provider validation
and deferred publication behavior. MAF's default summarization prompt is used.
Summary output is trusted persisted assistant content, so deployment trust and
workload-specific recall testing are essential; no semantic-fidelity guarantee is implied.

Built-in profile kind and runtime DI identity are separate: the configured key selects
the built-in algorithm, while each helper invocation returns options with a unique
compactor registration key and captures a private per-profile summary-client key.
Agent providers reuse those returned options. One host can compose several built-in
profiles without last-registration wins or cross-agent model/threshold/mode changes.
Custom keyed registrations retain their explicit key unchanged. Clients stay lazily
created and DI-owned, without an additional strategy registry or provider lookups at Load.

Timeout applies to provider compactor calls and, for the local adapter, its foreground
worker too. This is a local worker deadline as well as a per-call limit, separate from
Save's wait window; custom remote engines define their own job deadlines. A compactor
cancellation without cancellation of the current caller/deadline token becomes an
explicit failure eligible for logged, revision-validated fallback. User cancellation
and the background Save deadline still propagate to their original handlers.

The installed MAF 1.23 exposes the ad-hoc `CompactionProvider.CompactAsync`
entry point publicly, but its `CompactionMessageIndex.Create` factory is internal.
The adapter uses only the public entry point, with MAF's default content token
estimation (bytes / 4 per group); it does not use reflection/unsafe access or
claim custom tokenizer support that this entry point does not expose.
The summary trigger uses `CompactionTriggers.TokensExceed` on `IncludedTokenCount`;
this estimated history-only count is not the full prompt or a model-window cap.
Debug logs in `SharedServices.MafForegroundHistoryCompactor` expose
`EstimatedHistoryTokens` and `TriggerTokens`, not content. Preserved groups are
MAF non-system atomic groups, not human turns, and form a hard floor even above
the threshold. Both the threshold and preservation count must be workload-calibrated.
Optional `HistoryCompaction:TargetTokens` must be positive and below `TriggerTokens`.
When configured, the profile passes MAF's native target predicate
`index.IncludedTokenCount <= targetTokens`; omitted/null retains the native
inverse-trigger default. MAF evaluates the target while excluding older groups,
before adding the generated summary, and cannot cross the preserved-group floor.
The final history can therefore exceed the target even in estimated tokens.
This is an estimated retained-history target, not a guaranteed prompt/window cap
or an additional compaction engine.

`IHistoryCompactor` is the only history-reduction path. There is no in-place
clear-and-rewrite reduction or automatic permanent archive.
When compaction is off, `MaxMessagesToRetrieve` can select the latest N messages
in chronological order without modifying stored history or its revision. A
positive value enables the window; null loads the complete history. It counts
messages, not tokens or complete tool groups.
Configuring compaction against a partial `MaxMessagesToRetrieve` window is an
error: safe compaction requires the complete history.

#### Publishing a compacted history

The provider loads C1 at an expected revision and obtains a candidate. At Save it
writes C2 completely (including the filtered new turn) under the same scope and
conditionally publishes its replacement.
C1's message documents and their existing TTLs stay unchanged; its control
metadata prevents new writes after retirement. New C2 documents receive the
provider's configured retention. The reference in the working session changes
only after successful publication, and hosting persists the snapshot normally.

This is optimistic concurrency, **not** automatic request queuing or merging of
competing writers. Background publishes only against the exact history reference
captured by the current Load.
If another request advances C1 during compaction, the stale rotation cannot win.
A request already running on retired C1 cannot silently redirect its output to
C2. A conflicting turn is not automatically replayed.

Retrying the same unpublished candidate first queries its live messages and verifies
the exact count, contiguous sequences, and canonical payload hash against the rotation
binding. Missing/expired messages or payload/sequence mismatches reject the retry before retiring C1.
This extra read applies only when reusing an already staged target, not every rotation.
As with ordinary history reads, it is not atomic with Cosmos's independent TTL sweeper.
Application-created message envelopes are immutable: initial message TTL and rotation
binding TTL come from the same validated setting, and retries with another setting
conflict. Out-of-band edits of message-envelope metadata, including TTL, are outside
this repository's supported writer contract; the retry check is not a general tamper audit.

Rotation is not a transaction across partitions or containers. If publication
completed but the session checkpoint did not, recovery may follow only the
exact recorded source revision to the exact published, still-unadvanced target.
It must not chase the newest history and thereby revive a stale Responses
snapshot. Incomplete/unpublished candidates can require maintenance; permanent
TTL does not make them disappear automatically.
Because the initial target now includes the current turn, exact recovery after a
lost checkpoint can return that completed turn, even if the client did not receive
its successful completion. This preserves a single linear history, not immutable
historical Responses snapshots. Recovery advances only the history reference: other
skill/context/session state remains at the last successful checkpoint. It cannot identify a resent user
request as a retry or deduplicate model/tool execution; application-level request
idempotency is outside this provider's contract.

The provider has no notification that hosting successfully checkpointed the
session. It can therefore rotate once per successful Save, including more than once
on the same working session across inner approval invocations. If a checkpoint
is lost after C1 -> C2 -> C3, recovery from C1 fails explicitly: it follows one
exact transition, not an arbitrary chain. Repeated, very aggressive test triggers
are not a promise of automatic crash recovery through every intermediate history.

Compactor failure during Load may use the original history only when its revision
is still current and its structural/summary validation passes. The MAF strategy controls
its own trigger and target. Cancellation,
storage conflicts, Save validation/publication failures and an unusable context must be surfaced, not converted into
a successful empty or truncated history.

#### Scope and measurement

Shared repositories and compactor APIs use `Task`/`Task<T>` for asynchronous I/O.
MAF overrides retain the framework's `ValueTask`/`ValueTask<T>` signatures.
Do not convert the other APIs solely to avoid allocations: use `ValueTask` only
when profiling demonstrates a benefit, and consume each instance once.

This delivery covers **text only**. Voice keeps its existing load/replay/save
flow and separate transcript. Live audio compaction, ACS adapters, independent
Responses branches and a durable cross-process background compactor remain separate work.

Measure history loading, local indexing, optional summarizer inference, target
writes and publication separately. Foreground still awaits compaction before model
inference, but target staging/publication is deferred until Save, after inference.
Even a no-op requires local analysis. No latency SLO is
implied by the configuration, and the compactor's success is distinct from a
successful history publication and session checkpoint.

Enable only after all writers understand the additive rotation metadata.
Existing nonrotated schema-v2 histories remain readable and no partition-key
migration is required. Disabling the feature later stops new compactions; a
checkpoint still pointing to a purely rotated source can use the same exact
recovery path even when the profile is disabled.

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
