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
- **Cosmos DB** stores agent sessions and conversation history with agent-specific keys, so the same `contextId` cannot mix histories between orchestrators.
- **Aspire** starts both orchestrators, all specialist agents, the geocoding MCP server, Cosmos DB, Foundry connections, and the frontend.

## Configuration

Aspire injects Foundry, Cosmos, and service-discovery settings. The A2A orchestrator requires:

- `services__restaurantagent__https__0` or `services__restaurantagent__http__0`
- `services__activitiesagent__https__0` or `services__activitiesagent__http__0`
- `services__accommodationagent__https__0` or `services__accommodationagent__http__0`

The class-skills orchestrator has no specialist-agent service dependencies.
