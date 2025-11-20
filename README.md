# City Assistant - Agent Framework Demo

A multi-agent application built with Microsoft Agent Framework, featuring a restaurant recommendation system with orchestrated agents.

## Architecture

The application consists of three main components:

1. **Restaurant Agent** - A specialized agent that can search and recommend restaurants by category or keywords
2. **Orchestrator Agent** - An orchestrator that uses the restaurant agent as a tool via A2A (Agent-to-Agent) communication
3. **Frontend** - A React-based chat interface for interacting with the orchestrator

For detailed documentation, see:
- [Architecture Guide](.github/architecture.md) - Complete system architecture and technology stack
- [Copilot Instructions](.github/copilot-instructions.md) - Guidelines for working with this codebase
- [MAF Agent Development](.github/agents/maf-dotnet.agent.md) - Agent development patterns and best practices

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
- Custom streaming API endpoint at `/agent/chat/stream`
- Integrates with restaurant agent via A2A protocol

### Frontend
- Clean, modern chat interface
- Streaming responses
- Theme support (light/dark/system)
- Session management with conversation history

## Mock Data

The restaurant agent includes mock data for 11 restaurants across various categories:
- 3 Vegetarian restaurants
- 3 Pizza places
- 5 Other cuisines (Japanese, Mexican, French, Indian, Steakhouse)

All restaurant data is hardcoded in `RestaurantService.cs` and doesn't require external data sources.

## API Endpoints

### Restaurant Agent
- `POST /agenta2a/v1/run` - A2A endpoint for agent communication
- `POST /v1/chat/completions` - OpenAI-compatible chat endpoint
- `GET /health` - Health check endpoint

### Orchestrator Agent
- `POST /agent/chat/stream` - Streaming chat endpoint for frontend
- `GET /health` - Health check endpoint

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

### Testing the Restaurant Agent Directly

You can test the restaurant agent's A2A endpoint:

```bash
curl -X POST https://localhost:5196/agenta2a/v1/run \
  -H "Content-Type: application/json" \
  -d '{
    "messages": [
      {
        "role": "user",
        "content": "Find me a vegetarian restaurant"
      }
    ]
  }'
```

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
