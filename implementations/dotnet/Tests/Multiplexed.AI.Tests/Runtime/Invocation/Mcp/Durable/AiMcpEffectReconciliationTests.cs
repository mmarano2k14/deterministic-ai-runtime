using System.Collections.Concurrent;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.McpEffects.Durable
{
    /// <summary>Explicit reconciliation changes evidence only and never reissues tools/call.</summary>
    public sealed class AiMcpEffectReconciliationTests
    {
        [Fact]
        public async Task Confirmed_Result_Reconciles_Uncertain_Evidence_And_Replays_Without_Transport()
        {
            var fixture = await Fixture.CreateUncertainAsync();
            var response = Response("attempt-a", value: "reconciled");
            var provider = new Provider(fixture.Intent, AiMcpEffectReconciliationResult.Completed(response));
            var service = new AiMcpEffectReconciliationService(fixture.Journal, [provider]);

            var completed = await service.ReconcileAsync(fixture.Scope, fixture.EffectId);

            Assert.Equal(AiMcpEffectEvidenceStatus.Completed, completed.Status);
            Assert.Equal(1, provider.Calls);
            var physical = new CountingTransport();
            var durable = new AiDurableMcpToolTransport(fixture.Journal, physical);
            var replay = await durable.InvokeAsync(fixture.Request with { RequestId = "attempt-b" });
            Assert.Equal("attempt-b", replay.GetProperty("requestId").GetString());
            Assert.Equal("reconciled", replay.GetProperty("structuredContent").GetProperty("value").GetString());
            Assert.Equal(0, physical.Calls);
        }

        [Fact]
        public async Task Confirmed_NotSent_Reconciliation_Is_Durable_But_Does_Not_Authorize_Redelivery()
        {
            var fixture = await Fixture.CreateUncertainAsync();
            var provider = new Provider(
                fixture.Intent,
                AiMcpEffectReconciliationResult.NotSent("provider-confirmed-absent"));
            var service = new AiMcpEffectReconciliationService(fixture.Journal, [provider]);

            var resolved = await service.ReconcileAsync(fixture.Scope, fixture.EffectId);

            Assert.Equal(AiMcpEffectEvidenceStatus.NotSent, resolved.Status);
            Assert.Equal("provider-confirmed-absent", resolved.NonEmission!.ReasonCode);
            var physical = new CountingTransport();
            var durable = new AiDurableMcpToolTransport(fixture.Journal, physical);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                durable.InvokeAsync(fixture.Request with { RequestId = "attempt-b" }));
            Assert.Equal(0, physical.Calls);
        }

        [Fact]
        public async Task Unknown_Reconciliation_Leaves_Existing_Uncertainty_Unchanged()
        {
            var fixture = await Fixture.CreateUncertainAsync();
            var provider = new Provider(fixture.Intent, AiMcpEffectReconciliationResult.Unknown());
            var service = new AiMcpEffectReconciliationService(fixture.Journal, [provider]);

            var unresolved = await service.ReconcileAsync(fixture.Scope, fixture.EffectId);

            Assert.Equal(AiMcpEffectEvidenceStatus.Uncertain, unresolved.Status);
            Assert.Equal(fixture.Uncertain.Revision, unresolved.Revision);
            Assert.Equal(1, provider.Calls);
        }

        [Fact]
        public async Task Unknown_Reconciliation_Converts_Stale_Dispatching_To_Uncertain_Without_Tool_Call()
        {
            var fixture = await Fixture.CreateDispatchingAsync();
            var provider = new Provider(
                fixture.Intent,
                AiMcpEffectReconciliationResult.Unknown("provider-no-proof"));
            var service = new AiMcpEffectReconciliationService(fixture.Journal, [provider]);

            var unresolved = await service.ReconcileAsync(fixture.Scope, fixture.EffectId);

            Assert.Equal(AiMcpEffectEvidenceStatus.Uncertain, unresolved.Status);
            Assert.Equal("provider-no-proof", unresolved.Uncertainty!.ReasonCode);
            Assert.Equal(1, provider.Calls);
        }

        [Fact]
        public async Task Reconciliation_Requires_Exactly_One_Explicit_Provider()
        {
            var fixture = await Fixture.CreateUncertainAsync();
            var none = new AiMcpEffectReconciliationService(fixture.Journal, []);
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                none.ReconcileAsync(fixture.Scope, fixture.EffectId));

            var first = new Provider(fixture.Intent, AiMcpEffectReconciliationResult.Unknown());
            var second = new Provider(fixture.Intent, AiMcpEffectReconciliationResult.Unknown());
            var ambiguous = new AiMcpEffectReconciliationService(fixture.Journal, [first, second]);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ambiguous.ReconcileAsync(fixture.Scope, fixture.EffectId));
            Assert.Equal(0, first.Calls);
            Assert.Equal(0, second.Calls);
        }

        private static JsonElement Response(string requestId, bool isError = false, string value = "ok") =>
            JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1,
                requestId,
                isError,
                content = Array.Empty<object>(),
                structuredContent = new { value }
            });

        private sealed class Provider : IAiMcpEffectReconciliationProvider
        {
            private readonly AiMcpEffectIntent _intent;
            private readonly AiMcpEffectReconciliationResult _result;
            private int _calls;
            internal int Calls => Volatile.Read(ref _calls);

            internal Provider(AiMcpEffectIntent intent, AiMcpEffectReconciliationResult result)
            {
                _intent = intent;
                _result = result;
            }

            public bool CanReconcile(AiMcpEffectIntent intent) => intent == _intent;

            public Task<AiMcpEffectReconciliationResult> ReconcileAsync(
                AiMcpEffectEvidenceRecord evidence,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _calls);
                return Task.FromResult(_result);
            }
        }

        private sealed class CountingTransport : IAiMcpToolTransport
        {
            private int _calls;
            internal int Calls => Volatile.Read(ref _calls);

            public Task<JsonElement> InvokeAsync(
                AiMcpToolRequest request,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _calls);
                return Task.FromResult(Response(request.RequestId));
            }
        }

        private sealed class Fixture
        {
            private Fixture(
                MemoryStore store,
                AiMcpEffectEvidenceJournal journal,
                AiMcpToolRequest request,
                AiMcpEffectEvidenceRecord dispatching,
                AiMcpEffectEvidenceRecord? uncertain)
            {
                Store = store;
                Journal = journal;
                Request = request;
                Dispatching = dispatching;
                Uncertain = uncertain ?? dispatching;
            }

            internal MemoryStore Store { get; }
            internal AiMcpEffectEvidenceJournal Journal { get; }
            internal AiMcpToolRequest Request { get; }
            internal AiMcpEffectEvidenceRecord Dispatching { get; }
            internal AiMcpEffectEvidenceRecord Uncertain { get; }
            internal AiMcpEffectEvidenceScope Scope => Dispatching.Scope;
            internal AiMcpEffectIntent Intent => Dispatching.Intent;
            internal string EffectId => Dispatching.Intent.Effect.EffectId;

            internal static async Task<Fixture> CreateDispatchingAsync()
            {
                var store = new MemoryStore();
                var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
                var request = CreateRequest("attempt-a");
                var prepared = await journal.PrepareAsync(request);
                var dispatching = await journal.TryBeginDispatchAsync(prepared, request);
                Assert.NotNull(dispatching);
                return new Fixture(store, journal, request, dispatching!, null);
            }

            internal static async Task<Fixture> CreateUncertainAsync()
            {
                var fixture = await CreateDispatchingAsync();
                var uncertain = await fixture.Journal.TryMarkUncertainAsync(
                    fixture.Dispatching, "transport-timeout");
                Assert.NotNull(uncertain);
                return new Fixture(
                    fixture.Store,
                    fixture.Journal,
                    fixture.Request,
                    fixture.Dispatching,
                    uncertain);
            }

            private static AiMcpToolRequest CreateRequest(string requestId)
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
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-16T04:30:00Z");
        }

        private sealed class MemoryStore : IAiMcpEffectEvidenceStore
        {
            private readonly ConcurrentDictionary<string, AiMcpEffectEvidenceRecord> _records = new(StringComparer.Ordinal);

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
                return Task.FromResult(_records.GetOrAdd(Key(prepared.Scope, prepared.Intent.Effect.EffectId), prepared));
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
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<AiMcpEffectEvidenceRecord>>([]);

            private static string Key(AiMcpEffectEvidenceScope scope, string effectId) =>
                scope.TenantId + "\n" + scope.TenantGroupId + "\n" + effectId;
        }
    }
}
