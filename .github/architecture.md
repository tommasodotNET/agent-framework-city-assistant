# Architecture

## System Overview

Three-tier multi-agent system where specialized agents communicate via A2A protocol

### Detailed Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                          Frontend (React)                       │
│                                                                 │
│  • A2A JavaScript SDK (@a2a-js/sdk)                             │
│  • Streaming chat interface                                     │
│  • Theme support & session management                           │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               │ A2A Protocol
                               │ /agenta2a/v1/*
                               │ (Agent Card, Run, Stream)
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                     Orchestrator Agent (.NET)                   │
│                                                                 │
│  • Receives user requests via A2A                               │
│  • Maintains conversation context (contextId)                   │
│  • Invokes Restaurant & Accommodation Agents as tools           │
│  • Stores conversation history in Cosmos DB                     │
└──────────────────┬───────────────────────────┬──────────────────┘
                   │                           │
                   │ A2A Protocol              │ Azure Cosmos DB
                   │ /agenta2a/v1/*            │ (Thread Storage)
                   │ (Agent-to-Agent)          │
                   │                           │
                   ▼                           ▼
┌──────────────────────────────┐   ┌──────────────────────────────┐
│  Restaurant Agent (.NET)     │   │ Accommodation Agent (.NET)   │
│                              │   │                              │
│  • Restaurant search tools   │   │  • Multi-criteria search     │
│  • Category filtering        │   │  • LLM-based reranking       │
│  • Mock restaurant data      │   │  • Mock accommodation data   │
│  • A2A endpoint              │   │  • A2A endpoint              │
└──────────────┬───────────────┘   └─────────┬────────────────────┘
               │                             │
               │ Azure Cosmos DB             │ MCP Protocol
               │ (Thread Storage)            │ (HTTP)
               │                             │
               ▼                             ▼
   ┌────────────────────────┐   ┌────────────────────────────────┐
   │   Cosmos DB            │   │ Geocoding MCP Server (.NET)    │
   │                        │   │                                │
   │  • Conversation threads│   │  • geocode_location tool       │
   │  • Message history     │   │  • Mock Rome landmarks data    │
   │  • Context persistence │   │  • MCP protocol endpoints      │
   └────────────────────────┘   │  • HTTP transport              │
                                └────────────────────────────────┘
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

### Accommodation Agent
Domain-specific agent for accommodation search and recommendations with multi-criteria filtering and LLM-based reranking. Uses the geocoding MCP server for location-based queries.

### Geocoding MCP Server
Standalone Model Context Protocol (MCP) server that provides geocoding services. Converts addresses and landmarks to geographic coordinates. Can be used by any MCP-compatible client or agent in the system.

### Shared Services
Centralized conversation persistence using Cosmos DB, shared across all agents to maintain conversation history.

### Aspire Host
Orchestrates the entire application, managing service connections and dependencies between frontend, agents, MCP server, and Azure services.

## Data Flow

User interactions flow through four phases:

1. **User Request**: Frontend sends message to orchestrator via A2A protocol with `contextId` for conversation continuity
2. **Agent Processing**: Orchestrator retrieves conversation thread from Cosmos DB, processes the request, and invokes specialized agents as needed via A2A
3. **Tool Invocation**: If needed, accommodation agent calls geocoding MCP server via MCP protocol to convert locations to coordinates
4. **Response & Persistence**: Orchestrator streams response back to frontend and persists updated conversation thread to Cosmos DB

## Communication Protocols

### A2A (Agent-to-Agent)
Used for agent-to-agent and frontend-to-agent communication:

- **Agent Discovery**: `/agenta2a/v1/card` endpoint exposes agent metadata (capabilities, skills, input/output modes)
- **Agent Invocation**: `/agenta2a/v1/run` endpoint executes agent with messages
- **Features**: Streaming support, conversation continuity via `contextId`, standardized message format

### MCP (Model Context Protocol)
Used for tool-based services that can be shared across agents:

- **Tool Discovery**: `/mcp/v1/tools/list` endpoint lists available MCP tools
- **Tool Invocation**: `/mcp/v1/tools/call` endpoint executes a specific tool
- **Features**: Standardized tool interface, HTTP transport, can be consumed by any MCP client
- **Example**: Geocoding MCP Server provides `geocode_location` tool

## Storage

**Cosmos DB** stores conversation threads using a composite key pattern (`{agentId}:{conversationId}`):

- Maintains conversation history across requests
- Enables conversation continuity
- Shared session store implementation used by all agents

## Configuration

Aspire manages all service connections and configuration through environment variable injection:

- Azure AI Foundry endpoints
- Cosmos DB connections
- Inter-agent URLs

Azure-specific settings (tenant, subscription, location) are configured via `src/aspire/apphost.run.json`, `apphost.cs`, environment variables, or the Aspire CLI.
