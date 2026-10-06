using Microsoft.Agents.AI;

namespace SharedServices.Tests;

// Only the framework session abstraction is doubled. Persistence and context helpers remain real.
internal sealed class TestAgentSession : AgentSession
{
    internal TestAgentSession()
    {
    }

    internal TestAgentSession(AgentSessionStateBag stateBag) : base(stateBag)
    {
    }
}
