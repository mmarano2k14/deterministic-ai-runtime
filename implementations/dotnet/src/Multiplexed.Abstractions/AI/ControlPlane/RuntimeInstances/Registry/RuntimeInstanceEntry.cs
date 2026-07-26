using System;
using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Represents the persisted registry entry for a runtime instance.
    /// </summary>
    /// <remarks>
    /// This model is used by runtime instance registries to preserve runtime visibility,
    /// heartbeat state, local queue state, local run capacity, local worker capacity,
    /// and tenant ownership before projecting the data into <see cref="AiRuntimeInstanceSnapshot"/>.
    /// </remarks>
    public sealed class RuntimeInstanceEntry
    {
        /// <summary>
        /// Gets the runtime process, pod, or replica identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the tenant identifier that owns this runtime instance, when tenant-scoped.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the tenant group identifier that owns this runtime instance, when group-scoped.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the logical control-plane identifier that owns this runtime instance entry.
        /// </summary>
        public string? ControlPlaneId { get; init; }

        /// <summary>
        /// Gets the immutable identity of the exact host incarnation that owns this runtime instance.
        /// </summary>
        /// <remarks>
        /// Multiple independently addressable runtime instances may share this identity when they
        /// belong to the same runtime pool host.
        /// </remarks>
        public string? HostId { get; init; }

        /// <summary>
        /// Gets the logical runtime pool identifier that owns this runtime instance.
        /// </summary>
        public string? PoolId { get; init; }

        /// <summary>
        /// Gets the logical runtime identity inside the owning host.
        /// </summary>
        public string? RuntimeId { get; init; }

        /// <summary>
        /// Gets the control-plane host identity that owns or manages this runtime instance.
        /// </summary>
        public string? ControlPlaneHostId { get; init; }

        /// <summary>
        /// Gets the role of this runtime instance.
        /// </summary>
        public AiRuntimeInstanceRole Role { get; init; }

        /// <summary>
        /// Gets the current runtime instance status.
        /// </summary>
        public AiRuntimeInstanceStatus Status { get; init; }

        /// <summary>
        /// Gets the optional host name where the runtime instance is running.
        /// </summary>
        public string? HostName { get; init; }

        /// <summary>
        /// Gets the optional process id for local diagnostics.
        /// </summary>
        public int? ProcessId { get; init; }

        /// <summary>
        /// Gets the optional Kubernetes namespace.
        /// </summary>
        public string? KubernetesNamespace { get; init; }

        /// <summary>
        /// Gets the optional Kubernetes pod name.
        /// </summary>
        public string? KubernetesPodName { get; init; }

        /// <summary>
        /// Gets the optional Kubernetes node name.
        /// </summary>
        public string? KubernetesNodeName { get; init; }

        /// <summary>
        /// Gets the total number of local workers owned by this runtime instance.
        /// </summary>
        public int WorkerCount { get; init; }

        /// <summary>
        /// Gets the number of local workers currently assigned to active executions.
        /// </summary>
        public int? ActiveWorkerCount { get; init; }

        /// <summary>
        /// Gets the number of local workers currently available on this runtime instance.
        /// </summary>
        public int? AvailableWorkerCount { get; init; }

        /// <summary>
        /// Gets the maximum number of local workers allowed to work on one execution.
        /// </summary>
        public int? MaxLocalWorkersPerExecution { get; init; }

        /// <summary>
        /// Gets the number of runs currently queued locally.
        /// </summary>
        public int QueuedRunCount { get; init; }

        /// <summary>
        /// Gets the number of runs currently running locally.
        /// </summary>
        public int RunningRunCount { get; init; }

        /// <summary>
        /// Gets the number of active runs known by the local runtime controller.
        /// </summary>
        public int ActiveRunCount { get; init; }

        /// <summary>
        /// Gets the maximum local queue capacity.
        /// </summary>
        public int? QueueCapacity { get; init; }

        /// <summary>
        /// Gets the maximum number of local runs that can execute concurrently.
        /// </summary>
        public int? MaxConcurrentRuns { get; init; }

        /// <summary>
        /// Gets the number of currently available local run slots.
        /// </summary>
        public int? AvailableRunSlots { get; init; }

        /// <summary>
        /// Gets a value indicating whether the local runtime queue is paused.
        /// </summary>
        public bool IsQueuePaused { get; init; }

        /// <summary>
        /// Gets a value indicating whether this runtime instance can accept a new local run.
        /// </summary>
        public bool CanAcceptRun { get; init; }

        /// <summary>
        /// Gets the UTC timestamp when this runtime instance was registered.
        /// </summary>
        public DateTimeOffset RegisteredAtUtc { get; init; }

        /// <summary>
        /// Gets the UTC timestamp of the last heartbeat received from this runtime instance.
        /// </summary>
        public DateTimeOffset LastHeartbeatAtUtc { get; init; }

        /// <summary>
        /// Gets the optional runtime version, package version, or build version.
        /// </summary>
        public string? RuntimeVersion { get; init; }

        /// <summary>
        /// Gets optional non-authoritative metadata for diagnostics, observability, provider labels,
        /// dashboards, zones, or deployment information.
        /// </summary>
        /// <remarks>
        /// Metadata must not control routing, membership, lifecycle, draining, capacity selection,
        /// or recovery. Any value required for correctness must be represented by a typed property.
        /// </remarks>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Creates a new runtime instance entry from a registration request.
        /// </summary>
        /// <param name="registration">The runtime instance registration.</param>
        /// <param name="now">The current UTC timestamp.</param>
        /// <returns>The created runtime instance entry.</returns>
        public static RuntimeInstanceEntry Create(
            AiRuntimeInstanceRegistration registration,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(registration);

            var canAcceptRun =
                registration.Role == AiRuntimeInstanceRole.Runtime;

            return new RuntimeInstanceEntry
            {
                RuntimeInstanceId = registration.RuntimeInstanceId,
                TenantId = registration.TenantId,
                TenantGroupId = registration.TenantGroupId,
                ControlPlaneId = registration.ControlPlaneId,
                HostId = registration.HostId,
                PoolId = registration.PoolId,
                RuntimeId = registration.RuntimeId,
                ControlPlaneHostId = registration.ControlPlaneHostId,
                Role = registration.Role,
                Status = AiRuntimeInstanceStatus.Ready,
                HostName = registration.HostName,
                ProcessId = registration.ProcessId,
                KubernetesNamespace = registration.KubernetesNamespace,
                KubernetesPodName = registration.KubernetesPodName,
                KubernetesNodeName = registration.KubernetesNodeName,
                WorkerCount = registration.WorkerCount,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = canAcceptRun ? registration.WorkerCount : 0,
                MaxLocalWorkersPerExecution = null,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                QueueCapacity = registration.QueueCapacity,
                MaxConcurrentRuns = registration.MaxConcurrentRuns,
                AvailableRunSlots = canAcceptRun ? registration.MaxConcurrentRuns : 0,
                IsQueuePaused = false,
                CanAcceptRun = canAcceptRun,
                RegisteredAtUtc = now,
                LastHeartbeatAtUtc = now,
                RuntimeVersion = registration.RuntimeVersion,
                Metadata = CopyMetadata(registration.Metadata)
            };
        }

        /// <summary>
        /// Updates registration-level metadata while preserving current heartbeat state.
        /// </summary>
        /// <param name="registration">The updated runtime instance registration.</param>
        /// <param name="now">The current UTC timestamp.</param>
        /// <returns>The updated runtime instance entry.</returns>
        public RuntimeInstanceEntry UpdateRegistration(
            AiRuntimeInstanceRegistration registration,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(registration);

            var canAcceptRun =
                registration.Role == AiRuntimeInstanceRole.Runtime &&
                CanAcceptRun;

            return new RuntimeInstanceEntry
            {
                RuntimeInstanceId = registration.RuntimeInstanceId,
                TenantId = registration.TenantId ?? TenantId,
                TenantGroupId = registration.TenantGroupId ?? TenantGroupId,
                ControlPlaneId = registration.ControlPlaneId ?? ControlPlaneId,
                HostId = registration.HostId ?? HostId,
                PoolId = registration.PoolId ?? PoolId,
                RuntimeId = registration.RuntimeId ?? RuntimeId,
                ControlPlaneHostId = registration.ControlPlaneHostId ?? ControlPlaneHostId,
                Role = registration.Role,
                Status = Status == AiRuntimeInstanceStatus.Stopped
                    ? AiRuntimeInstanceStatus.Ready
                    : Status,
                HostName = registration.HostName,
                ProcessId = registration.ProcessId,
                KubernetesNamespace = registration.KubernetesNamespace,
                KubernetesPodName = registration.KubernetesPodName,
                KubernetesNodeName = registration.KubernetesNodeName,
                WorkerCount = registration.WorkerCount,
                ActiveWorkerCount = registration.Role == AiRuntimeInstanceRole.Runtime ? ActiveWorkerCount : 0,
                AvailableWorkerCount = registration.Role == AiRuntimeInstanceRole.Runtime ? AvailableWorkerCount : 0,
                MaxLocalWorkersPerExecution = registration.Role == AiRuntimeInstanceRole.Runtime ? MaxLocalWorkersPerExecution : null,
                QueuedRunCount = QueuedRunCount,
                RunningRunCount = RunningRunCount,
                ActiveRunCount = ActiveRunCount,
                QueueCapacity = registration.QueueCapacity,
                MaxConcurrentRuns = registration.MaxConcurrentRuns,
                AvailableRunSlots = registration.Role == AiRuntimeInstanceRole.Runtime ? AvailableRunSlots : 0,
                IsQueuePaused = IsQueuePaused,
                CanAcceptRun = canAcceptRun,
                RegisteredAtUtc = RegisteredAtUtc,
                LastHeartbeatAtUtc = now,
                RuntimeVersion = registration.RuntimeVersion,
                Metadata = CopyMetadata(registration.Metadata)
            };
        }

        /// <summary>
        /// Updates heartbeat, queue, run, and worker capacity visibility for this runtime instance.
        /// </summary>
        /// <param name="queuedRunCount">The number of locally queued runs.</param>
        /// <param name="runningRunCount">The number of locally running runs.</param>
        /// <param name="activeRunCount">The number of active runs known by the local controller.</param>
        /// <param name="availableRunSlots">The number of available local run slots.</param>
        /// <param name="activeWorkerCount">The number of local workers currently assigned to active executions.</param>
        /// <param name="availableWorkerCount">The number of local workers currently available.</param>
        /// <param name="maxLocalWorkersPerExecution">The maximum number of local workers allowed per execution.</param>
        /// <param name="isQueuePaused">Whether the local runtime queue is paused.</param>
        /// <param name="canAcceptRun">Whether this runtime instance can accept a new local run.</param>
        /// <param name="status">The current runtime instance status.</param>
        /// <param name="now">The current UTC timestamp.</param>
        /// <returns>The updated runtime instance entry.</returns>
        public RuntimeInstanceEntry UpdateHeartbeat(
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
            DateTimeOffset now)
        {
            return new RuntimeInstanceEntry
            {
                RuntimeInstanceId = RuntimeInstanceId,
                TenantId = TenantId,
                TenantGroupId = TenantGroupId,
                ControlPlaneId = ControlPlaneId,
                HostId = HostId,
                PoolId = PoolId,
                RuntimeId = RuntimeId,
                ControlPlaneHostId = ControlPlaneHostId,
                Role = Role,
                Status = status,
                HostName = HostName,
                ProcessId = ProcessId,
                KubernetesNamespace = KubernetesNamespace,
                KubernetesPodName = KubernetesPodName,
                KubernetesNodeName = KubernetesNodeName,
                WorkerCount = WorkerCount,
                ActiveWorkerCount = activeWorkerCount,
                AvailableWorkerCount = availableWorkerCount,
                MaxLocalWorkersPerExecution = maxLocalWorkersPerExecution,
                QueuedRunCount = queuedRunCount,
                RunningRunCount = runningRunCount,
                ActiveRunCount = activeRunCount,
                QueueCapacity = QueueCapacity,
                MaxConcurrentRuns = MaxConcurrentRuns,
                AvailableRunSlots = availableRunSlots,
                IsQueuePaused = isQueuePaused,
                CanAcceptRun = canAcceptRun,
                RegisteredAtUtc = RegisteredAtUtc,
                LastHeartbeatAtUtc = now,
                RuntimeVersion = RuntimeVersion,
                Metadata = Metadata
            };
        }

        /// <summary>
        /// Returns a copy of this runtime instance entry with an updated status.
        /// </summary>
        /// <param name="status">The new runtime instance status.</param>
        /// <param name="now">The current UTC timestamp.</param>
        /// <returns>The updated runtime instance entry.</returns>
        public RuntimeInstanceEntry WithStatus(
            AiRuntimeInstanceStatus status,
            DateTimeOffset now)
        {
            var canAcceptRun =
                Role == AiRuntimeInstanceRole.Runtime &&
                CanAcceptRun &&
                IsAcceptingStatus(status);

            return new RuntimeInstanceEntry
            {
                RuntimeInstanceId = RuntimeInstanceId,
                TenantId = TenantId,
                TenantGroupId = TenantGroupId,
                ControlPlaneId = ControlPlaneId,
                HostId = HostId,
                PoolId = PoolId,
                RuntimeId = RuntimeId,
                ControlPlaneHostId = ControlPlaneHostId,
                Role = Role,
                Status = status,
                HostName = HostName,
                ProcessId = ProcessId,
                KubernetesNamespace = KubernetesNamespace,
                KubernetesPodName = KubernetesPodName,
                KubernetesNodeName = KubernetesNodeName,
                WorkerCount = WorkerCount,
                ActiveWorkerCount = ActiveWorkerCount,
                AvailableWorkerCount = AvailableWorkerCount,
                MaxLocalWorkersPerExecution = MaxLocalWorkersPerExecution,
                QueuedRunCount = QueuedRunCount,
                RunningRunCount = RunningRunCount,
                ActiveRunCount = ActiveRunCount,
                QueueCapacity = QueueCapacity,
                MaxConcurrentRuns = MaxConcurrentRuns,
                AvailableRunSlots = AvailableRunSlots,
                IsQueuePaused = status == AiRuntimeInstanceStatus.Paused || IsQueuePaused,
                CanAcceptRun = canAcceptRun,
                RegisteredAtUtc = RegisteredAtUtc,
                LastHeartbeatAtUtc = now,
                RuntimeVersion = RuntimeVersion,
                Metadata = Metadata
            };
        }

        /// <summary>
        /// Determines whether a runtime instance status may still accept new runs.
        /// </summary>
        /// <param name="status">The runtime instance status.</param>
        /// <returns><c>true</c> when the status may accept new runs; otherwise, <c>false</c>.</returns>
        private static bool IsAcceptingStatus(
            AiRuntimeInstanceStatus status)
        {
            return status is AiRuntimeInstanceStatus.Ready or AiRuntimeInstanceStatus.Busy;
        }

        /// <summary>
        /// Projects this registry entry into an immutable runtime instance snapshot.
        /// </summary>
        /// <param name="now">The snapshot UTC timestamp.</param>
        /// <returns>The runtime instance snapshot.</returns>
        public AiRuntimeInstanceSnapshot ToSnapshot(
            DateTimeOffset now)
        {
            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = RuntimeInstanceId,
                TenantId = TenantId,
                TenantGroupId = TenantGroupId,
                ControlPlaneId = ControlPlaneId,
                HostId = HostId,
                PoolId = PoolId,
                RuntimeId = RuntimeId,
                ControlPlaneHostId = ControlPlaneHostId,
                Role = Role,
                Status = Status,
                HostName = HostName,
                ProcessId = ProcessId,
                KubernetesNamespace = KubernetesNamespace,
                KubernetesPodName = KubernetesPodName,
                KubernetesNodeName = KubernetesNodeName,
                WorkerCount = WorkerCount,
                ActiveWorkerCount = ActiveWorkerCount,
                AvailableWorkerCount = AvailableWorkerCount,
                MaxLocalWorkersPerExecution = MaxLocalWorkersPerExecution,
                QueuedRunCount = QueuedRunCount,
                RunningRunCount = RunningRunCount,
                ActiveRunCount = ActiveRunCount,
                QueueCapacity = QueueCapacity,
                MaxConcurrentRuns = MaxConcurrentRuns,
                AvailableRunSlots = AvailableRunSlots,
                IsQueuePaused = IsQueuePaused,
                CanAcceptRun = CanAcceptRun,
                RegisteredAtUtc = RegisteredAtUtc,
                LastHeartbeatAtUtc = LastHeartbeatAtUtc,
                SnapshotAtUtc = now,
                RuntimeVersion = RuntimeVersion,
                Metadata = Metadata
            };
        }

        /// <summary>
        /// Copies metadata using ordinal key comparison.
        /// </summary>
        /// <param name="metadata">The source metadata.</param>
        /// <returns>A copied metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CopyMetadata(
            IReadOnlyDictionary<string, string> metadata)
        {
            return new Dictionary<string, string>(
                metadata,
                StringComparer.Ordinal);
        }
    }
}