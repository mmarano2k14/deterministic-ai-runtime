using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    public sealed class RuntimeInstanceEntry
    {
        public required string RuntimeInstanceId { get; init; }

        public AiRuntimeInstanceRole Role { get; init; }

        public AiRuntimeInstanceStatus Status { get; init; }

        public string? HostName { get; init; }

        public int? ProcessId { get; init; }

        public string? KubernetesNamespace { get; init; }

        public string? KubernetesPodName { get; init; }

        public string? KubernetesNodeName { get; init; }

        public int WorkerCount { get; init; }

        public int QueuedRunCount { get; init; }

        public int RunningRunCount { get; init; }

        public int ActiveRunCount { get; init; }

        public int? QueueCapacity { get; init; }

        public int? MaxConcurrentRuns { get; init; }

        public int? AvailableRunSlots { get; init; }

        public bool IsQueuePaused { get; init; }

        public bool CanAcceptRun { get; init; }

        public DateTimeOffset RegisteredAtUtc { get; init; }

        public DateTimeOffset LastHeartbeatAtUtc { get; init; }

        public string? RuntimeVersion { get; init; }

        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        public static RuntimeInstanceEntry Create(
            AiRuntimeInstanceRegistration registration,
            DateTimeOffset now)
        {
            var canAcceptRun =
                registration.Role == AiRuntimeInstanceRole.Runtime;

            return new RuntimeInstanceEntry
            {
                RuntimeInstanceId = registration.RuntimeInstanceId,
                Role = registration.Role,
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
                AvailableRunSlots = canAcceptRun
                    ? registration.MaxConcurrentRuns
                    : 0,
                IsQueuePaused = false,
                CanAcceptRun = canAcceptRun,
                RegisteredAtUtc = now,
                LastHeartbeatAtUtc = now,
                RuntimeVersion = registration.RuntimeVersion,
                Metadata = CopyMetadata(registration.Metadata)
            };
        }

        public RuntimeInstanceEntry UpdateRegistration(
            AiRuntimeInstanceRegistration registration,
            DateTimeOffset now)
        {
            var canAcceptRun =
                registration.Role == AiRuntimeInstanceRole.Runtime &&
                CanAcceptRun;

            return new RuntimeInstanceEntry
            {
                RuntimeInstanceId = registration.RuntimeInstanceId,
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
                QueuedRunCount = QueuedRunCount,
                RunningRunCount = RunningRunCount,
                ActiveRunCount = ActiveRunCount,
                QueueCapacity = registration.QueueCapacity,
                MaxConcurrentRuns = registration.MaxConcurrentRuns,
                AvailableRunSlots = registration.Role == AiRuntimeInstanceRole.Runtime
                    ? AvailableRunSlots
                    : 0,
                IsQueuePaused = IsQueuePaused,
                CanAcceptRun = canAcceptRun,
                RegisteredAtUtc = RegisteredAtUtc,
                LastHeartbeatAtUtc = now,
                RuntimeVersion = registration.RuntimeVersion,
                Metadata = CopyMetadata(registration.Metadata)
            };
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
            return new RuntimeInstanceEntry
            {
                RuntimeInstanceId = RuntimeInstanceId,
                Role = Role,
                Status = status,
                HostName = HostName,
                ProcessId = ProcessId,
                KubernetesNamespace = KubernetesNamespace,
                KubernetesPodName = KubernetesPodName,
                KubernetesNodeName = KubernetesNodeName,
                WorkerCount = WorkerCount,
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

        public RuntimeInstanceEntry WithStatus(
            AiRuntimeInstanceStatus status,
            DateTimeOffset now)
        {
            return new RuntimeInstanceEntry
            {
                RuntimeInstanceId = RuntimeInstanceId,
                Role = Role,
                Status = status,
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
                LastHeartbeatAtUtc = now,
                RuntimeVersion = RuntimeVersion,
                Metadata = Metadata
            };
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
