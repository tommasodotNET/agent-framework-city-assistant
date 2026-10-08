using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharedServices;

#pragma warning disable MAAI001 // Exercise the installed experimental hosting contracts.

namespace SharedServices.Tests;

/// <summary>
/// Characterizes the real MAF protocol adapters independently of Cosmos transport doubles.
/// Repository tests separately cover Cosmos schema, transactional operations and concurrency.
/// </summary>
public sealed class ProtocolPersistenceTests
{
    [Fact]
    public async Task A2A_preserves_the_opaque_context_id_across_turns()
    {
        await using var host = await ProtocolHost.StartAsync();
        const string contextId = "+39/external-context?one";

        await host.SendA2AAsync(contextId, "first");
        await host.Store.WaitForWritesAsync(1);
        await host.SendA2AAsync(contextId, "second");
        await host.Store.WaitForWritesAsync(2);

        Assert.All(host.Store.Reads, address => Assert.Equal(contextId, address.SessionId));
        Assert.All(host.Store.Writes, write => Assert.Equal(contextId, write.Address.SessionId));
        Assert.All(host.Store.Writes, write => Assert.Equal(ProtocolHost.AgentName, write.Address.AgentId));
        Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "first");
    }

    [Fact]
    public async Task Responses_previous_response_id_reads_old_alias_and_writes_new_alias()
    {
        await using var host = await ProtocolHost.StartAsync();
        var first = await host.CreateResponseAsync("first");
        var firstId = first.GetProperty("id").GetString()!;
        var firstWrite = Assert.Single(host.Store.Writes);

        var second = await host.CreateResponseAsync("second", previousResponseId: firstId);
        var secondId = second.GetProperty("id").GetString()!;
        var writes = host.Store.Writes.ToArray();

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, host.Store.Reads.Last().SessionId);
        Assert.Equal(secondId, writes[1].Address.SessionId);
        Assert.NotEqual(firstWrite.Address.ScopeKey, writes[1].Address.ScopeKey);
        Assert.Equal(firstWrite.History, writes[1].History);
        Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "first");
    }

    [Fact]
    public async Task Responses_conversation_saves_the_same_working_copy_under_two_addresses()
    {
        await using var host = await ProtocolHost.StartAsync();
        var conversationId = await host.CreateConversationAsync();
        var response = await host.CreateResponseAsync("hello", conversationId: conversationId);
        var responseId = response.GetProperty("id").GetString()!;
        var writes = host.Store.Writes.ToArray();

        Assert.Equal(conversationId, host.Store.Reads.First().SessionId);
        Assert.Equal(2, writes.Length);
        Assert.Equal(responseId, writes[0].Address.SessionId);
        Assert.Equal(conversationId, writes[1].Address.SessionId);
        Assert.Same(writes[0].Session, writes[1].Session);
        Assert.Equal(writes[0].History, writes[1].History);
    }

    [Fact]
    public async Task Responses_store_false_without_conversation_does_not_save_a_snapshot()
    {
        await using var host = await ProtocolHost.StartAsync();
        var response = await host.CreateResponseAsync("hello", store: false);

        Assert.Equal("completed", response.GetProperty("status").GetString());
        Assert.Empty(host.Store.Writes);
    }

    [Fact]
    public async Task Responses_store_false_still_advances_an_explicit_conversation()
    {
        await using var host = await ProtocolHost.StartAsync();
        var conversationId = await host.CreateConversationAsync();
        await host.CreateResponseAsync("hello", conversationId: conversationId, store: false);

        Assert.Equal(conversationId, Assert.Single(host.Store.Writes).Address.SessionId);
    }

    [Fact]
    public async Task Responses_stream_completion_follows_snapshot_save()
    {
        await using var host = await ProtocolHost.StartAsync();
        using var response = await host.PostResponseAsync("hello", stream: true);
        response.EnsureSuccessStatusCode();
        var events = await response.Content.ReadAsStringAsync();

        Assert.Contains("response.completed", events);
        Assert.Single(host.Store.Writes);
    }

    [Fact]
    public async Task Responses_does_not_report_completion_when_snapshot_save_fails()
    {
        await using var host = await ProtocolHost.StartAsync();
        host.Store.RejectWrites = true;
        using var response = await host.PostResponseAsync("hello", stream: true);
        var events = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("response.completed", events);
        Assert.Contains("response.failed", events);
        Assert.Empty(host.Store.Writes);
    }

    [Fact]
    public async Task A2A_same_context_is_partitioned_by_the_trusted_test_identity()
    {
        await using var host = await ProtocolHost.StartAsync(withIsolation: true);
        await host.SendA2AAsync("same-context", "alice-first", "alice");
        await host.Store.WaitForWritesAsync(1);
        await host.SendA2AAsync("same-context", "bob-first", "bob");
        await host.Store.WaitForWritesAsync(2);

        var writes = host.Store.Writes.ToArray();
        Assert.NotEqual(writes[0].Address.ScopeKey, writes[1].Address.ScopeKey);
        Assert.Equal(CallerScope("alice"), writes[0].Address.ScopeKey);
        Assert.Equal(CallerScope("bob"), writes[1].Address.ScopeKey);
        Assert.DoesNotContain(host.Model.Inputs.Last(), message => message.Text == "alice-first");
    }

    [Fact]
    public async Task Responses_background_save_uses_identity_captured_before_request_ended()
    {
        await using var host = await ProtocolHost.StartAsync(withIsolation: true);
        host.Model.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var response = await host.PostResponseAsync("hello", caller: "alice", background: true);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual("completed", body.GetProperty("status").GetString());

        host.Model.Pause.SetResult();
        await host.Store.WaitForWritesAsync(1);

        Assert.Equal(CallerScope("alice"), Assert.Single(host.Store.Writes).Address.ScopeKey);
    }

    [Fact]
    public async Task Responses_resources_are_not_visible_to_another_caller()
    {
        await using var host = await ProtocolHost.StartAsync(withIsolation: true);
        var first = await host.CreateResponseAsync("hello", caller: "alice");
        var responseId = first.GetProperty("id").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/responses/{responseId}");
        request.Headers.Add("X-Test-Identity", "bob");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Responses_requires_an_identity_when_a_provider_is_registered()
    {
        await using var host = await ProtocolHost.StartAsync(withIsolation: true);
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await host.PostResponseAsync("hello");
            Assert.False(response.IsSuccessStatusCode);
        });

        // TestServer can surface the framework exception instead of rendering an HTTP 500.
        if (failure is not null)
        {
            var exception = Assert.IsType<InvalidOperationException>(failure);
            Assert.Contains("isolation key", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(host.Store.Writes);
        Assert.Empty(host.Model.Inputs);
    }

    [Fact]
    public async Task Restart_retains_custom_snapshots_but_not_OpenAI_protocol_resources()
    {
        var snapshots = new ConcurrentDictionary<SessionStorageAddress, JsonElement>();
        string responseId;
        string conversationId;
        await using (var firstHost = await ProtocolHost.StartAsync(snapshots: snapshots))
        {
            conversationId = await firstHost.CreateConversationAsync();
            var response = await firstHost.CreateResponseAsync("remember me", conversationId: conversationId);
            responseId = response.GetProperty("id").GetString()!;
        }

        await using var secondHost = await ProtocolHost.StartAsync(snapshots: snapshots);
        using var get = await secondHost.Client.GetAsync($"/v1/responses/{responseId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var conversationResponse = await secondHost.PostResponseAsync("next", conversationId: conversationId);
        Assert.Equal(HttpStatusCode.NotFound, conversationResponse.StatusCode);

        var continued = await secondHost.CreateResponseAsync("next", previousResponseId: responseId);
        Assert.Equal("completed", continued.GetProperty("status").GetString());
        Assert.Contains(secondHost.Model.Inputs.Last(), message => message.Text == "remember me");
    }

    [Fact]
    public async Task Real_Cosmos_adapter_creates_response_aliases_and_conditionally_replaces_conversation()
    {
        var cosmos = new SessionCosmosSdkFixture();
        await using var host = await ProtocolHost.StartAsync(cosmos: cosmos);
        var conversationId = await host.CreateConversationAsync();
        var first = await host.CreateResponseAsync("first", conversationId: conversationId);
        var second = await host.CreateResponseAsync("second", conversationId: conversationId);
        var writes = cosmos.Requests.Where(request => request.Operation != "read").ToArray();

        Assert.Equal(["create", "create", "create", "replace"], writes.Select(write => write.Operation));
        Assert.Null(writes[0].ETag);
        Assert.NotNull(writes[3].ETag);
        var documents = writes.Select(write => JsonSerializer.Deserialize<SessionDocument>(write.Body)!).ToArray();
        Assert.Equal(first.GetProperty("id").GetString(), documents[0].SessionId);
        Assert.Equal(conversationId, documents[1].SessionId);
        Assert.Equal(second.GetProperty("id").GetString(), documents[2].SessionId);
        Assert.Equal(conversationId, documents[3].SessionId);
        Assert.Equal(writes[1].Partition, writes[3].Partition);
        Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "first");
    }

    [Fact]
    public async Task Real_Cosmos_adapter_preserves_history_anchor_across_anonymous_response_aliases()
    {
        var cosmos = new SessionCosmosSdkFixture();
        string firstId;
        await using (var firstHost = await ProtocolHost.StartAsync(cosmos: cosmos))
        {
            firstId = (await firstHost.CreateResponseAsync("first")).GetProperty("id").GetString()!;
        }
        await using var nextHost = await ProtocolHost.StartAsync(cosmos: cosmos);
        var next = await nextHost.CreateResponseAsync("second", previousResponseId: firstId);
        var nextId = next.GetProperty("id").GetString()!;
        var source = await cosmos.Repository.ReadAsync(SessionStorageAddress.Create(ProtocolHost.AgentName, firstId));
        var target = await cosmos.Repository.ReadAsync(SessionStorageAddress.Create(ProtocolHost.AgentName, nextId));

        Assert.NotNull(source);
        Assert.NotNull(target);
        Assert.NotEqual(source.Document.ScopeKey, target.Document.ScopeKey);
        var sourceState = source.Document.SerializedSession.GetProperty("stateBag")
            .GetProperty(SessionPersistenceState.StateKey).Deserialize<SessionPersistenceContext>();
        var targetState = target.Document.SerializedSession.GetProperty("stateBag")
            .GetProperty(SessionPersistenceState.StateKey).Deserialize<SessionPersistenceContext>();
        Assert.Equal(sourceState, targetState);
        Assert.Contains(nextHost.Model.Inputs.Last(), message => message.Text == "first");
    }

    [Fact]
    public async Task Real_Cosmos_adapter_store_false_writes_only_the_conversation_head()
    {
        var cosmos = new SessionCosmosSdkFixture();
        await using var host = await ProtocolHost.StartAsync(cosmos: cosmos);
        var conversationId = await host.CreateConversationAsync();
        await host.CreateResponseAsync("first", conversationId: conversationId, store: false);
        await host.CreateResponseAsync("second", conversationId: conversationId, store: false);
        var writes = cosmos.Requests.Where(request => request.Operation != "read").ToArray();

        Assert.Equal(["create", "replace"], writes.Select(write => write.Operation));
        Assert.All(writes, write =>
            Assert.Equal(conversationId, JsonSerializer.Deserialize<SessionDocument>(write.Body)!.SessionId));
    }

    [Fact]
    public async Task External_history_continues_linearly_and_rejects_an_old_response_before_model_execution()
    {
        var cosmos = new SessionCosmosSdkFixture();
        var history = new HistoryCosmosFixture();
        await using var host = await ProtocolHost.StartAsync(cosmos: cosmos, history: history);
        var first = await host.CreateResponseAsync("first");
        var firstId = first.GetProperty("id").GetString()!;
        await host.CreateResponseAsync("second", previousResponseId: firstId);

        Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "first");
        Assert.Equal(4, history.Documents.Count(document => document.GetProperty("type").GetString() == "ChatMessage"));
        using var stale = await host.PostResponseAsync("branch", previousResponseId: firstId);
        Assert.Equal(HttpStatusCode.InternalServerError, stale.StatusCode);
        Assert.Equal(2, host.Model.Inputs.Count);
        Assert.Equal(4, history.Documents.Count(document => document.GetProperty("type").GetString() == "ChatMessage"));
    }

    [Fact]
    public async Task Failure_after_history_append_does_not_silently_reconcile_an_old_snapshot()
    {
        var cosmos = new SessionCosmosSdkFixture();
        var history = new HistoryCosmosFixture();
        await using var host = await ProtocolHost.StartAsync(cosmos: cosmos, history: history);
        var firstId = (await host.CreateResponseAsync("first")).GetProperty("id").GetString()!;
        cosmos.WriteFailure = HttpStatusCode.ServiceUnavailable;

        using var failedSave = await host.PostResponseAsync("second", previousResponseId: firstId);
        Assert.Equal(HttpStatusCode.InternalServerError, failedSave.StatusCode);
        Assert.Equal(4, history.Documents.Count(document => document.GetProperty("type").GetString() == "ChatMessage"));

        cosmos.WriteFailure = null;
        using var stale = await host.PostResponseAsync("retry", previousResponseId: firstId);
        Assert.Equal(HttpStatusCode.InternalServerError, stale.StatusCode);
        Assert.Equal(2, host.Model.Inputs.Count);
    }

    [Fact]
    public async Task Real_history_is_isolated_when_two_callers_choose_the_same_A2A_context()
    {
        var cosmos = new SessionCosmosSdkFixture();
        var history = new HistoryCosmosFixture();
        await using var host = await ProtocolHost.StartAsync(withIsolation: true, cosmos: cosmos, history: history);
        await host.SendA2AAsync("shared-context", "alice-private", "alice");
        await WaitForRepositoryWritesAsync(cosmos, 1);
        await host.SendA2AAsync("shared-context", "bob-private", "bob");
        await WaitForRepositoryWritesAsync(cosmos, 2);
        await host.SendA2AAsync("shared-context", "alice-next", "alice");
        await WaitForRepositoryWritesAsync(cosmos, 3);

        Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "alice-private");
        Assert.DoesNotContain(host.Model.Inputs.Last(), message => message.Text == "bob-private");
        var heads = history.Documents.Where(document => document.GetProperty("type").GetString() == "HistoryHead").ToArray();
        Assert.Equal(2, heads.Length);
        Assert.NotEqual(heads[0].GetProperty("scopeKey").GetString(), heads[1].GetProperty("scopeKey").GetString());
    }

    private static async Task WaitForRepositoryWritesAsync(SessionCosmosSdkFixture cosmos, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (cosmos.CompletedWrites < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreground_compaction_preserves_protocol_continuation_and_original_history(bool responses)
    {
        var cosmos = new SessionCosmosSdkFixture();
        var history = new HistoryCosmosFixture();
        string? previous = null;
        const string contextId = "foreground-session";
        string? originalConversation = null;
        await using (var host = await ProtocolHost.StartAsync(cosmos: cosmos, history: history,
            historyProviderFactory: CompactingHistory))
        {
            for (var turn = 1; turn <= 4; turn++)
            {
                if (responses)
                {
                    var response = await host.CreateResponseAsync($"question-{turn}", previousResponseId: previous);
                    previous = response.GetProperty("id").GetString()!;
                }
                else
                {
                    await host.SendA2AAsync(contextId, $"question-{turn}");
                    await WaitForRepositoryWritesAsync(cosmos, turn);
                }
                if (turn == 1)
                {
                    originalConversation = history.Documents.Single(document =>
                        document.GetProperty("type").GetString() == "HistoryHead").GetProperty("conversationId").GetString();
                }
            }

            Assert.DoesNotContain(host.Model.Inputs.Last(), message => message.Text == "question-1");
            Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "question-2");
            Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "question-4");
            Assert.Equal(6, history.Documents.Count(document =>
                document.GetProperty("type").GetString() == "ChatMessage"
                && document.GetProperty("conversationId").GetString() == originalConversation));
            Assert.Equal(2, history.Documents.Count(document => document.GetProperty("type").GetString() == "HistoryHead"));
        }

        // A new application host has no reference to the first host's working AgentSession.
        await using var restarted = await ProtocolHost.StartAsync(cosmos: cosmos, history: history,
            historyProviderFactory: CompactingHistory);
        if (responses)
        {
            await restarted.CreateResponseAsync("question-5", previousResponseId: previous);
        }
        else
        {
            await restarted.SendA2AAsync(contextId, "question-5");
            await WaitForRepositoryWritesAsync(cosmos, 5);
        }
        Assert.Contains(restarted.Model.Inputs.Last(), message => message.Text == "question-4");
        Assert.Contains(restarted.Model.Inputs.Last(), message => message.Text == "question-5");
        Assert.DoesNotContain(restarted.Model.Inputs.Last(), message => message.Text == "question-1");
        Assert.Equal(3, history.Documents.Count(document => document.GetProperty("type").GetString() == "HistoryHead"));
    }

    [Fact]
    public async Task Foreground_Response_conversation_aliases_save_rotated_state_without_extra_checkpoints()
    {
        var cosmos = new SessionCosmosSdkFixture();
        var history = new HistoryCosmosFixture();
        await using var host = await ProtocolHost.StartAsync(cosmos: cosmos, history: history,
            historyProviderFactory: CompactingHistory);
        var conversationId = await host.CreateConversationAsync();
        for (var turn = 1; turn <= 4; turn++)
            await host.CreateResponseAsync($"turn-{turn}", conversationId: conversationId, store: false);

        var writes = cosmos.Requests.Where(request => request.Operation != "read").ToArray();
        Assert.Equal(4, writes.Length);
        Assert.All(writes, write => Assert.Equal(conversationId,
            JsonSerializer.Deserialize<SessionDocument>(write.Body)!.SessionId));
        Assert.Equal(2, history.Documents.Count(document => document.GetProperty("type").GetString() == "HistoryHead"));
        Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "turn-3");
        Assert.DoesNotContain(host.Model.Inputs.Last(), message => message.Text == "turn-1");
    }

    [Fact]
    public async Task Failed_checkpoint_after_deferred_rotation_recovers_exact_target_but_not_its_later_append()
    {
        var cosmos = new SessionCosmosSdkFixture();
        var history = new HistoryCosmosFixture();
        await using var host = await ProtocolHost.StartAsync(cosmos: cosmos, history: history,
            historyProviderFactory: CompactingHistory);
        string? previous = null;
        for (var turn = 1; turn <= 3; turn++)
            previous = (await host.CreateResponseAsync($"turn-{turn}", previousResponseId: previous))
                .GetProperty("id").GetString();

        cosmos.WriteFailure = HttpStatusCode.ServiceUnavailable;
        using var failed = await host.PostResponseAsync("turn-4", previousResponseId: previous);
        Assert.False(failed.IsSuccessStatusCode);
        Assert.Equal(2, history.Documents.Count(document => document.GetProperty("type").GetString() == "HistoryHead"));
        cosmos.WriteFailure = null;
        using var recovered = await host.PostResponseAsync("retry", previousResponseId: previous);
        Assert.True(recovered.IsSuccessStatusCode);
        Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "turn-4");
        Assert.DoesNotContain(host.Model.Inputs.Last(), message => message.Text == "turn-1");

        using var stale = await host.PostResponseAsync("stale retry", previousResponseId: previous);
        Assert.False(stale.IsSuccessStatusCode);
        Assert.Equal(5, host.Model.Inputs.Count);
    }

    [Fact]
    public async Task Foreground_Response_model_failure_leaves_source_and_checkpoint_intact_for_restart()
    {
        var cosmos = new SessionCosmosSdkFixture();
        var history = new HistoryCosmosFixture();
        string? previous = null;
        await using (var host = await ProtocolHost.StartAsync(cosmos: cosmos, history: history,
            historyProviderFactory: CompactingHistory))
        {
            for (var turn = 1; turn <= 3; turn++)
                previous = (await host.CreateResponseAsync($"turn-{turn}", previousResponseId: previous))
                    .GetProperty("id").GetString();
            host.Model.Failure = new HttpRequestException("model unavailable");

            using var failed = await host.PostResponseAsync("failed turn", previousResponseId: previous);

            Assert.False(failed.IsSuccessStatusCode);
            Assert.Equal(3, history.Batches.Count);
            Assert.Equal(3, cosmos.CompletedWrites);
        }

        await using var restarted = await ProtocolHost.StartAsync(cosmos: cosmos, history: history,
            historyProviderFactory: CompactingHistory);
        await restarted.CreateResponseAsync("turn-4", previousResponseId: previous);
        Assert.DoesNotContain(restarted.Model.Inputs.Last(), message => message.Text == "failed turn");
        Assert.DoesNotContain(restarted.Model.Inputs.Last(), message => message.Text == "turn-1");
        Assert.Equal(2, history.Documents.Count(document => document.GetProperty("type").GetString() == "HistoryHead"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_ticket_survives_protocol_checkpoint_and_restart_before_save_rotation(bool responses)
    {
        var cosmos = new SessionCosmosSdkFixture();
        var history = new HistoryCosmosFixture();
        var jobs = new ProtocolBackgroundJobs();
        CosmosChatHistoryProvider Provider(CosmosChatMessageRepository repository) => new(repository,
            compactor: new ProtocolBackgroundCompactor(jobs),
            compactionOptions: new() { CompactorKey = "protocol-background", Mode = HistoryCompactionMode.Background });
        const string contextId = "background-context";
        string? previous = null;
        var firstQuestion = new string('x', 500);
        await using (var host = await ProtocolHost.StartAsync(cosmos: cosmos, history: history, historyProviderFactory: Provider))
        {
            if (responses)
            {
                previous = (await host.CreateResponseAsync(firstQuestion)).GetProperty("id").GetString();
                previous = (await host.CreateResponseAsync("second", previousResponseId: previous)).GetProperty("id").GetString();
            }
            else
            {
                await host.SendA2AAsync(contextId, firstQuestion);
                await WaitForRepositoryWritesAsync(cosmos, 1);
                await host.SendA2AAsync(contextId, "second");
                await WaitForRepositoryWritesAsync(cosmos, 2);
            }
            Assert.NotNull(jobs.Request);
            Assert.Single(history.Documents, document => document.GetProperty("type").GetString() == "HistoryHead");
        }

        jobs.Ready = true;
        await using var restarted = await ProtocolHost.StartAsync(cosmos: cosmos, history: history, historyProviderFactory: Provider);
        if (responses)
        {
            previous = (await restarted.CreateResponseAsync("third", previousResponseId: previous)).GetProperty("id").GetString();
            await restarted.CreateResponseAsync("fourth", previousResponseId: previous);
        }
        else
        {
            await restarted.SendA2AAsync(contextId, "third");
            await WaitForRepositoryWritesAsync(cosmos, 3);
            await restarted.SendA2AAsync(contextId, "fourth");
            await WaitForRepositoryWritesAsync(cosmos, 4);
        }

        Assert.Contains(restarted.Model.Inputs.First(), message => message.Text == firstQuestion);
        Assert.DoesNotContain(restarted.Model.Inputs.Last(), message => message.Text == firstQuestion);
        Assert.Contains(restarted.Model.Inputs.Last(), message => message.Text == "second");
        Assert.Contains(restarted.Model.Inputs.Last(), message => message.Text == "third");
        Assert.Contains(restarted.Model.Inputs.Last(), message => message.Text == "summary");
        Assert.Equal(2, history.Documents.Count(document => document.GetProperty("type").GetString() == "HistoryHead"));
    }

    [Fact]
    public async Task Background_conversation_aliases_keep_store_false_checkpoint_semantics()
    {
        var cosmos = new SessionCosmosSdkFixture();
        var history = new HistoryCosmosFixture();
        var jobs = new ProtocolBackgroundJobs();
        await using var host = await ProtocolHost.StartAsync(cosmos: cosmos, history: history,
            historyProviderFactory: repository => new(repository,
                compactor: new ProtocolBackgroundCompactor(jobs),
                compactionOptions: new() { CompactorKey = "background", Mode = HistoryCompactionMode.Background }));
        var conversationId = await host.CreateConversationAsync();
        await host.CreateResponseAsync(new string('x', 500), conversationId: conversationId, store: false);
        await host.CreateResponseAsync("second", conversationId: conversationId, store: false);
        jobs.Ready = true;
        await host.CreateResponseAsync("third", conversationId: conversationId, store: false);
        await host.CreateResponseAsync("fourth", conversationId: conversationId, store: false);

        var writes = cosmos.Requests.Where(request => request.Operation != "read").ToArray();
        Assert.Equal(4, writes.Length);
        Assert.All(writes, write => Assert.Equal(conversationId, JsonSerializer.Deserialize<SessionDocument>(write.Body)!.SessionId));
        Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "summary");
        Assert.Contains(host.Model.Inputs.Last(), message => message.Text == "second");
        Assert.Equal(2, history.Documents.Count(document => document.GetProperty("type").GetString() == "HistoryHead"));
    }

    private sealed class ProtocolBackgroundJobs
    {
        public HistoryCompactionRequest? Request { get; set; }
        public bool Ready { get; set; }
    }

    private sealed class ProtocolBackgroundCompactor(ProtocolBackgroundJobs jobs) : IHistoryCompactor
    {
        public IReadOnlySet<HistoryCompactionMode> SupportedModes { get; } =
            new HashSet<HistoryCompactionMode> { HistoryCompactionMode.Background };
        public Task<HistoryCompactionResult> CompactAsync(HistoryCompactionRequest request, CancellationToken cancellationToken = default)
        {
            if (jobs.Request is not null)
            {
                var size = HistoryCompactionValidation.Measure(request.Messages);
                return Task.FromResult(new HistoryCompactionResult(HistoryCompactionStatus.Unchanged, request.SourceBinding,
                    request.Messages, size, size));
            }
            jobs.Request = request;
            return Task.FromResult(HistoryCompactionResult.Pending(new("protocol-job", request.SourceBinding)));
        }
        public Task<HistoryCompactionResult> GetResultAsync(HistoryCompactionTicket ticket, CancellationToken cancellationToken = default)
        {
            if (jobs.Request is not { } request || ticket != new HistoryCompactionTicket("protocol-job", request.SourceBinding))
                throw new InvalidOperationException("The accepted job is no longer available.");
            ChatMessage[] summary = [new(ChatRole.User, "summary")];
            return Task.FromResult(jobs.Ready
                ? new HistoryCompactionResult(HistoryCompactionStatus.Completed, ticket.SourceBinding, summary,
                    HistoryCompactionValidation.Measure(request.Messages), HistoryCompactionValidation.Measure(summary))
                : HistoryCompactionResult.Pending(ticket));
        }
    }

    private static CosmosChatHistoryProvider CompactingHistory(CosmosChatMessageRepository repository) =>
        new(repository,
            compactor: new MafForegroundHistoryCompactor(new SlidingWindowCompactionStrategy(
                CompactionTriggers.TurnsExceed(2), minimumPreservedTurns: 2)),
            compactionOptions: new HistoryCompactionOptions
            {
                CompactorKey = "protocol-sliding",
                MaxHistoryUtf8Bytes = 64_000
            });

    private static string CallerScope(string caller) =>
        StorageScope.Create("ignored", new Dictionary<string, string> { ["isolation"] = caller });

    private sealed class ProtocolHost(WebApplication app, RecordingSessionStore store, DeterministicChatClient model)
        : IAsyncDisposable
    {
        public const string AgentName = "protocol-agent";
        public HttpClient Client { get; } = app.GetTestClient();
        public RecordingSessionStore Store { get; } = store;
        public DeterministicChatClient Model { get; } = model;

        public static async Task<ProtocolHost> StartAsync(bool withIsolation = false,
            ConcurrentDictionary<SessionStorageAddress, JsonElement>? snapshots = null,
            SessionCosmosSdkFixture? cosmos = null, HistoryCosmosFixture? history = null,
            Func<CosmosChatMessageRepository, CosmosChatHistoryProvider>? historyProviderFactory = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddOpenAIResponses();
            builder.Services.AddOpenAIConversations();
            builder.Services.AddHttpContextAccessor();
            if (withIsolation)
            {
                builder.Services.AddSingleton<AgentIsolationKeyProvider, TestIdentityProvider>();
            }

            var store = new RecordingSessionStore(snapshots ?? new());
            var model = new DeterministicChatClient();
            var agentBuilder = builder.AddAIAgent(AgentName, (_, key) =>
                new ChatClientAgent(model, new ChatClientAgentOptions
                {
                    Id = key,
                    Name = key,
                    ChatHistoryProvider = history is null ? null
                        : historyProviderFactory?.Invoke(history.CreateRepository())
                            ?? new CosmosChatHistoryProvider(history.CreateRepository())
                }));
            if (cosmos is null)
            {
                agentBuilder.WithSessionStore(store, withIsolation: withIsolation);
            }
            else
            {
                builder.Services.AddKeyedSingleton("sessions", cosmos.Container.Object);
                builder.Services.AddCosmosAgentSessionStore("sessions");
                agentBuilder.WithCosmosSessionStore();
            }
            agentBuilder.AddA2AServer();
            var app = builder.Build();
            // This is a trusted test-host fixture, not a production authentication scheme.
            app.Use(async (context, next) =>
            {
                var caller = context.Request.Headers["X-Test-Identity"].FirstOrDefault();
                if (caller is not null)
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, caller)], "TestHost"));
                }
                await next(context);
            });
            app.MapA2AHttpJson(AgentName, "/agenta2a");
            app.MapOpenAIResponses();
            app.MapOpenAIConversations();
            await app.StartAsync();
            return new(app, store, model);
        }

        public async Task SendA2AAsync(string contextId, string text, string? caller = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/agenta2a/message:send");
            request.Content = JsonContent.Create(new
            {
                message = new
                {
                    messageId = Guid.NewGuid().ToString(),
                    contextId,
                    role = "ROLE_USER",
                    parts = new[] { new { text } }
                }
            });
            if (caller is not null) request.Headers.Add("X-Test-Identity", caller);
            using var response = await Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
        }

        public async Task<string> CreateConversationAsync()
        {
            using var response = await Client.PostAsJsonAsync("/v1/conversations", new { });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("id").GetString()!;
        }

        public async Task<JsonElement> CreateResponseAsync(string text, string? previousResponseId = null,
            string? conversationId = null, bool? store = null, string? caller = null)
        {
            using var response = await PostResponseAsync(text, previousResponseId, conversationId, store, caller);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("completed", json.RootElement.GetProperty("status").GetString());
            return json.RootElement.Clone();
        }

        public Task<HttpResponseMessage> PostResponseAsync(string text, string? previousResponseId = null,
            string? conversationId = null, bool? store = null, string? caller = null,
            bool stream = false, bool background = false)
        {
            var body = new Dictionary<string, object?>
            {
                ["agent"] = new { name = AgentName },
                ["input"] = text,
                ["stream"] = stream,
                ["background"] = background
            };
            if (previousResponseId is not null) body["previous_response_id"] = previousResponseId;
            if (conversationId is not null) body["conversation"] = conversationId;
            if (store.HasValue) body["store"] = store.Value;
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = JsonContent.Create(body)
            };
            if (caller is not null) request.Headers.Add("X-Test-Identity", caller);
            return SendAsync(request);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            using (request)
            {
                return await Client.SendAsync(request);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Model.Pause?.TrySetResult();
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed record WriteCall(SessionStorageAddress Address, AgentSession Session, HistoryReference History);

    private sealed class RecordingSessionStore(ConcurrentDictionary<SessionStorageAddress, JsonElement> snapshots)
        : AgentSessionStore
    {
        public ConcurrentQueue<SessionStorageAddress> Reads { get; } = new();
        public ConcurrentQueue<WriteCall> Writes { get; } = new();
        public bool RejectWrites { get; set; }
        private readonly SemaphoreSlim _written = new(0);

        public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key,
            CancellationToken cancellationToken = default)
        {
            var address = SessionStorageAddress.Create(agent.Id, key);
            Reads.Enqueue(address);
            if (!snapshots.TryGetValue(address, out var snapshot)) return null;
            var session = await agent.DeserializeSessionAsync(snapshot, cancellationToken: cancellationToken);
            SessionPersistenceState.GetRequired(session).ValidateFor(address);
            return session;
        }

        public override async ValueTask<AgentSession> GetOrCreateSessionAsync(AIAgent agent, AgentSessionStoreKey key,
            CancellationToken cancellationToken = default)
        {
            var stored = await GetSessionAsync(agent, key, cancellationToken);
            if (stored is not null) return stored;
            var created = await agent.CreateSessionAsync(cancellationToken);
            SessionPersistenceState.Initialize(created, SessionStorageAddress.Create(agent.Id, key));
            return created;
        }

        public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session,
            CancellationToken cancellationToken = default)
        {
            if (RejectWrites) throw new InvalidOperationException("Injected persistence failure.");
            var address = SessionStorageAddress.Create(agent.Id, key);
            var context = SessionPersistenceState.GetRequired(session);
            context.ValidateFor(address);
            snapshots[address] = (await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken)).Clone();
            Writes.Enqueue(new(address, session, context.ActiveHistory));
            _written.Release();
        }

        public async Task WaitForWritesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Writes.Count < count) await _written.WaitAsync(timeout.Token);
        }
    }

    private sealed class TestIdentityProvider(IHttpContextAccessor accessor) : AgentIsolationKeyProvider
    {
        public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    private sealed class DeterministicChatClient : IChatClient
    {
        public ConcurrentQueue<ChatMessage[]> Inputs { get; } = new();
        public TaskCompletionSource? Pause { get; set; }
        public HttpRequestException? Failure { get; set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Inputs.Enqueue(messages.ToArray());
            if (Pause is not null) await Pause.Task.WaitAsync(cancellationToken);
            if (Failure is { } failure) throw failure;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "deterministic answer"))
            {
                ResponseId = Guid.NewGuid().ToString("N"),
                FinishReason = ChatFinishReason.Stop
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text)
            {
                ResponseId = response.ResponseId,
                MessageId = response.ResponseId,
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
