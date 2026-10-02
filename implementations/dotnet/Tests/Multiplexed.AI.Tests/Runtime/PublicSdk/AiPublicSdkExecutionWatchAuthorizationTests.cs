using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.PublicSdk;
using Multiplexed.AI.Runtime.Execution.Control;
using Multiplexed.AI.Sdk.Contracts.Watch;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Runtime.Publication;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Runtime.PublicSdk
{
    /// <summary>
    /// Adversarial closure for the public execution Watch authorization boundary. Authorization and immutable
    /// ownership must converge before Decision Ledger history is touched, including caller-partition changes
    /// and corrupted execution-owner metadata.
    /// </summary>
    public sealed class AiPublicSdkExecutionWatchAuthorizationTests
    {
        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("project")]
        [InlineData("namespace")]
        [InlineData("user")]
        public async Task Foreign_Caller_Partitions_Are_Rejected_Before_Dag_Or_Ledger_Reads(string field)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            var identity = ForeignIdentity(field);
            var ledger = new TrackingDecisionLedger();
            var dag = new DagStoreProbe(fixture);
            var boundary = CreateBoundary(fixture, ledger, dag);

            await AssertDeniedAsync(() => fixture.AsAsync(
                () => boundary.WatchAsync(Watch(run.ExecutionId)),
                identity));

            Assert.Equal(0, dag.RecordReads);
            Assert.Equal(0, dag.StateReads);
            Assert.Equal(0, ledger.ReadCalls);
        }

        [Fact]
        public async Task Missing_Publication_Read_Capability_Is_Rejected_Before_Storage_Dag_Or_Ledger()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            var identity = PublicationTestSupport.Identity(actions: ["publish", "execute"]);
            var ledger = new TrackingDecisionLedger();
            var dag = new DagStoreProbe(fixture);
            var boundary = CreateBoundary(fixture, ledger, dag);
            var payloadReads = fixture.MemoryPayloads.Reads;

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AsAsync(
                () => boundary.WatchAsync(Watch(run.ExecutionId)),
                identity));

            Assert.Equal(payloadReads, fixture.MemoryPayloads.Reads);
            Assert.Equal(0, dag.RecordReads);
            Assert.Equal(0, dag.StateReads);
            Assert.Equal(0, ledger.ReadCalls);
        }

        [Fact]
        public async Task Context_Change_During_Pin_Read_Is_Rejected_Before_Dag_Or_Ledger()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            var identity = PublicationTestSupport.Identity();
            var ledger = new TrackingDecisionLedger();
            var dag = new DagStoreProbe(fixture);
            var boundary = CreateBoundary(fixture, ledger, dag);
            fixture.MemoryPayloads.OnRead = (_, value) =>
            {
                identity.UserId = "changed-user";
                return value;
            };

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AsAsync(
                () => boundary.WatchAsync(Watch(run.ExecutionId)),
                identity));

            Assert.Equal(0, dag.RecordReads);
            Assert.Equal(0, dag.StateReads);
            Assert.Equal(0, ledger.ReadCalls);
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("project")]
        [InlineData("namespace")]
        [InlineData("user")]
        public async Task Corrupted_Dag_Owner_Is_Rejected_After_Pin_But_Before_Ledger(string field)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            var record = await fixture.Store.GetRecordAsync(run.ExecutionId)
                ?? throw new InvalidOperationException("Published execution record was not created.");
            var state = await fixture.Store.GetStateAsync(run.ExecutionId)
                ?? throw new InvalidOperationException("Published execution state was not created.");
            CorruptOwner(record.ExecutionContextSnapshot!, field);
            await fixture.Store.CreateAsync(record, state);

            var identity = PublicationTestSupport.Identity();
            var ledger = new TrackingDecisionLedger();
            var dag = new DagStoreProbe(fixture);
            var boundary = CreateBoundary(fixture, ledger, dag);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AsAsync(
                () => boundary.WatchAsync(Watch(run.ExecutionId)),
                identity));

            Assert.Equal(1, dag.RecordReads);
            Assert.Equal(0, dag.StateReads);
            Assert.Equal(0, ledger.ReadCalls);
        }

        [Fact]
        public async Task Authorized_Owner_Reaches_Only_Its_Execution_Ledger_Stream()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            var identity = PublicationTestSupport.Identity();
            var ledger = new TrackingDecisionLedger(
            [
                Entry(run.ExecutionId, 1, AiEngineEvents.Execution.Started),
                Entry("foreign-execution", 1, AiEngineEvents.Execution.Started)
            ]);
            var dag = new DagStoreProbe(fixture);
            var boundary = CreateBoundary(fixture, ledger, dag);

            var result = await fixture.AsAsync(
                () => boundary.WatchAsync(Watch(run.ExecutionId)),
                identity);

            Assert.Equal(run.ExecutionId, result.ExecutionId);
            Assert.Equal(AiSdkExecutionWatchEventKind.Event, result.Kind);
            Assert.Equal(1, result.Sequence);
            Assert.Equal(AiSdkExecutionWatchChannel.Lifecycle, result.Channel);
            Assert.Equal(1, dag.RecordReads);
            Assert.Equal(0, dag.StateReads);
            Assert.Equal(1, ledger.ReadCalls);
            Assert.Equal(new[] { run.ExecutionId }, ledger.ExecutionReads);
        }

        private static AiPublicSdkBoundary CreateBoundary(
            PublicationTestSupport.Fixture fixture,
            TrackingDecisionLedger ledger,
            DagStoreProbe dag)
        {
            var controller = DagTestProxy.Create<IAiSharedRuntimeController>((method, _) =>
                throw new NotSupportedException($"Unexpected shared-controller call: {method.Name}"));
            var control = DagTestProxy.Create<IAiExecutionControlService>((method, _) =>
                throw new NotSupportedException($"Unexpected execution-control call: {method.Name}"));
            var executionControlPlane = DagTestProxy.Create<IAiExecutionControlPlane>((method, _) =>
                throw new NotSupportedException($"Unexpected execution control-plane call: {method.Name}"));
            var replayControlPlane = DagTestProxy.Create<IAiReplayControlPlane>((method, _) =>
                throw new NotSupportedException($"Unexpected replay control-plane call: {method.Name}"));

            return new AiPublicSdkBoundary(
                fixture.Publisher,
                fixture.Runs,
                controller,
                dag.Store,
                new AiDagExecutionCancellationCoordinator(dag.Store, control),
                new AccessorSnapshotProvider(fixture.Accessor),
                fixture.ControlPlane,
                ledger,
                Options.Create(new AiPublicSdkExecutionWatchOptions { RetainedPublicEventLimit = 32 }),
                executionControlPlane,
                replayControlPlane);
        }

        private static AiSdkExecutionWatchRequest Watch(string executionId) => new()
        {
            ExecutionId = executionId,
            IncludeInitialSnapshot = false,
            AfterSequence = 0
        };

        private static RbacExecutionContext ForeignIdentity(string field) => field switch
        {
            "tenant" => PublicationTestSupport.Identity(tenant: "tenant-b"),
            "group" => PublicationTestSupport.Identity(group: "group-b"),
            "project" => PublicationTestSupport.Identity(project: "other-project"),
            "namespace" => PublicationTestSupport.Identity(ns: "other-namespace"),
            "user" => PublicationTestSupport.Identity(user: "user-b"),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        private static void CorruptOwner(ExecutionContextSnapshot owner, string field)
        {
            switch (field)
            {
                case "tenant": owner.TenantId = "tenant-b"; break;
                case "group": owner.TenantGroupId = "group-b"; break;
                case "project": owner.Project = "other-project"; break;
                case "namespace": owner.CurrentNamespace = "other-namespace"; break;
                case "user": owner.UserId = "user-b"; break;
                default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
            }
        }

        private static async Task AssertDeniedAsync(Func<Task> action)
        {
            var error = await Record.ExceptionAsync(action);
            Assert.NotNull(error);
            Assert.True(
                error is UnauthorizedAccessException or KeyNotFoundException,
                $"Expected an authorization/not-found denial but received {error.GetType().Name}: {error.Message}");
        }

        private sealed class AccessorSnapshotProvider : IExecutionContextSnapshotProvider
        {
            private readonly Multiplexed.Rbac.Core.ExecutionContext.IExecutionContextAccessor _accessor;

            internal AccessorSnapshotProvider(Multiplexed.Rbac.Core.ExecutionContext.IExecutionContextAccessor accessor)
            {
                _accessor = accessor;
            }

            public ExecutionContextSnapshot MapToSnapshot()
            {
                var identity = _accessor.Current
                    ?? throw new InvalidOperationException("No test RBAC context is active.");
                return new ExecutionContextSnapshot
                {
                    ContextKey = identity.ContextKey,
                    Project = identity.Project,
                    UserId = identity.UserId,
                    TenantId = identity.TenantId,
                    TenantGroupId = identity.TenantGroupId,
                    CurrentNamespace = identity.CurrentNamespace,
                    Namespaces = identity.Namespaces.Select(entry => new NamespaceEntry
                    {
                        Name = entry.Name,
                        Trns = new HashSet<string>(entry.Trns, StringComparer.Ordinal)
                    }).ToList(),
                    InFlightCount = identity.InFlightCount,
                    TtlSeconds = identity.TtlSeconds
                };
            }
        }

        private static AiDecisionLedgerEntry Entry(string executionId, long sequence, string eventType) => new()
        {
            EntryId = $"entry-{executionId}-{sequence}",
            CorrelationContext = new AiRuntimeLedgerEventCorrelationContext { ExecutionId = executionId },
            Sequence = sequence,
            Category = AiDecisionLedgerCategory.Execution,
            EventType = eventType,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        private sealed class DagStoreProbe
        {
            internal DagStoreProbe(PublicationTestSupport.Fixture fixture)
            {
                Store = DagTestProxy.Create<IAiDagExecutionStore>((method, args) => method.Name switch
                {
                    nameof(IAiDagExecutionStore.GetRecordAsync) => ReadRecordAsync(
                        fixture,
                        (string)args![0]!,
                        (CancellationToken)args[1]!),
                    nameof(IAiDagExecutionStore.GetStateAsync) => ReadStateAsync(
                        fixture,
                        (string)args![0]!,
                        (CancellationToken)args[1]!),
                    _ => throw new NotSupportedException($"Unexpected DAG-store call: {method.Name}")
                });
            }

            internal IAiDagExecutionStore Store { get; }
            internal int RecordReads { get; private set; }
            internal int StateReads { get; private set; }

            private Task<Multiplexed.Abstractions.AI.Execution.AiExecutionRecord?> ReadRecordAsync(
                PublicationTestSupport.Fixture fixture,
                string executionId,
                CancellationToken cancellationToken)
            {
                RecordReads++;
                return fixture.Store.GetRecordAsync(executionId, cancellationToken);
            }

            private Task<Multiplexed.Abstractions.AI.Execution.AiExecutionState?> ReadStateAsync(
                PublicationTestSupport.Fixture fixture,
                string executionId,
                CancellationToken cancellationToken)
            {
                StateReads++;
                return fixture.Store.GetStateAsync(executionId, cancellationToken);
            }
        }

        private sealed class TrackingDecisionLedger : IAiDecisionLedger
        {
            private readonly List<AiDecisionLedgerEntry> _entries;

            internal TrackingDecisionLedger(IEnumerable<AiDecisionLedgerEntry>? entries = null)
            {
                _entries = entries?.ToList() ?? [];
            }

            internal int ReadCalls { get; private set; }
            internal List<string> ExecutionReads { get; } = [];

            public Task AppendAsync(AiDecisionLedgerEntry entry, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _entries.Add(entry);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<AiDecisionLedgerEntry>> GetByExecutionAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadCalls++;
                ExecutionReads.Add(executionId);
                IReadOnlyList<AiDecisionLedgerEntry> result = _entries
                    .Where(entry => entry.CorrelationContext.ExecutionId == executionId)
                    .OrderBy(entry => entry.Sequence)
                    .ToArray();
                return Task.FromResult(result);
            }

            public Task<IReadOnlyList<AiDecisionLedgerEntry>> QueryAsync(
                AiDecisionLedgerQuery query,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadCalls++;
                if (!string.IsNullOrWhiteSpace(query.ExecutionId)) ExecutionReads.Add(query.ExecutionId);

                IEnumerable<AiDecisionLedgerEntry> result = _entries;
                if (!string.IsNullOrWhiteSpace(query.ExecutionId))
                {
                    result = result.Where(entry => entry.CorrelationContext.ExecutionId == query.ExecutionId);
                }
                if (query.SequenceFrom.HasValue) result = result.Where(entry => entry.Sequence >= query.SequenceFrom.Value);
                if (query.SequenceTo.HasValue) result = result.Where(entry => entry.Sequence <= query.SequenceTo.Value);
                result = result.OrderBy(entry => entry.Sequence);
                if (query.Limit.HasValue) result = result.Take(query.Limit.Value);
                return Task.FromResult<IReadOnlyList<AiDecisionLedgerEntry>>(result.ToArray());
            }
        }
    }
}
