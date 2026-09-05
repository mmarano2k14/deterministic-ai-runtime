using Multiplexed.AI.Runtime.Observability.Performance;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Observability.Performance
{
    [CollectionDefinition("PERF2 Mongo attribution diagnostics", DisableParallelization = true)]
    public sealed class AiMongoAttributionDiagnosticsCollection
    {
        public const string CollectionName = "PERF2 Mongo attribution diagnostics";
    }

    [Collection(AiMongoAttributionDiagnosticsCollection.CollectionName)]
    public sealed class AiMongoAttributionDiagnosticsTests : IDisposable
    {
        private readonly string? previousEnabled;
        private readonly string? previousScope;

        public AiMongoAttributionDiagnosticsTests()
        {
            previousEnabled = Environment.GetEnvironmentVariable(
                AiMongoAttributionDiagnostics.EnabledEnvironmentVariable);
            previousScope = Environment.GetEnvironmentVariable(
                AiMongoAttributionDiagnostics.ScopeEnvironmentVariable);

            Environment.SetEnvironmentVariable(
                AiMongoAttributionDiagnostics.EnabledEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                AiMongoAttributionDiagnostics.ScopeEnvironmentVariable,
                null);
            AiMongoAttributionDiagnostics.ResetCurrentProcess();
        }

        [Fact]
        public void DisabledByDefault_Should_Record_Nothing()
        {
            var scope = AiMongoAttributionDiagnostics.BeginScope();
            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.LedgerEntryAppend,
                AiMongoAttributionCommands.Insert,
                requestedDocuments: 1);

            measurement.Succeed();

            Assert.Null(scope);
            Assert.False(measurement.IsActive);
            Assert.Empty(AiMongoAttributionDiagnostics.SnapshotCurrentProcessOperations());
        }

        [Fact]
        public void Enabled_Should_Record_Semantic_Operation_Documents_And_Bytes()
        {
            using var scope = EnableScope();

            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.PayloadSave,
                AiMongoAttributionCommands.Insert,
                requestedDocuments: 1,
                requestPayloadBytes: 512);
            measurement.Succeed(returnedDocuments: 0, responsePayloadBytes: 64);

            var item = Assert.Single(
                AiMongoAttributionDiagnostics.SnapshotCurrentProcessOperations());

            Assert.Equal(AiMongoAttributionOperations.PayloadSave, item.Operation);
            Assert.Equal(AiMongoAttributionCommands.Insert, item.Command);
            Assert.Equal(1L, item.Calls);
            Assert.Equal(1L, item.RequestedDocuments);
            Assert.Equal(512L, item.RequestPayloadBytes);
            Assert.Equal(64L, item.ResponsePayloadBytes);
            Assert.Equal(1L, item.Successes);
            Assert.Equal(0L, item.Failures);
            Assert.Equal(0L, item.Cancellations);
        }

        [Fact]
        public void Failure_And_DuplicateKeyRetry_Should_Be_Recorded()
        {
            using var scope = EnableScope();

            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.LedgerSequenceNext,
                AiMongoAttributionCommands.FindAndModify,
                requestedDocuments: 1);
            measurement.Fail(duplicateKeyRetry: true);

            var item = Assert.Single(
                AiMongoAttributionDiagnostics.SnapshotCurrentProcessOperations());

            Assert.Equal(1L, item.Calls);
            Assert.Equal(1L, item.Failures);
            Assert.Equal(1L, item.DuplicateKeyRetries);
        }

        [Fact]
        public void Cancellation_Should_Be_Recorded_Separately()
        {
            using var scope = EnableScope();

            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.SnapshotLoad,
                AiMongoAttributionCommands.Find);
            measurement.Cancel();

            var item = Assert.Single(
                AiMongoAttributionDiagnostics.SnapshotCurrentProcessOperations());

            Assert.Equal(1L, item.Calls);
            Assert.Equal(0L, item.Successes);
            Assert.Equal(0L, item.Failures);
            Assert.Equal(1L, item.Cancellations);
        }

        [Fact]
        public void HighCardinality_Operation_Should_Not_Be_Recorded()
        {
            using var scope = EnableScope();

            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                $"Mongo.Tenant.{Guid.NewGuid():N}",
                AiMongoAttributionCommands.Find);
            measurement.Succeed(1);

            Assert.False(measurement.IsActive);
            Assert.Empty(AiMongoAttributionDiagnostics.SnapshotCurrentProcessOperations());
        }

        [Fact]
        public void TestHarnessOverride_Should_Reclassify_Reads_Only()
        {
            using var scope = EnableScope();

            using (AiMongoAttributionDiagnostics.OverrideForTestHarnessAudit())
            {
                var read = AiMongoAttributionDiagnostics.StartOperation(
                    AiMongoAttributionOperations.LedgerExecutionLoad,
                    AiMongoAttributionCommands.Find);
                read.Succeed(7);

                var write = AiMongoAttributionDiagnostics.StartOperation(
                    AiMongoAttributionOperations.LedgerEntryAppend,
                    AiMongoAttributionCommands.Insert,
                    requestedDocuments: 1);
                write.Succeed();
            }

            var items = AiMongoAttributionDiagnostics.SnapshotCurrentProcessOperations();
            Assert.Contains(
                items,
                item => item.Operation == AiMongoAttributionOperations.TestHarnessLedgerExecutionLoad &&
                        item.Command == AiMongoAttributionCommands.Find &&
                        item.Calls == 1L);
            Assert.Contains(
                items,
                item => item.Operation == AiMongoAttributionOperations.LedgerEntryAppend &&
                        item.Command == AiMongoAttributionCommands.Insert &&
                        item.Calls == 1L);
        }

        [Fact]
        public void Concurrent_Recording_Should_Remain_Exact()
        {
            using var scope = EnableScope();
            const int expected = 10_000;

            Parallel.For(
                0,
                expected,
                _ =>
                {
                    var measurement = AiMongoAttributionDiagnostics.StartOperation(
                        AiMongoAttributionOperations.MetricAppend,
                        AiMongoAttributionCommands.Insert,
                        requestedDocuments: 1);
                    measurement.Succeed();
                });

            var item = Assert.Single(
                AiMongoAttributionDiagnostics.SnapshotCurrentProcessOperations());

            Assert.Equal((long)expected, item.Calls);
            Assert.Equal((long)expected, item.RequestedDocuments);
            Assert.Equal((long)expected, item.Successes);
        }

        [Fact]
        public void CurrentProcessIdentity_Should_Match_Bounded_Process_Format()
        {
            Assert.Equal(
                $"{Environment.MachineName}:{Environment.ProcessId}",
                AiMongoAttributionDiagnostics.CurrentProcessIdentity);
        }

        [Fact]
        public void Aggregate_Should_Preserve_Process_Snapshots()
        {
            var operation = new AiMongoAttributionOperationSnapshot(
                AiMongoAttributionOperations.LedgerSequenceNext,
                AiMongoAttributionCommands.FindAndModify,
                7L,
                7L,
                7L,
                0L,
                0L,
                7L,
                0L,
                0L,
                0L,
                123L,
                1L,
                1L,
                1L,
                1L,
                1L,
                1L,
                0L,
                0L,
                1L);
            var process = new AiMongoAttributionProcessSnapshot(
                "host:42",
                3L,
                DateTimeOffset.UnixEpoch,
                new[] { operation },
                Array.Empty<AiMongoAttributionDriverCommandSnapshot>(),
                Array.Empty<AiMongoAttributionDriverPoolSnapshot>());
            var aggregate = new AiMongoAttributionAggregate(
                1,
                3L,
                new[] { operation },
                Array.Empty<AiMongoAttributionDriverCommandSnapshot>(),
                Array.Empty<AiMongoAttributionDriverPoolSnapshot>())
            {
                ProcessSnapshots = new[] { process }
            };

            var actual = Assert.Single(aggregate.ProcessSnapshots);
            Assert.Equal("host:42", actual.ProcessIdentity);
            Assert.Same(operation, Assert.Single(actual.Operations));
        }

        public void Dispose()
        {
            var currentScope = Environment.GetEnvironmentVariable(
                AiMongoAttributionDiagnostics.ScopeEnvironmentVariable);
            AiMongoAttributionDiagnostics.EndScope(currentScope);
            Environment.SetEnvironmentVariable(
                AiMongoAttributionDiagnostics.EnabledEnvironmentVariable,
                previousEnabled);
            Environment.SetEnvironmentVariable(
                AiMongoAttributionDiagnostics.ScopeEnvironmentVariable,
                previousScope);
            AiMongoAttributionDiagnostics.ResetCurrentProcess();
        }

        private static ScopeLease EnableScope()
        {
            Environment.SetEnvironmentVariable(
                AiMongoAttributionDiagnostics.EnabledEnvironmentVariable,
                "1");

            var scope = AiMongoAttributionDiagnostics.BeginScope();
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
                AiMongoAttributionDiagnostics.EndScope(scope);
            }
        }
    }
}
