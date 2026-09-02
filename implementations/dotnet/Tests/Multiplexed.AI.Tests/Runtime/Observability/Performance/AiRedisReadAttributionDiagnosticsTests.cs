using Multiplexed.AI.Runtime.Observability.Performance;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Observability.Performance
{
    [CollectionDefinition("PERF1 Redis attribution diagnostics", DisableParallelization = true)]
    public sealed class AiRedisReadAttributionDiagnosticsCollection
    {
        public const string CollectionName = "PERF1 Redis attribution diagnostics";
    }

    [Collection(AiRedisReadAttributionDiagnosticsCollection.CollectionName)]
    public sealed class AiRedisReadAttributionDiagnosticsTests : IDisposable
    {
        private readonly string? previousEnabled;
        private readonly string? previousScope;

        public AiRedisReadAttributionDiagnosticsTests()
        {
            previousEnabled = Environment.GetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.EnabledEnvironmentVariable);
            previousScope = Environment.GetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.ScopeEnvironmentVariable);

            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.EnabledEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.ScopeEnvironmentVariable,
                null);
            AiRedisReadAttributionDiagnostics.ResetCurrentProcess();
        }

        [Fact]
        public void DisabledByDefault_Should_Record_Nothing()
        {
            var scope = AiRedisReadAttributionDiagnostics.BeginScope();

            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.ExecutionStateLoad,
                "GET",
                (RedisValue)"payload");

            Assert.Null(scope);
            Assert.Empty(AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());
        }

        [Fact]
        public void Enabled_Should_Record_Command_Count()
        {
            using var scope = EnableScope();

            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.ExecutionStateLoad,
                "GET",
                (RedisValue)"one");
            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.ExecutionStateLoad,
                "GET",
                (RedisValue)"two");

            var item = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            Assert.Equal("GET", item.Command);
            Assert.Equal(2L, item.Calls);
        }

        [Fact]
        public void Enabled_Should_Attribute_By_Semantic_Operation()
        {
            using var scope = EnableScope();

            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.ExecutionStateLoad,
                "GET",
                (RedisValue)"state");
            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.DagStateBlobLoad,
                "GET",
                (RedisValue)"dag");

            var items = AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess();

            Assert.Equal(2, items.Count);
            Assert.Contains(
                items,
                item => item.Operation == AiRedisReadAttributionOperations.ExecutionStateLoad &&
                        item.Calls == 1L);
            Assert.Contains(
                items,
                item => item.Operation == AiRedisReadAttributionOperations.DagStateBlobLoad &&
                        item.Calls == 1L);
        }

        [Fact]
        public void PayloadMeasurement_Should_Count_Returned_Values()
        {
            using var scope = EnableScope();

            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.SharedRunRecordLoad,
                "HGETALL",
                new HashEntry[]
                {
                    new("f", "é"),
                    new("abc", "1234")
                });

            var item = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            Assert.Equal(10L, item.ResponsePayloadBytes);
        }

        [Fact]
        public void Attribution_Should_Not_Expose_HighCardinality_Identity()
        {
            using var scope = EnableScope();

            AiRedisReadAttributionDiagnostics.Record(
                $"tenant-{Guid.NewGuid():N}",
                "GET",
                (RedisValue)"payload");

            Assert.Empty(AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());
        }

        [Fact]
        public void OperationOverride_Should_Reclassify_Only_Target_Operation()
        {
            using var scope = EnableScope();

            using (AiRedisReadAttributionDiagnostics.OverrideOperation(
                       AiRedisReadAttributionOperations.SharedRunRecordLoad,
                       AiRedisReadAttributionOperations.TestHarnessRuntimePoolWorkloadSharedRunLoad))
            using (AiRedisReadAttributionDiagnostics.OverrideOperation(
                       AiRedisReadAttributionOperations.SharedRunListRecordLoad,
                       AiRedisReadAttributionOperations.TestHarnessRuntimePoolWorkloadSharedRunLoad))
            {
                AiRedisReadAttributionDiagnostics.Record(
                    AiRedisReadAttributionOperations.SharedRunRecordLoad,
                    "HGETALL",
                    new HashEntry[] { new("status", "Completed") });

                AiRedisReadAttributionDiagnostics.Record(
                    AiRedisReadAttributionOperations.SharedRunListRecordLoad,
                    "HGETALL",
                    new HashEntry[] { new("status", "Completed") });

                AiRedisReadAttributionDiagnostics.Record(
                    AiRedisReadAttributionOperations.ExecutionStateLoad,
                    "GET",
                    (RedisValue)"running");
            }

            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.SharedRunRecordLoad,
                "HGETALL",
                new HashEntry[] { new("status", "Completed") });

            var items = AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess();

            Assert.Contains(
                items,
                item =>
                    item.Operation == AiRedisReadAttributionOperations.TestHarnessRuntimePoolWorkloadSharedRunLoad &&
                    item.Command == "HGETALL" &&
                    item.Calls == 2L);
            Assert.Contains(
                items,
                item =>
                    item.Operation == AiRedisReadAttributionOperations.SharedRunRecordLoad &&
                    item.Command == "HGETALL" &&
                    item.Calls == 1L);
            Assert.Contains(
                items,
                item =>
                    item.Operation == AiRedisReadAttributionOperations.ExecutionStateLoad &&
                    item.Command == "GET" &&
                    item.Calls == 1L);
        }

        [Fact]
        public void TestHarness_PublicGet_Operation_Should_Remain_Bounded()
        {
            using var scope = EnableScope();

            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.TestHarnessSharedRunPublicGetRecordLoad,
                "HGETALL",
                new HashEntry[] { new("status", "Completed") });

            var item = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            Assert.Equal(
                AiRedisReadAttributionOperations.TestHarnessSharedRunPublicGetRecordLoad,
                item.Operation);
            Assert.Equal("HGETALL", item.Command);
            Assert.Equal(1L, item.Calls);
        }

        [Fact]
        public void OverrideOperationIfUnchanged_Should_Classify_Unlabeled_Reads()
        {
            using var scope = EnableScope();

            using (AiRedisReadAttributionDiagnostics.OverrideOperationIfUnchanged(
                       AiRedisReadAttributionOperations.SharedRunRecordLoad,
                       AiRedisReadAttributionOperations.SharedRunPublicGetRecordLoad))
            {
                AiRedisReadAttributionDiagnostics.Record(
                    AiRedisReadAttributionOperations.SharedRunRecordLoad,
                    "HGETALL",
                    new HashEntry[] { new("status", "Completed") });
            }

            var item = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            Assert.Equal(
                AiRedisReadAttributionOperations.SharedRunPublicGetRecordLoad,
                item.Operation);
            Assert.Equal("HGETALL", item.Command);
            Assert.Equal(1L, item.Calls);
        }

        [Fact]
        public void OverrideOperationIfUnchanged_Should_Preserve_Outer_TestHarness_Classification()
        {
            using var scope = EnableScope();

            using (AiRedisReadAttributionDiagnostics.OverrideOperation(
                       AiRedisReadAttributionOperations.SharedRunRecordLoad,
                       AiRedisReadAttributionOperations.TestHarnessRuntimePoolWorkloadSharedRunLoad))
            using (AiRedisReadAttributionDiagnostics.OverrideOperationIfUnchanged(
                       AiRedisReadAttributionOperations.SharedRunRecordLoad,
                       AiRedisReadAttributionOperations.SharedRunPublicGetRecordLoad))
            {
                AiRedisReadAttributionDiagnostics.Record(
                    AiRedisReadAttributionOperations.SharedRunRecordLoad,
                    "HGETALL",
                    new HashEntry[] { new("status", "Completed") });
            }

            var item = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            Assert.Equal(
                AiRedisReadAttributionOperations.TestHarnessRuntimePoolWorkloadSharedRunLoad,
                item.Operation);
            Assert.Equal("HGETALL", item.Command);
            Assert.Equal(1L, item.Calls);
        }

        [Fact]
        public void Concurrent_Record_Should_Remain_Exact()
        {
            using var scope = EnableScope();
            const int expected = 10_000;

            Parallel.For(
                0,
                expected,
                _ => AiRedisReadAttributionDiagnostics.Record(
                    AiRedisReadAttributionOperations.DagStepLoadCluster,
                    "GET",
                    (RedisValue)"x"));

            var item = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            Assert.Equal((long)expected, item.Calls);
            Assert.Equal((long)expected, item.ResponsePayloadBytes);
        }

        [Fact]
        public void SnapshotAndReset_Should_Be_Deterministic()
        {
            using var scope = EnableScope();

            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.DagStepLoadMany,
                "MGET",
                new RedisValue[] { "a", "bb" });

            var beforeReset = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            AiRedisReadAttributionDiagnostics.ResetCurrentProcess();
            var afterReset = AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess();

            Assert.Equal(1L, beforeReset.Calls);
            Assert.Equal(3L, beforeReset.ResponsePayloadBytes);
            Assert.Empty(afterReset);
        }

        [Fact]
        public void RecordStateLoadMany_Should_Record_One_Combined_MGet()
        {
            using var scope = EnableScope();

            AiRedisReadAttributionDiagnostics.Record(
                AiRedisReadAttributionOperations.DagRecordStateLoadMany,
                "MGET",
                new RedisValue[] { "record", "state" });

            var item = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            Assert.Equal(
                AiRedisReadAttributionOperations.DagRecordStateLoadMany,
                item.Operation);
            Assert.Equal("MGET", item.Command);
            Assert.Equal(1L, item.Calls);
            Assert.Equal(11L, item.ResponsePayloadBytes);
        }

        [Fact]
        public void CurrentProcessIdentity_Should_Match_Published_Process_Identity_Format()
        {
            Assert.Equal(
                $"{Environment.MachineName}:{Environment.ProcessId}",
                AiRedisReadAttributionDiagnostics.CurrentProcessIdentity);
        }

        [Fact]
        public void Aggregate_Should_Preserve_Process_Snapshots()
        {
            var processOperation = new AiRedisReadAttributionOperationSnapshot(
                AiRedisReadAttributionOperations.SharedRunRecordLoad,
                "HGETALL",
                7L,
                123L);
            var processSnapshot = new AiRedisReadAttributionProcessSnapshot(
                "host:42",
                3L,
                DateTimeOffset.UnixEpoch,
                new[] { processOperation });

            var aggregate = new AiRedisReadAttributionAggregate(
                1,
                3L,
                new[] { processOperation })
            {
                ProcessSnapshots = new[] { processSnapshot }
            };

            var actual = Assert.Single(aggregate.ProcessSnapshots);
            Assert.Equal("host:42", actual.ProcessIdentity);
            Assert.Equal(3L, actual.PublicationSequence);
            Assert.Same(processOperation, Assert.Single(actual.Operations));
        }

        public void Dispose()
        {
            var currentScope = Environment.GetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.ScopeEnvironmentVariable);
            AiRedisReadAttributionDiagnostics.EndScope(currentScope);
            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.EnabledEnvironmentVariable,
                previousEnabled);
            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.ScopeEnvironmentVariable,
                previousScope);
            AiRedisReadAttributionDiagnostics.ResetCurrentProcess();
        }

        private static ScopeLease EnableScope()
        {
            Environment.SetEnvironmentVariable(
                AiRedisReadAttributionDiagnostics.EnabledEnvironmentVariable,
                "1");

            var scope = AiRedisReadAttributionDiagnostics.BeginScope();
            Assert.False(string.IsNullOrWhiteSpace(scope));
            return new ScopeLease(scope!);
        }

        private sealed class ScopeLease : IDisposable
        {
            private readonly string scope;

            public ScopeLease(string scope)
            {
                this.scope = scope;
            }

            public void Dispose()
            {
                AiRedisReadAttributionDiagnostics.EndScope(scope);
            }
        }
        [Fact]
        public void RecordInvocation_Should_Record_Bounded_Lua_Family()
        {
            using var scope = EnableScope();

            AiRedisReadAttributionDiagnostics.RecordInvocation(
                AiRedisReadAttributionOperations.LuaDag);

            var operation = Assert.Single(
                AiRedisReadAttributionDiagnostics.SnapshotCurrentProcess());

            Assert.Equal(AiRedisReadAttributionOperations.LuaDag, operation.Operation);
            Assert.Equal("LUA", operation.Command);
            Assert.Equal(1L, operation.Calls);
            Assert.Equal(0L, operation.ResponsePayloadBytes);
        }

    }
}
