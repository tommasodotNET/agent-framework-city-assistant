# City Assistant - Agent Framework Demo

A multi-agent application built with Microsoft Agent Framework. It demonstrates two equivalent non-voice orchestration styles side by side: in-process class-based skills and remote agents used as tools over A2A.

## Architecture

The application includes:

1. **Class Skills Orchestrator** - Uses only in-process `AgentClassSkill<T>` implementations for restaurants, activities, accommodations, and weather.
2. **A2A Agent Tools Orchestrator** - Uses only the standalone restaurant, activities, and accommodation agents as tools over A2A.
3. **Specialist Agents** - Standalone restaurant, activities, and accommodation agents with A2A endpoints.
4. **Geocoding MCP Server** - Provides landmark-to-coordinate conversion to the standalone specialist agents.
5. **Frontend** - A React chat interface with a selector for the two orchestration modes.
6. **Cosmos DB** - Persists agent sessions and conversation history.

See [`.github/architecture.md`](.github/architecture.md) for the topology, data flow, service dependencies, and stable frontend routes.

## Prerequisites

- .NET 10 SDK
- Node.js 18+ and npm
- Azure AI Inference (Foundry) connection
- Azure Cosmos DB instance

## Run the sample

> This sample requires latest .Net 10 Preview SDK (RC2) and Python 3.11+ installed on your machine.

To allow Aspire to create or reference existing resources on Azure (e.g. Foundry), you need to configure Azure settings in the [appsettings.json](./src/aspire/apphost.settings.json) file:

```json
"Azure": {
  "TenantId": "<YOUR-TENANT-ID>",
  "SubscriptionId": "<YOUR-SUBSCRIPTION-ID>",
  "AllowResourceGroupCreation": false,
  "Location": "<YOUR-LOCATION>",
  "CredentialSource": "AzureCli"
}
```

Use [aspire cli](https://learn.microsoft.com/en-us/dotnet/aspire/cli/install) to run the sample.

The five text agents use the deployment name in `AI:ChatModel` (environment
variable `AI__ChatModel`), defaulting to `gpt-4.1`. Set this on the agent processes
when testing with a different existing Foundry deployment. Voice retains its
separate `VoiceLive:Model` setting.

Some model inference endpoints accept chat requests but do not implement
`/models/info`, which the Aspire inference health check uses. For those endpoints
only, local runs can set `Aspire__Azure__AI__Inference__DisableHealthChecks=true`.
This disables that specific model-info probe; a healthy process is still not
proof of model access. Verify an actual chat/tool request instead.

Start the distributed application:

```bash
aspire start
```

When running from a worktree, isolate ports and local state:

```bash
aspire start --isolated
```

**Existing Cosmos data:** the persistence schema uses hierarchical keys on both
`sessions` (`/scopeKey`, `/sessionId`) and `conversations` (`/scopeKey`, `/conversationId`).
Old single-key containers must be backed up if needed and manually recreated before
running this version. No automatic migration or deletion is performed. See
[session persistence and isolation](.github/architecture.md#session-persistence-and-user-isolation)
for the schema, protocol limitations and per-agent retention policies.

To ease the debug experience, you can use the [Aspire extension for Visual Studio Code](https://marketplace.visualstudio.com/items?itemName=microsoft-aspire.aspire-vscode#:~:text=The%20Aspire%20VS%20Code%20extension,directly%20from%20Visual%20Studio%20Code.).

## Features

### Restaurant Agent
- Search restaurants by category (vegetarian, pizza, japanese, mexican, french, indian, steakhouse)
- Search restaurants by keywords
- Get all available restaurants
- A2A endpoint at `/agenta2a`
- OpenAI-compatible endpoints for testing

The standalone agent remains available for independent clients and for the A2A agent-tools orchestrator. The class-skills orchestrator carries equivalent restaurant behavior in an `AgentClassSkill<T>` exposed through `AgentSkillsProvider`.

### Accommodation Agent
- **Multi-criteria search** with the following filters:
  - User rating (1-5 scale)
  - Location (city name or proximity to coordinates)
  - Amenities (parking, wifi, breakfast, room-service, gym, spa, restaurant, pool, etc.)
  - Price per night (in euros)
  - Accommodation type (Hotel, BedAndBreakfast, Hostel, Apartment, Resort, Guesthouse, Motel, Villa, Boutique)
- **Geocoding via MCP** - Uses the shared Geocoding MCP Server to convert addresses/landmarks to coordinates
  - Communicates via Model Context Protocol (MCP) over HTTP
  - Known locations: Colosseum, Vatican, Pantheon, Trevi Fountain, Rome, Latina, etc.
  - Smart fallback with Rome city center coordinates for unknown locations
- **LLM-based reranking** using pointwise scoring (1-10 scale)
  - Parallel processing with configurable MAXDOP (default: 3)
  - Returns only highly relevant results (score > 6)
  - Detailed grading criteria and evaluation process
- Semantically rich accommodation descriptions for optimal reranking
- A2A endpoint at `/agenta2a`
- OpenAI-compatible endpoints for testing

### Geocoding MCP Server
- **Model Context Protocol (MCP) compliant** server for geocoding services
- Exposes `geocode_location` tool via MCP protocol
- Mock geocoding data for Rome landmarks and cities:
  - Rome landmarks: Colosseum, Vatican, Pantheon, Trevi Fountain, Spanish Steps, Trastevere, etc.
  - Cities: Rome, Latina
  - Areas: Downtown Rome, Termini Station
- Returns coordinates in latitude/longitude format
- Fallback to Rome city center for unknown locations
- HTTP-based MCP transport
- Can be consumed by any MCP-compatible client or agent
- Shared across multiple agents in the system
- Health check endpoint at `/health`
- MCP endpoints at `/mcp/v1/*`

### Class Skills Orchestrator
- Uses only in-process class-based skills for restaurants, activities, accommodations, and weather
- Uses `AgentSkillsProvider` for progressive skill disclosure
- Has no remote specialist-agent dependencies
- Maintains conversation history via Cosmos DB
- Exposes A2A endpoint at `/agenta2a` for frontend communication
- Uses contextId for conversation management

### A2A Agent Tools Orchestrator
- Resolves restaurant, activities, and accommodation agent cards at startup
- Exposes only those remote agents as tools with `AsAIFunction()`
- Contains no class-based skills
- Maintains conversation history via Cosmos DB
- Exposes A2A endpoint at `/agenta2a`

### Frontend
- Clean, modern chat interface
- Selectable **Class-based skills** and **A2A agents as tools** modes
- Streaming responses via A2A JavaScript SDK
- Theme support (light/dark/system)
- Session management with conversation history using contextId
- Communicates with either orchestrator via stable same-origin A2A routes

## Mock Data

### Restaurant Agent
The restaurant agent includes mock data for 11 restaurants across various categories:
- 3 Vegetarian restaurants
- 3 Pizza places
- 5 Other cuisines (Japanese, Mexican, French, Indian, Steakhouse)

### Accommodation Agent
The accommodation agent includes mock data for 12 accommodations in Rome and Latina:
- 5 Hotels (ranging from budget to luxury, €45-€450 per night)
- 3 Bed & Breakfasts (cozy options, €65-€80 per night)
- 1 Hostel (budget-friendly, €30 per night)
- 1 Boutique hotel (premium location, €280 per night)
- 2 Hotels in Latina (€85-€110 per night)

Each accommodation includes:
- Detailed description with location context, amenities, and target audience
- User ratings (3.8-4.9 out of 5)
- GPS coordinates for proximity search
- Complete address information
- List of amenities (parking, wifi, breakfast, gym, spa, pool, etc.)
- Accommodation type (enum-based)

All data is hardcoded in service classes and doesn't require external data sources.

## API Endpoints

### Restaurant Agent
- `GET /.well-known/agent-card.json` - A2A agent card (metadata and capabilities)
- `POST /agenta2a/v1/run` - A2A endpoint for agent-to-agent communication
- `POST /agenta2a/v1/stream` - A2A streaming endpoint
- `POST /v1/chat/completions` - OpenAI-compatible chat endpoint (for testing)
- `GET /health` - Health check endpoint

### Accommodation Agent
- `GET /.well-known/agent-card.json` - A2A agent card (metadata and capabilities)
- `POST /agenta2a/v1/run` - A2A endpoint for agent-to-agent communication
- `POST /agenta2a/v1/stream` - A2A streaming endpoint
- `POST /v1/chat/completions` - OpenAI-compatible chat endpoint (for testing)
- `GET /health` - Health check endpoint

### Geocoding MCP Server
- `POST /mcp/v1/initialize` - Initialize MCP session
- `GET /mcp/v1/tools/list` - List available MCP tools
- `POST /mcp/v1/tools/call` - Call an MCP tool (e.g., geocode_location)
- `GET /health` - Health check endpoint

### Class Skills Orchestrator
- `GET /.well-known/agent-card.json` - A2A agent card (metadata and capabilities)
- `POST /agenta2a/v1/run` - A2A endpoint for frontend and agent communication
- `POST /agenta2a/v1/stream` - A2A streaming endpoint for real-time responses
- `GET /health` - Health check endpoint

Frontend proxy:
- Agent card: `/orchestrators/class-skills/.well-known/agent-card.json`
- A2A service: `/orchestrators/class-skills/agenta2a`

### A2A Agent Tools Orchestrator
- `GET /.well-known/agent-card.json` - A2A agent card
- `POST /agenta2a/v1/run` - A2A endpoint
- `POST /agenta2a/v1/stream` - A2A streaming endpoint
- `GET /health` - Health check endpoint

Frontend proxy:
- Agent card: `/orchestrators/a2a/.well-known/agent-card.json`
- A2A service: `/orchestrators/a2a/agenta2a`

All communication between the frontend and both orchestrators uses A2A for standardized messages, streaming, and `contextId`-based conversation management.

The accommodation agent uses the Model Context Protocol (MCP) to communicate with the geocoding server for location-based queries.

## Development

### Project Structure

```
src/
├── service-defaults/          # Shared Aspire service configuration
├── shared-services/           # Shared services (Cosmos session store)
├── restaurant-agent/          # Restaurant recommendation agent
│   ├── Models/               # Data models
│   ├── Services/             # Business logic and storage
│   └── Tools/                # Agent tools/functions
├── accommodation-agent/       # Accommodation recommendation agent
│   ├── Models/               # Data models (Accommodation, AccommodationType, etc.)
│   ├── Services/             # Business logic (search, reranking, MCP geocoding client)
│   └── Tools/                # Agent tools/functions
├── geocoding-mcp-server/     # Geocoding MCP server
│   ├── Tools/                # MCP tools (geocode_location)
│   └── Program.cs            # MCP server setup
├── orchestrator-agent/       # Class-skills-only orchestrator
│   ├── Skills/               # Restaurant, activities, accommodation, weather skills
│   ├── Services/             # In-process skill services
│   └── Program.cs
├── a2a-orchestrator-agent/   # A2A-agent-as-tool-only orchestrator
│   └── Program.cs
├── frontend/                 # React frontend
│   └── src/
│       ├── Chat.tsx         # Main chat component
│       └── ...
└── aspire/                   # Aspire orchestration
```

### Building

```bash
# Build all projects
dotnet build

# Build specific project
cd src/restaurant-agent && dotnet build
cd src/orchestrator-agent && dotnet build
cd src/a2a-orchestrator-agent && dotnet build
```

### Persistence with standard MAF hosting (no Aspire)

`SharedServices` does not require Aspire. A keyed Cosmos `Container` can come from
ordinary DI or Aspire; existing keyed registrations remain supported. For per-agent
composition, attach an existing `CosmosAgentSessionStore` or a store factory directly
to `IHostedAgentBuilder`, and configure history on `ChatClientAgentOptions` using a
`Container` or a DI container factory.

For example, with an `IChatClient` already registered for the selected model:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharedServices;

var builder = Host.CreateApplicationBuilder(args);
// Register the application's IChatClient before resolving the agent.
builder.Services.AddSingleton<CosmosClient>(_ => new CosmosClient(
    builder.Configuration.GetConnectionString("cosmos")
        ?? throw new InvalidOperationException("Configure ConnectionStrings:cosmos.")));

builder.Services.AddAIAgent("city-agent", (sp, name) =>
{
    var options = new ChatClientAgentOptions { Id = name, Name = name }
        .WithCosmosChatHistoryProvider(
            services => services.GetRequiredService<CosmosClient>()
                .GetContainer("city", "conversations"),
            sp,
            history => history.MessageTtlSeconds = 86400 * 7);
    return sp.GetRequiredService<IChatClient>().AsAIAgent(options, services: sp);
}).WithCosmosSessionStore((sp, name) => new CosmosAgentSessionStore(
    sp.GetRequiredService<CosmosClient>().GetContainer("city", "sessions"),
    sp.GetRequiredService<ILogger<CosmosAgentSessionStore>>(),
    ttl: 86400 * 7));
```

Keep credentials in application configuration/secret management, not source. Alternatively, construct
the singleton client with the account endpoint and the application's `TokenCredential`.
Containers must already have the documented schema and TTL settings; these helpers
do not provision or migrate storage.

The hosted store factory receives the registered agent name, runs lazily once per
agent (singleton), and can select a different container/TTL for each agent. The
instance overload is `.WithCosmosSessionStore(existingStore)`. Both preserve the
existing anonymous warning and strict isolation when an `AgentIsolationKeyProvider`
is registered. The history factory runs once when constructing that agent's options;
it resolves the optional keyed compactor and logger through the supplied service
provider. Direct history options do not inherit a separate global history registration.
Shared clients/containers are never owned or disposed by these per-agent helpers.
DI owns clients created by its singleton factory; externally supplied clients remain
the caller's responsibility.

`WithSessionStore` configures a hosting service, not an automatic wrapper around
arbitrary direct calls to `agent.RunAsync`. A non-HTTP application can explicitly
use the registered hosted store:

```csharp
using var host = builder.Build();
var agent = host.Services.GetRequiredKeyedService<AIAgent>("city-agent");
var store = host.Services.GetRequiredKeyedService<AgentSessionStore>("city-agent");
var key = new AgentSessionStoreKey("conversation-1");
var session = await store.GetOrCreateSessionAsync(agent, key);
await agent.RunAsync("Find a restaurant in Agentburg.", session);
await store.SaveSessionAsync(agent, key, session);
```

A2A/Responses hosting integrations perform their own load/save lifecycle. A completely
standalone agent without a session store must instead initialize
`SessionPersistenceState` explicitly before using external history.

### Optional history compaction

Text history compaction is **disabled by default**. Register an
`IHistoryCompactor` through DI and select it in the history provider's options.
The compactor produces a candidate or an accepted-job ticket; the provider owns the Cosmos writes and
the active-history reference.

For example, this deliberately small demonstration profile retains recent turns:

```csharp
using Microsoft.Agents.AI.Compaction;
using SharedServices;

#pragma warning disable MAAI001 // Experimental MAF compaction APIs.
builder.Services.AddHistoryCompactor("recent-turns", _ =>
    new MafForegroundHistoryCompactor(
        new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(4))));
#pragma warning restore MAAI001

builder.Services.AddCosmosChatHistoryProvider("conversations", options =>
{
    options.Compaction = new HistoryCompactionOptions
    {
        Mode = HistoryCompactionMode.Foreground,
        CompactorKey = "recent-turns"
    };
});
```

Extend the existing registration rather than registering the same history
provider twice. Choose the trigger and algorithm for the application; these
sample limits are not production recommendations.

`MaxHistoryUtf8Bytes` is an **optional** application guard, defaulting to `null`.
Omit it or set it to `null` to skip the byte-cap checks. Set a positive value
only when the use case needs a maximum size for serialized history. It is not a
token limit or a MAF trigger. Structural validation, concurrency checks and
Cosmos document/batch limits still apply without it.

Any supported MAF `CompactionStrategy`, including a pipeline or a summarization
strategy with a separately injected chat client, can be supplied. The foreground
adapter uses the public ad-hoc MAF API and its default token estimate; it does
not depend on internal index factories or keep an index in the session snapshot.

When compaction changes history, foreground Load prepares and validates a detached
candidate without writing to Cosmos or changing the active history. Only a successful
Save writes the candidate plus the exact filtered new turn to a new conversation
and conditionally retires the original. Original messages retain their TTLs.
The external continuation id is unchanged; the normal hosting layer saves the
updated session. Concurrent writes fail explicitly. Recovery can follow only one
exact published, unadvanced target; after a failed session checkpoint that target
can include an unacknowledged turn, without restoring newer skill/session state or
deduplicating a resent request. See the
[compaction architecture](.github/architecture.md#retention-and-history-compaction)
for publication, recovery and remaining cross-container consistency limitations.

Without compaction, `MaxMessagesToRetrieve` optionally returns only the latest
N messages in chronological order without deleting or rewriting persisted history.
It must be positive when set; `null` loads the complete history. This is a
message-count window, not a token budget or a guarantee of complete tool groups.
Compaction is the only supported history-reduction mechanism.

Do not combine compaction with `MaxMessagesToRetrieve`: the compactor must read
the complete history. The provider supports both execution lifecycles:

| Mode | At Load | At Save |
|---|---|---|
| `Foreground` | Await and validate `Unchanged`/`Completed`; use a detached candidate for inference, without publishing it | Publish candidate + exact filtered new messages once, or append normally when no candidate was prepared |
| `Background` | Start best-effort work and keep the ticket only for this invocation; inference uses the original history | Poll until `BackgroundSaveWaitTimeout`; publish result + current turn when ready, otherwise append normally |

Foreground preparation and the background ticket/request are transient `[JsonIgnore]`
values inside the existing `SessionPersistenceContext`, not durable jobs or additional
StateBag entries. A model failure, restart, or Save timeout leaves the source history
intact. The next Load starts fresh work when compaction is still needed. Calls on the
same `AgentSession` must not overlap.

The simple MAF provider hooks retain their default error handling and storage filters:
history supplied to inference is not appended again. The output filter affects only the
model view, not the canonical candidate. Context-provider state, tools and instructions
are not copied into history; a contributed `ChatMessage` still follows MAF's normal
request filtering policy. Intermediate tool/approval Saves may publish a prevalidated
complete prefix plus an unchanged pending exchange. That suffix is never compacted;
the combined history must fit an explicit byte cap or Save fails without publication.
Target staging, source CAS publication and the normal hosted session checkpoint remain
separate operations, not a single atomic write.

Each Load prepares one model-history view. Approval-only messages are omitted only
when a matching, completed function call/result proves they are consumed. Pending,
denied, mixed-content and ambiguous approval messages remain. Actual tool calls and
results remain too; no source documents are deleted or rewritten. The compactor
receives a detached copy of this same view, not a separately filtered transcript.
Completed background results are validated against that exact filtered view.

The built-in `test-sliding-window` and `summary` profiles wrap
`MafForegroundHistoryCompactor` in `LocalBackgroundHistoryCompactor` when
`Mode` is `Background`. This worker is deliberately best-effort and in-process:
it does not survive a restart or move across replicas. `BackgroundSaveWaitTimeout`
defaults to two seconds and may be set to zero for one immediate result check.
If the result is still pending, missing, failed, or invalid, Save requests
`CancelAsync(ticket)`, discards the live ticket, and appends the current turn normally.
The same ticket identifies both retrieval and cancellation; no additional id or
session field is needed. Cancellation signals the worker without awaiting its
completion, so a strategy ignoring cancellation cannot delay Save. A later Load
starts a new job. No user message is dropped merely because compaction missed
its deadline. New Load, Clear, and caller cancellation during Save also abandon outstanding work.

The local worker's five-minute cleanup window starts only after the task terminates,
not at enqueue. It covers orphan results, not running jobs or Save's two-second wait.
Cancellation is cooperative; stopping the local request does not guarantee that
the remote model service stops processing immediately.

Custom background implementations still use the same `IHistoryCompactor` contract,
advertise `Background` in `SupportedModes`, and implement `GetResultAsync` and
idempotent `CancelAsync` without waiting for the worker to finish.
Voice compaction remains out of scope.

The five text hosts also accept an explicit `HistoryCompaction` configuration
section. An absent/empty section or `Enabled=false` leaves the feature off:

```json
{
  "HistoryCompaction": {
    "Enabled": true,
    "Mode": "Foreground",
    "CompactorKey": "test-sliding-window",
    "MaxTurns": 4
  }
}
```

These values can also be passed to an agent process as environment variables,
for example `HistoryCompaction__Enabled=true`. The built-in
`test-sliding-window` profile is model-free and requires `MaxTurns`; it is meant
for controlled tests. `MaxTurns` follows MAF's turn grouping, not a guaranteed
number of human requests: user-role approval responses can consume the window.
In the skills UI, a two-turn window can remove the latest human prompt and its
constraints while retaining tool output. Do not treat this test profile as a
production policy for preserving recent user intent.
For real LLM summarization, the same five text hosts support the opt-in `summary`
profile using the public MAF `SummarizationCompactionStrategy` through
`MafForegroundHistoryCompactor`:

```json
{
  "HistoryCompaction": {
    "Enabled": true,
    "CompactorKey": "summary",
    "Mode": "Foreground",
    "Model": "gpt-5.4-mini",
    "TriggerTokens": 24000,
    "TargetTokens": 12000,
    "MinimumPreservedGroups": 6,
    "Timeout": "00:01:30",
    "MaxHistoryUtf8Bytes": null
  }
}
```

The dedicated summary client retains MAF's system prompt and appends a final
instruction to summarize the preceding transcript rather than answer its historical
requests. That instruction exists only in the summarizer request, never in persisted
conversation history. This improves task framing; it does not guarantee semantic
fidelity, which still needs workload-specific evaluation.

To use this profile in the local background worker, set `"Mode": "Background"`.
`"BackgroundSaveWaitTimeout": "00:00:02"` explicitly sets the default Save wait;
the same setting is available as `HistoryCompaction__BackgroundSaveWaitTimeout`.

If an agent response finishes without any non-whitespace text, the chat UI reports
`empty_response` instead of silently returning to idle. It does not retry the request:
tools may already have executed. Empty intermediate streaming events remain valid.

These are explicit example/calibration values, not production defaults or a model-window
limit. Measure the actual tool-heavy workload before selecting a threshold and preservation
floor. Nothing is enabled in checked-in appsettings. `Model` (deployment name),
positive `TriggerTokens`, and positive `MinimumPreservedGroups` are required; invalid
configuration fails before inference. `Mode` defaults to `Foreground`, and omitting
the timeout or byte cap leaves them disabled. Recent groups are MAF atomic message/tool
groups, **not human turns**. The floor can prevent reduction even above the trigger;
there is no guarantee the result fits a model context window.

Optional `TargetTokens` must be positive and strictly less than `TriggerTokens`.
It passes MAF's native target predicate `index.IncludedTokenCount <= TargetTokens`:
once triggered, MAF selects older groups for summarization until the retained-history
estimate reaches that target or the preserved-group floor prevents further reduction.
Omitting it (or setting it to `null`) retains MAF's default inverse-trigger target.
A lower target can leave more room before the next trigger, instead of summarizing
only enough history to fall just below it. The target is evaluated **before the new
summary is added**, so even estimated final history can exceed it; this is not a
guaranteed exact budget, output-size cap, or a separate hysteresis mechanism.

`TriggerTokens` compares MAF's **estimated included-history tokens** (default content
bytes / 4 per group). It excludes instructions/tools added outside stored history,
the new input and reserved output; it is not exact tokenizer usage or a full-prompt budget.
Set `Logging:LogLevel:SharedServices.MafForegroundHistoryCompactor` to `Debug` to
inspect the structured `EstimatedHistoryTokens` and `TriggerTokens` diagnostics.
For offline calibration, a no-op public MAF strategy can observe
`index.IncludedTokenCount` through its trigger when run via `CompactionProvider.CompactAsync`;
no internal index factory is needed.

Each host passes a lazy factory to `AddHistoryCompactionProfile` that creates a
dedicated `ChatCompletionsClient.AsIChatClient(Model)` adapter from its existing
Azure Inference client. It uses the same `foundry` endpoint/credential registration
(`Aspire:Azure:AI:Inference` / `ConnectionStrings:foundry`, with the existing
`DefaultAzureCredential` configuration), but **never** resolves the agent's wrapped
`IChatClient` or falls back to `AI:ChatModel`. The adapter has no function-invoking,
agent history, or compaction middleware. MAF receives old tool calls/results as history,
not as executable tools; the native system prompt and the final task instruction ask
for key facts, preferences and tool outcomes to be preserved. Only use a trusted summarization deployment: summary content becomes
persisted assistant history. DI owns the adapter; a missing/disabled profile creates
no extra client. Summaries are lossy and require realistic recall testing.

Other keys must be supplied through a normal keyed DI registration.
`Timeout` optionally accepts a positive TimeSpan for cooperative
foreground execution or each background enqueue/retrieval call, not a job deadline;
it does not abandon a plugin task that ignores cancellation. The local background
adapter additionally passes this timeout to its foreground worker. Save's separate
deadline cancels that worker through its ticket rather than awaiting its completion.

The existing hierarchical container schemas are retained. Rotation adds optional
control metadata rather than changing partition keys. Update every writer before
enabling compaction: an older application does not understand retired history.
The feature does not migrate or delete existing histories.

### Persistence regression tests

```powershell
dotnet test tests\SharedServices.Tests\SharedServices.Tests.csproj
Set-Location src\frontend
npm test
```

The frontend tests use Node's built-in test runner and the existing TypeScript
compiler to verify voice acknowledgement, timeout and cleanup behavior without a
microphone or live model.

The suite exercises storage contracts, Cosmos SDK doubles, and the installed MAF
A2A/Responses HTTP adapters with a deterministic chat client. These tests do not
require Foundry credentials.

Real Cosmos tests are explicitly **skipped** unless
`CITY_ASSISTANT_COSMOS_TEST_CONNECTION` contains a connection string for a running
**loopback emulator**. When configured, run them with:

```powershell
dotnet test tests\SharedServices.Tests\SharedServices.Tests.csproj --filter FullyQualifiedName~CosmosEmulatorIntegrationTests
```

These tests create and delete uniquely named temporary test databases; they never
use the application's database and reject non-loopback endpoints. Do not put the
connection string in source control. Passing SDK-double tests is not evidence that
the emulator's hierarchical keys, transactional batches or TTL work.

#### Running the emulator with WSL Containers

Docker Desktop is not required for these tests. They were also exercised with
WSLC 3.0.1 and Cosmos vNext `EN20260907` (image digest
`sha256:2db1f9e74c506bcf6fc347aa937aea1c00fa756061296a5a9efba530ce86ec02`).
Use an unused container name and free loopback ports:

```powershell
wslc run --detach --name city-assistant-cosmos-test `
  --publish 127.0.0.1:18081:8081 --publish 127.0.0.1:18080:8080 `
  --env PROTOCOL=https --env ENABLE_EXPLORER=false --env ENABLE_TELEMETRY=false `
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260907

Invoke-RestMethod http://127.0.0.1:18080/ready
```

Wait until `ready` is true, then set `CITY_ASSISTANT_COSMOS_TEST_CONNECTION` with
`AccountEndpoint=https://127.0.0.1:18081/` and the
[documented emulator key](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-develop-emulator#connect-to-the-emulator-from-the-sdk).
Run the tests above. Their certificate exception applies only to the loopback
emulator; it does not change the machine's trusted certificates.

After testing, stop and remove only the container you created:

```powershell
wslc stop city-assistant-cosmos-test
wslc remove city-assistant-cosmos-test
Remove-Item Env:\CITY_ASSISTANT_COSMOS_TEST_CONNECTION
```

This runs Cosmos directly under WSLC, independently of Aspire's runtime
detection. It does not start the application or call a live Foundry model.

### Testing the Agents via A2A

You can test the agents' A2A endpoints directly:

```bash
# Get the agent card to see capabilities
curl https://localhost:5197/.well-known/agent-card.json  # Orchestrator
curl https://localhost:5198/.well-known/agent-card.json  # Accommodation Agent

# Send a message to the orchestrator
# Note: messageId should be a unique UUID for each message
# Note: contextId maintains conversation continuity across requests
curl -X POST https://localhost:5197/agenta2a/v1/run \
  -H "Content-Type: application/json" \
  -d '{
    "message": {
      "messageId": "550e8400-e29b-41d4-a716-446655440000",
      "role": "user",
      "kind": "message",
      "parts": [
        {
          "kind": "text",
          "text": "Find me a vegetarian restaurant"
        }
      ],
      "contextId": "conversation-abc123"
    }
  }'

# Example: Search for accommodations
curl -X POST https://localhost:5197/agenta2a/v1/run \
  -H "Content-Type: application/json" \
  -d '{
    "message": {
      "messageId": "550e8400-e29b-41d4-a716-446655440001",
      "role": "user",
      "kind": "message",
      "parts": [
        {
          "kind": "text",
          "text": "Find me a hotel near the Colosseum with parking for less than 80€ per night"
        }
      ],
      "contextId": "conversation-abc123"
    }
  }'
```

The frontend uses the `@a2a-js/sdk` package to handle A2A protocol communication, including streaming responses and conversation context management.

## Troubleshooting

### A2A Orchestrator Connection Issues
- Ensure the activities and accommodation agents are running and accessible
- Check that environment variables for specialist-agent URLs are set correctly in the A2A agent-tools orchestrator
  - `services__restaurantagent__https__0` or `services__restaurantagent__http__0`
  - `services__activitiesagent__https__0` or `services__activitiesagent__http__0`
  - `services__accommodationagent__https__0` or `services__accommodationagent__http__0`
- The class-skills orchestrator does not require any specialist-agent URL
- Verify SSL certificate if using HTTPS in development

### Cosmos DB Connection Issues
- Verify your Cosmos DB connection string is valid
- Ensure both `sessions` and `conversations` have the hierarchical partition keys declared by the AppHost and `DefaultTimeToLive = -1`
- A schema mismatch requires manual container recreation; restarting an existing persistent emulator does not change its partition keys
- Check that your Azure Cosmos DB firewall rules allow your IP

### Frontend Connection Issues
- Verify the proxy configuration in `vite.config.ts`
- Check that both orchestrator resources are healthy in Aspire
- Look for CORS issues in browser console

### LLM Reranking Issues (Accommodation Agent)
- Verify Azure AI Foundry connection is properly configured
- Check that the chat client model (gpt-4.1) is available
- Monitor logs for reranking errors or invalid scores
- Ensure parallel processing limit (MAXDOP) is appropriate for your setup

## License

MIT
