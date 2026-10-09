#pragma warning disable MAAI001

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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

        var request = Assert.Single(view.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        if (scenario != "mixed-text")
            Assert.False(Assert.IsType<FunctionCallContent>(request.ToolCall).InformationalOnly);
    }

    [Fact]
    public async Task WindowWithoutWholeExchangeKeepsItsApprovalRecords()
    {
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository()) { MaxMessagesToRetrieve = 2 };
        var messages = CompletedHistory().ToList();
        messages.Add(new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", Call())]));
        var session = await SeedAsync(provider, messages);

        var view = await provider.InvokingAsync(new(Agent(), session, []));

        var request = Assert.Single(view.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        Assert.False(Assert.IsType<FunctionCallContent>(request.ToolCall).InformationalOnly);
    }

    [Theory]
    [InlineData("missing-call", false)]
    [InlineData("missing-call", true)]
    [InlineData("wrong-result-role", false)]
    [InlineData("wrong-result-role", true)]
    [InlineData("wrong-call-role", false)]
    [InlineData("result-before-call", false)]
    [InlineData("interrupted-exchange", false)]
    [InlineData("duplicate-call", false)]
    [InlineData("duplicate-result", false)]
    [InlineData("missing-parallel-result", false)]
    [InlineData("reused-call-id", false)]
    public async Task InvalidExchangesPreserveAllApprovalFlagsAndStoredMessages(string scenario, bool informational)
    {
        var messages = CompletedHistory().ToList();
        Assert.IsType<FunctionCallContent>(((ToolApprovalRequestContent)messages[1].Contents[0]).ToolCall)
            .InformationalOnly = informational;
        Assert.IsType<FunctionCallContent>(((ToolApprovalResponseContent)messages[2].Contents[0]).ToolCall)
            .InformationalOnly = informational;
        switch (scenario)
        {
            case "missing-call": messages.RemoveAt(3); break;
            case "wrong-result-role": messages[4].Role = ChatRole.User; break;
            case "wrong-call-role": messages[3].Role = ChatRole.User; break;
            case "result-before-call": (messages[3], messages[4]) = (messages[4], messages[3]); break;
            case "interrupted-exchange": messages.Insert(4, new(ChatRole.User, "interruption")); break;
            case "duplicate-call": messages[3].Contents.Add(Call()); break;
            case "duplicate-result": messages[4].Contents.Add(new FunctionResultContent("call", "duplicate")); break;
            case "missing-parallel-result":
                messages[3].Contents.Add(new FunctionCallContent("second", "lookup", null));
                break;
            case "reused-call-id":
                messages.Add(new(ChatRole.Assistant, [Call()]));
                messages.Add(new(ChatRole.Tool, [new FunctionResultContent("call", "another result")]));
                break;
        }
        var fixture = new HistoryCosmosFixture();
        var logger = new RecordingLogger();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(), logger: logger);
        var session = await SeedAsync(provider, messages);
        var documents = fixture.Documents.Select(document => document.GetRawText()).ToArray();

        var view = (await provider.InvokingAsync(new(Agent(), session, []))).ToArray();

        var request = Assert.Single(view.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        var response = Assert.Single(view.SelectMany(message => message.Contents).OfType<ToolApprovalResponseContent>());
        Assert.Equal(informational, Assert.IsType<FunctionCallContent>(request.ToolCall).InformationalOnly);
        Assert.Equal(informational, Assert.IsType<FunctionCallContent>(response.ToolCall).InformationalOnly);
        Assert.Equal(messages.Count, view.Length);
        Assert.Equal(documents, fixture.Documents.Select(document => document.GetRawText()));
        Assert.Contains(logger.Messages, message => message.Contains("Approval consumption proof rejected", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("name")]
    [InlineData("denied")]
    public async Task ApprovalIdentityMismatchOrDenialNeverChangesItsFlag(string mismatch)
    {
        var messages = CompletedHistory();
        var requestCall = Assert.IsType<FunctionCallContent>(((ToolApprovalRequestContent)messages[1].Contents[0]).ToolCall);
        var responseCall = Assert.IsType<FunctionCallContent>(((ToolApprovalResponseContent)messages[2].Contents[0]).ToolCall);
        if (mismatch == "arguments")
        {
            requestCall.Arguments!["city"] = "other";
            responseCall.Arguments!["city"] = "other";
        }
        else if (mismatch == "name")
        {
            messages[1].Contents[0] = new ToolApprovalRequestContent("approval", new FunctionCallContent("call", "different-tool", requestCall.Arguments));
            messages[2].Contents[0] = new ToolApprovalResponseContent("approval", true, new FunctionCallContent("call", "different-tool", responseCall.Arguments));
        }
        else
        {
            messages[2].Contents[0] = new ToolApprovalResponseContent("approval", false, responseCall);
        }
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository());
        var session = await SeedAsync(provider, messages);

        var view = (await provider.InvokingAsync(new(Agent(), session, []))).ToArray();

        var request = Assert.Single(view.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        var response = Assert.Single(view.SelectMany(message => message.Contents).OfType<ToolApprovalResponseContent>());
        Assert.False(Assert.IsType<FunctionCallContent>(request.ToolCall).InformationalOnly);
        Assert.False(Assert.IsType<FunctionCallContent>(response.ToolCall).InformationalOnly);
    }

    [Fact]
    public async Task CompleteParallelExchangeWithReasoningStillConsumesMatchingApprovals()
    {
        var messages = CompletedHistory().ToList();
        var second = new FunctionCallContent("second", "lookup", null);
        messages.Insert(3, new(ChatRole.Assistant, [new ToolApprovalRequestContent("second-approval", second)]));
        messages.Insert(4, new(ChatRole.User, [new ToolApprovalResponseContent("second-approval", true,
            new FunctionCallContent("second", "lookup", null))]));
        messages[5].Contents.Add(new FunctionCallContent("second", "lookup", null));
        messages.Insert(6, new(ChatRole.Assistant, [new TextReasoningContent("reasoning")]));
        messages[7].Contents.Insert(0, new FunctionResultContent("second", "parallel result"));
        using var provider = new CosmosChatHistoryProvider(new HistoryCosmosFixture().CreateRepository());
        var session = await SeedAsync(provider, messages);

        var view = (await provider.InvokingAsync(new(Agent(), session, []))).ToArray();

        Assert.DoesNotContain(view.SelectMany(message => message.Contents), IsApproval);
        Assert.Equal(2, view.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Count());
        Assert.Equal(2, view.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
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

    private sealed class RecordingLogger : ILogger<CosmosChatHistoryProvider>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
