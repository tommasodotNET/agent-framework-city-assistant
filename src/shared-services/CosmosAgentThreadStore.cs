using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Logging;

namespace SharedServices;

public sealed class CosmosAgentSessionStore : AgentSessionStore
{
    private readonly ICosmosThreadRepository _repository;
    private readonly ILogger<CosmosAgentSessionStore> _logger;

    public CosmosAgentSessionStore(
        ICosmosThreadRepository repository,
        ILogger<CosmosAgentSessionStore> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async ValueTask SaveSessionAsync(
        AIAgent agent,
        string conversationId,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        var serializedThread = session.Serialize();
        
        _logger.LogInformation("Saving thread for conversation {ConversationId} and agent {AgentId}", conversationId, agent.Id);
        await _repository.SaveThreadAsync(key, serializedThread, cancellationToken);
    }

    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        var serializedThread = await _repository.GetThreadAsync(key, cancellationToken);

        if (serializedThread == null)
        {
            _logger.LogInformation("Creating new session for conversation {ConversationId} and agent {AgentId}", conversationId, agent.Id);
            return await agent.GetNewSessionAsync();
        }

        _logger.LogInformation("Loading existing session for conversation {ConversationId} and agent {AgentId}", conversationId, agent.Id);
        return await agent.DeserializeSessionAsync(serializedThread.Value);
    }

    private static string GetKey(string conversationId, string agentId) => $"{agentId}:{conversationId}";
}
