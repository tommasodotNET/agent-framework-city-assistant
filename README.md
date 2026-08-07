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

Start the distributed application:

```bash
aspire start
```

When running from a worktree, isolate ports and local state:

```bash
aspire start --isolated
```

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
- Ensure the `conversations` container exists or can be created
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
