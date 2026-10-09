#pragma warning disable MAAI001 // Exercise the default interface method on the foreground MAF adapter.

using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SharedServices;

namespace SharedServices.Tests;

public class CompactionContractTests
{
    [Fact]
    public void ProviderOptionsDisableCompactionByDefault()
    {
        Assert.Null(new CosmosChatHistoryProviderOptions().Compaction);
    }

    [Fact]
    public void EnabledProfileDefaultsToForeground()
    {
        Assert.Equal(HistoryCompactionMode.Foreground, new HistoryCompactionOptions().Mode);
    }

    [Fact]
    public void ProfileDoesNotInventATimeout()
    {
        Assert.Null(new HistoryCompactionOptions().Timeout);
    }

    [Fact]
    public void ProfileHasNoByteCapByDefault()
    {
        var profile = new HistoryCompactionOptions { CompactorKey = "text" };
        profile.Validate();
        Assert.Null(profile.MaxHistoryUtf8Bytes);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void NonpositiveHistoryBudgetIsRejected(long budget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (Profile() with { MaxHistoryUtf8Bytes = budget }).Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyCompactorKeyIsRejected(string? key)
    {
        Assert.Throws<ArgumentException>(() => (Profile() with { CompactorKey = key! }).Validate());
    }

    [Fact]
    public void NullCompactorKeyIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => (Profile() with { CompactorKey = null! }).Validate());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(4294967295L)]
    public void InvalidTimeoutIsRejected(long milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (Profile() with { Timeout = TimeSpan.FromMilliseconds(milliseconds) }).Validate());
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(4294967294L)]
    public void FiniteCancelableTimeoutIsAccepted(long milliseconds)
    {
        Assert.Null(Record.Exception(() =>
            (Profile() with { Timeout = TimeSpan.FromMilliseconds(milliseconds) }).Validate()));
    }

    [Fact]
    public void ValidProfileWithoutTimeoutIsAccepted()
    {
        Assert.Null(Record.Exception(() => Profile().Validate()));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(4294967295L)]
    public void InvalidBackgroundSaveWaitIsRejected(long milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (Profile() with { BackgroundSaveWaitTimeout = TimeSpan.FromMilliseconds(milliseconds) }).Validate());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(2000L)]
    public void FiniteBackgroundSaveWaitIsAccepted(long milliseconds)
    {
        Assert.Null(Record.Exception(() =>
            (Profile() with { BackgroundSaveWaitTimeout = TimeSpan.FromMilliseconds(milliseconds) }).Validate()));
    }

    [Fact]
    public void UnknownExecutionModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (Profile() with { Mode = (HistoryCompactionMode)42 }).Validate());
    }

    [Fact]
    public void BackgroundProfileIsValidBeforeCapabilityResolution()
    {
        (Profile() with { Mode = HistoryCompactionMode.Background }).Validate();
    }

    [Fact]
    public void PendingOutcomeHasOnlyATicketAndCannotBeValidatedAsFinalHistory()
    {
        var ticket = new HistoryCompactionTicket("job", "source");
        var result = HistoryCompactionResult.Pending(ticket);
        Assert.Equal(HistoryCompactionStatus.Pending, result.Status);
        Assert.Same(ticket, result.Ticket);
        Assert.Empty(result.Messages);
        var request = new HistoryCompactionRequest("agent", "source", [], Profile());
        var error = Assert.Throws<HistoryCompactionValidationException>(() =>
            HistoryCompactionValidation.ValidateResult(request, result));
        Assert.Equal(HistoryCompactionFailureReason.InvalidLifecycle, error.Reason);
        Assert.Throws<ArgumentException>(() => new HistoryCompactionResult(HistoryCompactionStatus.Pending, "source", [], 0, 0));
    }

    [Theory]
    [InlineData("", "binding")]
    [InlineData("job", "")]
    public void PendingTicketRequiresJobAndSourceIdentity(string job, string binding)
    {
        Assert.Throws<ArgumentException>(() => new HistoryCompactionTicket(job, binding));
    }

    [Fact]
    public void PendingOutcomeRoundTripsForRemoteRetrieval()
    {
        var ticket = new HistoryCompactionTicket("job", "binding");
        var json = System.Text.Json.JsonSerializer.Serialize(HistoryCompactionResult.Pending(ticket));
        var result = System.Text.Json.JsonSerializer.Deserialize<HistoryCompactionResult>(json);

        Assert.NotNull(result);
        Assert.Equal(HistoryCompactionStatus.Pending, result.Status);
        Assert.Equal(ticket, result.Ticket);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void PendingAndFinalPayloadsCannotBeMixed()
    {
        var ticket = new HistoryCompactionTicket("job", "binding");
        Assert.Throws<ArgumentException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Completed, "binding", [new(ChatRole.User, "summary")], 100, 50, ticket));
        Assert.Throws<ArgumentException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Pending, "wrong-binding", [], 0, 0, ticket));
        Assert.Throws<ArgumentException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Pending, "binding", [], 100, 0, ticket));
    }

    [Fact]
    public void ProfileCopyDoesNotChangeOriginalBudget()
    {
        var original = Profile();
        _ = original with { MaxHistoryUtf8Bytes = 200 };

        Assert.Equal(1_000, original.MaxHistoryUtf8Bytes);
    }

    [Fact]
    public void DisabledResolutionDoesNotLookUpServices()
    {
        var services = new Mock<IServiceProvider>(MockBehavior.Strict);

        Assert.Null(services.Object.GetHistoryCompactor(null));
    }

    [Fact]
    public void ResolutionRejectsNullServiceProvider()
    {
        Assert.Throws<ArgumentNullException>(() => HistoryCompactionExtensions.GetHistoryCompactor(null!, Profile()));
    }

    [Fact]
    public void MissingKeyedCompactorIsAnExplicitError()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetHistoryCompactor(Profile()));
    }

    [Fact]
    public void UnkeyedCompactorCannotSilentlyReplaceAKeyedProfile()
    {
        var services = new ServiceCollection();
        services.AddSingleton(ForegroundCompactor().Object);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetHistoryCompactor(Profile()));
    }

    [Fact]
    public void KeyedProfileResolvesWithoutAnyStorageRegistration()
    {
        var compactor = ForegroundCompactor();
        var services = new ServiceCollection();
        services.AddHistoryCompactor("text", _ => compactor.Object);
        using var provider = services.BuildServiceProvider();

        Assert.Same(compactor.Object, provider.GetHistoryCompactor(Profile()));
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public void DistinctAgentProfilesResolveTheirOwnKeyedCompactor(string key)
    {
        var compactors = new Dictionary<string, IHistoryCompactor>
        {
            ["first"] = ForegroundCompactor().Object,
            ["second"] = ForegroundCompactor().Object
        };
        var services = new ServiceCollection();
        services.AddHistoryCompactor("first", _ => compactors["first"]);
        services.AddHistoryCompactor("second", _ => compactors["second"]);
        using var provider = services.BuildServiceProvider();

        Assert.Same(compactors[key], provider.GetHistoryCompactor(Profile() with { CompactorKey = key }));
    }

    [Fact]
    public void RegistrationIsAKeyedSingleton()
    {
        var services = new ServiceCollection();
        services.AddHistoryCompactor("text", _ => ForegroundCompactor().Object);
        using var provider = services.BuildServiceProvider();
        var first = provider.GetHistoryCompactor(Profile());

        Assert.Same(first, provider.GetHistoryCompactor(Profile()));
    }

    [Fact]
    public void RegistrationFactoryCanResolveStrategyDependencies()
    {
        var compactor = ForegroundCompactor().Object;
        var services = new ServiceCollection();
        services.AddSingleton(compactor);
        services.AddHistoryCompactor("text", provider => provider.GetRequiredService<IHistoryCompactor>());
        using var provider = services.BuildServiceProvider();

        Assert.Same(compactor, provider.GetHistoryCompactor(Profile()));
    }

    [Fact]
    public void RegistrationReturnsTheOriginalServiceCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddHistoryCompactor("text", _ => ForegroundCompactor().Object));
    }

    [Fact]
    public void RegistrationRejectsNullServices()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HistoryCompactionExtensions.AddHistoryCompactor<IHistoryCompactor>(null!, "text", _ => ForegroundCompactor().Object));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RegistrationRejectsEmptyKey(string? key)
    {
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddHistoryCompactor(key!, _ => ForegroundCompactor().Object));
    }

    [Fact]
    public void RegistrationRejectsNullKey()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddHistoryCompactor(null!, _ => ForegroundCompactor().Object));
    }

    [Fact]
    public void RegistrationRejectsNullFactory()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddHistoryCompactor<IHistoryCompactor>("text", null!));
    }

    [Fact]
    public void ForegroundResolutionRequiresForegroundCapability()
    {
        var compactor = new Mock<IHistoryCompactor>();
        compactor.SetupGet(value => value.SupportedModes).Returns(new HashSet<HistoryCompactionMode>());
        var services = new ServiceCollection();
        services.AddHistoryCompactor("text", _ => compactor.Object);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<NotSupportedException>(() => provider.GetHistoryCompactor(Profile()));
    }

    [Fact]
    public void MissingCapabilitiesAreRejectedExplicitly()
    {
        var services = new ServiceCollection();
        services.AddHistoryCompactor("text", _ => new Mock<IHistoryCompactor>().Object);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<NotSupportedException>(() => provider.GetHistoryCompactor(Profile()));
    }

    [Fact]
    public void BackgroundResolutionUsesOnlyAdvertisedModesOnTheSingleContract()
    {
        var compactor = new Mock<IHistoryCompactor>();
        compactor.SetupGet(value => value.SupportedModes).Returns(new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Background });
        var services = new ServiceCollection();
        services.AddHistoryCompactor("text", _ => compactor.Object);
        using var provider = services.BuildServiceProvider();

        Assert.Same(compactor.Object,
            provider.GetHistoryCompactor(Profile() with { Mode = HistoryCompactionMode.Background }));
    }

    [Fact]
    public async Task ForegroundCompactorDefaultRetrievalExplicitlyRejectsBackgroundJobs()
    {
        IHistoryCompactor compactor = new MafForegroundHistoryCompactor(
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(2)));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            compactor.GetResultAsync(new("job", "source")));

        Assert.Equal("This history compactor does not implement background result retrieval. Foreground-only compactors cannot retrieve job results.",
            exception.Message);
    }

    [Fact]
    public async Task ForegroundCompactorDefaultCancellationExplicitlyRejectsBackgroundJobs()
    {
        IHistoryCompactor compactor = new MafForegroundHistoryCompactor(
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(2)));

        await Assert.ThrowsAsync<NotSupportedException>(() => compactor.CancelAsync(new("job", "source")));
    }

    [Fact]
    public void ForegroundOnlyCompactorRejectsBackgroundConfiguration()
    {
        var services = new ServiceCollection();
        services.AddHistoryCompactor("text", _ => new MafForegroundHistoryCompactor(
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(2))));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<NotSupportedException>(() =>
            provider.GetHistoryCompactor(Profile() with { Mode = HistoryCompactionMode.Background }));
    }

    [Fact]
    public void InvalidProfileIsRejectedBeforeAnyServiceLookup()
    {
        var provider = new Mock<IServiceProvider>(MockBehavior.Strict);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            provider.Object.GetHistoryCompactor(Profile() with { MaxHistoryUtf8Bytes = 0 }));
    }

    [Fact]
    public void RequestPreservesOpaqueSourceBinding()
    {
        var request = new HistoryCompactionRequest("agent", "opaque-source:revision", [], Profile());

        Assert.Equal("opaque-source:revision", request.SourceBinding);
    }

    [Fact]
    public void RequestRetainsLogicalAgentId()
    {
        var request = new HistoryCompactionRequest("agent", "source", [], Profile());

        Assert.Equal("agent", request.AgentId);
    }

    [Fact]
    public void RequestRetainsImmutableProfile()
    {
        var profile = Profile();
        var request = new HistoryCompactionRequest("agent", "source", [], profile);

        Assert.Same(profile, request.Options);
    }

    [Fact]
    public void RequestSnapshotsCollectionMembership()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "original") };
        var request = new HistoryCompactionRequest("agent", "source", messages, Profile());
        messages.Clear();

        Assert.Equal("original", Assert.Single(request.Messages).Text);
    }

    [Fact]
    public void RequestCollectionCannotBeMutated()
    {
        var request = new HistoryCompactionRequest("agent", "source", [new(ChatRole.User, "original")], Profile());

        Assert.Throws<NotSupportedException>(() =>
            ((IList<ChatMessage>)request.Messages).Add(new(ChatRole.Assistant, "replacement")));
    }

    [Fact]
    public void RequestRejectsNullMessages()
    {
        Assert.Throws<ArgumentNullException>(() => new HistoryCompactionRequest("agent", "source", null!, Profile()));
    }

    [Fact]
    public void RequestRejectsNullMessageEntries()
    {
        Assert.Throws<ArgumentException>(() => new HistoryCompactionRequest("agent", "source", [null!], Profile()));
    }

    [Fact]
    public void RequestRejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new HistoryCompactionRequest("agent", "source", [], null!));
    }

    [Fact]
    public void RequestRejectsAnUnvalidatedProfile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HistoryCompactionRequest("agent", "source", [], Profile() with { MaxHistoryUtf8Bytes = 0 }));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RequestRejectsEmptyAgentId(string? agentId)
    {
        Assert.Throws<ArgumentException>(() => new HistoryCompactionRequest(agentId!, "source", [], Profile()));
    }

    [Fact]
    public void RequestRejectsNullAgentId()
    {
        Assert.Throws<ArgumentNullException>(() => new HistoryCompactionRequest(null!, "source", [], Profile()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RequestRejectsEmptyBinding(string? binding)
    {
        Assert.Throws<ArgumentException>(() => new HistoryCompactionRequest("agent", binding!, [], Profile()));
    }

    [Fact]
    public void RequestRejectsNullBinding()
    {
        Assert.Throws<ArgumentNullException>(() => new HistoryCompactionRequest("agent", null!, [], Profile()));
    }

    [Fact]
    public void CompletedResultSupportsSameMessageCountCompaction()
    {
        var original = new HistoryCompactionRequest("agent", "source", [new(ChatRole.User, "long history")], Profile());
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Completed, original.SourceBinding,
            [new(ChatRole.User, "short")], 500, 100);

        Assert.Equal(original.Messages.Count, result.Messages.Count);
    }

    [Theory]
    [InlineData(HistoryCompactionStatus.Unchanged)]
    [InlineData(HistoryCompactionStatus.Completed)]
    public void ResultRetainsStatus(HistoryCompactionStatus status)
    {
        var result = new HistoryCompactionResult(status, "source", [new(ChatRole.User, "text")], 100, 100);

        Assert.Equal(status, result.Status);
    }

    [Fact]
    public void ResultEchoesOpaqueBinding()
    {
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, "opaque-source:revision", [], 2, 2);

        Assert.Equal("opaque-source:revision", result.SourceBinding);
    }

    [Fact]
    public void ResultRetainsOriginalSizeDiagnostic()
    {
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Completed, "source", [new(ChatRole.User, "text")], 500, 100);

        Assert.Equal(500, result.BeforeUtf8Bytes);
    }

    [Fact]
    public void ResultRetainsCandidateSizeDiagnostic()
    {
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Completed, "source", [new(ChatRole.User, "text")], 500, 100);

        Assert.Equal(100, result.AfterUtf8Bytes);
    }

    [Fact]
    public void ResultSnapshotsCollectionMembership()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "candidate") };
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Completed, "source", messages, 500, 100);
        messages.Clear();

        Assert.Equal("candidate", Assert.Single(result.Messages).Text);
    }

    [Fact]
    public void ResultCollectionCannotBeMutated()
    {
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Completed, "source", [new(ChatRole.User, "text")], 500, 100);

        Assert.Throws<NotSupportedException>(() => ((IList<ChatMessage>)result.Messages).Clear());
    }

    [Fact]
    public void EmptyCompletedResultIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Completed, "source", [], 500, 2));
    }

    [Fact]
    public void EmptyUnchangedHistoryIsValid()
    {
        var result = new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, "source", [], 2, 2);

        Assert.Empty(result.Messages);
    }

    [Fact]
    public void UnchangedResultCannotClaimDifferentSizes()
    {
        Assert.Throws<ArgumentException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, "source", [], 500, 100));
    }

    [Fact]
    public void UnknownResultStatusIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HistoryCompactionResult((HistoryCompactionStatus)42, "source", [], 2, 2));
    }

    [Fact]
    public void NegativeOriginalSizeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Completed, "source", [new(ChatRole.User, "text")], -1, 100));
    }

    [Fact]
    public void NegativeCandidateSizeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Completed, "source", [new(ChatRole.User, "text")], 500, -1));
    }

    [Fact]
    public void ResultRejectsNullMessages()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, "source", null!, 2, 2));
    }

    [Fact]
    public void ResultRejectsNullMessageEntries()
    {
        Assert.Throws<ArgumentException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, "source", [null!], 2, 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ResultRejectsEmptySourceBinding(string? binding)
    {
        Assert.Throws<ArgumentException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, binding!, [], 2, 2));
    }

    [Fact]
    public void ResultRejectsNullSourceBinding()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, null!, [], 2, 2));
    }

    private static HistoryCompactionOptions Profile() => new() { CompactorKey = "text", MaxHistoryUtf8Bytes = 1_000 };

    private static Mock<IHistoryCompactor> ForegroundCompactor()
    {
        var compactor = new Mock<IHistoryCompactor>();
        compactor.SetupGet(value => value.SupportedModes).Returns(new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Foreground });
        return compactor;
    }
}
