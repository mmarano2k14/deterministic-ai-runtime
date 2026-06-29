using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability.Context;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Observability decorator for runtime instance registry implementations.
    /// </summary>
    public sealed class ObservedAiRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
    {
        private const string RuntimeInstanceRegisterOperation = "runtime-instance-register";
        private const string RuntimeInstanceHeartbeatOperation = "runtime-instance-heartbeat";
        private const string RuntimeInstanceGetOperation = "runtime-instance-get";
        private const string RuntimeInstanceListOperation = "runtime-instance-list";
        private const string RuntimeInstanceMarkDrainingOperation = "runtime-instance-mark-draining";
        private const string RuntimeInstanceMarkUnhealthyOperation = "runtime-instance-mark-unhealthy";
        private const string RuntimeInstanceUnregisterOperation = "runtime-instance-unregister";
        private readonly IAiRuntimeInstanceRegistry inner;
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObservedAiRuntimeInstanceRegistry"/> class.
        /// </summary>
        /// <param name="inner">The decorated runtime instance registry.</param>
        /// <param name="observer">The control-plane observer.</param>
        public ObservedAiRuntimeInstanceRegistry(
            IAiRuntimeInstanceRegistry inner,
            IAiControlPlaneObserver observer)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.observer = observer ?? new NoopAiControlPlaneObserver();
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot> RegisterAsync(
            AiRuntimeInstanceRegistration registration,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(registration);
            var startedAtUtc = DateTimeOffset.UtcNow;
            await this.RecordStartedAsync(RuntimeInstanceRegisterOperation, registration.RuntimeInstanceId, registration.ControlPlaneId, registration.TenantId, registration.TenantGroupId, null, cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await this.inner.RegisterAsync(registration, cancellationToken).ConfigureAwait(false);
                await this.RecordCompletedAsync(RuntimeInstanceRegisterOperation, snapshot.RuntimeInstanceId, snapshot.ControlPlaneId, snapshot.TenantId, snapshot.TenantGroupId, AiControlPlaneOperationOutcome.Succeeded, null, startedAtUtc, CreateSnapshotProperties(snapshot), cancellationToken).ConfigureAwait(false);
                return snapshot;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await this.RecordFailedAsync(RuntimeInstanceRegisterOperation, registration.RuntimeInstanceId, registration.ControlPlaneId, registration.TenantId, registration.TenantGroupId, exception, startedAtUtc, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
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
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            var startedAtUtc = DateTimeOffset.UtcNow;
            await this.RecordStartedAsync(RuntimeInstanceHeartbeatOperation, runtimeInstanceId, null, null, null, CreateHeartbeatProperties(queuedRunCount, runningRunCount, activeRunCount, availableRunSlots, activeWorkerCount, availableWorkerCount, maxLocalWorkersPerExecution, isQueuePaused, canAcceptRun, status), cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await this.inner.HeartbeatAsync(runtimeInstanceId, queuedRunCount, runningRunCount, activeRunCount, availableRunSlots, activeWorkerCount, availableWorkerCount, maxLocalWorkersPerExecution, isQueuePaused, canAcceptRun, status, cancellationToken).ConfigureAwait(false);
                var outcome = snapshot is null ? AiControlPlaneOperationOutcome.CompletedWithIssues : AiControlPlaneOperationOutcome.Succeeded;
                var failureReason = snapshot is null ? "runtime-instance-not-found" : null;
                await this.RecordCompletedAsync(RuntimeInstanceHeartbeatOperation, runtimeInstanceId, snapshot?.ControlPlaneId, snapshot?.TenantId, snapshot?.TenantGroupId, outcome, failureReason, startedAtUtc, MergeProperties(CreateHeartbeatProperties(queuedRunCount, runningRunCount, activeRunCount, availableRunSlots, activeWorkerCount, availableWorkerCount, maxLocalWorkersPerExecution, isQueuePaused, canAcceptRun, status), snapshot is null ? null : CreateSnapshotProperties(snapshot)), cancellationToken).ConfigureAwait(false);
                return snapshot;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await this.RecordFailedAsync(RuntimeInstanceHeartbeatOperation, runtimeInstanceId, null, null, null, exception, startedAtUtc, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            var startedAtUtc = DateTimeOffset.UtcNow;
            await this.RecordStartedAsync(RuntimeInstanceGetOperation, runtimeInstanceId, null, null, null, null, cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await this.inner.GetAsync(runtimeInstanceId, cancellationToken).ConfigureAwait(false);
                var outcome = snapshot is null ? AiControlPlaneOperationOutcome.CompletedWithIssues : AiControlPlaneOperationOutcome.Succeeded;
                var failureReason = snapshot is null ? "runtime-instance-not-found-or-not-visible" : null;
                await this.RecordCompletedAsync(RuntimeInstanceGetOperation, runtimeInstanceId, snapshot?.ControlPlaneId, snapshot?.TenantId, snapshot?.TenantGroupId, outcome, failureReason, startedAtUtc, snapshot is null ? null : CreateSnapshotProperties(snapshot), cancellationToken).ConfigureAwait(false);
                return snapshot;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await this.RecordFailedAsync(RuntimeInstanceGetOperation, runtimeInstanceId, null, null, null, exception, startedAtUtc, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
            bool includeStopped = false,
            CancellationToken cancellationToken = default)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            await this.RecordStartedAsync(RuntimeInstanceListOperation, null, null, null, null, new Dictionary<string, object?> { ["includeStopped"] = includeStopped }, cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshots = await this.inner.ListAsync(includeStopped, cancellationToken).ConfigureAwait(false);
                await this.RecordCompletedAsync(RuntimeInstanceListOperation, null, null, null, null, AiControlPlaneOperationOutcome.Succeeded, null, startedAtUtc, new Dictionary<string, object?> { ["includeStopped"] = includeStopped, ["runtimeInstanceCount"] = snapshots.Count }, cancellationToken).ConfigureAwait(false);
                return snapshots;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await this.RecordFailedAsync(RuntimeInstanceListOperation, null, null, null, null, exception, startedAtUtc, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            return this.RecordStatusMutationAsync(RuntimeInstanceMarkDrainingOperation, runtimeInstanceId, () => this.inner.MarkDrainingAsync(runtimeInstanceId, cancellationToken), cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            return this.RecordStatusMutationAsync(RuntimeInstanceMarkUnhealthyOperation, runtimeInstanceId, () => this.inner.MarkUnhealthyAsync(runtimeInstanceId, cancellationToken), cancellationToken);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            var startedAtUtc = DateTimeOffset.UtcNow;
            await this.RecordStartedAsync(RuntimeInstanceUnregisterOperation, runtimeInstanceId, null, null, null, null, cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await this.inner.UnregisterAsync(runtimeInstanceId, cancellationToken).ConfigureAwait(false);
                var outcome = snapshot is null ? AiControlPlaneOperationOutcome.CompletedWithIssues : AiControlPlaneOperationOutcome.Succeeded;
                var failureReason = snapshot is null ? "runtime-instance-not-found" : null;
                await this.RecordCompletedAsync(RuntimeInstanceUnregisterOperation, runtimeInstanceId, snapshot?.ControlPlaneId, snapshot?.TenantId, snapshot?.TenantGroupId, outcome, failureReason, startedAtUtc, snapshot is null ? null : CreateSnapshotProperties(snapshot), cancellationToken).ConfigureAwait(false);
                return snapshot;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await this.RecordFailedAsync(RuntimeInstanceUnregisterOperation, runtimeInstanceId, null, null, null, exception, startedAtUtc, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<AiRuntimeInstanceSnapshot?> RecordStatusMutationAsync(
            string operation,
            string runtimeInstanceId,
            Func<Task<AiRuntimeInstanceSnapshot?>> action,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            var startedAtUtc = DateTimeOffset.UtcNow;
            await this.RecordStartedAsync(operation, runtimeInstanceId, null, null, null, null, cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await action().ConfigureAwait(false);
                var outcome = snapshot is null ? AiControlPlaneOperationOutcome.CompletedWithIssues : AiControlPlaneOperationOutcome.Succeeded;
                var failureReason = snapshot is null ? "runtime-instance-not-found" : null;
                await this.RecordCompletedAsync(operation, runtimeInstanceId, snapshot?.ControlPlaneId, snapshot?.TenantId, snapshot?.TenantGroupId, outcome, failureReason, startedAtUtc, snapshot is null ? null : CreateSnapshotProperties(snapshot), cancellationToken).ConfigureAwait(false);
                return snapshot;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await this.RecordFailedAsync(operation, runtimeInstanceId, null, null, null, exception, startedAtUtc, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        private Task RecordStartedAsync(
            string operation,
            string? runtimeInstanceId,
            string? controlPlaneId,
            string? tenantId,
            string? tenantGroupId,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            return this.RecordEventAsync(AiControlPlaneEventType.OperationStarted, operation, runtimeInstanceId, controlPlaneId, tenantId, tenantGroupId, null, null, null, properties, cancellationToken);
        }

        private Task RecordCompletedAsync(
            string operation,
            string? runtimeInstanceId,
            string? controlPlaneId,
            string? tenantId,
            string? tenantGroupId,
            AiControlPlaneOperationOutcome outcome,
            string? failureReason,
            DateTimeOffset startedAtUtc,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            return this.RecordEventAsync(AiControlPlaneEventType.OperationCompleted, operation, runtimeInstanceId, controlPlaneId, tenantId, tenantGroupId, outcome, failureReason, CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow), properties, cancellationToken);
        }

        private Task RecordFailedAsync(
            string operation,
            string? runtimeInstanceId,
            string? controlPlaneId,
            string? tenantId,
            string? tenantGroupId,
            Exception exception,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            return this.RecordEventAsync(AiControlPlaneEventType.OperationFailed, operation, runtimeInstanceId, controlPlaneId, tenantId, tenantGroupId, AiControlPlaneOperationOutcome.Failed, exception.GetType().Name, CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow), new Dictionary<string, object?> { ["exception.type"] = exception.GetType().FullName, ["exception.message"] = exception.Message }, cancellationToken);
        }

        private async Task RecordEventAsync(
            AiControlPlaneEventType eventType,
            string operation,
            string? runtimeInstanceId,
            string? controlPlaneId,
            string? tenantId,
            string? tenantGroupId,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            long? durationMs,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.observer.RecordAsync(
                    new AiControlPlaneEvent
                    {
                        EventType = eventType,
                        Area = AiControlPlaneArea.InstanceRegistry,
                        Operation = operation,
                        Outcome = outcome,
                        FailureReason = failureReason,
                        DurationMs = durationMs,
                        Correlation = new AiRuntimeExecutionCorrelationContext
                        {
                            CorrelationId = string.IsNullOrWhiteSpace(runtimeInstanceId) ? Guid.NewGuid().ToString("N") : runtimeInstanceId,
                            RuntimeInstanceId = runtimeInstanceId
                        },
                        Properties = MergeProperties(
                            properties,
                            new Dictionary<string, object?>
                            {
                                ["runtimeInstanceId"] = runtimeInstanceId,
                                ["controlPlaneId"] = controlPlaneId,
                                ["tenantId"] = tenantId,
                                ["tenantGroupId"] = tenantGroupId
                            })
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break registry operations.
            }
        }

        private static IReadOnlyDictionary<string, object?> CreateSnapshotProperties(
            AiRuntimeInstanceSnapshot snapshot)
        {
            return new Dictionary<string, object?>
            {
                ["runtimeInstanceId"] = snapshot.RuntimeInstanceId,
                ["controlPlaneId"] = snapshot.ControlPlaneId,
                ["tenantId"] = snapshot.TenantId,
                ["tenantGroupId"] = snapshot.TenantGroupId,
                ["status"] = snapshot.Status.ToString(),
                ["role"] = snapshot.Role.ToString(),
                ["workerCount"] = snapshot.WorkerCount,
                ["maxConcurrentRuns"] = snapshot.MaxConcurrentRuns,
                ["queueCapacity"] = snapshot.QueueCapacity,
                ["queuedRunCount"] = snapshot.QueuedRunCount,
                ["runningRunCount"] = snapshot.RunningRunCount,
                ["activeRunCount"] = snapshot.ActiveRunCount,
                ["canAcceptRun"] = snapshot.CanAcceptRun,
                ["isQueuePaused"] = snapshot.IsQueuePaused
            };
        }

        private static IReadOnlyDictionary<string, object?> CreateHeartbeatProperties(
            int queuedRunCount,
            int runningRunCount,
            int activeRunCount,
            int? availableRunSlots,
            int? activeWorkerCount,
            int? availableWorkerCount,
            int? maxLocalWorkersPerExecution,
            bool isQueuePaused,
            bool canAcceptRun,
            AiRuntimeInstanceStatus status)
        {
            return new Dictionary<string, object?>
            {
                ["queuedRunCount"] = queuedRunCount,
                ["runningRunCount"] = runningRunCount,
                ["activeRunCount"] = activeRunCount,
                ["availableRunSlots"] = availableRunSlots,
                ["activeWorkerCount"] = activeWorkerCount,
                ["availableWorkerCount"] = availableWorkerCount,
                ["maxLocalWorkersPerExecution"] = maxLocalWorkersPerExecution,
                ["isQueuePaused"] = isQueuePaused,
                ["canAcceptRun"] = canAcceptRun,
                ["status"] = status.ToString()
            };
        }

        private static IReadOnlyDictionary<string, object?> MergeProperties(
            IReadOnlyDictionary<string, object?>? first,
            IReadOnlyDictionary<string, object?>? second)
        {
            var merged = new Dictionary<string, object?>();
            if (first is not null)
            {
                foreach (var pair in first)
                {
                    merged[pair.Key] = pair.Value;
                }
            }
            if (second is not null)
            {
                foreach (var pair in second)
                {
                    merged[pair.Key] = pair.Value;
                }
            }
            return merged;
        }

        private static long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }
    }
}
