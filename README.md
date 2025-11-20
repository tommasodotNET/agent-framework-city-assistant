# City Assistant - Agent Framework Demo

A multi-agent application built with Microsoft Agent Framework, featuring a restaurant recommendation system with orchestrated agents.

## Architecture

The application consists of three main components:

1. **Restaurant Agent** - A specialized agent that can search and recommend restaurants by category or keywords
2. **Orchestrator Agent** - An orchestrator that uses the restaurant agent as a tool via A2A (Agent-to-Agent) communication
3. **Frontend** - A React-based chat interface that communicates with the orchestrator via A2A protocol

### Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                          Frontend (React)                        │
│                                                                   │
│  • A2A JavaScript SDK (@a2a-js/sdk)                             │
│  • Streaming chat interface                                      │
│  • Theme support & session management                            │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             │ A2A Protocol
                             │ /agenta2a/v1/*
                             │ (Agent Card, Run, Stream)
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│                     Orchestrator Agent (.NET)                    │
│                                                                   │
│  • Receives user requests via A2A                                │
│  • Maintains conversation context (contextId)                    │
│  • Invokes Restaurant Agent as a tool                            │
│  • Stores conversation history in Cosmos DB                      │
└───────────────┬────────────────────────────┬────────────────────┘
                │                            │
                │ A2A Protocol               │ Azure Cosmos DB
                │ /agenta2a/v1/*             │ (Thread Storage)
                │ (Agent-to-Agent)           │
                ▼                            ▼
┌───────────────────────────────┐            │
│  Restaurant Agent (.NET)      │            │
│                               │            │
│  • Restaurant search tools    │            │
│  • Category filtering         │            │
│  • Mock restaurant data       │            │
│  • A2A endpoint               │            │
└───────────────┬───────────────┘            │
                │                            │
                └───────────────┬────────────┘
                                │
                                ▼
                  ┌────────────────────────────┐
                  │     Cosmos DB              │
                  │                            │
                  │  • Conversation threads    │
                  │  • Message history         │
                  │  • Context persistence     │
                  └────────────────────────────┘

Data Flow:
1. User sends message via Frontend → Orchestrator (A2A)
2. Orchestrator determines if Restaurant Agent is needed
3. If needed: Orchestrator → Restaurant Agent (A2A as tool)
4. Restaurant Agent searches mock data and returns results
5. Orchestrator streams response back to Frontend (A2A)
6. Both agents persist conversation state to Cosmos DB
```

## Prerequisites

- .NET 10 SDK
- Node.js 18+ and npm
- Azure AI Inference (Foundry) connection
- Azure Cosmos DB instance

## Run the sample

> This sample requires latest .Net 10 Preview SDK (RC2) and Python 3.11+ installed on your machine.

To allow Aspire to create or reference existing resources on Azure (e.g. Foundry), you need to configure Azure settings in the [appsettings.json](./src/aspire/appsettings.json) file:

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

Powershell:
```bash
iex "& { $(irm https://aspire.dev/install.ps1) } -InstallExtension"

aspire run
```

Bash:
```bash
curl -sSL https://aspire.dev/install.sh -o aspire-install.sh
./aspire-install.sh -InstallExtension

aspire run
```

To ease the debug experience, you can use the [Aspire extension for Visual Studio Code](https://marketplace.visualstudio.com/items?itemName=microsoft-aspire.aspire-vscode#:~:text=The%20Aspire%20VS%20Code%20extension,directly%20from%20Visual%20Studio%20Code.).

## Features

### Restaurant Agent
- Search restaurants by category (vegetarian, pizza, japanese, mexican, french, indian, steakhouse)
- Search restaurants by keywords
- Get all available restaurants
- A2A endpoint at `/agenta2a`
- OpenAI-compatible endpoints for testing

### Orchestrator Agent
- Orchestrates calls to the restaurant agent
- Maintains conversation history via Cosmos DB
- Exposes A2A endpoint at `/agenta2a` for frontend communication
- Integrates with restaurant agent via A2A protocol
- Uses contextId for conversation management

### Frontend
- Clean, modern chat interface
- Streaming responses via A2A JavaScript SDK
- Theme support (light/dark/system)
- Session management with conversation history using contextId
- Communicates with orchestrator via A2A protocol

## Mock Data

The restaurant agent includes mock data for 11 restaurants across various categories:
- 3 Vegetarian restaurants
- 3 Pizza places
- 5 Other cuisines (Japanese, Mexican, French, Indian, Steakhouse)

All restaurant data is hardcoded in `RestaurantService.cs` and doesn't require external data sources.

## API Endpoints

### Restaurant Agent
- `GET /agenta2a/v1/card` - A2A agent card (metadata and capabilities)
- `POST /agenta2a/v1/run` - A2A endpoint for agent-to-agent communication
- `POST /agenta2a/v1/stream` - A2A streaming endpoint
- `POST /v1/chat/completions` - OpenAI-compatible chat endpoint (for testing)
- `GET /health` - Health check endpoint

### Orchestrator Agent
- `GET /agenta2a/v1/card` - A2A agent card (metadata and capabilities)
- `POST /agenta2a/v1/run` - A2A endpoint for frontend and agent communication
- `POST /agenta2a/v1/stream` - A2A streaming endpoint for real-time responses
- `GET /health` - Health check endpoint

All communication between frontend and orchestrator uses the A2A protocol for standardized message formats, streaming support, and contextId-based conversation management.

## Development

### Project Structure

```
src/
├── service-defaults/          # Shared Aspire service configuration
├── restaurant-agent/          # Restaurant recommendation agent
│   ├── Models/               # Data models
│   ├── Services/             # Business logic and storage
│   └── Tools/                # Agent tools/functions
├── orchestrator-agent/       # Orchestrator agent
│   ├── Models/               # API models
│   └── Services/             # Storage services
├── frontend/                 # React frontend
│   └── src/
│       ├── Chat.tsx         # Main chat component
│       └── ...
└── aspire/                   # Aspire orchestration (placeholder)
```

### Building

```bash
# Build all projects
dotnet build

# Build specific project
cd src/restaurant-agent && dotnet build
cd src/orchestrator-agent && dotnet build
```

### Testing the Agents via A2A

You can test the agent's A2A endpoint directly:

```bash
# Get the agent card to see capabilities
curl https://localhost:5197/agenta2a/v1/card

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
```

The frontend uses the `@a2a-js/sdk` package to handle A2A protocol communication, including streaming responses and conversation context management.

## Troubleshooting

### Restaurant Agent not connecting
- Ensure the restaurant agent is running and accessible
- Check that `services__restaurant-agent__https__0` environment variable is set correctly in orchestrator
- Verify SSL certificate if using HTTPS in development

### Cosmos DB connection issues
- Verify your Cosmos DB connection string is valid
- Ensure the `conversations` container exists or can be created
- Check that your Azure Cosmos DB firewall rules allow your IP

### Frontend not connecting
- Verify the proxy configuration in `vite.config.ts`
- Check that orchestrator agent is running on the expected port
- Look for CORS issues in browser console

## License

MIT
