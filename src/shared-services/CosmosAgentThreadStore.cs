using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Logging;

namespace SharedServices;

public sealed class CosmosAgentThreadStore : AgentThreadStore
{
    private readonly ICosmosThreadRepository _repository;
    private readonly ILogger<CosmosAgentThreadStore> _logger;

    public CosmosAgentThreadStore(
        ICosmosThreadRepository repository,
        ILogger<CosmosAgentThreadStore> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async ValueTask SaveThreadAsync(
        AIAgent agent,
        string conversationId,
        AgentThread thread,
        CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        var serializedThread = thread.Serialize();
        
        _logger.LogInformation("Saving thread for conversation {ConversationId} and agent {AgentId}", conversationId, agent.Id);
        await _repository.SaveThreadAsync(key, serializedThread, cancellationToken);
    }

    public override async ValueTask<AgentThread> GetThreadAsync(
        AIAgent agent,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var key = GetKey(conversationId, agent.Id);
        var serializedThread = await _repository.GetThreadAsync(key, cancellationToken);

        if (serializedThread == null)
        {
            _logger.LogInformation("Creating new thread for conversation {ConversationId} and agent {AgentId}", conversationId, agent.Id);
            return agent.GetNewThread();
        }

        _logger.LogInformation("Loading existing thread for conversation {ConversationId} and agent {AgentId}", conversationId, agent.Id);
        return agent.DeserializeThread(serializedThread.Value);
    }

    private static string GetKey(string conversationId, string agentId) => $"{agentId}:{conversationId}";
}
