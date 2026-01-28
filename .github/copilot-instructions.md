# Copilot Instructions

This is a multi-agent system using Microsoft Agent Framework (.NET 10) with A2A protocol.

## Reference Documentation

- [`architecture.md`](architecture.md) - Complete system architecture, technology stack, and component details
- [`agents/maf-dotnet.agent.md`](agents/maf-dotnet.agent.md) - MAF agent development patterns and best practices

## Key Patterns

When creating or modifying agents, consult the agent instructions file for MAF patterns and best practices.

**Shared services** exist in `src/shared-services/` - never duplicate Cosmos DB code:
- Use `CosmosAgentSessionStore` for conversation persistence
- Use `CosmosThreadRepository` for thread storage
- Reference `SharedServices.csproj` in agent projects

**Agent communication**:
- Restaurant agent exposes A2A at `/agenta2a/v1/*`
- Orchestrator consumes restaurant agent as tool via A2A
- Frontend calls orchestrator via A2A at `/agenta2a/v1/*`
- A2A is the preferred protocol for inter-agent communication

## Build Commands

Run with: `aspire run`

## Project Layout

- `src/aspire/` - Single-file Aspire host (apphost.cs)
- `src/frontend/` - React chat UI
- `src/orchestrator-agent/` - Main orchestrator (uses restaurant agent)
- `src/restaurant-agent/` - Restaurant search (A2A endpoint)
- `src/service-defaults/` - Aspire defaults
- `src/shared-services/` - Cosmos DB services (shared)

## Important Rules

- Never duplicate Cosmos DB logic - use shared-services
- All agents must use Cosmos DB for conversation history
- Agent-specific services handle domain logic (e.g., restaurant search, reservations)
- Always use absolute file paths when editing files
- Health checks are at `/health` for each agent
