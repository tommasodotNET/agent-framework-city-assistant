using SharedServices;

namespace SharedServices.Tests;

public class StorageAddressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinedPartitionKeyAcceptsExactUtf8BudgetAndRejectsOneByteMore(bool history)
    {
        var scope = StorageScope.Create("unused",
            new Dictionary<string, string> { ["isolation"] = "owner" });
        var remaining = StorageSchema.MaxPartitionKeyBytes - System.Text.Encoding.UTF8.GetByteCount(scope);
        var second = new string('\u00e9', remaining / 2) + new string('x', remaining % 2);

        if (history)
        {
            _ = new HistoryStorageAddress(scope, second);
            Assert.Throws<ArgumentException>(() => new HistoryStorageAddress(scope, second + "x"));
        }
        else
        {
            _ = new SessionStorageAddress("agent", scope, second);
            Assert.Throws<ArgumentException>(() => new SessionStorageAddress("agent", scope, second + "x"));
        }
    }

    [Fact]
    public void FullLengthScopeLeavesNoBudgetForAnotherPartitionComponent()
    {
        var scope = StorageScope.Create(new string('x', 2032));
        Assert.Equal(2048, System.Text.Encoding.UTF8.GetByteCount(scope));

        Assert.Throws<ArgumentException>(() => new HistoryStorageAddress(scope, "history"));
        Assert.Throws<ArgumentException>(() => new SessionStorageAddress("agent", scope, new string('x', 2032)));
    }

    [Fact]
    public void AnonymousScopeAndRepeatedLookupIdShareTheSameBudget()
    {
        _ = SessionStorageAddress.Create("agent", new string('x', 1016));
        Assert.Throws<ArgumentException>(() => SessionStorageAddress.Create("agent", new string('x', 1017)));
    }

    [Fact]
    public void EscapingInsideCanonicalScopeCountsTowardTheCombinedValueBudget()
    {
        var scope = StorageScope.Create("unused",
            new Dictionary<string, string> { ["isolation"] = new string('"', 900) });
        var remaining = StorageSchema.MaxPartitionKeyBytes - System.Text.Encoding.UTF8.GetByteCount(scope);
        _ = new HistoryStorageAddress(scope, new string('x', remaining));

        Assert.Throws<ArgumentException>(() => new HistoryStorageAddress(scope, new string('x', remaining + 1)));
    }

    [Theory]
    [InlineData("agent/one\\?#:%")]
    [InlineData("Città 東京")]
    public void AgentDocumentIdIsReversibleAndCosmosSafe(string agentId)
    {
        var address = SessionStorageAddress.Create(agentId, "lookup");

        Assert.Equal(agentId, Uri.UnescapeDataString(address.DocumentId));
    }

    [Fact]
    public void CosmosDocumentIdEncodesForbiddenCharacters()
    {
        var address = SessionStorageAddress.Create("a/b\\c?#%", "lookup");

        Assert.Equal("a%2Fb%5Cc%3F%23%25", address.DocumentId);
    }

    [Fact]
    public void EncodedAndUnencodedAgentIdsCannotCollide()
    {
        Assert.NotEqual(SessionStorageAddress.Create("a/b", "id"), SessionStorageAddress.Create("a%2Fb", "id"));
    }

    [Fact]
    public void AgentIdentitySeparatesSessionDocumentsSharingFullPartitionKey()
    {
        var text = SessionStorageAddress.Create("text", "lookup");
        var voice = SessionStorageAddress.Create("voice", "lookup");

        Assert.NotEqual(text.DocumentId, voice.DocumentId);
    }

    [Fact]
    public void DifferentUsersHaveDifferentFullAddresses()
    {
        var first = SessionStorageAddress.Create("agent", "lookup", new Dictionary<string, string> { ["isolation"] = "one" });
        var second = SessionStorageAddress.Create("agent", "lookup", new Dictionary<string, string> { ["isolation"] = "two" });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ReconstructedAddressHasStableValueEquality()
    {
        var address = SessionStorageAddress.Create("agent", "lookup");

        Assert.Equal(address, new SessionStorageAddress(address.AgentId, address.ScopeKey, address.SessionId));
    }

    [Fact]
    public void SessionHierarchicalKeyUsesScopeThenRawLookupId()
    {
        var address = SessionStorageAddress.Create("agent", "+393331234567");
        var expected = new Microsoft.Azure.Cosmos.PartitionKeyBuilder().Add(address.ScopeKey).Add("+393331234567").Build();

        Assert.Equal(expected, address.ToPartitionKey());
    }

    [Fact]
    public void HistoryHierarchicalKeyUsesAnchorThenInternalConversationId()
    {
        var address = new HistoryStorageAddress(StorageScope.Create("lookup"), "server-history");
        var expected = new Microsoft.Azure.Cosmos.PartitionKeyBuilder().Add(address.ScopeKey).Add("server-history").Build();

        Assert.Equal(expected, address.ToPartitionKey());
    }

    [Fact]
    public void AnonymousDocumentScopeMustMatchItsOwnLookupId()
    {
        Assert.Throws<ArgumentException>(() => new SessionStorageAddress("agent", StorageScope.Create("old"), "new"));
    }

    [Fact]
    public void ExactDocumentIdByteLimitIsAccepted()
    {
        Assert.Equal(1023, SessionStorageAddress.Create(new string('a', 1023), "id").DocumentId.Length);
    }

    [Theory]
    [InlineData("a", 1024)]
    [InlineData("/", 342)]
    public void OversizedEncodedDocumentIdsAreRejected(string component, int count)
    {
        Assert.Throws<ArgumentException>(() => SessionStorageAddress.Create(string.Concat(Enumerable.Repeat(component, count)), "id"));
    }

#pragma warning disable MAAI001 // Testing the actual installed framework key contract.
    [Fact]
    public void FrameworkKeyConsumesEveryPartition()
    {
        var partitions = new Dictionary<string, string> { ["isolation"] = "owner", ["region"] = "it" };
        var key = new Microsoft.Agents.AI.AgentSessionStoreKey("lookup", partitions);

        Assert.Equal(SessionStorageAddress.Create("agent", "lookup", partitions), SessionStorageAddress.Create("agent", key));
    }
#pragma warning restore MAAI001
}
