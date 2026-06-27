using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake runtime instance registry used by reconciler tests.
    /// </summary>
    public sealed class FakeRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
    {
        /// <summary>
        /// Gets runtime instances returned by the fake registry.
        /// </summary>
        public List<AiRuntimeInstanceSnapshot> RuntimeInstances { get; } = [];

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
            AiRuntimeInstanceRegistration registration,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(registration);

            var now = DateTimeOffset.UtcNow;
            var registeredAtUtc = registration.RegisteredAtUtc == default
                ? now
                : registration.RegisteredAtUtc;

            var snapshot = new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = registration.RuntimeInstanceId,
                TenantId = registration.TenantId,
                TenantGroupId = registration.TenantGroupId,
                Status = AiRuntimeInstanceStatus.Ready,
                HostName = registration.HostName,
                ProcessId = registration.ProcessId,
                KubernetesNamespace = registration.KubernetesNamespace,
                KubernetesPodName = registration.KubernetesPodName,
                KubernetesNodeName = registration.KubernetesNodeName,
                WorkerCount = registration.WorkerCount,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                QueueCapacity = registration.QueueCapacity,
                MaxConcurrentRuns = registration.MaxConcurrentRuns,
                AvailableRunSlots = registration.MaxConcurrentRuns,
                IsQueuePaused = false,
                CanAcceptRun = true,
                RegisteredAtUtc = registeredAtUtc,
                LastHeartbeatAtUtc = now,
                SnapshotAtUtc = now,
                RuntimeVersion = registration.RuntimeVersion,
                Metadata = registration.Metadata,
                Role = registration.Role,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = registration.WorkerCount,
                MaxLocalWorkersPerExecution = null,
                HostId = registration.HostId,
                RuntimeId = registration.RuntimeId,
                ControlPlaneHostId = registration.ControlPlaneHostId,
                ControlPlaneId = registration.ControlPlaneId
            };

            RuntimeInstances.RemoveAll(x => string.Equals(
                x.RuntimeInstanceId,
                snapshot.RuntimeInstanceId,
                StringComparison.OrdinalIgnoreCase));

            RuntimeInstances.Add(snapshot);

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
            var current = RuntimeInstances.FirstOrDefault(x => string.Equals(
                x.RuntimeInstanceId,
                runtimeInstanceId,
                StringComparison.OrdinalIgnoreCase));

            if (current is null)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            var updated = Clone(
                current,
                status,
                queuedRunCount,
                runningRunCount,
                activeRunCount,
                availableRunSlots,
                activeWorkerCount,
                availableWorkerCount,
                maxLocalWorkersPerExecution,
                isQueuePaused,
                canAcceptRun);

            RuntimeInstances.Remove(current);
            RuntimeInstances.Add(updated);

            return Task.FromResult<AiRuntimeInstanceSnapshot?>(updated);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                RuntimeInstances.FirstOrDefault(x => string.Equals(
                    x.RuntimeInstanceId,
                    runtimeInstanceId,
                    StringComparison.OrdinalIgnoreCase)));
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
            bool includeStopped = false,
            CancellationToken cancellationToken = default)
        {
            var items = includeStopped
                ? RuntimeInstances.ToList()
                : RuntimeInstances
                    .Where(x => x.Status != AiRuntimeInstanceStatus.Stopped)
                    .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(items);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            return MarkStatusAsync(runtimeInstanceId, AiRuntimeInstanceStatus.Draining);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            return MarkStatusAsync(runtimeInstanceId, AiRuntimeInstanceStatus.Stopped);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            return MarkStatusAsync(runtimeInstanceId, AiRuntimeInstanceStatus.Unhealthy);
        }

        /// <summary>
        /// Marks a runtime instance with a new status.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="status">The new runtime instance status.</param>
        /// <returns>The updated runtime instance snapshot when found; otherwise, null.</returns>
        private Task<AiRuntimeInstanceSnapshot?> MarkStatusAsync(
            string runtimeInstanceId,
            AiRuntimeInstanceStatus status)
        {
            var current = RuntimeInstances.FirstOrDefault(x => string.Equals(
                x.RuntimeInstanceId,
                runtimeInstanceId,
                StringComparison.OrdinalIgnoreCase));

            if (current is null)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            var updated = Clone(
                current,
                status,
                current.QueuedRunCount,
                current.RunningRunCount,
                current.ActiveRunCount,
                current.AvailableRunSlots,
                current.ActiveWorkerCount,
                current.AvailableWorkerCount,
                current.MaxLocalWorkersPerExecution,
                current.IsQueuePaused,
                current.CanAcceptRun);

            RuntimeInstances.Remove(current);
            RuntimeInstances.Add(updated);

            return Task.FromResult<AiRuntimeInstanceSnapshot?>(updated);
        }

        /// <summary>
        /// Clones a runtime instance snapshot with updated visibility fields.
        /// </summary>
        /// <param name="current">The current snapshot.</param>
        /// <param name="status">The updated status.</param>
        /// <param name="queuedRunCount">The queued run count.</param>
        /// <param name="runningRunCount">The running run count.</param>
        /// <param name="activeRunCount">The active run count.</param>
        /// <param name="availableRunSlots">The available run slots.</param>
        /// <param name="activeWorkerCount">The active worker count.</param>
        /// <param name="availableWorkerCount">The available worker count.</param>
        /// <param name="maxLocalWorkersPerExecution">The maximum local workers per execution.</param>
        /// <param name="isQueuePaused">A value indicating whether the runtime queue is paused.</param>
        /// <param name="canAcceptRun">A value indicating whether the runtime can accept a run.</param>
        /// <returns>The cloned snapshot.</returns>
        private static AiRuntimeInstanceSnapshot Clone(
            AiRuntimeInstanceSnapshot current,
            AiRuntimeInstanceStatus status,
            int queuedRunCount,
            int runningRunCount,
            int activeRunCount,
            int? availableRunSlots,
            int? activeWorkerCount,
            int? availableWorkerCount,
            int? maxLocalWorkersPerExecution,
            bool isQueuePaused,
            bool canAcceptRun)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = current.RuntimeInstanceId,
                TenantId = current.TenantId,
                TenantGroupId = current.TenantGroupId,
                Status = status,
                HostName = current.HostName,
                ProcessId = current.ProcessId,
                KubernetesNamespace = current.KubernetesNamespace,
                KubernetesPodName = current.KubernetesPodName,
                KubernetesNodeName = current.KubernetesNodeName,
                WorkerCount = current.WorkerCount,
                QueuedRunCount = queuedRunCount,
                RunningRunCount = runningRunCount,
                ActiveRunCount = activeRunCount,
                QueueCapacity = current.QueueCapacity,
                MaxConcurrentRuns = current.MaxConcurrentRuns,
                AvailableRunSlots = availableRunSlots,
                IsQueuePaused = isQueuePaused,
                CanAcceptRun = canAcceptRun,
                RegisteredAtUtc = current.RegisteredAtUtc,
                LastHeartbeatAtUtc = now,
                SnapshotAtUtc = now,
                RuntimeVersion = current.RuntimeVersion,
                Metadata = current.Metadata,
                Role = current.Role,
                ActiveWorkerCount = activeWorkerCount,
                AvailableWorkerCount = availableWorkerCount,
                MaxLocalWorkersPerExecution = maxLocalWorkersPerExecution,
                HostId = current.HostId,
                RuntimeId = current.RuntimeId,
                ControlPlaneHostId = current.ControlPlaneHostId,
                ControlPlaneId = current.ControlPlaneId
            };
        }
    }
}