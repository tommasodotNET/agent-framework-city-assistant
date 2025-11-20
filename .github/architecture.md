# Architecture

## System Overview

Three-tier multi-agent system:

```
Frontend (React + TypeScript + Vite)
    ↓ HTTP POST /agent/chat/stream
Orchestrator Agent (.NET 10 + MAF)
    ↓ A2A Protocol /agenta2a/v1/*
Restaurant Agent (.NET 10 + MAF)
```

## Technology Stack

**Frontend:**
- React 18
- TypeScript
- Vite 5
- CSS Modules

**Backend (.NET Agents):**
- .NET 10 SDK (RC2)
- Microsoft Agent Framework 1.0.0-preview.251113.1
  - `Microsoft.Agents.AI`
  - `Microsoft.Agents.AI.Hosting`
  - `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`
  - `Microsoft.Agents.AI.OpenAI`
- Aspire 13.0.0

**Azure Services:**
- Azure AI Inference (Foundry) - LLM backend (gpt-4.1)
- Azure Cosmos DB - Conversation persistence

**Protocols:**
- A2A (Agent-to-Agent) - Inter-agent communication
- HTTP/REST - Frontend-to-orchestrator
- Server-Sent Events - Streaming responses

## Components

### Frontend (`src/frontend/`)

**Purpose**: User-facing chat interface

**Key Files:**
- `src/App.tsx` - Main application component
- `src/Chat.tsx` - Chat interface component
- `vite.config.ts` - Build configuration with proxy to orchestrator

**API Integration:**
- Endpoint: `POST /agent/chat/stream`
- Request: `{ messages: [], sessionState?: string }`
- Response: Newline-delimited JSON stream of `AIChatCompletionDelta`

**Session Management:**
- Generates/maintains `sessionState` (GUID) for conversation continuity
- Sends all messages in context window

### Orchestrator Agent (`src/orchestrator-agent/`)

**Purpose**: Main entry point that coordinates specialized agents

**Structure:**
- `Program.cs` - Agent setup, A2A client, endpoints
- `appsettings.json` - Configuration (Azure, Cosmos)

**Agent Configuration:**
- Name: `orchestrator-agent`
- Instructions: Coordinate restaurant queries via A2A
- Tools: Restaurant agent (consumed as `AIFunction`)
- LLM: Azure AI Foundry (gpt-4.1)

**Endpoints:**
- `POST /agent/chat/stream` - Custom streaming API for frontend
- `GET /health` - Health check

**Dependencies:**
- `ServiceDefaults.csproj` - Aspire defaults
- `SharedServices.csproj` - Cosmos thread store

**A2A Client Setup:**
```csharp
var httpClient = new HttpClient() { BaseAddress = restaurantAgentUrl };
var cardResolver = new A2ACardResolver(httpClient.BaseAddress, httpClient, "/agenta2a/v1/card");
var restaurantAgent = cardResolver.GetAIAgentAsync().Result;
```

### Restaurant Agent (`src/restaurant-agent/`)

**Purpose**: Specialized agent for restaurant search and recommendations

**Structure:**
- `Program.cs` - Agent setup, A2A endpoint, OpenAI endpoints
- `Models/Restaurant.cs` - Restaurant data model
- `Services/RestaurantService.cs` - Mock data service (11 restaurants)
- `Tools/RestaurantTools.cs` - AI function definitions

**Agent Configuration:**
- Name: `restaurant-agent`
- Instructions: Help users find restaurants by category or search
- Tools: 3 functions from `RestaurantTools`
- LLM: Azure AI Foundry (gpt-4.1)

**Tools (AI Functions):**
- `GetAllRestaurants()` - Returns all 11 restaurants
- `GetRestaurantsByCategory(string category)` - Filters by category (vegetarian, pizza, japanese, mexican, french, indian, steakhouse)
- `SearchRestaurants(string query)` - Keyword search in name/description

**Endpoints:**
- `POST /agenta2a/v1/run` - A2A protocol endpoint
- `GET /agenta2a/v1/card` - Agent card metadata
- `POST /v1/chat/completions` - OpenAI-compatible endpoint
- `GET /health` - Health check

**Mock Data:**
- 11 hardcoded restaurants in `RestaurantService.cs`
- Categories: Vegetarian (3), Pizza (3), Other (5)
- No external data sources required

## Shared Infrastructure

### Service Defaults (`src/service-defaults/`)

**Package**: `ServiceDefaults.csproj`

**Provides:**
- OpenTelemetry configuration
- Health checks (`/health`)
- Service discovery for Aspire
- HTTP client resilience policies

**Usage**: Referenced by all agent projects via `<ProjectReference>`

### Shared Services (`src/shared-services/`)

**Package**: `SharedServices.csproj`

**Purpose**: Centralized Cosmos DB conversation persistence

**Classes:**
- `CosmosAgentThreadStore : AgentThreadStore` - Thread storage implementation for MAF
- `CosmosThreadRepository : ICosmosThreadRepository` - CRUD operations for threads
- `ICosmosThreadRepository` - Repository interface
- `CosmosSystemTextJsonSerializer : CosmosSerializer` - Custom JSON serializer for Cosmos

**Key Pattern:**
- Both agents reference this project
- Avoids duplicating Cosmos logic
- Thread key format: `{agentName}:{conversationId}`

**Registration:**
```csharp
builder.AddKeyedAzureCosmosContainer("conversations", 
    configureClientOptions: (option) => option.Serializer = new CosmosSystemTextJsonSerializer());
builder.Services.AddSingleton<ICosmosThreadRepository, CosmosThreadRepository>();
builder.Services.AddSingleton<CosmosAgentThreadStore>();
```

### Aspire Host (`src/aspire/`)

**File**: `apphost.cs` - Single-file Aspire app model

**Features:**
- Uses `#:sdk` and `#:package` directives for dependencies
- Configures Azure resources (Foundry, Cosmos DB)
- Orchestrates frontend + 2 agents
- Service discovery and environment variables

**Config**: `apphost.run.json` - Launch settings (port 17085)

**Resources Configured:**
- `foundry` - Azure AI Inference connection
- `conversations` - Cosmos DB container
- `frontend` - React app (npm project)
- `restaurant-agent` - .NET agent project
- `orchestrator-agent` - .NET agent project

## Data Flow

### User Query Flow

1. **User Input**: User types message in frontend chat
2. **Frontend → Orchestrator**: `POST /agent/chat/stream`
   - Payload: `{ messages: [{role, content}], sessionState: "guid" }`
   - Headers: `Content-Type: application/json`
3. **Orchestrator Processing**:
   - Retrieves thread from Cosmos: `await threadStore.GetThreadAsync(agent, conversationId)`
   - Creates `ChatMessage(ChatRole.User, text)`
   - Runs agent: `agent.RunStreamingAsync(chatMessage, thread)`
   - Agent determines if restaurant tool needed
4. **A2A Call** (if restaurant query):
   - Orchestrator invokes restaurant agent function
   - A2A protocol: `POST /agenta2a/v1/run`
   - Restaurant agent executes tool (`GetRestaurantsByCategory`, etc.)
   - Returns JSON results to orchestrator
5. **Response Streaming**:
   - Orchestrator streams deltas: `AIChatCompletionDelta` objects
   - Newline-delimited JSON format
   - Frontend renders incrementally
6. **Persistence**:
   - Orchestrator saves thread: `await threadStore.SaveThreadAsync(agent, conversationId, thread)`
   - Restaurant agent also saves its thread if directly queried

## Communication Protocols

### A2A (Agent-to-Agent) Protocol

**Purpose**: Standard inter-agent communication

**Endpoints:**
- `GET /agenta2a/v1/card` - Agent metadata (AgentCard)
- `POST /agenta2a/v1/run` - Execute agent with message

**AgentCard Structure:**
- Name, description, version
- Capabilities (streaming, push notifications)
- Skills (name, description, examples)
- Input/output modes

**Usage Pattern:**
```csharp
// Consumer side (orchestrator)
var cardResolver = new A2ACardResolver(url, httpClient, "/agenta2a/v1/card");
var agent = await cardResolver.GetAIAgentAsync();
var function = agent.AsAIFunction(); // Convert to tool
```

### Custom Streaming API

**Endpoint**: `POST /agent/chat/stream`

**Request Format:**
```json
{
  "messages": [{"role": "user", "content": "text"}],
  "sessionState": "optional-guid"
}
```

**Response Format**: Newline-delimited JSON
```
{"sessionState":"guid","message":{"role":"assistant","content":"Hi, I'm..."}}\n
{"message":{"content":"text"}}\n
```

**Session Management:**
- Frontend generates GUID on first message
- Passes `sessionState` on subsequent requests
- Maps to `conversationId` in Cosmos DB

## Storage Architecture

### Cosmos DB Configuration

**Container**: `conversations`
**Database**: Auto-created by Aspire

**Document Structure:**
```json
{
  "id": "restaurant-agent:conversation-guid",
  "agentId": "restaurant-agent",
  "conversationId": "conversation-guid",
  "messages": [...],
  "_ts": timestamp
}
```

**Serialization**: Custom `CosmosSystemTextJsonSerializer` for .NET System.Text.Json compatibility

**Access Pattern:**
- Read: `GetThreadAsync(agent, conversationId)` - retrieves or creates new thread
- Write: `SaveThreadAsync(agent, conversationId, thread)` - upserts document

### Thread Management

**Key Components:**
- `AgentThread` - MAF abstraction for conversation state
- `CosmosAgentThreadStore` - Implements MAF's `AgentThreadStore`
- Composite key: `{agent.Name}:{conversationId}`

**Lifecycle:**
1. First request: Thread created with empty history
2. During conversation: Messages appended to thread
3. Agent execution: Thread passed to `agent.RunStreamingAsync()`
4. After response: Thread saved with updated history

## Dependencies

### NuGet Packages (Agents)

**Core MAF:**
- `Microsoft.Agents.AI` (1.0.0-preview.251113.1)
- `Microsoft.Agents.AI.Abstractions`
- `Microsoft.Agents.AI.Hosting`
- `Microsoft.Agents.AI.OpenAI`

**A2A Support:**
- `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`

**Azure Integration:**
- `Aspire.Azure.AI.Inference` (13.0.0-preview.1.25560.3)
- `Aspire.Microsoft.Azure.Cosmos` (13.0.0)

**Optional:**
- `Microsoft.Agents.AI.DevUI` - Development UI
- `Microsoft.Agents.AI.Workflows` - Multi-agent workflows

### NPM Packages (Frontend)

**Core:**
- `react@18.x`, `react-dom@18.x`
- `typescript@5.x`

**Build:**
- `vite@5.x`
- `@vitejs/plugin-react`

**Development:**
- `@types/react`, `@types/react-dom`
- `eslint`, `typescript-eslint`

## Configuration

### Azure Settings (`src/aspire/appsettings.json`)

Required for Aspire to provision Azure resources:
```json
{
  "Azure": {
    "TenantId": "<guid>",
    "SubscriptionId": "<guid>",
    "Location": "eastus",
    "AllowResourceGroupCreation": false,
    "CredentialSource": "AzureCli"
  }
}
```

### Agent Settings (`src/*/appsettings.json`)

**Development:**
```json
{
  "Logging": {
    "LogLevel": { "Default": "Information" }
  }
}
```

**Azure Connections**: Injected by Aspire via environment variables
- `services__foundry__https__0` - Foundry endpoint
- `services__conversations__*` - Cosmos DB connection
- `services__restaurant-agent__https__0` - Restaurant agent URL (for orchestrator)
