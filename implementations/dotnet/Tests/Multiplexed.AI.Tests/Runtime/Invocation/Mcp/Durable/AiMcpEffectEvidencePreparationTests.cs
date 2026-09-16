using System.Collections.Concurrent;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.McpEffects.Durable
{
    /// <summary>Pure durable-intent tests; no network call or Mongo instance is required.</summary>
    public sealed class AiMcpEffectEvidencePreparationTests
    {
        [Fact]
        public async Task Attempt_Id_And_Deadline_Do_Not_Change_The_Durable_Logical_Intent()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var first = Request("attempt-a", "{\"value\":1}");
            var second = first with
            {
                RequestId = "attempt-b",
                DeadlineUtc = first.DeadlineUtc.AddMinutes(1)
            };

            var prepared = await journal.PrepareAsync(first);
            var repeated = await journal.PrepareAsync(second);

            Assert.Equal(prepared, repeated);
            Assert.Equal(AiMcpEffectEvidenceStatus.Prepared, prepared.Status);
            Assert.Equal(0, prepared.Revision);
            Assert.Null(prepared.Attempt);
            Assert.DoesNotContain("attempt-a", prepared.Intent.ArgumentsJson);
        }

        [Fact]
        public async Task Same_Effect_With_Changed_Intent_Is_An_Integrity_Conflict()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var first = Request("attempt-a", "{\"value\":1}");
            var changed = Request("attempt-b", "{\"value\":2}");
            Assert.Equal(first.Effect!.EffectId, changed.Effect!.EffectId);
            Assert.NotEqual(first.Effect.RequestDigest, changed.Effect.RequestDigest);

            await journal.PrepareAsync(first);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(changed));
            Assert.Single(store.Records);
        }

        [Fact]
        public async Task Object_Property_Order_Does_Not_Create_A_False_Intent_Conflict()
        {
            var store = new MemoryStore();
            var journal = new AiMcpEffectEvidenceJournal(store, new FixedTimeProvider());
            var first = Request("attempt-a", "{\"a\":1,\"b\":2}");
            var reordered = Request("attempt-b", "{\"b\":2,\"a\":1}");
            Assert.Equal(first.Effect, reordered.Effect);

            var prepared = await journal.PrepareAsync(first);
            var repeated = await journal.PrepareAsync(reordered);

            Assert.Equal(prepared, repeated);
            Assert.Single(store.Records);
        }

        [Fact]
        public async Task Historical_Effectless_Envelope_Cannot_Create_Stronger_Durable_Evidence()
        {
            var journal = new AiMcpEffectEvidenceJournal(new MemoryStore(), new FixedTimeProvider());
            var legacy = Request("attempt-a", "{\"value\":1}") with
            {
                SchemaVersion = 1,
                Effect = null
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(legacy));
        }

        [Fact]
        public async Task Tenant_Scope_Is_Persisted_Separately_From_The_Hashed_Effect_Id()
        {
            var journal = new AiMcpEffectEvidenceJournal(new MemoryStore(), new FixedTimeProvider());
            var prepared = await journal.PrepareAsync(Request("attempt-a", "{\"value\":1}"));
            Assert.Equal("tenant-a", prepared.Scope.TenantId);
            Assert.Equal("group-a", prepared.Scope.TenantGroupId);
            Assert.Equal("execution-a", prepared.Intent.Context.ExecutionId);
            Assert.Equal("connection-a", prepared.Intent.ConnectionRef);
            Assert.Equal("revision-1", prepared.Intent.ConnectionRevision);
            Assert.Equal("probe.echo", prepared.Intent.Tool);
        }

        private static AiMcpToolRequest Request(string requestId, string argumentsJson)
        {
            using var document = JsonDocument.Parse(argumentsJson);
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

        private sealed class FixedTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-16T04:30:00Z");
        }

        private sealed class MemoryStore : IAiMcpEffectEvidenceStore
        {
            internal ConcurrentDictionary<string, AiMcpEffectEvidenceRecord> Records { get; } = new(StringComparer.Ordinal);

            public Task<AiMcpEffectEvidenceRecord?> GetAsync(
                AiMcpEffectEvidenceScope scope, string effectId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Records.TryGetValue(Key(scope, effectId), out var record);
                return Task.FromResult(record);
            }

            public Task<AiMcpEffectEvidenceRecord> GetOrCreateAsync(
                AiMcpEffectEvidenceRecord prepared, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Key(prepared.Scope, prepared.Intent.Effect.EffectId);
                var existing = Records.GetOrAdd(key, prepared);
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
                var key = Key(expected.Scope, expected.Intent.Effect.EffectId);
                return Task.FromResult(Records.TryUpdate(key, replacement, expected));
            }

            public Task<IReadOnlyList<AiMcpEffectEvidenceRecord>> ListReconciliationCandidatesAsync(
                AiMcpEffectEvidenceScope scope,
                DateTimeOffset dispatchStartedBeforeUtc,
                int maxCount,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<AiMcpEffectEvidenceRecord> result = Records.Values
                    .Where(record => record.Scope == scope &&
                        (record.Status == AiMcpEffectEvidenceStatus.Uncertain ||
                         record.Status == AiMcpEffectEvidenceStatus.Dispatching &&
                         record.Attempt!.StartedAtUtc <= dispatchStartedBeforeUtc))
                    .Take(maxCount).ToArray();
                return Task.FromResult(result);
            }

            private static string Key(AiMcpEffectEvidenceScope scope, string effectId) =>
                scope.TenantId + "\n" + scope.TenantGroupId + "\n" + effectId;
        }
    }
}
