using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests health reconciliation observability through the observed runtime instance registry decorator.
    /// </summary>
    public sealed class AiRuntimeInstanceHealthReconcilerDecoratorObservabilityTests
    {
        /// <summary>
        /// Verifies that stale runtime health reconciliation records registry list and mark-unhealthy events through the decorator.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ReconcileAsync_Should_Record_Registry_List_And_MarkUnhealthy_Events_To_Ledger_When_Runtime_Is_Stale()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var runtimeObservability = new FakeRuntimeObservability(ledger);
            var sink = new RuntimeObservabilityAiControlPlaneEventSink(runtimeObservability);
            var observer = new CompositeAiControlPlaneObserver(new[] { sink });
            var innerRegistry = new CapturingRuntimeInstanceRegistry(new[] { CreateStaleRuntimeSnapshot() });
            var observedRegistry = new ObservedAiRuntimeInstanceRegistry(innerRegistry, observer);
            var reconciler = new AiRuntimeInstanceHealthReconciler(
                observedRegistry,
                Options.Create(new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.FromSeconds(1),
                    MarkStaleRuntimeUnhealthy = true,
                    DryRun = false,
                    IncludeReadyRuntimeInstances = true,
                    IncludeBusyRuntimeInstances = true,
                    IgnoreStoppedRuntimeInstances = true,
                    IgnorePausedRuntimeInstances = true,
                    IgnoreDrainingRuntimeInstances = true
                }));

            var result = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(1, result.MarkedUnhealthyCount);
            Assert.Contains(result.Decisions, decision => decision.RuntimeInstanceId == "runtime-1" && decision.Changed && decision.NewStatus == AiRuntimeInstanceStatus.Unhealthy);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, innerRegistry.GetStoredStatus("runtime-1"));
            Assert.Contains(ledger.Records, record => record.EventType == "control.instanceregistry.runtime-instance-list.operationstarted" && record.Outcome == AiDecisionLedgerOutcome.Started);
            Assert.Contains(ledger.Records, record => record.EventType == "control.instanceregistry.runtime-instance-list.succeeded" && record.Outcome == AiDecisionLedgerOutcome.Succeeded);
            Assert.Contains(ledger.Records, record => record.EventType == "control.instanceregistry.runtime-instance-mark-unhealthy.operationstarted" && record.Outcome == AiDecisionLedgerOutcome.Started && record.Metadata["runtime.instance.id"] == "runtime-1");
            Assert.Contains(ledger.Records, record => record.EventType == "control.instanceregistry.runtime-instance-mark-unhealthy.succeeded" && record.Outcome == AiDecisionLedgerOutcome.Succeeded && record.Metadata["runtime.instance.id"] == "runtime-1");
        }

        /// <summary>
        /// Verifies that dry-run health reconciliation records list events but does not record mark-unhealthy events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ReconcileAsync_Should_Record_List_Events_Only_When_Health_Reconciliation_Is_DryRun()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var runtimeObservability = new FakeRuntimeObservability(ledger);
            var sink = new RuntimeObservabilityAiControlPlaneEventSink(runtimeObservability);
            var observer = new CompositeAiControlPlaneObserver(new[] { sink });
            var innerRegistry = new CapturingRuntimeInstanceRegistry(new[] { CreateStaleRuntimeSnapshot() });
            var observedRegistry = new ObservedAiRuntimeInstanceRegistry(innerRegistry, observer);
            var reconciler = new AiRuntimeInstanceHealthReconciler(
                observedRegistry,
                Options.Create(new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.FromSeconds(1),
                    MarkStaleRuntimeUnhealthy = true,
                    DryRun = true,
                    IncludeReadyRuntimeInstances = true,
                    IncludeBusyRuntimeInstances = true
                }));

            var result = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, innerRegistry.GetStoredStatus("runtime-1"));
            Assert.Contains(ledger.Records, record => record.EventType == "control.instanceregistry.runtime-instance-list.succeeded" && record.Outcome == AiDecisionLedgerOutcome.Succeeded);
            Assert.DoesNotContain(ledger.Records, record => record.EventType.Contains("runtime-instance-mark-unhealthy", StringComparison.Ordinal));
        }

        /// <summary>
        /// Creates a stale runtime instance snapshot.
        /// </summary>
        /// <returns>The stale runtime instance snapshot.</returns>
        private static AiRuntimeInstanceSnapshot CreateStaleRuntimeSnapshot()
        {
            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = "runtime-1",
                ControlPlaneId = "control-plane-1",
                TenantId = "tenant-a",
                TenantGroupId = "group-a",
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 2,
                MaxConcurrentRuns = 4,
                QueueCapacity = 8,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                CanAcceptRun = true,
                IsQueuePaused = false,
                RegisteredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pipelineKey"] = "pipeline-a"
                }
            };
        }

        /// <summary>
        /// Capturing runtime instance registry used by health reconciliation tests.
        /// </summary>
        private sealed class CapturingRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly Dictionary<string, AiRuntimeInstanceSnapshot> snapshots = new(StringComparer.Ordinal);

            /// <summary>
            /// Initializes a new instance of the <see cref="CapturingRuntimeInstanceRegistry"/> class.
            /// </summary>
            /// <param name="snapshots">The initial runtime instance snapshots.</param>
            public CapturingRuntimeInstanceRegistry(
                IEnumerable<AiRuntimeInstanceSnapshot> snapshots)
            {
                foreach (var snapshot in snapshots)
                {
                    this.snapshots[snapshot.RuntimeInstanceId] = snapshot;
                }
            }

            /// <summary>
            /// Gets the stored runtime instance status.
            /// </summary>
            /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
            /// <returns>The stored runtime instance status.</returns>
            public AiRuntimeInstanceStatus? GetStoredStatus(
                string runtimeInstanceId)
            {
                return this.snapshots.TryGetValue(runtimeInstanceId, out var snapshot)
                    ? snapshot.Status
                    : null;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                var snapshot = CreateSnapshotFromRegistration(registration, AiRuntimeInstanceStatus.Ready);
                this.snapshots[registration.RuntimeInstanceId] = snapshot;
                return Task.FromResult(snapshot);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
                string runtimeInstanceId,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? availableRunSlots,
                int? activeWorkerCount,
                int? availableWorkerCount,
                int? maxLocalWorkersPerExecution,
                bool isQueuePaused,
                bool canAcceptRun,
                AiRuntimeInstanceStatus status,
                CancellationToken cancellationToken = default)
            {
                if (!this.snapshots.TryGetValue(runtimeInstanceId, out var existing))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                var updated = CloneSnapshot(existing, status);
                this.snapshots[runtimeInstanceId] = updated;
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(updated);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                this.snapshots.TryGetValue(runtimeInstanceId, out var snapshot);
                return Task.FromResult(snapshot);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<AiRuntimeInstanceSnapshot> result = this.snapshots.Values
                    .Where(snapshot => includeStopped || snapshot.Status != AiRuntimeInstanceStatus.Stopped)
                    .OrderBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                    .ToArray();

                return Task.FromResult(result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return this.ChangeStatusAsync(runtimeInstanceId, AiRuntimeInstanceStatus.Draining);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return this.ChangeStatusAsync(runtimeInstanceId, AiRuntimeInstanceStatus.Unhealthy);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                if (!this.snapshots.TryGetValue(runtimeInstanceId, out var existing))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                var stopped = CloneSnapshot(existing, AiRuntimeInstanceStatus.Stopped);
                this.snapshots.Remove(runtimeInstanceId);
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(stopped);
            }

            private Task<AiRuntimeInstanceSnapshot?> ChangeStatusAsync(
                string runtimeInstanceId,
                AiRuntimeInstanceStatus status)
            {
                if (!this.snapshots.TryGetValue(runtimeInstanceId, out var existing))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                var updated = CloneSnapshot(existing, status);
                this.snapshots[runtimeInstanceId] = updated;
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(updated);
            }

            private static AiRuntimeInstanceSnapshot CreateSnapshotFromRegistration(
                AiRuntimeInstanceRegistration registration,
                AiRuntimeInstanceStatus status)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = registration.RuntimeInstanceId,
                    ControlPlaneId = registration.ControlPlaneId,
                    TenantId = registration.TenantId,
                    TenantGroupId = registration.TenantGroupId,
                    Status = status,
                    WorkerCount = registration.WorkerCount,
                    MaxConcurrentRuns = registration.MaxConcurrentRuns,
                    QueueCapacity = registration.QueueCapacity,
                    QueuedRunCount = 0,
                    RunningRunCount = 0,
                    ActiveRunCount = 0,
                    CanAcceptRun = true,
                    IsQueuePaused = false,
                    RegisteredAtUtc = registration.RegisteredAtUtc,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    Metadata = registration.Metadata
                };
            }

            private static AiRuntimeInstanceSnapshot CloneSnapshot(
                AiRuntimeInstanceSnapshot snapshot,
                AiRuntimeInstanceStatus status)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = snapshot.RuntimeInstanceId,
                    ControlPlaneId = snapshot.ControlPlaneId,
                    TenantId = snapshot.TenantId,
                    TenantGroupId = snapshot.TenantGroupId,
                    Status = status,
                    WorkerCount = snapshot.WorkerCount,
                    MaxConcurrentRuns = snapshot.MaxConcurrentRuns,
                    QueueCapacity = snapshot.QueueCapacity,
                    QueuedRunCount = snapshot.QueuedRunCount,
                    RunningRunCount = snapshot.RunningRunCount,
                    ActiveRunCount = snapshot.ActiveRunCount,
                    CanAcceptRun = snapshot.CanAcceptRun,
                    IsQueuePaused = snapshot.IsQueuePaused,
                    RegisteredAtUtc = snapshot.RegisteredAtUtc,
                    LastHeartbeatAtUtc = snapshot.LastHeartbeatAtUtc,
                    Metadata = snapshot.Metadata
                };
            }
        }

        /// <summary>
        /// Fake runtime observability facade.
        /// </summary>
        private sealed class FakeRuntimeObservability : IAiRuntimeObservability
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="FakeRuntimeObservability"/> class.
            /// </summary>
            /// <param name="ledger">The ledger recorder.</param>
            public FakeRuntimeObservability(
                IAiDecisionLedgerRecorder ledger)
            {
                this.Ledger = ledger;
            }

            /// <inheritdoc />
            public IAiRuntimeMetrics Metrics => throw new NotSupportedException();

            /// <inheritdoc />
            public IAiRuntimeTracer Tracer => throw new NotSupportedException();

            /// <inheritdoc />
            public IAiDecisionLedgerRecorder Ledger { get; }

            /// <inheritdoc />
            public IAiRuntimeCorrelationAccessor Correlation => throw new NotSupportedException();
        }

        /// <summary>
        /// Captures ledger records.
        /// </summary>
        private sealed class CapturingDecisionLedgerRecorder : IAiDecisionLedgerRecorder
        {
            /// <summary>
            /// Gets captured ledger records.
            /// </summary>
            public List<LedgerRecord> Records { get; } = new();

            /// <inheritdoc />
            public Task RecordAsync(
                AiRuntimeLedgerEventCorrelationContext context,
                AiDecisionLedgerCategory category,
                string eventType,
                AiDecisionLedgerOutcome outcome,
                string? reason = null,
                IReadOnlyDictionary<string, string?>? metadata = null,
                CancellationToken cancellationToken = default)
            {
                this.Records.Add(new LedgerRecord(context, category, eventType, outcome, reason, metadata ?? new Dictionary<string, string?>()));
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Captured ledger record.
        /// </summary>
        /// <param name="Context">The ledger context.</param>
        /// <param name="Category">The ledger category.</param>
        /// <param name="EventType">The ledger event type.</param>
        /// <param name="Outcome">The ledger outcome.</param>
        /// <param name="Reason">The optional reason.</param>
        /// <param name="Metadata">The metadata.</param>
        private sealed record LedgerRecord(
            AiRuntimeLedgerEventCorrelationContext Context,
            AiDecisionLedgerCategory Category,
            string EventType,
            AiDecisionLedgerOutcome Outcome,
            string? Reason,
            IReadOnlyDictionary<string, string?> Metadata);
    }
}
