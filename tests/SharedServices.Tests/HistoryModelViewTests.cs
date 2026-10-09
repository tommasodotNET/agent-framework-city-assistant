#pragma warning disable MAAI001

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;

namespace SharedServices.Tests;

public class HistoryModelViewTests
{
    [Fact]
    public async Task LoadRemovesOnlyCompletedApprovalMessagesWithoutChangingStorage()
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        var session = await SeedAsync(provider, CompletedHistory());
        var reference = State(session).ActiveHistory;
        var before = fixture.Documents.Select(document => document.GetRawText()).ToArray();

        var view = (await provider.InvokingAsync(new(Agent(), session, [new(ChatRole.User, "next")]))).ToArray();

        Assert.Equal(5, view.Length);
        Assert.DoesNotContain(view.SelectMany(message => message.Contents), IsApproval);
        Assert.Single(view.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Single(view.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Equal("next", view[^1].Text);
        Assert.Equal(reference, State(session).ActiveHistory);
        Assert.Equal(before, fixture.Documents.Select(document => document.GetRawText()));
        Assert.Equal(6, await provider.GetMessageCountAsync(session));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("missing-call")]
    [InlineData("different-arguments")]
    [InlineData("mixed-text")]
    [InlineData("denied")]
    [InlineData("system")]
    [InlineData("duplicate-call")]
    [InlineData("duplicate-result")]
    public async Task UnprovenOrMeaningfulApprovalMessagesAreNotRemoved(string scenario)
    {
        var messages = CompletedHistory().ToList();
        switch (scenario)
        {
            case "pending": messages.RemoveAt(4); break;
            case "missing-call": messages.RemoveAt(3); break;
            case "different-arguments":
                Assert.IsType<FunctionCallContent>(((ToolApprovalRequestContent)messages[1].Contents[0]).ToolCall).Arguments!["city"] = "other";
                break;
            case "mixed-text": messages[1].Contents.Add(new TextContent("Important audit explanation")); break;
            case "denied":
                messages[2].Contents[0] = new ToolApprovalResponseContent("approval", false, Call());
                break;
            case "system": messages[1].Role = ChatRole.System; break;
            case "duplicate-call": messages[3].Contents.Add(Call()); break;
            case "duplicate-result": messages[4].Contents.Add(new FunctionResultContent("call", "duplicate")); break;
        }
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository());
        var session = await SeedAsync(provider, messages);

        var view = await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Contains(view.SelectMany(message => message.Contents), content => content is ToolApprovalRequestContent);
    }

    [Fact]
    public async Task WindowWithoutWholeExchangeKeepsItsApprovalRecords()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository()) { MaxMessagesToRetrieve = 2 };
        var messages = CompletedHistory().ToList();
        messages.Add(new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", Call())]));
        var session = await SeedAsync(provider, messages);

        var view = await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Contains(view.SelectMany(message => message.Contents), content => content is ToolApprovalRequestContent);
    }

    [Fact]
    public async Task AgentAndForegroundCompactorReceiveTheSameCleanedHistoryButNotSharedObjects()
    {
        var compactor = new ObservingCompactor();
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository(),
            compactor: compactor, compactionOptions: new() { CompactorKey = "test" });
        var session = await SeedAsync(provider, CompletedHistory());

        var view = (await provider.InvokingAsync(new(Agent(), session, []))).ToArray();

        Assert.NotNull(compactor.Request);
        Assert.Equal(4, compactor.Request.Messages.Count);
        Assert.DoesNotContain(compactor.Request.Messages.SelectMany(message => message.Contents), IsApproval);
        Assert.Equal(compactor.Request.Messages.Select(message => message.Text), view.Select(message => message.Text));
        ((TextContent)compactor.Request.Messages[0].Contents[0]).Text = "plugin changed its copy";
        Assert.Equal("long original question " + new string('x', 800), view[0].Text);
    }

    [Fact]
    public async Task FailureFallbackComparesFilteredViewsNotFilteredAgainstRawHistory()
    {
        var compactor = new Mock<IHistoryCompactor>();
        compactor.SetupGet(value => value.SupportedModes).Returns(new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground });
        compactor.Setup(value => value.CompactAsync(It.IsAny<HistoryCompactionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unavailable"));
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            compactor: compactor.Object, compactionOptions: new() { CompactorKey = "test" });
        var session = await SeedAsync(provider, CompletedHistory());

        var view = (await provider.InvokingAsync(new(Agent(), session, []))).ToArray();

        Assert.Equal(4, view.Length);
        Assert.DoesNotContain(view.SelectMany(message => message.Contents), IsApproval);
        Assert.Single(fixture.Batches);
    }

    [Fact]
    public async Task BackgroundJobAndResultUseTheSameFilteredView()
    {
        var compactor = new ObservingCompactor { Background = true };
        var fixture = new HistoryCosmosFixture();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            compactor: compactor, compactionOptions: new() { CompactorKey = "test", Mode = HistoryCompactionMode.Background });
        var session = await SeedAsync(provider, CompletedHistory());
        var source = State(session).ActiveHistory;
        await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Equal(4, compactor.Request!.Messages.Count);
        Assert.Equal(4, State(session).PendingCompaction!.Request.Messages.Count);
        compactor.Ready = true;
        await provider.InvokedAsync(new(Agent(), session,
            [new(ChatRole.User, "current")], [new(ChatRole.Assistant, "final answer")]));

        var stored = (await fixture.CreateRepository().ReadAsync(State(session).ActiveHistory)).Messages;
        Assert.Equal(new[] { "summary", "current", "final answer" }, stored.Select(message => message.Text));
        Assert.NotEqual(source.ConversationId, State(session).ActiveHistory.ConversationId);
        Assert.Null(State(session).PendingCompaction);
        Assert.Equal(6, fixture.Documents.Count(document => document.GetProperty("type").GetString() == "ChatMessage"
            && document.GetProperty("conversationId").GetString() == source.ConversationId));
    }

    private static bool IsApproval(AIContent content) => content is ToolApprovalRequestContent or ToolApprovalResponseContent;
    private static FunctionCallContent Call() => new("call", "lookup", new Dictionary<string, object?> { ["city"] = "Agentburg" });
    private static ChatMessage[] CompletedHistory() =>
    [
        new(ChatRole.User, "long original question " + new string('x', 800)),
        new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", Call())]),
        new(ChatRole.User, [new ToolApprovalResponseContent("approval", true, Call())]),
        new(ChatRole.Assistant, [Call()]),
        new(ChatRole.Tool, [new FunctionResultContent("call", "long result " + new string('r', 800))]),
        new(ChatRole.Assistant, "original answer")
    ];
    private static AIAgent Agent() => new Mock<AIAgent>().Object;
    private static SessionPersistenceContext State(AgentSession session) => SessionPersistenceState.GetRequired(session);
    private static async Task<TestAgentSession> SeedAsync(CosmosChatHistoryProvider provider, IReadOnlyList<ChatMessage> messages)
    {
        var session = new TestAgentSession();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("agent", "context"));
        await provider.InvokedAsync(new(Agent(), session, messages, []));
        return session;
    }
    private sealed class ObservingCompactor : IHistoryCompactor
    {
        public bool Background { get; init; }
        public bool Ready { get; set; }
        public HistoryCompactionRequest? Request { get; private set; }
        public IReadOnlySet<HistoryCompactionMode> SupportedModes { get; } =
            new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground, HistoryCompactionMode.Background };
        public Task<HistoryCompactionResult> CompactAsync(HistoryCompactionRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            var size = HistoryCompactionValidation.Measure(request.Messages);
            return Task.FromResult(Background ? HistoryCompactionResult.Pending(new("job", request.SourceBinding))
                : new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, request.SourceBinding, request.Messages, size, size));
        }
        public Task<HistoryCompactionResult> GetResultAsync(HistoryCompactionTicket ticket, CancellationToken cancellationToken = default)
        {
            ChatMessage[] summary = [new(ChatRole.User, "summary")];
            return Task.FromResult(Ready ? new HistoryCompactionResult(HistoryCompactionStatus.Completed, ticket.SourceBinding,
                summary, HistoryCompactionValidation.Measure(Request!.Messages), HistoryCompactionValidation.Measure(summary))
                : HistoryCompactionResult.Pending(ticket));
        }

        public Task CancelAsync(HistoryCompactionTicket ticket, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
