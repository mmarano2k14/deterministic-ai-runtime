using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;
using Multiplexed.AI.McpServer.DependencyInjection;
using Multiplexed.AI.McpServer.Invocation.Outbound;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.McpEffects.Durable
{
    /// <summary>Durable dispatch-fence tests use a substituted physical transport only.</summary>
    public sealed class AiMcpDurableDispatchFenceTests
    {
        [Fact]
        public async Task Dispatch_Is_Durably_Fenced_Before_The_Physical_Transport_Is_Called()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var inner = new ProbeTransport();
            var request = Request("attempt-a");
            inner.Handler = async (current, _) =>
            {
                var evidence = await store.GetAsync(Scope(current), current.Effect!.EffectId);
                Assert.NotNull(evidence);
                Assert.Equal(AiMcpEffectEvidenceStatus.Dispatching, evidence.Status);
                Assert.Equal(current.RequestId, evidence.Attempt!.RequestId);
                return Response(current.RequestId, value: "remote");
            };

            var transport = new AiDurableMcpToolTransport(journal, inner);
            var response = await transport.InvokeAsync(request);

            Assert.Equal("attempt-a", response.GetProperty("requestId").GetString());
            Assert.Equal(1, inner.Calls);
            var completed = await store.GetAsync(Scope(request), request.Effect!.EffectId);
            Assert.NotNull(completed);
            Assert.Equal(AiMcpEffectEvidenceStatus.Completed, completed.Status);
            Assert.Equal(2, completed.Revision);
        }

        [Fact]
        public async Task Completed_Effect_Is_Replayed_With_The_Current_Request_Id_Without_Reemission()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var inner = new ProbeTransport
            {
                Handler = (current, _) => Task.FromResult(Response(current.RequestId, value: "once"))
            };
            var transport = new AiDurableMcpToolTransport(journal, inner);
            var first = Request("attempt-a");
            var second = first with
            {
                RequestId = "attempt-b",
                DeadlineUtc = first.DeadlineUtc.AddMinutes(1)
            };

            var original = await transport.InvokeAsync(first);
            var replay = await transport.InvokeAsync(second);

            Assert.Equal(1, inner.Calls);
            Assert.Equal("attempt-a", original.GetProperty("requestId").GetString());
            Assert.Equal("attempt-b", replay.GetProperty("requestId").GetString());
            Assert.Equal("once", replay.GetProperty("structuredContent").GetProperty("value").GetString());
            var completed = await store.GetAsync(Scope(first), first.Effect!.EffectId);
            Assert.Equal("attempt-a", completed!.Attempt!.RequestId);
            Assert.Contains("attempt-a", completed.Result!.ResponseJson);
            Assert.DoesNotContain("attempt-b", completed.Result.ResponseJson);
        }

        [Fact]
        public async Task Concurrent_Call_Cannot_Cross_The_Network_After_Another_Attempt_Holds_The_Fence()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inner = new ProbeTransport
            {
                Handler = async (current, cancellationToken) =>
                {
                    started.TrySetResult(true);
                    await release.Task.WaitAsync(cancellationToken);
                    return Response(current.RequestId, value: "winner");
                }
            };
            var transport = new AiDurableMcpToolTransport(journal, inner);
            var first = Request("attempt-a");
            var second = first with { RequestId = "attempt-b" };

            var winner = transport.InvokeAsync(first);
            await started.Task;
            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.InvokeAsync(second));
            Assert.Contains("automatic outbound re-emission is forbidden", blocked.Message);
            Assert.Equal(1, inner.Calls);

            release.TrySetResult(true);
            var result = await winner;
            Assert.Equal("winner", result.GetProperty("structuredContent").GetProperty("value").GetString());
            Assert.Equal(1, inner.Calls);
        }

        [Fact]
        public async Task Failed_Physical_Attempt_Becomes_Uncertain_And_Is_Not_Blindly_Reemitted()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var inner = new ProbeTransport
            {
                Handler = (_, _) => Task.FromException<JsonElement>(new IOException("connection lost"))
            };
            var transport = new AiDurableMcpToolTransport(journal, inner);
            var first = Request("attempt-a");

            await Assert.ThrowsAsync<IOException>(() => transport.InvokeAsync(first));
            var uncertain = await store.GetAsync(Scope(first), first.Effect!.EffectId);
            Assert.NotNull(uncertain);
            Assert.Equal(AiMcpEffectEvidenceStatus.Uncertain, uncertain.Status);
            Assert.Equal("transport-failure", uncertain.Uncertainty!.ReasonCode);

            var second = first with { RequestId = "attempt-b" };
            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.InvokeAsync(second));
            Assert.Contains("Uncertain", blocked.Message);
            Assert.Equal(1, inner.Calls);
        }

        [Fact]
        public async Task Cancelled_Dispatched_Attempt_Persists_Uncertain_With_An_Independent_Evidence_Token()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            using var cancellation = new CancellationTokenSource();
            var inner = new ProbeTransport
            {
                Handler = (_, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<JsonElement>(token);
                }
            };
            var transport = new AiDurableMcpToolTransport(journal, inner);
            var request = Request("attempt-a");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                transport.InvokeAsync(request, cancellation.Token));

            var uncertain = await store.GetAsync(Scope(request), request.Effect!.EffectId);
            Assert.NotNull(uncertain);
            Assert.Equal(AiMcpEffectEvidenceStatus.Uncertain, uncertain.Status);
            Assert.Equal("transport-cancelled", uncertain.Uncertainty!.ReasonCode);
            Assert.Equal(1, inner.Calls);
        }

        [Fact]
        public void Outbound_Registration_Uses_The_Durable_Decorator_When_The_Journal_Is_Installed()
        {
            var store = new MemoryStore();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAiMcpEffectEvidenceStore>(store);
            services.AddSingleton(new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider()));
            services.AddAiOutboundMcpToolExecution(new AiOutboundMcpToolExecutionOptions
            {
                Connections = [new AiOutboundMcpConnectionRegistration
                {
                    TenantId = "tenant-a",
                    TenantGroupId = "group-a",
                    ConnectionRef = "connection-a",
                    Revision = "revision-1",
                    Endpoint = new Uri("https://example.com/mcp"),
                    Tools = [new AiOutboundMcpToolRegistration(
                        "probe.echo", "reports", "publish", "invoke")]
                }]
            });

            using var provider = services.BuildServiceProvider();
            Assert.IsType<AiDurableMcpToolTransport>(provider.GetRequiredService<IAiMcpToolTransport>());
        }

        [Fact]
        public async Task Expired_Request_Does_Not_Acquire_Dispatch_Authority_Or_Call_The_Transport()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var inner = new ProbeTransport();
            var transport = new AiDurableMcpToolTransport(journal, inner);
            var request = Request("attempt-a") with
            {
                DeadlineUtc = DateTimeOffset.Parse("2026-09-16T04:29:59Z")
            };

            await Assert.ThrowsAsync<TimeoutException>(() => transport.InvokeAsync(request));
            Assert.Equal(0, inner.Calls);
            var prepared = await store.GetAsync(Scope(request), request.Effect!.EffectId);
            Assert.NotNull(prepared);
            Assert.Equal(AiMcpEffectEvidenceStatus.Prepared, prepared.Status);
            Assert.Null(prepared.Attempt);
        }

        private static AiMcpToolRequest Request(string requestId)
        {
            using var document = JsonDocument.Parse("{\"value\":\"same\"}");
            var request = new AiMcpToolRequest(
                AiMcpEffectIdentities.RequestSchemaVersion,
                requestId,
                DateTimeOffset.Parse("2026-09-16T05:00:00Z"),
                new AiMcpToolInvocationContext(
                    "tenant-a", "group-a", "execution-a", "pipeline-a", "v1", "publish", "step-001"),
                "connection-a",
                "revision-1",
                "probe.echo",
                document.RootElement.Clone());
            return request with { Effect = AiMcpEffectIdentities.Create(request) };
        }

        private static JsonElement Response(string requestId, bool isError = false, string value = "ok")
        {
            return JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1,
                requestId,
                isError,
                content = Array.Empty<object>(),
                structuredContent = new { value }
            });
        }

        private static AiMcpEffectEvidenceScope Scope(AiMcpToolRequest request) =>
            new(request.Context.TenantId, request.Context.TenantGroupId);

        private sealed class FixedTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-16T04:30:00Z");
        }

        private sealed class ProbeTransport : IAiMcpToolTransport
        {
            private int _calls;
            internal int Calls => Volatile.Read(ref _calls);
            internal Func<AiMcpToolRequest, CancellationToken, Task<JsonElement>> Handler { get; set; } =
                (request, _) => Task.FromResult(Response(request.RequestId));

            public Task<JsonElement> InvokeAsync(
                AiMcpToolRequest request,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _calls);
                return Handler(request, cancellationToken);
            }
        }

        private sealed class MemoryStore : IAiMcpEffectEvidenceStore
        {
            private readonly ConcurrentDictionary<string, AiMcpEffectEvidenceRecord> _records =
                new(StringComparer.Ordinal);

            public Task<AiMcpEffectEvidenceRecord?> GetAsync(
                AiMcpEffectEvidenceScope scope,
                string effectId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _records.TryGetValue(Key(scope, effectId), out var record);
                return Task.FromResult(record);
            }

            public Task<AiMcpEffectEvidenceRecord> GetOrCreateAsync(
                AiMcpEffectEvidenceRecord prepared,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Key(prepared.Scope, prepared.Intent.Effect.EffectId);
                var existing = _records.GetOrAdd(key, prepared);
                if (existing.Intent.Effect != prepared.Intent.Effect ||
                    existing.Intent.Context != prepared.Intent.Context ||
                    existing.Intent.ConnectionRef != prepared.Intent.ConnectionRef ||
                    existing.Intent.ConnectionRevision != prepared.Intent.ConnectionRevision ||
                    existing.Intent.Tool != prepared.Intent.Tool)
                {
                    throw new InvalidOperationException("Conflicting frozen intent.");
                }
                return Task.FromResult(existing);
            }

            public Task<bool> TryReplaceAsync(
                AiMcpEffectEvidenceRecord expected,
                AiMcpEffectEvidenceRecord replacement,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_records.TryUpdate(
                    Key(expected.Scope, expected.Intent.Effect.EffectId), replacement, expected));
            }

            public Task<IReadOnlyList<AiMcpEffectEvidenceRecord>> ListReconciliationCandidatesAsync(
                AiMcpEffectEvidenceScope scope,
                DateTimeOffset dispatchStartedBeforeUtc,
                int maxCount,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<AiMcpEffectEvidenceRecord> result = _records.Values
                    .Where(record => record.Scope == scope &&
                        (record.Status == AiMcpEffectEvidenceStatus.Uncertain ||
                         record.Status == AiMcpEffectEvidenceStatus.Dispatching &&
                         record.Attempt!.StartedAtUtc <= dispatchStartedBeforeUtc))
                    .Take(maxCount)
                    .ToArray();
                return Task.FromResult(result);
            }

            private static string Key(AiMcpEffectEvidenceScope scope, string effectId) =>
                scope.TenantId + "\n" + scope.TenantGroupId + "\n" + effectId;
        }
    }
}
