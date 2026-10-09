#pragma warning disable MAAI001 // Exercise real MAF approval middleware and compaction strategies.

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using SharedServices;

namespace SharedServices.Tests;

public class CompactionMafApprovalTests
{
    [Fact]
    public async Task RealAutoApprovalProducesConsumedRecordsAndACompletedToolExchange()
    {
        var history = await AutoApprovedHistoryAsync();
        var approvals = history.SelectMany(message => message.Contents)
            .Where(content => content is ToolApprovalRequestContent or ToolApprovalResponseContent).ToArray();

        Assert.Equal(new[] { typeof(ToolApprovalRequestContent), typeof(ToolApprovalResponseContent) },
            approvals.Select(content => content.GetType()));
    }

    [Fact]
    public async Task RealAutoApprovedRequestResponseAndCallAreInformational()
    {
        var history = await AutoApprovedHistoryAsync();

        Assert.Equal(new[] { true, true, true }, FunctionCallsIncludingApprovals(history).Select(call => call.InformationalOnly));
    }

    [Fact]
    public async Task RealAutoApprovalContainsFullCallAndResultWithMatchingIds()
    {
        var history = await AutoApprovedHistoryAsync();
        var contents = history.SelectMany(message => message.Contents).ToArray();

        Assert.Equal(("approved-call", "approved-call"),
            (contents.OfType<FunctionCallContent>().Single().CallId, contents.OfType<FunctionResultContent>().Single().CallId));
    }

    [Theory]
    [InlineData("sliding")]
    [InlineData("truncation")]
    public async Task BuiltInReducerCanDropOldCompletedAutoApprovalHistory(string reducer)
    {
        var history = await AutoApprovedHistoryAsync();
        var result = await new MafForegroundHistoryCompactor(Strategy(reducer)).CompactAsync(Request(history));

        Assert.Equal(new[] { "instructions", "recent question", "answer" }, result.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task BuiltInSummarizationCanReplaceCompletedAutoApprovalHistory()
    {
        var history = await AutoApprovedHistoryAsync();
        using var summarizer = new ApprovalClient(callTool: false, answer: "Lookup completed.");
        var strategy = new SummarizationCompactionStrategy(summarizer, CompactionTriggers.Always, minimumPreservedGroups: 2);

        var result = await new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(history));

        Assert.Equal(new[] { "instructions", "[Summary]\nLookup completed.", "recent question", "answer" },
            result.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task DroppingConsumedApprovalsDoesNotMutateOriginalAuditHistory()
    {
        var history = await AutoApprovedHistoryAsync();
        var original = JsonSerializer.Serialize(history);

        await new MafForegroundHistoryCompactor(Strategy("sliding")).CompactAsync(Request(history));

        Assert.Equal(original, JsonSerializer.Serialize(history));
    }

    [Fact]
    public async Task CompactionDoesNotApproveOrInvokeTheToolAgain()
    {
        var invocations = 0;
        var history = await AutoApprovedHistoryAsync(() => Interlocked.Increment(ref invocations));

        await new MafForegroundHistoryCompactor(Strategy("truncation")).CompactAsync(Request(history));

        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task ConsumedApprovalsMayBeRemovedWhileFullCompletedExchangeIsRetained()
    {
        var source = await AutoApprovedHistoryAsync();
        var candidate = source.Where(message => !HasApproval(message)).ToArray();

        var error = Record.Exception(() => HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Null(error);
    }

    [Fact]
    public async Task RetainedConsumedApprovalsAndFullExchangeStayUsableAfterReduction()
    {
        var source = await AutoApprovedHistoryAsync();
        var candidate = source.Where((_, index) => index != 1).ToArray();

        var error = Record.Exception(() => HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Null(error);
    }

    [Theory]
    [InlineData("request-id")]
    [InlineData("response-id")]
    [InlineData("response-decision")]
    [InlineData("response-reason")]
    [InlineData("request-rearmed")]
    [InlineData("response-rearmed")]
    [InlineData("request-tool")]
    [InlineData("request-metadata")]
    [InlineData("invent-request")]
    [InlineData("invent-response")]
    [InlineData("duplicate-request")]
    [InlineData("reorder")]
    public async Task RetainedConsumedApprovalCannotBeInventedOrTamperedWith(string attack)
    {
        var source = await AutoApprovedHistoryAsync();
        var candidate = Detached(source);
        AttackApproval(candidate, attack);

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Equal(HistoryCompactionFailureReason.ProtectedMessagesChanged, error.Reason);
    }

    [Fact]
    public async Task RetainedConsumedApprovalsCannotLoseTheirFullCompletedExchange()
    {
        var source = await AutoApprovedHistoryAsync();
        var candidate = source.Where(message => !message.Contents.Any(
            content => content is FunctionCallContent or FunctionResultContent)).ToArray();

        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateCandidate(source, candidate));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public async Task ToolOnlyCollapseCannotLeaveOrphanConsumedApprovals()
    {
        var source = await AutoApprovedHistoryAsync();
        var compactor = new MafForegroundHistoryCompactor(
            new ToolResultCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2));

        var error = await Assert.ThrowsAsync<HistoryCompactionValidationException>(() => compactor.CompactAsync(Request(source)));

        Assert.Equal(HistoryCompactionFailureReason.UnsafeToolHistory, error.Reason);
    }

    [Fact]
    public async Task PipelineCanCollapseThenSummarizeCompletedAutoApprovalHistory()
    {
        var source = await AutoApprovedHistoryAsync();
        using var summarizer = new ApprovalClient(callTool: false, answer: "Lookup completed.");
        var strategy = new PipelineCompactionStrategy(
        [
            new ToolResultCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2),
            new SummarizationCompactionStrategy(summarizer, CompactionTriggers.Always, minimumPreservedGroups: 2)
        ]);

        var result = await new MafForegroundHistoryCompactor(strategy).CompactAsync(Request(source));

        Assert.Equal(new[] { "instructions", "[Summary]\nLookup completed.", "recent question", "answer" },
            result.Messages.Select(message => message.Text));
    }

    private static CompactionStrategy Strategy(string reducer) => reducer switch
    {
        "sliding" => new SlidingWindowCompactionStrategy(CompactionTriggers.Always, minimumPreservedTurns: 1),
        "truncation" => new TruncationCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 2),
        _ => throw new ArgumentOutOfRangeException(nameof(reducer))
    };

    private static async Task<List<ChatMessage>> AutoApprovedHistoryAsync(Action? onToolInvocation = null)
    {
        var tool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() =>
        {
            onToolInvocation?.Invoke();
            return "completed";
        }, "lookup"));
        using var model = new ApprovalClient();
        var provider = new InMemoryChatHistoryProvider();
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Id = "compaction-approval-test",
            Name = "compaction-approval-test",
            ChatHistoryProvider = provider,
            ChatOptions = new() { Tools = [tool] }
        }).AsBuilder().UseToolApproval(new ToolApprovalAgentOptions
        {
            AutoApprovalRules = [ToolApprovalAgent.AllToolsAutoApprovalRule]
        }).Build();
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("old question", session);
        await agent.RunAsync("recent question", session);
        return [new(ChatRole.System, "instructions"), .. provider.GetMessages(session)];
    }

    private static HistoryCompactionRequest Request(IReadOnlyList<ChatMessage> messages) =>
        new("agent", "source", messages, new HistoryCompactionOptions
        {
            CompactorKey = "approval-test"
        });

    private static bool HasApproval(ChatMessage message) =>
        message.Contents.Any(content => content is ToolApprovalRequestContent or ToolApprovalResponseContent);

    private static IEnumerable<FunctionCallContent> FunctionCallsIncludingApprovals(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents).Select(content => content switch
        {
            ToolApprovalRequestContent request => request.ToolCall as FunctionCallContent,
            ToolApprovalResponseContent response => response.ToolCall as FunctionCallContent,
            FunctionCallContent call => call,
            _ => null
        }).OfType<FunctionCallContent>();

    private static List<ChatMessage> Detached(IReadOnlyList<ChatMessage> source) =>
        JsonSerializer.Deserialize<List<ChatMessage>>(JsonSerializer.Serialize(source),
            new JsonSerializerOptions { AllowOutOfOrderMetadataProperties = true })!;

    private static void AttackApproval(List<ChatMessage> candidate, string attack)
    {
        var requestMessage = candidate.Single(message => message.Contents.Any(content => content is ToolApprovalRequestContent));
        var responseMessage = candidate.Single(message => message.Contents.Any(content => content is ToolApprovalResponseContent));
        var request = requestMessage.Contents.OfType<ToolApprovalRequestContent>().Single();
        var response = responseMessage.Contents.OfType<ToolApprovalResponseContent>().Single();
        // Make every attacked candidate a genuine reduction independently of the malicious edit.
        candidate.RemoveAt(1);
        switch (attack)
        {
            case "request-id":
                requestMessage.Contents[0] = new ToolApprovalRequestContent("forged-id", request.ToolCall);
                break;
            case "response-id":
                responseMessage.Contents[0] = new ToolApprovalResponseContent("forged-id", response.Approved, response.ToolCall);
                break;
            case "response-decision":
                responseMessage.Contents[0] = new ToolApprovalResponseContent(response.RequestId, false, response.ToolCall)
                {
                    Reason = response.Reason
                };
                break;
            case "response-reason":
                response.Reason = "forged reason";
                break;
            case "request-rearmed":
                ((FunctionCallContent)request.ToolCall).InformationalOnly = false;
                break;
            case "response-rearmed":
                ((FunctionCallContent)response.ToolCall).InformationalOnly = false;
                break;
            case "request-tool":
                requestMessage.Contents[0] = new ToolApprovalRequestContent(request.RequestId,
                    new FunctionCallContent(request.ToolCall.CallId, "another-tool") { InformationalOnly = true });
                break;
            case "request-metadata":
                request.AdditionalProperties = new() { ["forged"] = true };
                break;
            case "invent-request":
            case "invent-response":
                candidate.RemoveAll(HasApproval);
                var completed = candidate.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
                candidate.Insert(1, attack == "invent-request"
                    ? new(ChatRole.Assistant, [new ToolApprovalRequestContent("invented", completed)])
                    : new(ChatRole.User, [new ToolApprovalResponseContent("invented", true, completed)]));
                break;
            case "duplicate-request":
                candidate.Remove(responseMessage);
                candidate.Insert(candidate.IndexOf(requestMessage), requestMessage);
                break;
            case "reorder":
                candidate.Remove(responseMessage);
                candidate.Insert(candidate.IndexOf(requestMessage), responseMessage);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(attack));
        }
    }

    private sealed class ApprovalClient(bool callTool = true, string answer = "answer") : IChatClient
    {
        private int _requests;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = Interlocked.Increment(ref _requests);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, callTool && request == 1
                ? [new FunctionCallContent("approved-call", "lookup")]
                : [new TextContent(answer)]))
            {
                FinishReason = callTool && request == 1 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop
            };
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
