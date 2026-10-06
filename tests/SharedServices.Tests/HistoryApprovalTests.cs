using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SharedServices;

#pragma warning disable MAAI001 // Exercise actual framework approval and session middleware.

namespace SharedServices.Tests;

public sealed class HistoryApprovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Auto_approved_tools_survive_repeated_runs_and_persisted_session_resume(bool streaming)
    {
        var history = new HistoryCosmosFixture();
        var sessions = new SessionCosmosSdkFixture();
        var invocations = 0;
        var tool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => ++invocations, "increment"));
        var model = new SequenceChatClient();
        using var provider = new CosmosChatHistoryProvider(history.CreateRepository());
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Id = "approval-agent",
            Name = "approval-agent",
            ChatHistoryProvider = provider,
            ChatOptions = new() { Tools = [tool] }
        }).AsBuilder().UseToolApproval(new ToolApprovalAgentOptions
        {
            AutoApprovalRules = [ToolApprovalAgent.AllToolsAutoApprovalRule]
        }).Build();
        var store = new CosmosAgentSessionStore(sessions.Repository, NullLogger<CosmosAgentSessionStore>.Instance);
        var key = new AgentSessionStoreKey("approval-session");
        var session = await store.GetOrCreateSessionAsync(agent, key);
        if (streaming)
        {
            await foreach (var _ in agent.RunStreamingAsync("run twice", session)) { }
        }
        else
        {
            await agent.RunAsync("run twice", session);
        }
        Assert.Equal(2, invocations);
        await store.SaveSessionAsync(agent, key, session);
        var restored = await store.GetSessionAsync(agent, key);
        await agent.RunAsync("follow-up", restored);
        Assert.Equal(2, invocations);
        Assert.Equal(4, model.Requests);
    }

    [Fact]
    public async Task Pending_approval_without_a_result_is_not_marked_consumed()
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        var session = new TestAgentSession();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("test", "pending"));
        var request = new ToolApprovalRequestContent("request", new FunctionCallContent("call", "tool"));
        var agent = new ChatClientAgent(new Mock<IChatClient>().Object);
        await provider.InvokedAsync(new(agent, session, [], [new(ChatRole.Assistant, [request])]));

        var returned = await provider.InvokingAsync(new(agent, session, []));

        var approval = Assert.IsType<ToolApprovalRequestContent>(Assert.Single(Assert.Single(returned).Contents));
        Assert.False(Assert.IsType<FunctionCallContent>(approval.ToolCall).InformationalOnly);
    }

    private sealed class SequenceChatClient : IChatClient
    {
        public int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests++;
            var contents = Requests <= 2
                ? new List<AIContent> { new FunctionCallContent($"call-{Requests}", "increment") }
                : [new TextContent("done")];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
            {
                ResponseId = $"response-{Requests}",
                FinishReason = Requests <= 2 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
