using System.Collections.Concurrent;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.McpEffects.Durable
{
    /// <summary>Crash, restart and concurrency proofs around one durable MCP logical effect.</summary>
    public sealed class AiMcpEffectRecoveryAdversarialTests
    {
        [Fact]
        public async Task Lost_Completion_Write_Leaves_Dispatching_And_Restart_Cannot_Reemit()
        {
            var durableStore = new MemoryStore();
            var faultingStore = new CompletionFaultStore(durableStore, commitBeforeThrow: false);
            var firstJournal = new AiMcpEffectEvidenceJournal(faultingStore, new FixedTimeProvider());
            var firstPhysical = new BoundaryTransport();
            var firstTransport = new AiDurableMcpToolTransport(firstJournal, firstPhysical);
            var first = CreateRequest("attempt-a");

            var failure = await Assert.ThrowsAsync<IOException>(() => firstTransport.InvokeAsync(first));

            Assert.Contains("durable completion evidence", failure.Message);
            Assert.Equal(1, firstPhysical.Calls);
            var stranded = await durableStore.GetAsync(Scope(first), first.Effect!.EffectId);
            Assert.NotNull(stranded);
            Assert.Equal(AiMcpEffectEvidenceStatus.Dispatching, stranded.Status);
            Assert.Null(stranded.Result);

            var restartPhysical = new CountingTransport();
            var restartJournal = new AiMcpEffectEvidenceJournal(durableStore, new FixedTimeProvider());
            var restartTransport = new AiDurableMcpToolTransport(restartJournal, restartPhysical);
            var second = first with { RequestId = "attempt-b", DeadlineUtc = first.DeadlineUtc.AddMinutes(1) };

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => restartTransport.InvokeAsync(second));

            Assert.Contains("Dispatching", blocked.Message);
            Assert.Equal(0, restartPhysical.Calls);
            Assert.Equal(1, firstPhysical.Calls);
        }

        [Fact]
        public async Task Ambiguous_Completion_Acknowledgement_Replays_Committed_Result_After_Restart()
        {
            var durableStore = new MemoryStore();
            var faultingStore = new CompletionFaultStore(durableStore, commitBeforeThrow: true);
            var firstJournal = new AiMcpEffectEvidenceJournal(faultingStore, new FixedTimeProvider());
            var firstPhysical = new BoundaryTransport();
            var firstTransport = new AiDurableMcpToolTransport(firstJournal, firstPhysical);
            var first = CreateRequest("attempt-a");

            await Assert.ThrowsAsync<IOException>(() => firstTransport.InvokeAsync(first));

            Assert.Equal(1, firstPhysical.Calls);
            var committed = await durableStore.GetAsync(Scope(first), first.Effect!.EffectId);
            Assert.NotNull(committed);
            Assert.Equal(AiMcpEffectEvidenceStatus.Completed, committed.Status);
            Assert.Equal(2, committed.Revision);

            var restartPhysical = new CountingTransport();
            var restartJournal = new AiMcpEffectEvidenceJournal(durableStore, new FixedTimeProvider());
            var restartTransport = new AiDurableMcpToolTransport(restartJournal, restartPhysical);
            var second = first with { RequestId = "attempt-b", DeadlineUtc = first.DeadlineUtc.AddMinutes(1) };

            var replay = await restartTransport.InvokeAsync(second);

            Assert.Equal("attempt-b", replay.GetProperty("requestId").GetString());
            Assert.Equal("remote", replay.GetProperty("structuredContent").GetProperty("value").GetString());
            Assert.Equal(0, restartPhysical.Calls);
            Assert.Equal(1, firstPhysical.Calls);
        }

        [Fact]
        public async Task Crash_After_Dispatch_Fence_Before_Send_Requires_Reconciliation_And_Does_Not_Auto_Redeliver()
        {
            var store = new MemoryStore();
            var firstJournal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var first = CreateRequest("attempt-a");
            var prepared = await firstJournal.PrepareAsync(first);
            var dispatching = await firstJournal.TryBeginDispatchAsync(prepared, first);
            Assert.NotNull(dispatching);

            var restartPhysical = new CountingTransport();
            var restartJournal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var restartTransport = new AiDurableMcpToolTransport(restartJournal, restartPhysical);
            var second = first with { RequestId = "attempt-b", DeadlineUtc = first.DeadlineUtc.AddMinutes(1) };

            await Assert.ThrowsAsync<InvalidOperationException>(() => restartTransport.InvokeAsync(second));
            Assert.Equal(0, restartPhysical.Calls);

            var provider = new FixedProvider(
                dispatching!.Intent,
                AiMcpEffectReconciliationResult.NotSent("provider-confirmed-absent"));
            var reconciliation = new AiMcpEffectReconciliationService(restartJournal, [provider]);
            var notSent = await reconciliation.ReconcileAsync(dispatching.Scope, dispatching.Intent.Effect.EffectId);

            Assert.Equal(AiMcpEffectEvidenceStatus.NotSent, notSent.Status);
            Assert.Equal(1, provider.Calls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restartTransport.InvokeAsync(second));
            Assert.Equal(0, restartPhysical.Calls);
        }

        [Fact]
        public async Task Concurrent_Reconciliation_Of_The_Same_Result_Converges_On_One_Completed_Revision()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var request = CreateRequest("attempt-a");
            var prepared = await journal.PrepareAsync(request);
            var dispatching = await journal.TryBeginDispatchAsync(prepared, request);
            Assert.NotNull(dispatching);
            var uncertain = await journal.TryMarkUncertainAsync(dispatching!, "transport-timeout");
            Assert.NotNull(uncertain);

            var result = Response("attempt-a", "reconciled");
            var provider = new FixedProvider(
                uncertain!.Intent,
                AiMcpEffectReconciliationResult.Completed(result));
            var first = new AiMcpEffectReconciliationService(
                new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider()),
                [provider]);
            var second = new AiMcpEffectReconciliationService(
                new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider()),
                [provider]);

            var reconciled = await Task.WhenAll(
                first.ReconcileAsync(uncertain.Scope, uncertain.Intent.Effect.EffectId),
                second.ReconcileAsync(uncertain.Scope, uncertain.Intent.Effect.EffectId));

            Assert.All(reconciled, item => Assert.Equal(AiMcpEffectEvidenceStatus.Completed, item.Status));
            var completed = await store.GetAsync(uncertain.Scope, uncertain.Intent.Effect.EffectId);
            Assert.NotNull(completed);
            Assert.Equal(AiMcpEffectEvidenceStatus.Completed, completed.Status);
            Assert.Equal(3, completed.Revision);
            using var persistedResponse = JsonDocument.Parse(completed.Result!.ResponseJson);
            Assert.Equal("reconciled", persistedResponse.RootElement
                .GetProperty("structuredContent").GetProperty("value").GetString());
            Assert.InRange(provider.Calls, 1, 2);
        }

        [Fact]
        public async Task Tenant_Scope_Cannot_Reconcile_Another_Tenants_Effect()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var request = CreateRequest("attempt-a");
            var prepared = await journal.PrepareAsync(request);
            var dispatching = await journal.TryBeginDispatchAsync(prepared, request);
            Assert.NotNull(dispatching);
            var uncertain = await journal.TryMarkUncertainAsync(dispatching!, "transport-timeout");
            Assert.NotNull(uncertain);

            var provider = new FixedProvider(
                uncertain!.Intent,
                AiMcpEffectReconciliationResult.Unknown());
            var service = new AiMcpEffectReconciliationService(journal, [provider]);
            var wrongScope = uncertain.Scope with { TenantGroupId = "group-b" };

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                service.ReconcileAsync(wrongScope, uncertain.Intent.Effect.EffectId));

            Assert.Equal(0, provider.Calls);
            var preserved = await journal.GetAsync(uncertain.Scope, uncertain.Intent.Effect.EffectId);
            Assert.Equal(AiMcpEffectEvidenceStatus.Uncertain, preserved!.Status);
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

        private static AiMcpEffectEvidenceScope Scope(AiMcpToolRequest request) =>
            new(request.Context.TenantId, request.Context.TenantGroupId);

        private static JsonElement Response(string requestId, string value) =>
            JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1,
                requestId,
                isError = false,
                content = Array.Empty<object>(),
                structuredContent = new { value }
            });

        private sealed class BoundaryTransport : IAiMcpDispatchBoundaryAwareTransport
        {
            private int _calls;
            internal int Calls => Volatile.Read(ref _calls);

            public Task<JsonElement> InvokeAsync(
                AiMcpToolRequest request,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("The adversarial transport requires the explicit dispatch boundary.");

            public Task<JsonElement> InvokeAsync(
                AiMcpToolRequest request,
                IAiMcpDispatchBoundary boundary,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                boundary.MarkPossiblySent();
                Interlocked.Increment(ref _calls);
                return Task.FromResult(Response(request.RequestId, "remote"));
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
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _calls);
                return Task.FromResult(Response(request.RequestId, "unexpected"));
            }
        }

        private sealed class FixedProvider : IAiMcpEffectReconciliationProvider
        {
            private readonly AiMcpEffectIntent _intent;
            private readonly AiMcpEffectReconciliationResult _result;
            private int _calls;
            internal int Calls => Volatile.Read(ref _calls);

            internal FixedProvider(AiMcpEffectIntent intent, AiMcpEffectReconciliationResult result)
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

        private sealed class CompletionFaultStore : IAiMcpEffectEvidenceStore
        {
            private readonly IAiMcpEffectEvidenceStore _inner;
            private readonly bool _commitBeforeThrow;
            private int _remainingCompletionFaults = 1;

            internal CompletionFaultStore(
                IAiMcpEffectEvidenceStore inner,
                bool commitBeforeThrow)
            {
                _inner = inner;
                _commitBeforeThrow = commitBeforeThrow;
            }

            public Task<AiMcpEffectEvidenceRecord?> GetAsync(
                AiMcpEffectEvidenceScope scope,
                string effectId,
                CancellationToken cancellationToken = default) =>
                _inner.GetAsync(scope, effectId, cancellationToken);

            public Task<AiMcpEffectEvidenceRecord> GetOrCreateAsync(
                AiMcpEffectEvidenceRecord prepared,
                CancellationToken cancellationToken = default) =>
                _inner.GetOrCreateAsync(prepared, cancellationToken);

            public async Task<bool> TryReplaceAsync(
                AiMcpEffectEvidenceRecord expected,
                AiMcpEffectEvidenceRecord replacement,
                CancellationToken cancellationToken = default)
            {
                if (expected.Status == AiMcpEffectEvidenceStatus.Dispatching &&
                    replacement.Status == AiMcpEffectEvidenceStatus.Completed &&
                    Interlocked.Exchange(ref _remainingCompletionFaults, 0) == 1)
                {
                    if (_commitBeforeThrow)
                    {
                        var committed = await _inner.TryReplaceAsync(
                            expected, replacement, cancellationToken).ConfigureAwait(false);
                        Assert.True(committed);
                    }

                    throw new IOException("Injected ambiguous completion persistence failure.");
                }

                return await _inner.TryReplaceAsync(expected, replacement, cancellationToken).ConfigureAwait(false);
            }

            public Task<IReadOnlyList<AiMcpEffectEvidenceRecord>> ListReconciliationCandidatesAsync(
                AiMcpEffectEvidenceScope scope,
                DateTimeOffset dispatchStartedBeforeUtc,
                int maxCount,
                CancellationToken cancellationToken = default) =>
                _inner.ListReconciliationCandidatesAsync(
                    scope, dispatchStartedBeforeUtc, maxCount, cancellationToken);
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
                return Task.FromResult(_records.GetOrAdd(
                    Key(prepared.Scope, prepared.Intent.Effect.EffectId),
                    prepared));
            }

            public Task<bool> TryReplaceAsync(
                AiMcpEffectEvidenceRecord expected,
                AiMcpEffectEvidenceRecord replacement,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_records.TryUpdate(
                    Key(expected.Scope, expected.Intent.Effect.EffectId),
                    replacement,
                    expected));
            }

            public Task<IReadOnlyList<AiMcpEffectEvidenceRecord>> ListReconciliationCandidatesAsync(
                AiMcpEffectEvidenceScope scope,
                DateTimeOffset dispatchStartedBeforeUtc,
                int maxCount,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var records = _records.Values
                    .Where(record => record.Scope == scope &&
                        (record.Status == AiMcpEffectEvidenceStatus.Uncertain ||
                         record.Status == AiMcpEffectEvidenceStatus.Dispatching &&
                         record.Attempt is not null &&
                         record.Attempt.StartedAtUtc <= dispatchStartedBeforeUtc))
                    .OrderBy(record => record.UpdatedAtUtc)
                    .ThenBy(record => record.Intent.Effect.EffectId, StringComparer.Ordinal)
                    .Take(maxCount)
                    .ToArray();
                return Task.FromResult<IReadOnlyList<AiMcpEffectEvidenceRecord>>(records);
            }

            private static string Key(AiMcpEffectEvidenceScope scope, string effectId) =>
                scope.TenantId + "\n" + scope.TenantGroupId + "\n" + effectId;
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-16T04:30:00Z");
        }
    }
}
