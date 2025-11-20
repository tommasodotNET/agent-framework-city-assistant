# Architecture

## System Overview

Three-tier multi-agent system where specialized agents communicate via A2A protocol:

```
Frontend (React + A2A SDK)
    ↓ A2A Protocol
Orchestrator Agent (.NET + MAF)
    ↓ A2A Protocol
Restaurant Agent (.NET + MAF)
```

## Technology Stack

**Frontend:** React + TypeScript + A2A JavaScript SDK  
→ Dependencies: [`src/frontend/package.json`](../src/frontend/package.json)

**Backend:** .NET 10 + Microsoft Agent Framework  
→ Dependencies: [`src/restaurant-agent/RestaurantAgent.csproj`](../src/restaurant-agent/RestaurantAgent.csproj), [`src/orchestrator-agent/OrchestratorAgent.csproj`](../src/orchestrator-agent/OrchestratorAgent.csproj), [`src/shared-services/SharedServices.csproj`](../src/shared-services/SharedServices.csproj)

**Infrastructure:** Azure AI Foundry (LLM) + Azure Cosmos DB (persistence) + .NET Aspire (orchestration)

**Protocol:** A2A (Agent-to-Agent) for all communication layers

## Components

### Frontend
User-facing chat interface that connects to the orchestrator via A2A protocol, maintaining conversation state through `contextId`.

### Orchestrator Agent
Main entry point that coordinates specialized agents. Uses other agents as tools via A2A protocol to fulfill user requests.

### Restaurant Agent
Domain-specific agent for restaurant search and recommendations. Exposes capabilities via A2A protocol.

### Shared Services
Centralized conversation persistence using Cosmos DB, shared across all agents to maintain conversation history.

### Aspire Host
Orchestrates the entire application, managing service connections and dependencies between frontend, agents, and Azure services.

## Data Flow

User interactions flow through three phases:

1. **User Request**: Frontend sends message to orchestrator via A2A protocol with `contextId` for conversation continuity
2. **Agent Processing**: Orchestrator retrieves conversation thread from Cosmos DB, processes the request, and invokes specialized agents as needed via A2A
3. **Response & Persistence**: Orchestrator streams response back to frontend and persists updated conversation thread to Cosmos DB

## Communication Protocol

**A2A (Agent-to-Agent)** is used throughout the system for standardized communication:

- **Agent Discovery**: `/agenta2a/v1/card` endpoint exposes agent metadata (capabilities, skills, input/output modes)
- **Agent Invocation**: `/agenta2a/v1/run` endpoint executes agent with messages
- **Features**: Streaming support, conversation continuity via `contextId`, standardized message format

## Storage

**Cosmos DB** stores conversation threads using a composite key pattern (`{agentName}:{conversationId}`):

- Maintains conversation history across requests
- Enables conversation continuity
- Shared thread store implementation used by all agents

## Configuration

Aspire manages all service connections and configuration through environment variable injection:

- Azure AI Foundry endpoints
- Cosmos DB connections
- Inter-agent URLs

Azure-specific settings (tenant, subscription, location) are configured via `src/aspire/apphost.run.json`, `apphost.cs`, environment variables, or the Aspire CLI.
