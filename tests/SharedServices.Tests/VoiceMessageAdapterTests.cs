using System.Text.Json;
using Azure.AI.VoiceLive;
using Microsoft.Extensions.AI;
using VoiceOrchestratorAgent;

namespace SharedServices.Tests;

public class VoiceMessageAdapterTests
{
    [Fact]
    public void UserReplayIsANativeUserItem()
    {
        var message = VoiceMessageAdapter.ToChatMessage(
            new(DateTimeOffset.UtcNow, "user", "text", "hello"));

        Assert.IsType<UserMessageItem>(Assert.Single(VoiceMessageAdapter.ToVoiceLiveItems(message)));
    }

    [Fact]
    public void AssistantReplayIsANativeAssistantItem()
    {
        var message = VoiceMessageAdapter.ToChatMessage(
            new(DateTimeOffset.UtcNow, "assistant", "text", "hello"));

        Assert.IsType<AssistantMessageItem>(Assert.Single(VoiceMessageAdapter.ToVoiceLiveItems(message)));
    }

    [Fact]
    public async Task FunctionCallReplayRetainsTheCallIdAfterPersistence()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address),
            [new(DateTimeOffset.UtcNow, "assistant", "tool_call", ToolCallId: "call-1",
                ToolName: "weather", ToolArguments: """{"city":"Zürich"}""")]);
        var resumed = await fixture.Store.LoadAsync(address);

        var item = Assert.IsType<FunctionCallItem>(Assert.Single(
            VoiceMessageAdapter.ToVoiceLiveItems(Assert.Single(resumed.Messages))));

        Assert.Equal("call-1", item.CallId);
    }

    [Fact]
    public async Task FunctionCallReplayRetainsTheNameAfterPersistence()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address),
            [new(DateTimeOffset.UtcNow, "assistant", "tool_call", ToolCallId: "call-1",
                ToolName: "weather", ToolArguments: """{"city":"Zürich"}""")]);
        var resumed = await fixture.Store.LoadAsync(address);

        var item = Assert.IsType<FunctionCallItem>(Assert.Single(
            VoiceMessageAdapter.ToVoiceLiveItems(Assert.Single(resumed.Messages))));

        Assert.Equal("weather", item.Name);
    }

    [Fact]
    public async Task FunctionCallReplayRetainsUnicodeArgumentsAfterPersistence()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address),
            [new(DateTimeOffset.UtcNow, "assistant", "tool_call", ToolCallId: "call-1",
                ToolName: "weather", ToolArguments: """{"city":"Zürich"}""")]);
        var resumed = await fixture.Store.LoadAsync(address);
        var item = Assert.IsType<FunctionCallItem>(Assert.Single(
            VoiceMessageAdapter.ToVoiceLiveItems(Assert.Single(resumed.Messages))));

        using var arguments = JsonDocument.Parse(item.Arguments);

        Assert.Equal("Zürich", arguments.RootElement.GetProperty("city").GetString());
    }

    [Fact]
    public async Task ToolOutputReplayRetainsUnicodeTextAndNativeCallIdentity()
    {
        var fixture = new VoiceStoreFixture();
        var address = fixture.Address("lookup");
        await fixture.Store.SaveAsync(await fixture.Store.LoadAsync(address),
            [new(DateTimeOffset.UtcNow, "tool", "tool_call_response", ToolCallId: "call-1",
                ToolName: "weather", ToolResult: "15°C 東京")]);
        var resumed = await fixture.Store.LoadAsync(address);

        var item = Assert.IsType<FunctionCallOutputItem>(Assert.Single(
            VoiceMessageAdapter.ToVoiceLiveItems(Assert.Single(resumed.Messages))));

        Assert.Equal(("call-1", "15°C 東京"), (item.CallId, item.Output));
    }

    [Fact]
    public void ToolNameIsPreservedForToolResults()
    {
        var message = VoiceMessageAdapter.ToChatMessage(new(DateTimeOffset.UtcNow, "tool",
            "tool_call_response", ToolCallId: "call", ToolName: "weather", ToolResult: "sunny"));

        Assert.Equal("weather", message.AdditionalProperties!["voiceToolName"]);
    }

    [Fact]
    public void SystemTextIsNotSilentlyInjectedAsUserHistory()
    {
        var message = new ChatMessage(ChatRole.System, "not voice history");

        Assert.Throws<InvalidOperationException>(() => VoiceMessageAdapter.ToVoiceLiveItems(message).ToArray());
    }

    [Fact]
    public void FunctionArgumentsMustBeAnObject()
    {
        var message = new ConversationMessage(DateTimeOffset.UtcNow, "assistant", "tool_call",
            ToolCallId: "call", ToolName: "weather", ToolArguments: "[]");

        Assert.Throws<JsonException>(() => VoiceMessageAdapter.ToChatMessage(message));
    }
}
