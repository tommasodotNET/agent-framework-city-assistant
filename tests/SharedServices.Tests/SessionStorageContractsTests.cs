using System.Text.Json;
using Microsoft.Azure.Cosmos;
using SharedServices;

namespace SharedServices.Tests;

public class SessionStorageContractsTests
{
    [Fact]
    public void VersionTrackerDoesNotLeakAcrossWorkingCopies()
    {
        var address = SessionStorageAddress.Create("agent", "id");
        var first = new SessionWriteTracker();
        first.Record(SessionWriteCondition.IfMatch(address, "\"one\""));
        var second = new SessionWriteTracker();

        Assert.True(second.ForAddress(address).IsCreateOnly);
    }

    [Fact]
    public void VersionTrackerDoesNotReuseAnAliasToken()
    {
        var tracker = new SessionWriteTracker();
        tracker.Record(SessionWriteCondition.IfMatch(SessionStorageAddress.Create("agent", "resp_1"), "\"one\""));

        Assert.True(tracker.ForAddress(SessionStorageAddress.Create("agent", "resp_2")).IsCreateOnly);
    }

    [Fact]
    public void VersionTrackerDoesNotReuseAnotherAgentToken()
    {
        var tracker = new SessionWriteTracker();
        tracker.Record(SessionWriteCondition.IfMatch(SessionStorageAddress.Create("first", "id"), "\"one\""));

        Assert.True(tracker.ForAddress(SessionStorageAddress.Create("second", "id")).IsCreateOnly);
    }

    [Fact]
    public void VersionTrackerRetainsDistinctAliasEtags()
    {
        var tracker = new SessionWriteTracker();
        var first = SessionStorageAddress.Create("agent", "resp_1");
        var second = SessionStorageAddress.Create("agent", "resp_2");
        tracker.Record(SessionWriteCondition.IfMatch(first, "\"one\""));
        tracker.Record(SessionWriteCondition.IfMatch(second, "\"two\""));

        Assert.Equal("\"one\"", tracker.ForAddress(first).ETag);
    }

    [Fact]
    public void ReadResultRejectsVersionForDifferentFullAddress()
    {
        var address = SessionStorageAddress.Create("agent", "id");
        var document = SessionDocument.Create(address, JsonSerializer.SerializeToElement(new { state = "ok" }), DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => new SessionReadResult(document,
            SessionWriteCondition.IfMatch(SessionStorageAddress.Create("other", "id"), "\"etag\"")));
    }

    [Fact]
    public void ReadResultRetainsAddressBoundResponseVersion()
    {
        var address = SessionStorageAddress.Create("agent", "id");
        var document = SessionDocument.Create(address, JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UnixEpoch);
        var read = new SessionReadResult(document, SessionWriteCondition.IfMatch(address, "\"etag\""));

        Assert.Equal("\"etag\"", read.Version.ETag);
    }

    [Fact]
    public void ReadResultRequiresActualResponseVersion()
    {
        var address = SessionStorageAddress.Create("agent", "id");
        var document = SessionDocument.Create(address, JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UnixEpoch);

        Assert.Throws<ArgumentException>(() => new SessionReadResult(document, SessionWriteCondition.CreateOnly(address)));
    }

    [Fact]
    public void SnapshotIsStoredAsJsonObjectAndPreservesUnknownFields()
    {
        var document = SessionDocument.Create(SessionStorageAddress.Create("agent", "id"),
            JsonSerializer.SerializeToElement(new { unknownFutureField = new { number = 42 } }), DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(document.SerializeToUtf8Bytes());

        Assert.Equal(42, json.RootElement.GetProperty("serializedSession").GetProperty("unknownFutureField").GetProperty("number").GetInt32());
    }

    [Fact]
    public void SessionEnvelopeDoesNotDuplicateHistoryOrIncludeEtags()
    {
        var document = SessionDocument.Create(SessionStorageAddress.Create("agent", "id"),
            JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(document.SerializeToUtf8Bytes());

        Assert.Equal(["schemaVersion", "id", "agentId", "scopeKey", "sessionId", "serializedSession", "lastUpdated", "ttl"],
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void SnapshotRemainsUsableAfterOriginalJsonDocumentDisposal()
    {
        SessionDocument snapshot;
        using (var source = JsonDocument.Parse("{\"future\":42}"))
        {
            snapshot = SessionDocument.Create(SessionStorageAddress.Create("agent", "id"), source.RootElement, DateTimeOffset.UtcNow);
        }

        Assert.Equal(42, snapshot.SerializedSession.GetProperty("future").GetInt32());
    }

    [Fact]
    public void SchemaV2EnvelopeRoundTrips()
    {
        var original = SessionDocument.Create(SessionStorageAddress.Create("agent", "id"),
            JsonSerializer.SerializeToElement(new { future = 42 }), DateTimeOffset.UtcNow);
        var restored = JsonSerializer.Deserialize<SessionDocument>(original.SerializeToUtf8Bytes());

        Assert.Equal(original.Id, restored?.Id);
    }

    [Fact]
    public void EscapedJsonSnapshotIsRejected()
    {
        Assert.Throws<ArgumentException>(() => SessionDocument.Create(SessionStorageAddress.Create("agent", "id"),
            JsonSerializer.SerializeToElement("{}"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExactFullDocumentBudgetIsAccepted()
    {
        var error = Record.Exception(() => StorageSchema.ValidateSessionDocumentSize(new byte[2_000_000]));

        Assert.Null(error);
    }

    [Fact]
    public void OneByteOverDocumentBudgetIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => StorageSchema.ValidateSessionDocumentSize(new byte[2_000_001]));
    }

    [Fact]
    public void EnvelopeOverheadParticipatesInSnapshotBudget()
    {
        var document = SessionDocument.Create(SessionStorageAddress.Create("agent", "id"),
            JsonSerializer.SerializeToElement(new { content = new string('a', 2_000_000) }), DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => document.SerializeToUtf8Bytes());
    }

    [Fact]
    public void ExactSerializedEnvelopeBudgetIsAccepted()
    {
        var address = SessionStorageAddress.Create("agent", "id");
        var timestamp = DateTimeOffset.UnixEpoch;
        var empty = SessionDocument.Create(address, JsonSerializer.SerializeToElement(new { content = "" }), timestamp);
        var overhead = empty.SerializeToUtf8Bytes().Length;
        var document = SessionDocument.Create(address,
            JsonSerializer.SerializeToElement(new { content = new string('a', 2_000_000 - overhead) }), timestamp);

        Assert.Equal(2_000_000, document.SerializeToUtf8Bytes().Length);
    }

    [Fact]
    public void OldDocumentSchemaIsRejected()
    {
        var address = SessionStorageAddress.Create("agent", "id");

        Assert.Throws<InvalidOperationException>(() => new SessionDocument(1, address.DocumentId, address.AgentId,
            address.ScopeKey, address.SessionId, JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UnixEpoch, -1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void InvalidDocumentTtlIsRejected(int ttl)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionDocument.Create(SessionStorageAddress.Create("agent", "id"),
            JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UnixEpoch, ttl));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HierarchicalSchemaWithPerDocumentTtlIsAccepted(bool sessions)
    {
        var properties = new ContainerProperties("container",
            sessions ? new[] { "/scopeKey", "/sessionId" } : new[] { "/scopeKey", "/conversationId" })
        {
            DefaultTimeToLive = -1
        };

        Assert.Null(Record.Exception(() => StorageSchema.ValidateContainer(properties, sessions)));
    }

    [Fact]
    public void LegacyContainerSchemaRequiresManualRecreation()
    {
        var properties = new ContainerProperties("sessions", "/conversationId") { DefaultTimeToLive = -1 };

        Assert.Throws<InvalidOperationException>(() => StorageSchema.ValidateContainer(properties, true));
    }

    [Fact]
    public void ReversedHierarchicalPathsAreRejected()
    {
        var properties = new ContainerProperties("sessions", new[] { "/sessionId", "/scopeKey" }) { DefaultTimeToLive = -1 };

        Assert.Throws<InvalidOperationException>(() => StorageSchema.ValidateContainer(properties, true));
    }

    [Fact]
    public void ContainerWideExpiryIsRejected()
    {
        var properties = new ContainerProperties("sessions", new[] { "/scopeKey", "/sessionId" }) { DefaultTimeToLive = 86400 };

        Assert.Throws<InvalidOperationException>(() => StorageSchema.ValidateContainer(properties, true));
    }
}
