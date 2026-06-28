using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Tests.Fixtures;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests recovery reconciler observability events.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryReconcilerObservabilityTests
    {
        /// <summary>
        /// Verifies that recovery reconciliation records started and completed control-plane events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ReconcileAsync_Should_Record_Recovery_Reconciliation_Started_And_Completed_Events()
        {
            var observer = new CapturingControlPlaneObserver();

            var reconciler = new AiRuntimeExecutionRecoveryReconciler(
                new ThrowingRuntimeInstanceRegistry(),
                new FakeRuntimeRunExecutionIndex(),
                new FakeSharedRunOwnershipResolver(),
                new FakeRuntimeExecutionRecoveryTransitionService(),
                Options.Create(
                    new AiRuntimeExecutionRecoveryReconciliationOptions
                    {
                        Enabled = true
                    }),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneArea.Recovery, observer.Events[0].Area);
            Assert.Equal("runtime-execution-recovery-reconcile", observer.Events[0].Operation);
            Assert.Null(observer.Events[0].Outcome);
            Assert.Null(observer.Events[0].FailureReason);

            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.Recovery, observer.Events[1].Area);
            Assert.Equal("runtime-execution-recovery-reconcile", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, observer.Events[1].Outcome);
            Assert.Null(observer.Events[1].FailureReason);

            Assert.NotNull(observer.Events[1].Properties);
            Assert.Equal(result.ScannedRuntimeInstanceCount.ToString(), observer.Events[1].Properties!["scannedRuntimeInstanceCount"]?.ToString());
            Assert.Equal(result.IgnoredRuntimeInstanceCount.ToString(), observer.Events[1].Properties!["ignoredRuntimeInstanceCount"]?.ToString());
            Assert.Equal(result.DiscoveredUnfinishedRunCount.ToString(), observer.Events[1].Properties!["discoveredUnfinishedRunCount"]?.ToString());
            Assert.Equal(result.RecoveredRunCount.ToString(), observer.Events[1].Properties!["recoveredRunCount"]?.ToString());
            Assert.Equal(result.Decisions.Count.ToString(), observer.Events[1].Properties!["decisionCount"]?.ToString());
        }

        /// <summary>
        /// Verifies that disabled recovery reconciliation does not record control-plane events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Record_Recovery_Reconciliation_Events_When_Disabled()
        {
            var observer = new CapturingControlPlaneObserver();

            var reconciler = new AiRuntimeExecutionRecoveryReconciler(
                new FakeRuntimeInstanceRegistry(),
                new FakeRuntimeRunExecutionIndex(),
                new FakeSharedRunOwnershipResolver(),
                new FakeRuntimeExecutionRecoveryTransitionService(),
                Options.Create(
                    new AiRuntimeExecutionRecoveryReconciliationOptions
                    {
                        Enabled = false
                    }),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Empty(observer.Events);
        }

        /// <summary>
        /// Verifies that recovery reconciliation records a failed control-plane event when reconciliation throws.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ReconcileAsync_Should_Record_Recovery_Reconciliation_Failed_Event_When_Reconciliation_Throws()
        {
            var observer = new CapturingControlPlaneObserver();

            var reconciler = new AiRuntimeExecutionRecoveryReconciler(
                new ThrowingRuntimeInstanceRegistry(),
                new FakeRuntimeRunExecutionIndex(),
                new FakeSharedRunOwnershipResolver(),
                new FakeRuntimeExecutionRecoveryTransitionService(),
                Options.Create(
                    new AiRuntimeExecutionRecoveryReconciliationOptions
                    {
                        Enabled = true
                    }),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                    () => reconciler.ReconcileAsync(CancellationToken.None))
                .ConfigureAwait(false);

            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneArea.Recovery, observer.Events[0].Area);
            Assert.Equal("runtime-execution-recovery-reconcile", observer.Events[0].Operation);

            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.Recovery, observer.Events[1].Area);
            Assert.Equal("runtime-execution-recovery-reconcile", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);

            Assert.NotNull(observer.Events[1].Properties);
            Assert.Equal(typeof(InvalidOperationException).FullName, observer.Events[1].Properties!["exception.type"]?.ToString());
            Assert.Equal("registry failed", observer.Events[1].Properties!["exception.message"]?.ToString());
        }

        /// <summary>
        /// Runtime instance registry that always throws when listed.
        /// </summary>
        /// <summary>
        /// Runtime instance registry that always throws when listed.
        /// </summary>
        /// <summary>
        /// Runtime instance registry that always throws when listed.
        /// </summary>
        private sealed class ThrowingRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
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
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("registry failed");
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(registration);

                return Task.FromResult(
                    new AiRuntimeInstanceSnapshot
                    {
                        RuntimeInstanceId = registration.RuntimeInstanceId,
                        Status = AiRuntimeInstanceStatus.Ready
                    });
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }
        }

        /// <summary>
        /// Captures control-plane events emitted by the reconciler.
        /// </summary>
        private sealed class CapturingControlPlaneObserver : IAiControlPlaneObserver
        {
            /// <summary>
            /// Gets the captured control-plane events.
            /// </summary>
            public List<AiControlPlaneEvent> Events { get; } = new();

            /// <inheritdoc />
            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                this.Events.Add(controlPlaneEvent);

                return Task.CompletedTask;
            }
        }
    }
}