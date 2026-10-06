using SharedServices;

namespace SharedServices.Tests;

public class StorageScopeTests
{
    [Theory]
    [InlineData("4eaa4fbe-6bd3-41c4-b85b-22dd8db0c2c1")]
    [InlineData("+39 333 1234567")]
    [InlineData("resp_:|/=?#\\")]
    [InlineData("Città 東京 🏙️")]
    public void AnonymousScopeRetainsRawLookupId(string lookupId)
    {
        var scope = StorageScope.Create(lookupId);
        using var json = System.Text.Json.JsonDocument.Parse(scope);

        Assert.Equal(lookupId, json.RootElement[1].GetString());
    }

    [Fact]
    public void EveryPartitionParticipatesInOrdinalOrder()
    {
        var partitions = new Dictionary<string, string> { ["z"] = "last", ["isolation"] = "owner", ["A"] = "first" };

        var scope = StorageScope.Create("lookup", partitions);

        Assert.Equal("[\"partitions\",[[\"A\",\"first\"],[\"isolation\",\"owner\"],[\"z\",\"last\"]]]", scope);
    }

    [Fact]
    public void InsertionOrderAndLookupAliasesDoNotAffectPartitionScope()
    {
        var first = StorageScope.Create("resp_1", new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });
        var second = StorageScope.Create("resp_2", new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnonymousCannotImpersonatePartitionScope()
    {
        var isolated = StorageScope.Create("id", new Dictionary<string, string> { ["isolation"] = "owner" });

        Assert.NotEqual(isolated, StorageScope.Create(isolated));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("line\nbreak")]
    public void InvalidLookupKeysAreRejected(string lookupId)
    {
        Assert.Throws<ArgumentException>(() => StorageScope.Create(lookupId));
    }

    [Fact]
    public void UnpairedUnicodeSurrogateIsRejected()
    {
        var invalidUnicode = new string((char)0xd800, 1);

        Assert.Throws<ArgumentException>(() => StorageScope.Create(invalidUnicode));
    }

    [Fact]
    public void ExactCanonicalScopeByteLimitIsAccepted()
    {
        Assert.Equal(2048, System.Text.Encoding.UTF8.GetByteCount(StorageScope.Create(new string('x', 2032))));
    }

    [Fact]
    public void UnicodeScopeLimitUsesUtf8BytesNotCharacters()
    {
        Assert.Throws<ArgumentException>(() => StorageScope.Create(new string('\u00e9', 1017)));
    }

    [Fact]
    public void CanonicalScopeByteLimitIsEnforcedWithoutTruncation()
    {
        Assert.Throws<ArgumentException>(() => StorageScope.Create(new string('x', 2048)));
    }

    [Theory]
    [InlineData("[ \"anonymous\", \"id\" ]")]
    [InlineData("[\"partitions\",[]]")]
    [InlineData("[\"partitions\",[[\"b\",\"2\"],[\"a\",\"1\"]]]")]
    [InlineData("[\"partitions\",[[\"a\",\"1\"],[\"a\",\"2\"]]]")]
    [InlineData("[\"unknown\",\"id\"]")]
    [InlineData("{}")]
    public void NoncanonicalStoredScopesAreRejected(string scope)
    {
        Assert.Throws<ArgumentException>(() => StorageScope.Validate(scope));
    }
}
