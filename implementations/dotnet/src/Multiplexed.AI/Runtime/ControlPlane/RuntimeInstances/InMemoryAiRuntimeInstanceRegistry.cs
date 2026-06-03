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
    public sealed class InMemoryAiRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
    {
        private readonly ConcurrentDictionary<string, RuntimeInstanceEntry> _instances =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
            AiRuntimeInstanceRegistration registration,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.RuntimeInstanceId);

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
                canAcceptRun;

            var effectiveAvailableRunSlots =
                existing.Role == AiRuntimeInstanceRole.Runtime
                    ? availableRunSlots
                    : 0;

            var updated = existing.UpdateHeartbeat(
                queuedRunCount,
                runningRunCount,
                activeRunCount,
                effectiveAvailableRunSlots,
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

        private sealed class RuntimeInstanceEntry
        {
            private RuntimeInstanceEntry(
                string runtimeInstanceId,
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
                bool isQueuePaused,
                bool canAcceptRun,
                DateTimeOffset registeredAtUtc,
                DateTimeOffset lastHeartbeatAtUtc,
                string? runtimeVersion,
                IReadOnlyDictionary<string, string> metadata)
            {
                RuntimeInstanceId = runtimeInstanceId;
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
                IsQueuePaused = isQueuePaused;
                CanAcceptRun = canAcceptRun;
                RegisteredAtUtc = registeredAtUtc;
                LastHeartbeatAtUtc = lastHeartbeatAtUtc;
                RuntimeVersion = runtimeVersion;
                Metadata = metadata;
            }

            public string RuntimeInstanceId { get; }

            public AiRuntimeInstanceRole Role { get; }

            public AiRuntimeInstanceStatus Status { get; }

            public string? HostName { get; }

            public int? ProcessId { get; }

            public string? KubernetesNamespace { get; }

            public string? KubernetesPodName { get; }

            public string? KubernetesNodeName { get; }

            public int WorkerCount { get; }

            public int QueuedRunCount { get; }

            public int RunningRunCount { get; }

            public int ActiveRunCount { get; }

            public int? QueueCapacity { get; }

            public int? MaxConcurrentRuns { get; }

            public int? AvailableRunSlots { get; }

            public bool IsQueuePaused { get; }

            public bool CanAcceptRun { get; }

            public DateTimeOffset RegisteredAtUtc { get; }

            public DateTimeOffset LastHeartbeatAtUtc { get; }

            public string? RuntimeVersion { get; }

            public IReadOnlyDictionary<string, string> Metadata { get; }

            public static RuntimeInstanceEntry Create(
                AiRuntimeInstanceRegistration registration,
                DateTimeOffset now)
            {
                var canAcceptRun =
                    registration.Role == AiRuntimeInstanceRole.Runtime;

                return new RuntimeInstanceEntry(
                    registration.RuntimeInstanceId,
                    registration.Role,
                    AiRuntimeInstanceStatus.Ready,
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
                    isQueuePaused: false,
                    canAcceptRun: canAcceptRun,
                    now,
                    now,
                    registration.RuntimeVersion,
                    CopyMetadata(registration.Metadata));
            }

            public RuntimeInstanceEntry UpdateRegistration(
                AiRuntimeInstanceRegistration registration,
                DateTimeOffset now)
            {
                var canAcceptRun =
                    registration.Role == AiRuntimeInstanceRole.Runtime &&
                    CanAcceptRun;

                return new RuntimeInstanceEntry(
                    registration.RuntimeInstanceId,
                    registration.Role,
                    Status == AiRuntimeInstanceStatus.Stopped
                        ? AiRuntimeInstanceStatus.Ready
                        : Status,
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
                    IsQueuePaused,
                    canAcceptRun,
                    RegisteredAtUtc,
                    now,
                    registration.RuntimeVersion,
                    CopyMetadata(registration.Metadata));
            }

            public RuntimeInstanceEntry UpdateHeartbeat(
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? availableRunSlots,
                bool isQueuePaused,
                bool canAcceptRun,
                AiRuntimeInstanceStatus status,
                DateTimeOffset now)
            {
                return new RuntimeInstanceEntry(
                    RuntimeInstanceId,
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
                    availableRunSlots,
                    isQueuePaused,
                    canAcceptRun,
                    RegisteredAtUtc,
                    now,
                    RuntimeVersion,
                    Metadata);
            }

            public RuntimeInstanceEntry WithStatus(
                AiRuntimeInstanceStatus status,
                DateTimeOffset now)
            {
                return new RuntimeInstanceEntry(
                    RuntimeInstanceId,
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
                    IsQueuePaused,
                    CanAcceptRun,
                    RegisteredAtUtc,
                    now,
                    RuntimeVersion,
                    Metadata);
            }

            public AiRuntimeInstanceSnapshot ToSnapshot(
                DateTimeOffset now)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = RuntimeInstanceId,
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
                    IsQueuePaused = IsQueuePaused,
                    CanAcceptRun = CanAcceptRun,
                    RegisteredAtUtc = RegisteredAtUtc,
                    LastHeartbeatAtUtc = LastHeartbeatAtUtc,
                    SnapshotAtUtc = now,
                    RuntimeVersion = RuntimeVersion,
                    Metadata = Metadata
                };
            }

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