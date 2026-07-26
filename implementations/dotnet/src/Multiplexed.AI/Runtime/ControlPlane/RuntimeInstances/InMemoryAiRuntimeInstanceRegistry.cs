using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using System.Collections.Concurrent;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// In-memory implementation of the runtime instance registry.
    ///
    /// This implementation is intended for single-process development,
    /// tests, local demos, and as a baseline implementation before adding
    /// a Redis-backed distributed registry for Kubernetes.
    /// </summary>
    public sealed class InMemoryAiRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry, IAiRuntimePoolMembershipReader
    {
        /// <summary>
        /// Stores runtime instance entries by runtime instance identifier.
        /// </summary>
        private readonly ConcurrentDictionary<string, RuntimeInstanceEntry> _instances =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
            AiRuntimeInstanceRegistration registration,
            CancellationToken cancellationToken = default)
        {
            AiRuntimePoolIdentityValidator.ValidateRegistration(registration);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            var entry = _instances.AddOrUpdate(
                registration.RuntimeInstanceId,
                _ => RuntimeInstanceEntry.Create(registration, now),
                (_, existing) => existing.UpdateRegistration(registration, now));

            return Task.FromResult(entry.ToSnapshot(now));
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
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            if (!_instances.TryGetValue(runtimeInstanceId, out var existing))
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            var now = DateTimeOffset.UtcNow;

            var effectiveCanAcceptRun =
                existing.Role == AiRuntimeInstanceRole.Runtime &&
                canAcceptRun &&
                IsAcceptingStatus(status);

            var effectiveAvailableRunSlots =
                existing.Role == AiRuntimeInstanceRole.Runtime
                    ? availableRunSlots
                    : 0;

            var effectiveActiveWorkerCount =
                existing.Role == AiRuntimeInstanceRole.Runtime
                    ? activeWorkerCount
                    : 0;

            var effectiveAvailableWorkerCount =
                existing.Role == AiRuntimeInstanceRole.Runtime
                    ? availableWorkerCount
                    : 0;

            var effectiveMaxLocalWorkersPerExecution =
                existing.Role == AiRuntimeInstanceRole.Runtime
                    ? maxLocalWorkersPerExecution
                    : null;

            var updated = existing.UpdateHeartbeat(
                queuedRunCount,
                runningRunCount,
                activeRunCount,
                effectiveAvailableRunSlots,
                effectiveActiveWorkerCount,
                effectiveAvailableWorkerCount,
                effectiveMaxLocalWorkersPerExecution,
                isQueuePaused,
                effectiveCanAcceptRun,
                status,
                now);

            _instances[runtimeInstanceId] = updated;

            return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                updated.ToSnapshot(now));
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            return Task.FromResult(
                _instances.TryGetValue(runtimeInstanceId, out var entry)
                    ? entry.ToSnapshot(now)
                    : null);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
            bool includeStopped = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            var snapshots = _instances.Values
                .Where(entry => includeStopped || entry.Status != AiRuntimeInstanceStatus.Stopped)
                .Select(entry => entry.ToSnapshot(now))
                .OrderBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(snapshots);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            var snapshots = _instances.Values
                .Where(entry =>
                    entry.Status != AiRuntimeInstanceStatus.Stopped &&
                    string.Equals(
                        entry.PoolId,
                        poolId,
                        StringComparison.Ordinal))
                .Select(entry => entry.ToSnapshot(now))
                .OrderBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(snapshots);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListByHostIdAsync(
            string hostId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            var snapshots = _instances.Values
                .Where(entry =>
                    entry.Status != AiRuntimeInstanceStatus.Stopped &&
                    string.Equals(
                        entry.HostId,
                        hostId,
                        StringComparison.Ordinal))
                .Select(entry => entry.ToSnapshot(now))
                .OrderBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(snapshots);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> ListHostIdsByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

            var members =
                await this.ListByPoolIdAsync(
                        poolId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return members
                .Select(member => member.HostId)
                .Where(hostId => !string.IsNullOrWhiteSpace(hostId))
                .Select(hostId => hostId!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(hostId => hostId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            if (!_instances.TryGetValue(runtimeInstanceId, out var existing))
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            var now = DateTimeOffset.UtcNow;
            var updated = existing.WithStatus(AiRuntimeInstanceStatus.Draining, now);

            _instances[runtimeInstanceId] = updated;

            return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                updated.ToSnapshot(now));
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            if (!_instances.TryGetValue(runtimeInstanceId, out var existing))
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            var now = DateTimeOffset.UtcNow;
            var updated = existing.WithStatus(AiRuntimeInstanceStatus.Unhealthy, now);

            _instances[runtimeInstanceId] = updated;

            return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                updated.ToSnapshot(now));
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            if (!_instances.TryGetValue(runtimeInstanceId, out var existing))
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            var now = DateTimeOffset.UtcNow;
            var updated = existing.WithStatus(AiRuntimeInstanceStatus.Stopped, now);

            _instances[runtimeInstanceId] = updated;

            return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                updated.ToSnapshot(now));
        }

        /// <summary>
        /// Determines whether a runtime instance status may accept new runs.
        /// </summary>
        /// <param name="status">The runtime instance status.</param>
        /// <returns><c>true</c> when the runtime instance status can accept new runs; otherwise, <c>false</c>.</returns>
        private static bool IsAcceptingStatus(
            AiRuntimeInstanceStatus status)
        {
            return status is AiRuntimeInstanceStatus.Ready or AiRuntimeInstanceStatus.Busy;
        }

        /// <summary>
        /// Immutable in-memory representation of a runtime instance registration and heartbeat state.
        /// </summary>
        private sealed class RuntimeInstanceEntry
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="RuntimeInstanceEntry"/> class.
            /// </summary>
            /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
            /// <param name="tenantId">The tenant identifier that owns the runtime instance.</param>
            /// <param name="tenantGroupId">The tenant group identifier that owns the runtime instance.</param>
            /// <param name="poolId">The logical runtime pool identifier.</param>
            /// <param name="hostId">The immutable host-incarnation identifier.</param>
            /// <param name="runtimeId">The logical runtime identifier inside the host.</param>
            /// <param name="controlPlaneHostId">The control-plane host identifier.</param>
            /// <param name="controlPlaneId">The logical control-plane identifier.</param>
            /// <param name="role">The runtime instance role.</param>
            /// <param name="status">The runtime instance status.</param>
            /// <param name="hostName">The host name where the runtime instance is running.</param>
            /// <param name="processId">The operating system process identifier.</param>
            /// <param name="kubernetesNamespace">The Kubernetes namespace.</param>
            /// <param name="kubernetesPodName">The Kubernetes pod name.</param>
            /// <param name="kubernetesNodeName">The Kubernetes node name.</param>
            /// <param name="workerCount">The configured worker count.</param>
            /// <param name="queuedRunCount">The number of queued local runs.</param>
            /// <param name="runningRunCount">The number of running local runs.</param>
            /// <param name="activeRunCount">The number of active local runs.</param>
            /// <param name="queueCapacity">The local queue capacity.</param>
            /// <param name="maxConcurrentRuns">The maximum number of concurrent runs.</param>
            /// <param name="availableRunSlots">The number of available run slots.</param>
            /// <param name="activeWorkerCount">The number of active workers.</param>
            /// <param name="availableWorkerCount">The number of available workers.</param>
            /// <param name="maxLocalWorkersPerExecution">The maximum number of local workers per execution.</param>
            /// <param name="isQueuePaused">A value indicating whether the local queue is paused.</param>
            /// <param name="canAcceptRun">A value indicating whether the runtime can accept new runs.</param>
            /// <param name="registeredAtUtc">The registration timestamp.</param>
            /// <param name="lastHeartbeatAtUtc">The last heartbeat timestamp.</param>
            /// <param name="runtimeVersion">The runtime version.</param>
            /// <param name="metadata">Additional runtime instance metadata.</param>
            private RuntimeInstanceEntry(
                string runtimeInstanceId,
                string? tenantId,
                string? tenantGroupId,
                string? poolId,
                string? hostId,
                string? runtimeId,
                string? controlPlaneHostId,
                string? controlPlaneId,
                AiRuntimeInstanceRole role,
                AiRuntimeInstanceStatus status,
                string? hostName,
                int? processId,
                string? kubernetesNamespace,
                string? kubernetesPodName,
                string? kubernetesNodeName,
                int workerCount,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? queueCapacity,
                int? maxConcurrentRuns,
                int? availableRunSlots,
                int? activeWorkerCount,
                int? availableWorkerCount,
                int? maxLocalWorkersPerExecution,
                bool isQueuePaused,
                bool canAcceptRun,
                DateTimeOffset registeredAtUtc,
                DateTimeOffset lastHeartbeatAtUtc,
                string? runtimeVersion,
                IReadOnlyDictionary<string, string> metadata)
            {
                RuntimeInstanceId = runtimeInstanceId;
                TenantId = tenantId;
                TenantGroupId = tenantGroupId;
                PoolId = poolId;
                HostId = hostId;
                RuntimeId = runtimeId;
                ControlPlaneHostId = controlPlaneHostId;
                ControlPlaneId = controlPlaneId;
                Role = role;
                Status = status;
                HostName = hostName;
                ProcessId = processId;
                KubernetesNamespace = kubernetesNamespace;
                KubernetesPodName = kubernetesPodName;
                KubernetesNodeName = kubernetesNodeName;
                WorkerCount = workerCount;
                QueuedRunCount = queuedRunCount;
                RunningRunCount = runningRunCount;
                ActiveRunCount = activeRunCount;
                QueueCapacity = queueCapacity;
                MaxConcurrentRuns = maxConcurrentRuns;
                AvailableRunSlots = availableRunSlots;
                ActiveWorkerCount = activeWorkerCount;
                AvailableWorkerCount = availableWorkerCount;
                MaxLocalWorkersPerExecution = maxLocalWorkersPerExecution;
                IsQueuePaused = isQueuePaused;
                CanAcceptRun = canAcceptRun;
                RegisteredAtUtc = registeredAtUtc;
                LastHeartbeatAtUtc = lastHeartbeatAtUtc;
                RuntimeVersion = runtimeVersion;
                Metadata = metadata;
            }

            /// <summary>
            /// Gets the runtime instance identifier.
            /// </summary>
            public string RuntimeInstanceId { get; }

            /// <summary>
            /// Gets the tenant identifier that owns the runtime instance.
            /// </summary>
            public string? TenantId { get; }

            /// <summary>
            /// Gets the tenant group identifier that owns the runtime instance.
            /// </summary>
            public string? TenantGroupId { get; }

            /// <summary>
            /// Gets the logical runtime pool identifier.
            /// </summary>
            public string? PoolId { get; }

            /// <summary>
            /// Gets the immutable host-incarnation identifier.
            /// </summary>
            public string? HostId { get; }

            /// <summary>
            /// Gets the logical runtime identifier inside the host.
            /// </summary>
            public string? RuntimeId { get; }

            /// <summary>
            /// Gets the control-plane host identifier.
            /// </summary>
            public string? ControlPlaneHostId { get; }

            /// <summary>
            /// Gets the logical control-plane identifier.
            /// </summary>
            public string? ControlPlaneId { get; }

            /// <summary>
            /// Gets the runtime instance role.
            /// </summary>
            public AiRuntimeInstanceRole Role { get; }

            /// <summary>
            /// Gets the runtime instance status.
            /// </summary>
            public AiRuntimeInstanceStatus Status { get; }

            /// <summary>
            /// Gets the host name where the runtime instance is running.
            /// </summary>
            public string? HostName { get; }

            /// <summary>
            /// Gets the operating system process identifier.
            /// </summary>
            public int? ProcessId { get; }

            /// <summary>
            /// Gets the Kubernetes namespace.
            /// </summary>
            public string? KubernetesNamespace { get; }

            /// <summary>
            /// Gets the Kubernetes pod name.
            /// </summary>
            public string? KubernetesPodName { get; }

            /// <summary>
            /// Gets the Kubernetes node name.
            /// </summary>
            public string? KubernetesNodeName { get; }

            /// <summary>
            /// Gets the configured worker count.
            /// </summary>
            public int WorkerCount { get; }

            /// <summary>
            /// Gets the number of queued local runs.
            /// </summary>
            public int QueuedRunCount { get; }

            /// <summary>
            /// Gets the number of running local runs.
            /// </summary>
            public int RunningRunCount { get; }

            /// <summary>
            /// Gets the number of active local runs.
            /// </summary>
            public int ActiveRunCount { get; }

            /// <summary>
            /// Gets the local queue capacity.
            /// </summary>
            public int? QueueCapacity { get; }

            /// <summary>
            /// Gets the maximum number of concurrent runs.
            /// </summary>
            public int? MaxConcurrentRuns { get; }

            /// <summary>
            /// Gets the number of available run slots.
            /// </summary>
            public int? AvailableRunSlots { get; }

            /// <summary>
            /// Gets the number of active workers.
            /// </summary>
            public int? ActiveWorkerCount { get; }

            /// <summary>
            /// Gets the number of available workers.
            /// </summary>
            public int? AvailableWorkerCount { get; }

            /// <summary>
            /// Gets the maximum number of local workers per execution.
            /// </summary>
            public int? MaxLocalWorkersPerExecution { get; }

            /// <summary>
            /// Gets a value indicating whether the local queue is paused.
            /// </summary>
            public bool IsQueuePaused { get; }

            /// <summary>
            /// Gets a value indicating whether the runtime can accept new runs.
            /// </summary>
            public bool CanAcceptRun { get; }

            /// <summary>
            /// Gets the registration timestamp.
            /// </summary>
            public DateTimeOffset RegisteredAtUtc { get; }

            /// <summary>
            /// Gets the last heartbeat timestamp.
            /// </summary>
            public DateTimeOffset LastHeartbeatAtUtc { get; }

            /// <summary>
            /// Gets the runtime version.
            /// </summary>
            public string? RuntimeVersion { get; }

            /// <summary>
            /// Gets additional runtime instance metadata.
            /// </summary>
            public IReadOnlyDictionary<string, string> Metadata { get; }

            /// <summary>
            /// Creates a new runtime instance entry from a runtime instance registration.
            /// </summary>
            /// <param name="registration">The runtime instance registration.</param>
            /// <param name="now">The current timestamp.</param>
            /// <returns>The created runtime instance entry.</returns>
            public static RuntimeInstanceEntry Create(
                AiRuntimeInstanceRegistration registration,
                DateTimeOffset now)
            {
                var status = AiRuntimeInstanceStatus.Ready;

                var canAcceptRun =
                    registration.Role == AiRuntimeInstanceRole.Runtime &&
                    IsAcceptingStatus(status);

                return new RuntimeInstanceEntry(
                    registration.RuntimeInstanceId,
                    registration.TenantId,
                    registration.TenantGroupId,
                    registration.PoolId,
                    registration.HostId,
                    registration.RuntimeId,
                    registration.ControlPlaneHostId,
                    registration.ControlPlaneId,
                    registration.Role,
                    status,
                    registration.HostName,
                    registration.ProcessId,
                    registration.KubernetesNamespace,
                    registration.KubernetesPodName,
                    registration.KubernetesNodeName,
                    registration.WorkerCount,
                    queuedRunCount: 0,
                    runningRunCount: 0,
                    activeRunCount: 0,
                    registration.QueueCapacity,
                    registration.MaxConcurrentRuns,
                    availableRunSlots: canAcceptRun
                        ? registration.MaxConcurrentRuns
                        : 0,
                    activeWorkerCount: 0,
                    availableWorkerCount: registration.Role == AiRuntimeInstanceRole.Runtime
                        ? registration.WorkerCount
                        : 0,
                    maxLocalWorkersPerExecution: null,
                    isQueuePaused: false,
                    canAcceptRun: canAcceptRun,
                    now,
                    now,
                    registration.RuntimeVersion,
                    CopyMetadata(registration.Metadata));
            }

            /// <summary>
            /// Updates an existing runtime instance entry from a new registration.
            /// </summary>
            /// <param name="registration">The runtime instance registration.</param>
            /// <param name="now">The current timestamp.</param>
            /// <returns>The updated runtime instance entry.</returns>
            public RuntimeInstanceEntry UpdateRegistration(
                AiRuntimeInstanceRegistration registration,
                DateTimeOffset now)
            {
                var wasStopped =
                    Status == AiRuntimeInstanceStatus.Stopped;

                var nextStatus = wasStopped
                    ? AiRuntimeInstanceStatus.Ready
                    : Status;

                var canAcceptRun =
                    registration.Role == AiRuntimeInstanceRole.Runtime &&
                    IsAcceptingStatus(nextStatus) &&
                    (wasStopped || CanAcceptRun);

                return new RuntimeInstanceEntry(
                    registration.RuntimeInstanceId,
                    registration.TenantId ?? TenantId,
                    registration.TenantGroupId ?? TenantGroupId,
                    registration.PoolId ?? PoolId,
                    registration.HostId ?? HostId,
                    registration.RuntimeId ?? RuntimeId,
                    registration.ControlPlaneHostId ?? ControlPlaneHostId,
                    registration.ControlPlaneId ?? ControlPlaneId,
                    registration.Role,
                    nextStatus,
                    registration.HostName,
                    registration.ProcessId,
                    registration.KubernetesNamespace,
                    registration.KubernetesPodName,
                    registration.KubernetesNodeName,
                    registration.WorkerCount,
                    QueuedRunCount,
                    RunningRunCount,
                    ActiveRunCount,
                    registration.QueueCapacity,
                    registration.MaxConcurrentRuns,
                    registration.Role == AiRuntimeInstanceRole.Runtime
                        ? AvailableRunSlots
                        : 0,
                    registration.Role == AiRuntimeInstanceRole.Runtime
                        ? ActiveWorkerCount
                        : 0,
                    registration.Role == AiRuntimeInstanceRole.Runtime
                        ? AvailableWorkerCount
                        : 0,
                    registration.Role == AiRuntimeInstanceRole.Runtime
                        ? MaxLocalWorkersPerExecution
                        : null,
                    IsQueuePaused,
                    canAcceptRun,
                    RegisteredAtUtc,
                    now,
                    registration.RuntimeVersion,
                    CopyMetadata(registration.Metadata));
            }

            /// <summary>
            /// Updates heartbeat, status, queue, worker, and capacity fields for the runtime instance entry.
            /// </summary>
            /// <param name="queuedRunCount">The number of queued local runs.</param>
            /// <param name="runningRunCount">The number of running local runs.</param>
            /// <param name="activeRunCount">The number of active local runs.</param>
            /// <param name="availableRunSlots">The number of available run slots.</param>
            /// <param name="activeWorkerCount">The number of active workers.</param>
            /// <param name="availableWorkerCount">The number of available workers.</param>
            /// <param name="maxLocalWorkersPerExecution">The maximum number of local workers per execution.</param>
            /// <param name="isQueuePaused">A value indicating whether the local queue is paused.</param>
            /// <param name="canAcceptRun">A value indicating whether the runtime reports that it can accept new runs.</param>
            /// <param name="status">The runtime instance status.</param>
            /// <param name="now">The current timestamp.</param>
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
                var effectiveCanAcceptRun =
                    Role == AiRuntimeInstanceRole.Runtime &&
                    canAcceptRun &&
                    IsAcceptingStatus(status);

                return new RuntimeInstanceEntry(
                    RuntimeInstanceId,
                    TenantId,
                    TenantGroupId,
                    PoolId,
                    HostId,
                    RuntimeId,
                    ControlPlaneHostId,
                    ControlPlaneId,
                    Role,
                    status,
                    HostName,
                    ProcessId,
                    KubernetesNamespace,
                    KubernetesPodName,
                    KubernetesNodeName,
                    WorkerCount,
                    queuedRunCount,
                    runningRunCount,
                    activeRunCount,
                    QueueCapacity,
                    MaxConcurrentRuns,
                    Role == AiRuntimeInstanceRole.Runtime
                        ? availableRunSlots
                        : 0,
                    Role == AiRuntimeInstanceRole.Runtime
                        ? activeWorkerCount
                        : 0,
                    Role == AiRuntimeInstanceRole.Runtime
                        ? availableWorkerCount
                        : 0,
                    Role == AiRuntimeInstanceRole.Runtime
                        ? maxLocalWorkersPerExecution
                        : null,
                    isQueuePaused,
                    effectiveCanAcceptRun,
                    RegisteredAtUtc,
                    now,
                    RuntimeVersion,
                    Metadata);
            }

            /// <summary>
            /// Returns a copy of the runtime instance entry with a new status.
            /// </summary>
            /// <param name="status">The new runtime instance status.</param>
            /// <param name="now">The current timestamp.</param>
            /// <returns>The updated runtime instance entry.</returns>
            public RuntimeInstanceEntry WithStatus(
                AiRuntimeInstanceStatus status,
                DateTimeOffset now)
            {
                var effectiveCanAcceptRun =
                    Role == AiRuntimeInstanceRole.Runtime &&
                    CanAcceptRun &&
                    IsAcceptingStatus(status);

                return new RuntimeInstanceEntry(
                    RuntimeInstanceId,
                    TenantId,
                    TenantGroupId,
                    PoolId,
                    HostId,
                    RuntimeId,
                    ControlPlaneHostId,
                    ControlPlaneId,
                    Role,
                    status,
                    HostName,
                    ProcessId,
                    KubernetesNamespace,
                    KubernetesPodName,
                    KubernetesNodeName,
                    WorkerCount,
                    QueuedRunCount,
                    RunningRunCount,
                    ActiveRunCount,
                    QueueCapacity,
                    MaxConcurrentRuns,
                    AvailableRunSlots,
                    ActiveWorkerCount,
                    AvailableWorkerCount,
                    MaxLocalWorkersPerExecution,
                    IsQueuePaused,
                    effectiveCanAcceptRun,
                    RegisteredAtUtc,
                    now,
                    RuntimeVersion,
                    Metadata);
            }

            /// <summary>
            /// Converts the entry to a runtime instance snapshot.
            /// </summary>
            /// <param name="now">The snapshot timestamp.</param>
            /// <returns>The runtime instance snapshot.</returns>
            public AiRuntimeInstanceSnapshot ToSnapshot(
                DateTimeOffset now)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = RuntimeInstanceId,
                    TenantId = TenantId,
                    TenantGroupId = TenantGroupId,
                    PoolId = PoolId,
                    HostId = HostId,
                    RuntimeId = RuntimeId,
                    ControlPlaneHostId = ControlPlaneHostId,
                    ControlPlaneId = ControlPlaneId,
                    Role = Role,
                    Status = Status,
                    HostName = HostName,
                    ProcessId = ProcessId,
                    KubernetesNamespace = KubernetesNamespace,
                    KubernetesPodName = KubernetesPodName,
                    KubernetesNodeName = KubernetesNodeName,
                    WorkerCount = WorkerCount,
                    QueuedRunCount = QueuedRunCount,
                    RunningRunCount = RunningRunCount,
                    ActiveRunCount = ActiveRunCount,
                    QueueCapacity = QueueCapacity,
                    MaxConcurrentRuns = MaxConcurrentRuns,
                    AvailableRunSlots = AvailableRunSlots,
                    ActiveWorkerCount = ActiveWorkerCount,
                    AvailableWorkerCount = AvailableWorkerCount,
                    MaxLocalWorkersPerExecution = MaxLocalWorkersPerExecution,
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
            /// Copies metadata into an ordinal dictionary.
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
}