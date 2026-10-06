using Microsoft.Agents.AI.Hosting;
using SharedServices;

namespace VoiceOrchestratorAgent;

/// <summary>Captures trusted isolation once before WebSocket acceptance, including nonpersistent calls.</summary>
public static class VoiceConnectionIdentity
{
    /// <summary>No provider permits the current anonymous contract; a missing registered key fails closed.</summary>
    public static async Task<SessionStorageAddress?> CaptureAsync(string? continuationId,
        AgentIsolationKeyProvider? provider, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        string? isolationKey = null;
        if (provider is null)
        {
            logger.LogWarning(VoiceErrors.Get("AnonymousWarning"));
        }
        else
        {
            isolationKey = await provider.GetIsolationKeyAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(isolationKey))
                throw new InvalidOperationException(VoiceErrors.Get("MissingIdentity"));
        }
        if (string.IsNullOrEmpty(continuationId)) return null;
        return SessionStorageAddress.Create(VoiceConversationStore.AgentId, continuationId,
            isolationKey is null ? null : new Dictionary<string, string> { ["isolation"] = isolationKey });
    }
}
