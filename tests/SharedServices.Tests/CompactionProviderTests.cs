#pragma warning disable MAAI001 // Exercise the public framework provider lifecycle.

using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;

namespace SharedServices.Tests;

public class CompactionProviderTests
{
    [Fact]
    public async Task ForegroundLoadReturnsCandidateWithoutWritingOrChangingActiveHistory()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));

        var messages = await scenario.LoadAsync();

        Assert.Equal((scenario.Source, 1, "short"),
            (scenario.Active, scenario.Fixture.Batches.Count, Assert.Single(messages).Text));
    }

    [Fact]
    public async Task UnchangedHistoryKeepsLargeMessages()
    {
        var text = new string('x', 120_000);
        using var scenario = await SetupAsync(new Plugin((request, _) => Task.FromResult(Unchanged(request))),
            messages: [new(ChatRole.User, text)]);

        var result = await scenario.LoadAsync();

        Assert.Equal(text, Assert.Single(result).Text);
        Assert.Equal(scenario.Source, scenario.Active);
        Assert.Single(scenario.Fixture.Batches);
    }

    [Fact]
    public async Task ForegroundRotationUsesValidatedReduction()
    {
        var plugin = new Plugin((request, _) => Task.FromResult(Completed(request, [new(ChatRole.User, "short")])));
        using var scenario = await SetupAsync(plugin);

        var result = await scenario.LoadAsync();
        await scenario.SaveAsync();

        Assert.Equal("short", Assert.Single(result).Text);
        Assert.NotEqual(scenario.Source.ConversationId, scenario.Active.ConversationId);
    }

    [Fact]
    public async Task StrategyFailureFallbackStillChecksSourceVersion()
    {
        var plugin = new Plugin((_, _) => throw new HttpRequestException("model unavailable"));
        using var scenario = await SetupAsync(plugin);

        var result = await scenario.LoadAsync();

        Assert.Equal(Original, Assert.Single(result).Text);
        Assert.Equal(scenario.Source, scenario.Active);
        Assert.Equal(2, scenario.Fixture.Queries.Count);
    }

    [Theory]
    [InlineData("[Summary]", false)]
    [InlineData("[Summary unavailable]", false)]
    [InlineData("[Summary]\nExplanation quoting [Summary unavailable]", false)]
    [InlineData("[Summary]", true)]
    [InlineData("[Summary unavailable]", true)]
    [InlineData("[Summary]\nExplanation quoting [Summary unavailable]", true)]
    public async Task OrdinaryMarkerTextDoesNotBlockUnchangedOrFailureFallback(string text, bool failStrategy)
    {
        using var scenario = await SetupAsync(new Plugin((request, _) => failStrategy
            ? throw new HttpRequestException("model unavailable") : Task.FromResult(Unchanged(request))),
            messages: [new(ChatRole.User, Original), new(ChatRole.Assistant, text)]);

        var loaded = await scenario.LoadAsync();
        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session,
            [new(ChatRole.User, "current")], [new(ChatRole.Assistant, "reply")]));

        Assert.Equal(text, loaded[^1].Text);
        Assert.Equal(scenario.Source.ConversationId, scenario.Active.ConversationId);
        Assert.Equal(new[] { Original, text, "current", "reply" },
            (await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData("[Summary]")]
    [InlineData("[Summary unavailable]")]
    [InlineData("[Summary]\nExplanation quoting [Summary unavailable]")]
    public async Task OrdinaryNewReplyDoesNotBlockPreparedCompactionSave(string text)
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        await scenario.LoadAsync();

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session,
            [new(ChatRole.User, "current")], [new(ChatRole.Assistant, text)]));

        Assert.NotEqual(scenario.Source.ConversationId, scenario.Active.ConversationId);
        Assert.Equal(new[] { "short", "current", text },
            (await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData("[Summary]")]
    [InlineData("[Summary unavailable]")]
    public async Task CompletedCandidateCanRetainOrdinarySourceMarkerText(string text)
    {
        var ordinary = new ChatMessage(ChatRole.Assistant, text);
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [request.Messages[^1]]))),
            messages: [new(ChatRole.User, Original), ordinary]);

        var loaded = await scenario.LoadAsync();
        await scenario.SaveAsync();

        Assert.Equal(text, Assert.Single(loaded).Text);
        Assert.NotEqual(scenario.Source.ConversationId, scenario.Active.ConversationId);
        Assert.Equal(text, Assert.Single((await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages).Text);
    }

    [Fact]
    public async Task ProtectedInstructionsRemainRequired()
    {
        var plugin = new Plugin((request, _) => Task.FromResult(Completed(request, [new(ChatRole.User, "short")])));
        using var scenario = await SetupAsync(plugin,
            messages: [new(ChatRole.System, "protected"), new(ChatRole.User, Original)]);

        var result = await scenario.LoadAsync();

        Assert.Equal(new[] { "protected", Original }, result.Select(message => message.Text));
        Assert.Equal(scenario.Source, scenario.Active);
        Assert.Single(scenario.Fixture.Batches);
    }

    [Fact]
    public async Task DisabledCompactionAddsNoReadsOrWrites()
    {
        var fixture = new HistoryCosmosFixture();
        var session = NewSession();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "original")], []));
        var readsBefore = fixture.PointReadPartitions.Count;

        await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Equal((1, 2, 1), (fixture.Queries.Count,
            fixture.PointReadPartitions.Count - readsBefore, fixture.Batches.Count));
    }

    [Fact]
    public async Task WrongSourceBindingFallsBackWithoutPublishingCandidate()
    {
        var plugin = new Plugin((request, _) => Task.FromResult(new HistoryCompactionResult(
            HistoryCompactionStatus.Completed, "another-source", [new(ChatRole.User, "short")],
            HistoryCompactionValidation.Measure(request.Messages), Size([new(ChatRole.User, "short")]))));
        using var scenario = await SetupAsync(plugin);

        var messages = await scenario.LoadAsync();

        Assert.Equal(new[] { Original }, messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ForgedDiagnosticsNeverPublishCandidate()
    {
        var plugin = new Plugin((request, _) => Task.FromResult(new HistoryCompactionResult(
            HistoryCompactionStatus.Completed, request.SourceBinding, [new(ChatRole.User, "short")], 0, 0)));
        using var scenario = await SetupAsync(plugin);

        await scenario.LoadAsync();

        Assert.Single(scenario.Fixture.Batches);
    }

    [Fact]
    public async Task BlankUnflaggedGeneratedOutputFallsBackAndStillPersistsCurrentTurn()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request,
                [new(ChatRole.Assistant, "   "), request.Messages[^2], request.Messages[^1]]))),
            messages: [new(ChatRole.User, Original), new(ChatRole.User, "recent"),
                new(ChatRole.Assistant, "recent reply")]);

        var loaded = await scenario.LoadAsync();
        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session,
            [new(ChatRole.User, "current")], [new(ChatRole.Assistant, "reply")]));

        Assert.Equal(new[] { Original, "recent", "recent reply" }, loaded.Select(message => message.Text));
        Assert.Equal(scenario.Source.ConversationId, scenario.Active.ConversationId);
        Assert.Equal(5, (await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages.Count);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("[Summary unavailable]")]
    public async Task DuplicatedSourceTextCannotBePublishedAsRetainedHistory(string text)
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request,
                [request.Messages[1], request.Messages[1], request.Messages[^1]]))),
            messages: [new(ChatRole.User, Original), new(ChatRole.Assistant, text),
                new(ChatRole.User, "recent")]);

        var loaded = await scenario.LoadAsync();
        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session,
            [new(ChatRole.User, "current")], [new(ChatRole.Assistant, "reply")]));

        Assert.Equal(new[] { Original, text, "recent" }, loaded.Select(message => message.Text));
        Assert.Equal(scenario.Source.ConversationId, scenario.Active.ConversationId);
        var stored = (await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages;
        Assert.Single(stored, message => message.Role == ChatRole.Assistant && message.Text == text);
        Assert.Equal(new[] { Original, text, "recent", "current", "reply" }, stored.Select(message => message.Text));
    }

    [Fact]
    public async Task AlreadyInformationalDenialSkipsCompactorAndPreservesStoredDecision()
    {
        var plugin = new Plugin((_, _) => throw new InvalidOperationException("must not run"));
        var fixture = new HistoryCosmosFixture();
        var logger = new RecordingLogger();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            compactor: plugin, compactionOptions: Options(), logger: logger);
        var session = NewSession();
        var call = new FunctionCallContent("denied-call", "lookup", null) { InformationalOnly = true };
        await provider.InvokedAsync(new(Agent(), session,
            [new(ChatRole.User, Original), new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", call)]),
                new(ChatRole.User, [new ToolApprovalResponseContent("approval", false, call)]),
                new(ChatRole.Assistant, [new FunctionCallContent("denied-call", "lookup", null)]),
                new(ChatRole.Tool, [new FunctionResultContent("denied-call", "result")])], []));
        var source = SessionPersistenceState.GetRequired(session).ActiveHistory;

        var loaded = (await provider.InvokingAsync(new(Agent(), session, []))).ToArray();
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "current")], []));

        Assert.Equal(0, plugin.Invocations);
        Assert.False(Assert.Single(loaded.SelectMany(message => message.Contents)
            .OfType<ToolApprovalResponseContent>()).Approved);
        Assert.Contains(logger.Messages, message => message.Contains("UnsafeToolHistory", StringComparison.Ordinal));
        var final = SessionPersistenceState.GetRequired(session).ActiveHistory;
        Assert.Equal(source.ConversationId, final.ConversationId);
        var stored = await fixture.CreateRepository().ReadAsync(final);
        Assert.False(Assert.Single(stored.Messages.SelectMany(message => message.Contents)
            .OfType<ToolApprovalResponseContent>()).Approved);
    }

    [Fact]
    public async Task EnabledUnchangedDoesNotAddARevisionReread()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) => Task.FromResult(Unchanged(request))));

        await scenario.LoadAsync();

        Assert.Single(scenario.Fixture.Queries);
    }

    [Fact]
    public async Task PluginCannotDropProtectedInstructions()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))),
            messages: [new(ChatRole.System, "protected"), new(ChatRole.User, Original)]);

        var messages = await scenario.LoadAsync();

        Assert.Equal(new[] { "protected", Original }, messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData("system", HistoryCompactionMode.Foreground)]
    [InlineData("developer", HistoryCompactionMode.Foreground)]
    [InlineData("system", HistoryCompactionMode.Background)]
    [InlineData("developer", HistoryCompactionMode.Background)]
    public async Task InterleavedInstructionsSkipCompactorLogAndStillSaveTurn(string role, HistoryCompactionMode mode)
    {
        var fixture = new HistoryCosmosFixture();
        var logger = new RecordingLogger();
        var plugin = new Plugin((_, _) => throw new InvalidOperationException("must not execute"))
        {
            SupportedModes = new HashSet<HistoryCompactionMode> { mode }
        };
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            compactor: plugin, compactionOptions: Options() with { Mode = mode }, logger: logger);
        var session = NewSession();
        ChatMessage[] original =
        [
            new(ChatRole.User, Original),
            new(new ChatRole(role), "interleaved instruction"),
            new(ChatRole.Assistant, "original reply")
        ];
        await provider.InvokedAsync(new(Agent(), session, original, []));
        var source = SessionPersistenceState.GetRequired(session).ActiveHistory;
        var sourceDocuments = fixture.Documents.Where(document =>
            document.GetProperty("type").GetString() == "ChatMessage")
            .Select(document => document.GetRawText()).ToArray();

        var loaded = (await provider.InvokingAsync(new(Agent(), session, []))).ToArray();
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, "current")],
            [new(ChatRole.Assistant, "current reply")]));

        Assert.Equal(original.Select(message => (message.Role, message.Text)),
            loaded.Select(message => (message.Role, message.Text)));
        Assert.Equal(0, plugin.Invocations);
        Assert.Contains(logger.Messages, message => message.Contains("ProtectedMessagesChanged", StringComparison.Ordinal));
        var reference = SessionPersistenceState.GetRequired(session).ActiveHistory;
        Assert.Equal(source.ConversationId, reference.ConversationId);
        Assert.Equal(new[] { Original, "interleaved instruction", "original reply", "current", "current reply" },
            (await fixture.CreateRepository().ReadAsync(reference)).Messages.Select(message => message.Text));
        Assert.Equal(sourceDocuments, fixture.Documents.Where(document =>
            document.GetProperty("type").GetString() == "ChatMessage").Take(original.Length)
            .Select(document => document.GetRawText()));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    public async Task MovedProtectedPrefixFallsBackWithoutPublication(string role)
    {
        var instruction = new ChatMessage(new ChatRole(role), "protected instruction");
        var plugin = new Plugin((request, _) => Task.FromResult(Completed(request,
            [new(ChatRole.User, "short"), request.Messages[0]])));
        using var scenario = await SetupAsync(plugin, messages:
            [instruction, new(ChatRole.User, Original)]);

        var loaded = await scenario.LoadAsync();
        await scenario.SaveAsync();

        Assert.Equal(new[] { "protected instruction", Original }, loaded.Select(message => message.Text));
        Assert.Equal(scenario.Source, scenario.Active);
        Assert.Single(scenario.Fixture.Batches);
    }

    [Fact]
    public async Task FailureLoggingContainsTypeButNoPluginPayloadOrSourceBinding()
    {
        var fixture = new HistoryCosmosFixture();
        var session = NewSession();
        var logger = new RecordingLogger();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            compactor: new Plugin((_, _) => throw new HttpRequestException("secret payload")),
            compactionOptions: Options(), logger: logger);
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, Original)], []));

        await provider.InvokingAsync(new(Agent(), session, []));

        Assert.Equal("Foreground compaction deferred or rejected: HttpRequestException.", Assert.Single(logger.Messages));
    }

    [Fact]
    public async Task StorageSchemaFaultPropagatesWithoutInvokingCompactor()
    {
        var fixture = new HistoryCosmosFixture
        {
            Properties = new("conversations", "/wrong")
        };
        using var provider = Provider(fixture, new Plugin((request, _) => Task.FromResult(Unchanged(request))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InvokingAsync(new(Agent(), NewSession(), [])).AsTask());
    }

    [Fact]
    public async Task MutatingPluginFailureDoesNotChangeFallbackBaseline()
    {
        var source = new ChatMessage(ChatRole.User, Original)
        {
            AdditionalProperties = new() { ["nested"] = new Dictionary<string, object?> { ["value"] = "original" } }
        };
        var plugin = new Plugin((request, _) =>
        {
            Assert.IsType<TextContent>(request.Messages[0].Contents[0]).Text = "mutated";
            request.Messages[0].AdditionalProperties?["nested"] = "mutated";
            throw new InvalidOperationException("sensitive plugin details must not be logged");
        });
        using var scenario = await SetupAsync(plugin, messages: [source]);

        var messages = await scenario.LoadAsync();

        Assert.Equal("original", Assert.IsType<JsonElement>(Assert.Single(messages).AdditionalProperties?["nested"])
            .GetProperty("value").GetString());
    }

    [Fact]
    public async Task MutatingPluginCannotForgeTheUnchangedBaseline()
    {
        var plugin = new Plugin((request, _) =>
        {
            Assert.IsType<TextContent>(request.Messages[0].Contents[0]).Text = "mutated";
            return Task.FromResult(Unchanged(request));
        });
        using var scenario = await SetupAsync(plugin);

        var messages = await scenario.LoadAsync();

        Assert.Equal(Original, Assert.Single(messages).Text);
    }

    [Fact]
    public async Task SameCountSmallerHistoryPublishesAndActivatesNewConversation()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));

        await scenario.LoadAsync();
        await scenario.SaveAsync();

        Assert.NotEqual(scenario.Source.ConversationId, scenario.Active.ConversationId);
    }

    [Fact]
    public async Task CompactionDoesNotRewriteOriginalMessagesOrRetention()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        var original = scenario.Fixture.Documents.Single(document => document.GetProperty("type").GetString() == "ChatMessage");

        await scenario.LoadAsync();
        await scenario.SaveAsync();

        Assert.Equal(original.GetRawText(), scenario.Fixture.Documents.Single(document =>
            document.GetProperty("conversationId").GetString() == scenario.Source.ConversationId
            && document.GetProperty("type").GetString() == "ChatMessage").GetRawText());
    }

    [Fact]
    public async Task PendingApprovalIsReturnedUnchangedWithoutInvokingPlugin()
    {
        var plugin = new Plugin((_, _) => throw new InvalidOperationException());
        var approval = new ToolApprovalRequestContent("approval", new FunctionCallContent("call", "tool"));
        using var scenario = await SetupAsync(plugin, messages: [new(ChatRole.Assistant, [approval])]);

        var messages = await scenario.LoadAsync();

        Assert.Equal((false, 0), (Assert.IsType<FunctionCallContent>(
            Assert.IsType<ToolApprovalRequestContent>(Assert.Single(Assert.Single(messages).Contents)).ToolCall).InformationalOnly,
            plugin.Invocations));
    }

    [Fact]
    public async Task FailedStrategyCannotFallbackWhenSourceHasAdvanced()
    {
        var fixture = new HistoryCosmosFixture();
        var session = NewSession();
        var plugin = new Plugin(async (_, token) =>
        {
            await fixture.CreateRepository().AppendAsync(SessionPersistenceState.GetRequired(session).ActiveHistory,
                [new(ChatRole.User, "concurrent")], cancellationToken: token);
            throw new InvalidOperationException();
        });
        using var provider = Provider(fixture, plugin);
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, Original)], []));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => provider.InvokingAsync(new(Agent(), session, [])).AsTask());
    }

    [Fact]
    public async Task SuccessfulStrategyCannotPublishOverConcurrentAppend()
    {
        var fixture = new HistoryCosmosFixture();
        var session = NewSession();
        var plugin = new Plugin(async (request, token) =>
        {
            await fixture.CreateRepository().AppendAsync(SessionPersistenceState.GetRequired(session).ActiveHistory,
                [new(ChatRole.User, "concurrent")], cancellationToken: token);
            return Completed(request, [new(ChatRole.User, "short")]);
        });
        using var provider = Provider(fixture, plugin);
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, Original)], []));

        await provider.InvokingAsync(new(Agent(), session, []));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() => provider.InvokedAsync(new(Agent(), session, [], [])).AsTask());
    }

    [Fact]
    public async Task PublicationFailureDoesNotAdvanceSessionOrFallback()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        scenario.Fixture.BatchFailures[3] = HttpStatusCode.BadRequest;
        await scenario.LoadAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(scenario.SaveAsync);

        Assert.Equal(scenario.Source, scenario.Active);
    }

    [Fact]
    public async Task LostPublicationResponseDoesNotBecomeSuccessfulFallback()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        scenario.Fixture.AfterCommitAsync = batch =>
        {
            if (batch.Partition == scenario.Source.ToAddress().ToPartitionKey())
                throw new TimeoutException();
            return Task.CompletedTask;
        };
        await scenario.LoadAsync();
        await Assert.ThrowsAsync<TimeoutException>(scenario.SaveAsync);

        Assert.Equal(scenario.Source, scenario.Active);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutFallbackRead()
    {
        using var cancellation = new CancellationTokenSource();
        using var scenario = await SetupAsync(new Plugin((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HistoryCompactionResult>(token);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.LoadAsync(cancellation.Token));

        Assert.Single(scenario.Fixture.Queries);
    }

    [Fact]
    public async Task StrategyTimeoutAllowsVerifiedFallback()
    {
        using var scenario = await SetupAsync(new Plugin(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }), Options() with { Timeout = TimeSpan.FromMilliseconds(10) });

        var messages = await scenario.LoadAsync();

        Assert.Equal(Original, Assert.Single(messages).Text);
    }

    [Fact]
    public async Task TimeoutAwaitsEvenAPluginThatIgnoresCancellation()
    {
        var completed = false;
        using var scenario = await SetupAsync(new Plugin(async (request, _) =>
        {
            await Task.Delay(50);
            completed = true;
            return Unchanged(request);
        }), Options() with { Timeout = TimeSpan.FromMilliseconds(10) });

        await scenario.LoadAsync();

        Assert.True(completed);
    }

    [Fact]
    public async Task CompactorStorageConflictIsNeverConvertedToFallback()
    {
        using var scenario = await SetupAsync(new Plugin((_, _) => throw new HistoryConcurrencyException()));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(scenario.LoadAsync);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactPureRotationRecoversEvenAfterFeatureIsDisabled(bool enabled)
    {
        var fixture = new HistoryCosmosFixture();
        var session = NewSession();
        var plugin = new Plugin((_, _) => throw new InvalidOperationException());
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository());
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, Original)], []));
        var source = SessionPersistenceState.GetRequired(session).ActiveHistory;
        var target = await fixture.CreateRepository().RotateAsync(source, [new(ChatRole.User, "short")],
            86400, Guid.NewGuid().ToString("N"));
        using var resumed = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            compactor: enabled ? plugin : null, compactionOptions: enabled ? Options() : null);

        await resumed.InvokingAsync(new(Agent(), session, []));

        Assert.Equal((target, 0), (SessionPersistenceState.GetRequired(session).ActiveHistory, plugin.Invocations));
    }

    [Fact]
    public async Task RecoveryRejectsAnAdvancedTargetWithoutChangingSession()
    {
        using var scenario = await SetupAsync(new Plugin((_, _) => throw new InvalidOperationException()));
        var repository = scenario.Fixture.CreateRepository();
        var target = await repository.RotateAsync(scenario.Source, [new(ChatRole.User, "short")],
            86400, Guid.NewGuid().ToString("N"));
        await repository.AppendAsync(target, [new(ChatRole.User, "future turn")]);
        await Assert.ThrowsAsync<HistoryConcurrencyException>(scenario.LoadAsync);

        Assert.Equal(scenario.Source, scenario.Active);
    }

    [Fact]
    public async Task RecoveryRereadRejectsTargetChangedAfterResolution()
    {
        using var scenario = await SetupAsync(new Plugin((_, _) => throw new InvalidOperationException()));
        var target = await scenario.Fixture.CreateRepository().RotateAsync(scenario.Source, [new(ChatRole.User, "short")],
            86400, Guid.NewGuid().ToString("N"));
        scenario.Fixture.BeforeQuery = () =>
        {
            scenario.Fixture.BeforeQuery = null;
            scenario.Fixture.SeedHead(target.WithRevision(2), 2);
        };

        await Assert.ThrowsAsync<HistoryConcurrencyException>(scenario.LoadAsync);
    }

    [Fact]
    public async Task RecoveredTargetStillValidatesItsSummary()
    {
        using var scenario = await SetupAsync(new Plugin((_, _) => throw new InvalidOperationException()));
        await scenario.Fixture.CreateRepository().RotateAsync(scenario.Source,
            [new(ChatRole.Assistant, "[Summary]\n[Summary unavailable]")
            {
                AdditionalProperties = new() { ["_is_summary"] = true }
            }],
            86400, Guid.NewGuid().ToString("N"));

        var exception = await Assert.ThrowsAsync<HistoryCompactionValidationException>(scenario.LoadAsync);

        Assert.Equal(HistoryCompactionFailureReason.InvalidSummary, exception.Reason);
    }

    [Fact]
    public async Task CurrentInputAndResponseAreStoredOnceOnTargetAndNextTurnUsesTarget()
    {
        var plugin = new Plugin((request, _) => Task.FromResult(request.Messages.Count == 1
            ? Completed(request, [new(ChatRole.User, "short")]) : Unchanged(request)));
        using var scenario = await SetupAsync(plugin);
        var current = await scenario.Provider.InvokingAsync(new(Agent(), scenario.Session, [new(ChatRole.User, "current")]));
        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, current, [new(ChatRole.Assistant, "reply")]));

        var next = await scenario.Provider.InvokingAsync(new(Agent(), scenario.Session, [new(ChatRole.User, "next")]));

        Assert.Equal(new[] { "short", "current", "reply", "next" }, next.Select(message => message.Text));
    }

    [Fact]
    public async Task SavePublishesCurrentTurnInTargetInitialRevisionWithoutAppendingToSource()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        var request = await scenario.Provider.InvokingAsync(new(Agent(), scenario.Session, [new(ChatRole.User, "current")]));

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, request, [new(ChatRole.Assistant, "reply")]));

        var stored = await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active);
        Assert.Equal(new[] { "short", "current", "reply" }, stored.Messages.Select(message => message.Text));
        Assert.Equal(1L, scenario.Active.Revision);
        Assert.Single(scenario.Fixture.Documents, document =>
            document.GetProperty("conversationId").GetString() == scenario.Source.ConversationId
            && document.GetProperty("type").GetString() == "ChatMessage");
    }

    [Fact]
    public async Task SuccessfulSaveConsumesPreparedCandidate()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        await scenario.LoadAsync();
        await scenario.SaveAsync();
        var target = scenario.Active;

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, [new(ChatRole.User, "later")], []));

        Assert.Equal(target.WithRevision(2), scenario.Active);
    }

    [Fact]
    public async Task ConcurrentAppendDuringPublicationCannotWinSourceCasOrAppendFallback()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        await scenario.LoadAsync();
        scenario.Fixture.BeforeExecuteAsync = () =>
        {
            scenario.Fixture.BeforeExecuteAsync = null;
            scenario.Fixture.SeedHead(scenario.Source.WithRevision(2), 1);
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<HistoryConcurrencyException>(scenario.SaveAsync);

        Assert.Equal((scenario.Source, 3), (scenario.Active, scenario.Fixture.Batches.Count));
    }

    [Fact]
    public async Task PreparedSourceIsNotReboundToAnAdvancedSessionReference()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        await scenario.LoadAsync();
        await scenario.Fixture.CreateRepository().AppendAsync(scenario.Source, [new(ChatRole.User, "other")],
            onCommitted: reference => SessionPersistenceState.SetHistory(scenario.Session, reference));

        await Assert.ThrowsAsync<HistoryConcurrencyException>(scenario.SaveAsync);
    }

    [Fact]
    public async Task NextLoadReplacesPreparedCandidateInsteadOfReusingAnAbortedInvocation()
    {
        var calls = 0;
        using var scenario = await SetupAsync(new Plugin((request, _) => Task.FromResult(Completed(request,
            [new(ChatRole.User, $"candidate-{++calls}")]))));
        await scenario.LoadAsync();
        await scenario.LoadAsync();

        await scenario.SaveAsync();

        Assert.Equal("candidate-2", Assert.Single(
            (await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages).Text);
    }

    [Fact]
    public async Task NewLoadClearsAbandonedCandidateEvenWhenSourceReadFails()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        await scenario.LoadAsync();
        scenario.Fixture.BeforeQuery = () => throw new HttpRequestException();
        await Assert.ThrowsAsync<HttpRequestException>(scenario.LoadAsync);
        scenario.Fixture.BeforeQuery = null;

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, [new(ChatRole.User, "later")], []));

        Assert.Equal(scenario.Source.WithRevision(2), scenario.Active);
    }

    [Fact]
    public async Task UnchangedNewLoadDiscardsEarlierPreparedCandidate()
    {
        var first = true;
        using var scenario = await SetupAsync(new Plugin((request, _) =>
        {
            var result = first ? Completed(request, [new(ChatRole.User, "short")]) : Unchanged(request);
            first = false;
            return Task.FromResult(result);
        }));
        await scenario.LoadAsync();
        await scenario.LoadAsync();

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, [new(ChatRole.User, "later")], []));

        Assert.Equal(scenario.Source.WithRevision(2), scenario.Active);
    }

    [Fact]
    public async Task ExplicitClearInvalidatesPreparedCandidate()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        await scenario.LoadAsync();
        await scenario.Provider.ClearMessagesAsync(scenario.Session);

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, [new(ChatRole.User, "after clear")], []));

        Assert.Equal("after clear", Assert.Single(
            (await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrDisabledProfileCannotPublishStalePreparation(bool disabled)
    {
        var plugin = new Plugin((request, _) => Task.FromResult(Completed(request, [new(ChatRole.User, "short")])));
        using var scenario = await SetupAsync(plugin);
        await scenario.LoadAsync();
        using var changed = new CosmosChatHistoryProvider(scenario.Fixture.CreateRepository(),
            compactor: disabled ? null : plugin,
            compactionOptions: disabled ? null : Options() with { CompactorKey = "changed-profile" });

        await changed.InvokedAsync(new(Agent(), scenario.Session, [new(ChatRole.User, "later")], []));

        Assert.Equal(scenario.Source.WithRevision(2), scenario.Active);
    }

    [Fact]
    public async Task PluginAndModelMutationsCannotChangeCanonicalCandidate()
    {
        ChatMessage[] candidate = [new(ChatRole.User, "short")
        {
            AdditionalProperties = new() { ["nested"] = new Dictionary<string, string> { ["value"] = "canonical" } }
        }];
        using var scenario = await SetupAsync(new Plugin((request, _) => Task.FromResult(Completed(request, candidate))),
            messages: [new(ChatRole.User, new string('x', 1000))]);
        var modelView = await scenario.LoadAsync();
        Assert.IsType<TextContent>(candidate[0].Contents[0]).Text = "plugin mutation";
        Assert.IsType<TextContent>(modelView[0].Contents[0]).Text = "model mutation";
        modelView[0].AdditionalProperties!["nested"] = "model mutation";

        await scenario.SaveAsync();

        var stored = Assert.Single((await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages);
        Assert.Equal(("short", "canonical"), (stored.Text,
            Assert.IsType<JsonElement>(stored.AdditionalProperties?["nested"]).GetProperty("value").GetString()));
    }

    [Fact]
    public async Task ModelViewFilterDoesNotFilterCanonicalCandidateAndStoreFilterStillControlsNewInput()
    {
        var fixture = new HistoryCosmosFixture();
        var session = NewSession();
        using var provider = new CosmosChatHistoryProvider(fixture.CreateRepository(),
            provideOutputMessageFilter: _ => [],
            storeInputMessageFilter: messages => messages.Where(message =>
                message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory && message.Text != "excluded"),
            compactor: new Plugin((request, _) => Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))),
            compactionOptions: Options());
        await provider.InvokedAsync(new(Agent(), session, [new(ChatRole.User, Original)], []));
        var request = (await provider.InvokingAsync(new(Agent(), session,
            [new(ChatRole.User, "current"), new(ChatRole.User, "excluded")]))).ToArray();

        await provider.InvokedAsync(new(Agent(), session, request, [new(ChatRole.Assistant, "reply")]));

        Assert.Equal(new[] { "short", "current", "reply" }, (await fixture.CreateRepository()
            .ReadAsync(SessionPersistenceState.GetRequired(session).ActiveHistory)).Messages.Select(message => message.Text));
        Assert.DoesNotContain(request, message => message.Text == "short");
    }

    [Fact]
    public async Task DefaultStoreFilterStillPersistsContextProviderMessagesButNotHistory()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        var contribution = new ChatMessage(ChatRole.User, "context message")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "test-provider");
        var request = await scenario.Provider.InvokingAsync(new(Agent(), scenario.Session,
            [contribution, new(ChatRole.User, "current")]));

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, request, [new(ChatRole.Assistant, "reply")]));

        Assert.Equal(new[] { "short", "context message", "current", "reply" },
            (await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task SharedProviderKeepsPreparedCandidatesInsideTheirOwnSessions()
    {
        var fixture = new HistoryCosmosFixture();
        using var provider = Provider(fixture, new Plugin((request, _) => Task.FromResult(Completed(request,
            [new(ChatRole.User, request.Messages[0].Text[..1])]))));
        var first = NewSession();
        var second = NewSession();
        await provider.InvokedAsync(new(Agent(), first, [new(ChatRole.User, "first original history")], []));
        await provider.InvokedAsync(new(Agent(), second, [new(ChatRole.User, "second original history")], []));
        await provider.InvokingAsync(new(Agent(), first, []));
        await provider.InvokingAsync(new(Agent(), second, []));

        await provider.InvokedAsync(new(Agent(), second, [], []));
        await provider.InvokedAsync(new(Agent(), first, [], []));

        var firstMessages = await fixture.CreateRepository().ReadAsync(SessionPersistenceState.GetRequired(first).ActiveHistory);
        var secondMessages = await fixture.CreateRepository().ReadAsync(SessionPersistenceState.GetRequired(second).ActiveHistory);
        Assert.Equal(("f", "s"), (Assert.Single(firstMessages.Messages).Text, Assert.Single(secondMessages.Messages).Text));
    }

    [Fact]
    public async Task CompleteHistoryCanExceedTwoMegabytesWhenIndividualDocumentsFitCosmos()
    {
        var original = Enumerable.Range(0, 12)
            .Select(index => new ChatMessage(ChatRole.User, $"{index}: {new string('x', 200_000)}")).ToArray();
        using var scenario = await SetupAsync(new Plugin((request, _) => Task.FromResult(Unchanged(request))),
            messages: original);
        var loaded = await scenario.LoadAsync();
        var queries = scenario.Fixture.Queries.Count;

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session,
            [new(ChatRole.User, new string('y', 500_000))], [new(ChatRole.Assistant, "reply")]));

        Assert.True(Size(loaded) > 2 * 1024 * 1024);
        Assert.Equal(queries, scenario.Fixture.Queries.Count);
        Assert.Equal(scenario.Source.ConversationId, scenario.Active.ConversationId);
        Assert.Equal(14, (await scenario.Fixture.CreateRepository().ReadAsync(scenario.Active)).Messages.Count);
    }

    [Fact]
    public async Task PublishingPendingApprovalDoesNotMutateCallerMessages()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) =>
            Task.FromResult(Completed(request, [new(ChatRole.User, "short")]))));
        await scenario.LoadAsync();
        ChatMessage[] suffix = [new(ChatRole.User, "current"),
            new(ChatRole.Assistant, [new ToolApprovalRequestContent("approval", new FunctionCallContent("call", "tool"))])];
        var original = JsonSerializer.Serialize(suffix);

        await scenario.Provider.InvokedAsync(new(Agent(), scenario.Session, [suffix[0]], [suffix[1]]));

        Assert.Equal(original, JsonSerializer.Serialize(suffix));
        Assert.Equal(original, JsonSerializer.Serialize((await scenario.Fixture.CreateRepository()
            .ReadAsync(scenario.Active)).Messages.Skip(1).ToArray()));
    }

    [Fact]
    public async Task MultipleLoadsWithoutSaveNeverRotate()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) => Task.FromResult(Completed(request,
            [new(ChatRole.User, request.Messages[0].Text[..(request.Messages[0].Text.Length / 2)])]))));
        await scenario.LoadAsync();
        await scenario.LoadAsync();

        Assert.Equal((scenario.Source, 1), (scenario.Active, scenario.Fixture.Batches.Count));
    }

    [Fact]
    public async Task OldSnapshotCannotRecoverThroughTwoUncheckpointedRotations()
    {
        using var scenario = await SetupAsync(new Plugin((request, _) => Task.FromResult(Completed(request,
            [new(ChatRole.User, request.Messages[0].Text[..(request.Messages[0].Text.Length / 2)])]))));
        await scenario.LoadAsync();
        await scenario.SaveAsync();
        await scenario.LoadAsync();
        await scenario.SaveAsync();

        await Assert.ThrowsAsync<HistoryConcurrencyException>(() =>
            scenario.Fixture.CreateRepository().ResolveRotationAsync(scenario.Source));
    }

    [Fact]
    public void CompactorWithoutOptionsIsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => new CosmosChatHistoryProvider(
            new HistoryCosmosFixture().CreateRepository(), compactor: new Plugin((request, _) => Task.FromResult(Unchanged(request)))));

        Assert.StartsWith("History compaction requires both compactor and compactionOptions. Supply both to enable compaction, or leave both null to disable it.",
            exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsWithoutCompactorAreRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => new CosmosChatHistoryProvider(
            new HistoryCosmosFixture().CreateRepository(), compactionOptions: Options()));

        Assert.StartsWith("History compaction requires both compactor and compactionOptions. Supply both to enable compaction, or leave both null to disable it.",
            exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundProviderConstructionUsesOnlyAdvertisedModes()
    {
        var plugin = new Plugin((request, _) => Task.FromResult(Unchanged(request)))
        {
            SupportedModes = new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground, HistoryCompactionMode.Background }
        };

        using var provider = Provider(new HistoryCosmosFixture(), plugin,
            Options() with { Mode = HistoryCompactionMode.Background });

        Assert.NotNull(provider);
    }

    [Fact]
    public void UnsupportedPluginModeIsRejected()
    {
        var plugin = new Plugin((request, _) => Task.FromResult(Unchanged(request))) { SupportedModes = new HashSet<HistoryCompactionMode>() };

        Assert.Throws<NotSupportedException>(() => Provider(new HistoryCosmosFixture(), plugin));
    }

    [Fact]
    public void PartialHistoryWindowCannotBeSetAfterConstructionWithCompaction()
    {
        using var provider = Provider(new HistoryCosmosFixture(), new Plugin((request, _) => Task.FromResult(Unchanged(request))));

        var exception = Assert.Throws<ArgumentException>(() => provider.MaxMessagesToRetrieve = 1);

        Assert.StartsWith("Compaction and MaxMessagesToRetrieve cannot be enabled together. Set MaxMessagesToRetrieve to null so compaction reads the complete history, or disable Compaction to use a partial-history window.",
            exception.Message, StringComparison.Ordinal);
    }

    private const string Original = "This original source is deliberately longer than a compacted summary. "
        + "The provider must keep an independent baseline, exact revision, and complete messages.";

    private static long Size(IReadOnlyList<ChatMessage> messages) => HistoryCompactionValidation.Measure(messages);

    private static HistoryCompactionOptions Options() => new()
    {
        CompactorKey = "provider-test"
    };

    private static HistoryCompactionResult Unchanged(HistoryCompactionRequest request) =>
        new(HistoryCompactionStatus.Unchanged, request.SourceBinding, request.Messages, Size(request.Messages), Size(request.Messages));

    private static HistoryCompactionResult Completed(HistoryCompactionRequest request, IReadOnlyList<ChatMessage> messages) =>
        new(HistoryCompactionStatus.Completed, request.SourceBinding, messages, Size(request.Messages), Size(messages));

    private static CosmosChatHistoryProvider Provider(HistoryCosmosFixture fixture, Plugin plugin, HistoryCompactionOptions? options = null) =>
        new(fixture.CreateRepository(), compactor: plugin, compactionOptions: options ?? Options());

    private static async Task<Scenario> SetupAsync(Plugin plugin, HistoryCompactionOptions? options = null,
        IReadOnlyList<ChatMessage>? messages = null)
    {
        var fixture = new HistoryCosmosFixture();
        var session = NewSession();
        var provider = Provider(fixture, plugin, options);
        await provider.InvokedAsync(new(Agent(), session, messages ?? [new(ChatRole.User, Original)], []));
        return new(fixture, session, provider, SessionPersistenceState.GetRequired(session).ActiveHistory);
    }

    private sealed record Scenario(HistoryCosmosFixture Fixture, TestAgentSession Session,
        CosmosChatHistoryProvider Provider, HistoryReference Source) : IDisposable
    {
        internal HistoryReference Active => SessionPersistenceState.GetRequired(Session).ActiveHistory;
        internal async Task<IReadOnlyList<ChatMessage>> LoadAsync(CancellationToken cancellationToken = default) =>
            (await Provider.InvokingAsync(new(Agent(), Session, []), cancellationToken)).ToArray();
        internal Task<IReadOnlyList<ChatMessage>> LoadAsync() => LoadAsync(CancellationToken.None);
        internal Task SaveAsync() => Provider.InvokedAsync(new(Agent(), Session, [], [])).AsTask();
        public void Dispose() => Provider.Dispose();
    }

    private sealed class Plugin(Func<HistoryCompactionRequest, CancellationToken, Task<HistoryCompactionResult>> compact) : IHistoryCompactor
    {
        internal int Invocations { get; private set; }
        public IReadOnlySet<HistoryCompactionMode> SupportedModes { get; init; } =
            new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground };
        public Task<HistoryCompactionResult> CompactAsync(HistoryCompactionRequest request, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return compact(request, cancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger<CosmosChatHistoryProvider>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static AIAgent Agent() => new Mock<AIAgent>().Object;

    private static TestAgentSession NewSession()
    {
        var session = new TestAgentSession();
        SessionPersistenceState.Initialize(session, SessionStorageAddress.Create("compaction-test", "session"));
        return session;
    }
}
