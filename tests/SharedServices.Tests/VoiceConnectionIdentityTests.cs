using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VoiceOrchestratorAgent;

namespace SharedServices.Tests;

public class VoiceConnectionIdentityTests
{
    [Fact]
    public async Task AbsentProviderUsesTheCommonAnonymousScopeWithoutChangingTheLookupId()
    {
        var address = await VoiceConnectionIdentity.CaptureAsync("+39-東京", null, NullLogger.Instance);

        Assert.Equal(SessionStorageAddress.Create(VoiceConversationStore.AgentId, "+39-東京"), address);
    }

    [Fact]
    public async Task AbsentProviderEmitsAnAnonymousWarning()
    {
        var logger = new Mock<ILogger>();

        await VoiceConnectionIdentity.CaptureAsync("lookup", null, logger.Object);

        logger.Verify(value => value.Log(LogLevel.Warning, It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task RegisteredProviderWithNoKeyFailsInsteadOfUsingAnonymousScope(string? key)
    {
        var provider = new Mock<AgentIsolationKeyProvider>();
        provider.Setup(value => value.GetIsolationKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(key);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VoiceConnectionIdentity.CaptureAsync("lookup", provider.Object, NullLogger.Instance));
    }

    [Fact]
    public async Task RegisteredProviderIsRequiredEvenForNonpersistentConnection()
    {
        var provider = new Mock<AgentIsolationKeyProvider>();
        provider.Setup(value => value.GetIsolationKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VoiceConnectionIdentity.CaptureAsync(null, provider.Object, NullLogger.Instance));
    }

    [Fact]
    public async Task CapturedIdentityUsesTheSameCanonicalIsolationPartitionAsText()
    {
        var provider = new Mock<AgentIsolationKeyProvider>();
        provider.Setup(value => value.GetIsolationKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("user-東京");

        var address = await VoiceConnectionIdentity.CaptureAsync("lookup", provider.Object, NullLogger.Instance);

        Assert.Equal(SessionStorageAddress.Create(VoiceConversationStore.AgentId, "lookup",
            new Dictionary<string, string> { ["isolation"] = "user-東京" }), address);
    }

    [Fact]
    public async Task SaveDoesNotResolveAmbientIdentityAgain()
    {
        var fixture = new VoiceStoreFixture();
        var provider = new Mock<AgentIsolationKeyProvider>();
        provider.SetupSequence(value => value.GetIsolationKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("original-user").ReturnsAsync("different-user");
        var address = await VoiceConnectionIdentity.CaptureAsync("lookup", provider.Object, NullLogger.Instance);
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address), []);

        provider.Verify(value => value.GetIsolationKeyAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
