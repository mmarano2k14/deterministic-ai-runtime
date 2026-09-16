using System.Collections.Concurrent;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.McpEffects.Durable
{
    /// <summary>Physical boundary classification remains separate from retry authority.</summary>
    public sealed class AiMcpEffectOutcomeClassificationTests
    {
        [Fact]
        public async Task Failure_Before_Tools_Call_Boundary_Is_Recorded_As_NotSent()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var inner = new BoundaryTransport(markBoundary: false);
            var transport = new AiDurableMcpToolTransport(journal, inner);
            var request = Request("attempt-a");

            await Assert.ThrowsAsync<IOException>(() => transport.InvokeAsync(request));

            var evidence = await store.GetAsync(Scope(request), request.Effect!.EffectId);
            Assert.NotNull(evidence);
            Assert.Equal(AiMcpEffectEvidenceStatus.NotSent, evidence.Status);
            Assert.Equal("transport-failure", evidence.NonEmission!.ReasonCode);
            Assert.Null(evidence.Uncertainty);
            Assert.Equal(1, inner.Calls);

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                transport.InvokeAsync(request with { RequestId = "attempt-b" }));
            Assert.Contains("does not grant automatic redelivery authority", blocked.Message);
            Assert.Equal(1, inner.Calls);
        }

        [Fact]
        public async Task Failure_After_Tools_Call_Boundary_Remains_Uncertain()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var inner = new BoundaryTransport(markBoundary: true);
            var transport = new AiDurableMcpToolTransport(journal, inner);
            var request = Request("attempt-a");

            await Assert.ThrowsAsync<IOException>(() => transport.InvokeAsync(request));

            var evidence = await store.GetAsync(Scope(request), request.Effect!.EffectId);
            Assert.NotNull(evidence);
            Assert.Equal(AiMcpEffectEvidenceStatus.Uncertain, evidence.Status);
            Assert.Equal("transport-failure", evidence.Uncertainty!.ReasonCode);
            Assert.Null(evidence.NonEmission);
        }

        [Fact]
        public async Task Transport_Without_Boundary_Contract_Remains_Conservatively_Uncertain()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var transport = new AiDurableMcpToolTransport(journal, new LegacyTransport());
            var request = Request("attempt-a");

            await Assert.ThrowsAsync<IOException>(() => transport.InvokeAsync(request));

            var evidence = await store.GetAsync(Scope(request), request.Effect!.EffectId);
            Assert.NotNull(evidence);
            Assert.Equal(AiMcpEffectEvidenceStatus.Uncertain, evidence.Status);
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

        private static AiMcpEffectEvidenceScope Scope(AiMcpToolRequest request) =>
            new(request.Context.TenantId, request.Context.TenantGroupId);

        private sealed class FixedTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-16T04:30:00Z");
        }

        private sealed class BoundaryTransport : IAiMcpDispatchBoundaryAwareTransport
        {
            private readonly bool _markBoundary;
            private int _calls;
            internal int Calls => Volatile.Read(ref _calls);

            internal BoundaryTransport(bool markBoundary) => _markBoundary = markBoundary;

            public Task<JsonElement> InvokeAsync(
                AiMcpToolRequest request,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("The durable test must use the boundary-aware overload.");

            public Task<JsonElement> InvokeAsync(
                AiMcpToolRequest request,
                IAiMcpDispatchBoundary boundary,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _calls);
                if (_markBoundary) boundary.MarkPossiblySent();
                return Task.FromException<JsonElement>(new IOException("simulated transport failure"));
            }
        }

        private sealed class LegacyTransport : IAiMcpToolTransport
        {
            public Task<JsonElement> InvokeAsync(
                AiMcpToolRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromException<JsonElement>(new IOException("legacy failure"));
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
