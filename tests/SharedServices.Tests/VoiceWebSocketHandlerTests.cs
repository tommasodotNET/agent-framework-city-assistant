using System.Net;
using System.Net.WebSockets;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using A2A;
using Azure;
using Azure.AI.VoiceLive;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VoiceOrchestratorAgent;

namespace SharedServices.Tests;

public class VoiceWebSocketHandlerTests
{
    [Fact]
    public async Task SnapshotReadFailureReportsErrorInsteadOfReadyWithoutCallingVoiceLive()
    {
        var fixture = new VoiceStoreFixture();
        fixture.Sessions.ItemFailure = HttpStatusCode.Forbidden;
        var events = new List<string>();
        var socket = Socket(events);
        var handler = Handler(socket.Object, fixture);

        await handler.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "error" }, events);
    }

    [Fact]
    public async Task HistoryReadFailureDoesNotEmitReadyOrSaveAnEmptySnapshot()
    {
        var fixture = new VoiceStoreFixture();
        var copy = await fixture.Store.LoadAsync(fixture.Address("lookup"));
        await fixture.Store.SaveAsync(copy, []);
        await fixture.Repository.AppendAsync(copy.Context!.ActiveHistory,
            [new(Microsoft.Extensions.AI.ChatRole.User, "competing turn")]);
        var events = new List<string>();
        var handler = Handler(Socket(events).Object, fixture);

        await handler.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "error" }, events);
    }

    [Fact]
    public async Task SnapshotReadFailureDoesNotAttemptPersistence()
    {
        var fixture = new VoiceStoreFixture();
        fixture.Sessions.ItemFailure = HttpStatusCode.Forbidden;
        var handler = Handler(Socket([]).Object, fixture);

        await handler.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(fixture.Sessions.Requests, request => request.Operation != "read");
    }

    [Fact]
    public async Task StopCancelsAndAwaitsModelProcessingBeforeSavingTheSnapshot()
    {
        var fixture = new VoiceStoreFixture();
        var events = new List<string>();
        var sdk = new VoiceSdkFixture();
        var socket = Socket(events);
        ReceiveStop(socket);
        var handler = Handler(socket.Object, fixture, sdk.Client.Object);

        await handler.RunAsync(CancellationToken.None);

        Assert.True(sdk.ModelStopped);
    }

    [Fact]
    public async Task StopPersistsTheSnapshotAndReportsCompletedPersistence()
    {
        var fixture = new VoiceStoreFixture();
        var events = new List<string>();
        var sdk = new VoiceSdkFixture();
        var socket = Socket(events);
        ReceiveStop(socket);

        await Handler(socket.Object, fixture, sdk.Client.Object).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "ready", "persisted" }, events);
    }

    [Fact]
    public async Task SnapshotSaveFailureReportsPersistenceErrorInsteadOfSuccess()
    {
        var fixture = new VoiceStoreFixture();
        fixture.Sessions.WriteFailure = HttpStatusCode.Forbidden;
        var events = new List<string>();
        var sdk = new VoiceSdkFixture();
        var socket = Socket(events);
        ReceiveStop(socket);

        await Handler(socket.Object, fixture, sdk.Client.Object).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "ready", "persistence_error" }, events);
    }

    [Fact]
    public async Task ResumeReplaysHistoryBeforeSendingReady()
    {
        var fixture = new VoiceStoreFixture();
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(fixture.Address("lookup")),
            [fixture.Text("stored transcript")]);
        var events = new List<string>();
        var sdk = new VoiceSdkFixture();
        var socket = Socket(events, type =>
        {
            Assert.NotEmpty(sdk.ReplayedItems);
        });
        ReceiveStop(socket);

        await Handler(socket.Object, fixture, sdk.Client.Object).RunAsync(CancellationToken.None);

        Assert.IsType<UserMessageItem>(Assert.Single(sdk.ReplayedItems));
    }

    [Fact]
    public async Task ReplayFailureDoesNotAdvertiseReadyOrPersistANewSnapshot()
    {
        var fixture = new VoiceStoreFixture();
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(fixture.Address("lookup")),
            [fixture.Text("stored transcript")]);
        var events = new List<string>();
        var sdk = new VoiceSdkFixture();
        sdk.Session.Setup(value => value.AddItemAsync(It.IsAny<ConversationRequestItem>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("replay failure"));

        await Handler(Socket(events).Object, fixture, sdk.Client.Object).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "error" }, events);
    }

    [Fact]
    public async Task A2AProtocolFailureRecordsAnErrorResultWithTheOriginalCallId()
    {
        var fixture = new VoiceStoreFixture();
        var scenario = new VoiceToolFailureScenario(new A2AException("remote protocol failure"));

        await Handler(scenario.Socket, fixture, scenario.Sdk.Client.Object, scenario.Agents)
            .RunAsync(CancellationToken.None);

        var resumed = await fixture.Store.LoadAsync(fixture.Address("lookup"));
        Assert.Equal(new[] { "call-1", "call-1" }, resumed.Messages.SelectMany(message => message.Contents)
            .Select(content => content switch
            {
                FunctionCallContent call => call.CallId,
                FunctionResultContent result => result.CallId,
                _ => throw new InvalidOperationException("Unexpected content")
            }));
    }

    [Fact]
    public async Task A2AProtocolFailureNotifiesTheClientAndStillCompletesPersistence()
    {
        var fixture = new VoiceStoreFixture();
        var scenario = new VoiceToolFailureScenario(new A2AException("remote protocol failure"));

        await Handler(scenario.Socket, fixture, scenario.Sdk.Client.Object, scenario.Agents)
            .RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "ready", "status", "error", "status", "persisted" }, scenario.Events);
    }

    [Fact]
    public async Task A2AProtocolFailureSendsANativeErrorOutputToVoiceLive()
    {
        var fixture = new VoiceStoreFixture();
        var scenario = new VoiceToolFailureScenario(new A2AException("sensitive remote diagnostics"));

        await Handler(scenario.Socket, fixture, scenario.Sdk.Client.Object, scenario.Agents)
            .RunAsync(CancellationToken.None);

        var output = Assert.IsType<FunctionCallOutputItem>(Assert.Single(scenario.Sdk.ReplayedItems));
        using var json = JsonDocument.Parse(output.Output);
        Assert.Equal("The requested tool could not complete. Please try again later.",
            json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AzureSdkToolFailureAlsoRecordsAnErrorResult()
    {
        var fixture = new VoiceStoreFixture();
        var scenario = new VoiceToolFailureScenario(new RequestFailedException(503, "remote unavailable"));

        await Handler(scenario.Socket, fixture, scenario.Sdk.Client.Object, scenario.Agents)
            .RunAsync(CancellationToken.None);

        var resumed = await fixture.Store.LoadAsync(fixture.Address("lookup"));
        Assert.IsType<FunctionResultContent>(resumed.Messages[1].Contents[0]);
    }

    [Fact]
    public async Task HttpTransportToolFailureAlsoRecordsAnErrorResult()
    {
        var fixture = new VoiceStoreFixture();
        var scenario = new VoiceToolFailureScenario(new HttpRequestException("remote unavailable"));

        await Handler(scenario.Socket, fixture, scenario.Sdk.Client.Object, scenario.Agents)
            .RunAsync(CancellationToken.None);

        var resumed = await fixture.Store.LoadAsync(fixture.Address("lookup"));
        Assert.IsType<FunctionResultContent>(resumed.Messages[1].Contents[0]);
    }

    [Fact]
    public async Task ToolCancellationDoesNotBecomeAFabricatedErrorResult()
    {
        var fixture = new VoiceStoreFixture();
        var scenario = new VoiceToolFailureScenario(new OperationCanceledException());

        await Handler(scenario.Socket, fixture, scenario.Sdk.Client.Object, scenario.Agents)
            .RunAsync(CancellationToken.None);

        Assert.Empty(scenario.Sdk.ReplayedItems);
    }

    [Fact]
    public async Task VoiceLiveOutputFailureDoesNotRecordASecondResultForTheCall()
    {
        var fixture = new VoiceStoreFixture();
        var scenario = new VoiceToolFailureScenario(new A2AException("remote protocol failure"));
        scenario.Sdk.Session.Setup(value => value.AddItemAsync(It.IsAny<ConversationRequestItem>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "VoiceLive output unavailable"));

        await Handler(scenario.Socket, fixture, scenario.Sdk.Client.Object, scenario.Agents)
            .RunAsync(CancellationToken.None);

        var resumed = await fixture.Store.LoadAsync(fixture.Address("lookup"));
        Assert.Single(resumed.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
    }

    private static VoiceWebSocketHandler Handler(WebSocket socket, VoiceStoreFixture fixture, VoiceLiveClient? client = null,
        Dictionary<string, AIAgent>? agents = null) => new(
        socket, client ?? new Mock<VoiceLiveClient>(MockBehavior.Strict).Object,
        "https://voice.invalid", "unused-model", "unused-voice", "unused-prompt", agents ?? [],
        NullLogger<VoiceWebSocketHandler>.Instance, fixture.Address("lookup"), fixture.Store);

    private static void ReceiveStop(Mock<WebSocket> socket)
    {
        socket.Setup(value => value.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns((ArraySegment<byte> buffer, CancellationToken _) =>
            {
                var message = Encoding.UTF8.GetBytes("""{"type":"stop"}""");
                message.CopyTo(buffer.AsSpan());
                return Task.FromResult(new WebSocketReceiveResult(message.Length, WebSocketMessageType.Text, true));
            });
    }

    private static Mock<WebSocket> Socket(List<string> events, Action<string>? onSend = null)
    {
        var socket = new Mock<WebSocket>(MockBehavior.Strict);
        socket.SetupGet(value => value.State).Returns(WebSocketState.Open);
        socket.Setup(value => value.SendAsync(It.IsAny<ArraySegment<byte>>(),
                WebSocketMessageType.Text, true, It.IsAny<CancellationToken>()))
            .Returns((ArraySegment<byte> buffer, WebSocketMessageType _, bool _, CancellationToken _) =>
            {
                using var message = JsonDocument.Parse(buffer.AsMemory());
                var type = message.RootElement.GetProperty("type").GetString()!;
                onSend?.Invoke(type);
                events.Add(type);
                return Task.CompletedTask;
            });
        return socket;
    }
}

internal sealed class VoiceToolFailureScenario
{
    internal VoiceSdkFixture Sdk { get; } = new();
    internal List<string> Events { get; } = [];
    internal Dictionary<string, AIAgent> Agents { get; }
    internal WebSocket Socket { get; }

    internal VoiceToolFailureScenario(Exception exception)
    {
        // The MAF A2A adapter is real; only the external protocol client boundary is doubled.
        var a2a = new Mock<IA2AClient>(MockBehavior.Strict);
        a2a.Setup(value => value.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
        Agents = new() { ["remote_agent"] = a2a.Object.AsAIAgent("remote", "remote_agent", "test tool") };
        Sdk.Session.Setup(value => value.GetUpdatesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => ToolUpdatesAsync(token));
        Sdk.Session.Setup(value => value.StartResponseAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var socket = new Mock<WebSocket>(MockBehavior.Strict);
        socket.SetupGet(value => value.State).Returns(WebSocketState.Open);
        socket.Setup(value => value.SendAsync(It.IsAny<ArraySegment<byte>>(),
                WebSocketMessageType.Text, true, It.IsAny<CancellationToken>()))
            .Returns((ArraySegment<byte> buffer, WebSocketMessageType _, bool _, CancellationToken _) =>
            {
                using var message = JsonDocument.Parse(buffer.AsMemory());
                Events.Add(message.RootElement.GetProperty("type").GetString()!);
                return Task.CompletedTask;
            });
        socket.Setup(value => value.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
            .Returns((ArraySegment<byte> _, CancellationToken token) => ReceiveUntilCanceledAsync(token));
        Socket = socket.Object;
    }

    private static async IAsyncEnumerable<SessionUpdate> ToolUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return ModelReaderWriter.Read<SessionUpdateResponseFunctionCallArgumentsDone>(BinaryData.FromString(
            """
            {"type":"response.function_call_arguments.done","response_id":"response","item_id":"item",
             "output_index":0,"call_id":"call-1","arguments":"{\"query\":\"weather\"}","name":"remote_agent"}
            """)) ?? throw new InvalidOperationException("SDK did not deserialize the function-call event");
        yield return ModelReaderWriter.Read<SessionUpdateResponseDone>(BinaryData.FromString(
            """{"type":"response.done","response":{"id":"response","object":"realtime.response","status":"completed","output":[]}}"""))
            ?? throw new InvalidOperationException("SDK did not deserialize the response-done event");
    }

    private static async Task<WebSocketReceiveResult> ReceiveUntilCanceledAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new(0, WebSocketMessageType.Close, true);
    }
}

internal sealed class VoiceSdkFixture
{
    internal Mock<VoiceLiveClient> Client { get; } = new(MockBehavior.Strict);
    internal Mock<VoiceLiveSession> Session { get; } = new();
    internal List<ConversationRequestItem> ReplayedItems { get; } = [];
    internal bool ModelStopped { get; private set; }

    internal VoiceSdkFixture()
    {
        Client.Setup(value => value.StartSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Session.Object);
        Session.Setup(value => value.ConfigureSessionAsync(It.IsAny<VoiceLiveSessionOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Session.Setup(value => value.AddItemAsync(It.IsAny<ConversationRequestItem>(), It.IsAny<CancellationToken>()))
            .Callback((ConversationRequestItem item, CancellationToken _) => ReplayedItems.Add(item))
            .Returns(Task.CompletedTask);
        Session.Setup(value => value.GetUpdatesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) => UpdatesAsync(cancellationToken));
    }

    private async IAsyncEnumerable<SessionUpdate> UpdatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            ModelStopped = true;
        }
        yield break;
    }
}
