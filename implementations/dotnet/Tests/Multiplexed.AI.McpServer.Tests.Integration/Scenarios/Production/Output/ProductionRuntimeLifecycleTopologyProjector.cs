using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output
{
    /// <summary>
    /// Reconstructs runtime topology and run placement from the durable append-only lifecycle journal.
    /// </summary>
    internal static class ProductionRuntimeLifecycleTopologyProjector
    {
        private static readonly string[] WorkKindMetadataKeys =
        {
            "work.kind",
            "recovery.candidateKind",
            "candidate.kind"
        };

        private static readonly string[] PipelineMetadataKeys =
        {
            "pipeline.name",
            "pipeline.key",
            "pipeline"
        };

        private static readonly string[] FailedRuntimeInstanceMetadataKeys =
        {
            "failed.runtimeInstanceId",
            "recovery.failedRuntimeInstanceId"
        };

        private static readonly string[] FailedLocalRunMetadataKeys =
        {
            "failed.localRunId",
            "recovery.failedLocalRunId"
        };

        private static readonly string[] FailedExecutionMetadataKeys =
        {
            "failed.executionId",
            "recovery.failedExecutionId"
        };

        /// <summary>
        /// Projects durable lifecycle events into the registry-shaped topology model already consumed
        /// by the production summary formatter.
        /// </summary>
        public static ProductionRuntimeLifecycleTopologyProjection Project(
            string controlPlaneId,
            IReadOnlyCollection<AiRuntimeLifecycleEvent> lifecycleEvents,
            IReadOnlyDictionary<string, string>? tenantRoles = null,
            IReadOnlySet<string>? trustedFailureIncidentIds = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(lifecycleEvents);

            var controlPlaneIncidentIds =
                lifecycleEvents
                    .Where(item => string.Equals(
                        item.ControlPlaneId,
                        controlPlaneId,
                        StringComparison.Ordinal))
                    .Where(item => !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId))
                    .Select(item => item.RuntimeFailureIncidentId!)
                    .ToHashSet(StringComparer.Ordinal);

            if (trustedFailureIncidentIds is not null)
            {
                foreach (var incidentId in trustedFailureIncidentIds)
                {
                    if (!string.IsNullOrWhiteSpace(incidentId))
                    {
                        controlPlaneIncidentIds.Add(incidentId);
                    }
                }
            }

            var events =
                lifecycleEvents
                    .Where(item =>
                        string.Equals(
                            item.ControlPlaneId,
                            controlPlaneId,
                            StringComparison.Ordinal) ||
                        (!string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId) &&
                         controlPlaneIncidentIds.Contains(item.RuntimeFailureIncidentId!)))
                    .OrderBy(item => item.TimestampUtc)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToArray();

            var hostTerminalEvents =
                events
                    .Where(item =>
                        string.Equals(item.EventType, AiRuntimeLifecycleEvents.HostDeleted, StringComparison.Ordinal) ||
                        string.Equals(item.EventType, AiRuntimeLifecycleEvents.HostDisappeared, StringComparison.Ordinal))
                    .ToArray();
            var demonstratedFailureIncidentIds = ResolveDemonstratedFailureIncidentIds(events);

            if (trustedFailureIncidentIds is not null)
            {
                foreach (var incidentId in trustedFailureIncidentIds)
                {
                    if (!string.IsNullOrWhiteSpace(incidentId))
                    {
                        demonstratedFailureIncidentIds.Add(incidentId);
                    }
                }
            }

            var demonstratedFailureHostIds = ResolveDemonstratedFailureHostIds(
                events,
                demonstratedFailureIncidentIds);
            var demonstratedFailurePodUids = ResolveDemonstratedFailurePodUids(
                events,
                demonstratedFailureIncidentIds);
            var demonstratedFailureRuntimeIds = ResolveDemonstratedFailureRuntimeIds(
                events,
                demonstratedFailureIncidentIds,
                demonstratedFailurePodUids);

            var currentSnapshots = new List<AiRuntimeInstanceSnapshot>();
            var historicalSnapshots = new List<AiRuntimeInstanceSnapshot>();

            foreach (var runtimeGroup in events
                         .Where(item => !string.IsNullOrWhiteSpace(item.RuntimeInstanceId))
                         .GroupBy(item => item.RuntimeInstanceId!, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var runtimeEvents = runtimeGroup.ToArray();
                var snapshot = CreateRuntimeSnapshot(runtimeGroup.Key, runtimeEvents, hostTerminalEvents);

                if (IsDurablyCurrent(snapshot.Status) ||
                    (snapshot.Status == AiRuntimeInstanceStatus.Unknown &&
                     HasPlacementHistoryWithoutFailure(runtimeEvents)))
                {
                    currentSnapshots.Add(snapshot);
                }
                else if (ShouldIncludeHistoricalRuntime(
                             runtimeEvents,
                             demonstratedFailureIncidentIds,
                             demonstratedFailureHostIds))
                {
                    historicalSnapshots.Add(snapshot);
                }
            }

            var placements =
                events
                    .Where(IsWorkEvent)
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(item.TenantId) &&
                        !string.IsNullOrWhiteSpace(item.SharedRunId))
                    .GroupBy(
                        item => new TenantSharedRunKey(item.TenantId!, item.SharedRunId!),
                        TenantSharedRunKeyComparer.Instance)
                    .Select(group => CreateRunPlacement(group.ToArray(), tenantRoles))
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .OrderBy(item => item.TenantId, StringComparer.Ordinal)
                    .ThenBy(item => item.SharedRunId, StringComparer.Ordinal)
                    .ToArray();

            return new ProductionRuntimeLifecycleTopologyProjection(
                currentSnapshots
                    .OrderBy(item => item.RegisteredAtUtc)
                    .ThenBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                    .ToArray(),
                historicalSnapshots
                    .OrderBy(item => item.RegisteredAtUtc)
                    .ThenBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                    .ToArray(),
                placements,
                demonstratedFailureIncidentIds.Count == 0
                    ? null
                    : demonstratedFailurePodUids.Count,
                demonstratedFailureIncidentIds.Count == 0
                    ? null
                    : demonstratedFailureRuntimeIds.Count);
        }

        private static AiRuntimeInstanceSnapshot CreateRuntimeSnapshot(
            string runtimeInstanceId,
            IReadOnlyCollection<AiRuntimeLifecycleEvent> runtimeEvents,
            IReadOnlyCollection<AiRuntimeLifecycleEvent> hostTerminalEvents)
        {
            var ordered =
                runtimeEvents
                    .OrderBy(item => item.TimestampUtc)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToArray();

            var statusEvent = ordered.LastOrDefault(IsRuntimeStatusEvent);
            var status = ResolveRuntimeStatus(statusEvent);
            var runtimeHostIds =
                ordered
                    .SelectMany(item => new[] { item.HostId, item.KubernetesPodUid })
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToHashSet(StringComparer.Ordinal);

            var hostTerminalEvent =
                hostTerminalEvents
                    .Where(item =>
                        (!string.IsNullOrWhiteSpace(item.HostId) && runtimeHostIds.Contains(item.HostId)) ||
                        (!string.IsNullOrWhiteSpace(item.KubernetesPodUid) && runtimeHostIds.Contains(item.KubernetesPodUid)))
                    .OrderBy(item => item.TimestampUtc)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .LastOrDefault();

            if (hostTerminalEvent is not null &&
                (statusEvent is null || hostTerminalEvent.TimestampUtc >= statusEvent.TimestampUtc))
            {
                status = string.Equals(
                    hostTerminalEvent.EventType,
                    AiRuntimeLifecycleEvents.HostDeleted,
                    StringComparison.Ordinal)
                    ? AiRuntimeInstanceStatus.Stopped
                    : AiRuntimeInstanceStatus.Unhealthy;
            }

            var registeredAtUtc =
                ordered
                    .Where(item =>
                        string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeRegistered, StringComparison.Ordinal) ||
                        string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeReplacementRegistered, StringComparison.Ordinal) ||
                        string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeReady, StringComparison.Ordinal))
                    .Select(item => (DateTimeOffset?)item.TimestampUtc)
                    .FirstOrDefault() ?? ordered[0].TimestampUtc;
            var latest = ordered[^1];

            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                Status = status,
                HostName = LastNonEmpty(ordered, item => ResolveMetadata(item.Metadata, "host.name")),
                ProcessId = ordered.LastOrDefault(item => item.ProcessId.HasValue)?.ProcessId,
                KubernetesNamespace = LastNonEmpty(ordered, item => item.KubernetesNamespace),
                KubernetesPodName = LastNonEmpty(ordered, item => item.KubernetesPodName),
                KubernetesNodeName = LastNonEmpty(ordered, item => item.KubernetesNodeName),
                WorkerCount = 0,
                RegisteredAtUtc = registeredAtUtc,
                LastHeartbeatAtUtc = latest.TimestampUtc,
                SnapshotAtUtc = latest.TimestampUtc,
                HostId = LastNonEmpty(ordered, item => FirstNonEmpty(item.HostId, item.KubernetesPodUid)),
                PoolId = LastNonEmpty(ordered, item => item.PoolId),
                RuntimeId = LastNonEmpty(ordered, item => item.RuntimeId),
                ControlPlaneId = latest.ControlPlaneId
            };
        }

        private static ProductionRuntimeRunPlacement? CreateRunPlacement(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> workEvents,
            IReadOnlyDictionary<string, string>? tenantRoles)
        {
            var ordered =
                workEvents
                    .OrderBy(item => item.TimestampUtc)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToArray();
            var placementEvents =
                ordered
                    .Where(item =>
                        (string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkAssigned, StringComparison.Ordinal) ||
                         string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkReassigned, StringComparison.Ordinal)) &&
                        !string.IsNullOrWhiteSpace(item.RuntimeInstanceId))
                    .ToArray();
            var releaseEvents =
                ordered
                    .Where(item =>
                        string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkReleased, StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(item.RuntimeInstanceId))
                    .ToArray();

            if (placementEvents.Length == 0 && releaseEvents.Length == 0)
            {
                return null;
            }

            var initialAssigned = placementEvents.FirstOrDefault(item =>
                string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkAssigned, StringComparison.Ordinal));
            var initialReleased = releaseEvents.FirstOrDefault();
            var initialFact = initialAssigned ?? initialReleased;
            var current = placementEvents.LastOrDefault() ?? releaseEvents.Last();
            var tenantId = LastNonEmpty(ordered, item => item.TenantId);
            var sharedRunId = LastNonEmpty(ordered, item => item.SharedRunId);

            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(sharedRunId))
            {
                return null;
            }

            var failedRuntimeInstanceId =
                LastNonEmpty(
                    ordered,
                    item => ResolveFirstMetadata(item.Metadata, FailedRuntimeInstanceMetadataKeys));
            var failedLocalRunId =
                LastNonEmpty(
                    ordered,
                    item => ResolveFirstMetadata(item.Metadata, FailedLocalRunMetadataKeys));
            var failedExecutionId =
                LastNonEmpty(
                    ordered,
                    item => ResolveFirstMetadata(item.Metadata, FailedExecutionMetadataKeys));
            var tenantRole =
                tenantRoles is not null && tenantRoles.TryGetValue(tenantId, out var configuredRole)
                    ? configuredRole
                    : FirstNonEmpty(
                        LastNonEmpty(ordered, item => ResolveMetadata(item.Metadata, "tenant.role")),
                        "Unknown");

            return new ProductionRuntimeRunPlacement
            {
                TenantId = tenantId,
                TenantRole = tenantRole,
                SharedRunId = sharedRunId,
                WorkKind = ResolveWorkKind(ordered, current),
                PipelineName = LastNonEmpty(ordered, item => ResolveFirstMetadata(item.Metadata, PipelineMetadataKeys)),
                InitialRuntimeInstanceId = FirstNonEmpty(
                    initialFact?.RuntimeInstanceId,
                    failedRuntimeInstanceId,
                    current.RuntimeInstanceId),
                InitialLocalRunId = FirstNonEmpty(
                    initialFact?.LocalRunId,
                    failedLocalRunId,
                    current.LocalRunId),
                InitialExecutionId = FirstNonEmpty(
                    initialFact?.ExecutionId,
                    initialReleased?.ExecutionId,
                    failedExecutionId),
                CurrentRuntimeInstanceId = current.RuntimeInstanceId,
                CurrentLocalRunId = current.LocalRunId,
                CurrentExecutionId = current.ExecutionId,
                RuntimeFailureIncidentId = LastNonEmpty(ordered, item => item.RuntimeFailureIncidentId),
                LedgerEntryId = LastNonEmpty(ordered, item => item.LedgerEntryId),
                ForensicsId = LastNonEmpty(ordered, item => item.ForensicsId)
            };
        }

        private static string ResolveWorkKind(
            IReadOnlyList<AiRuntimeLifecycleEvent> ordered,
            AiRuntimeLifecycleEvent current)
        {
            return FirstNonEmpty(
                LastNonEmpty(ordered, item => ResolveFirstMetadata(item.Metadata, WorkKindMetadataKeys)),
                string.Equals(current.EventType, AiRuntimeLifecycleEvents.WorkReassigned, StringComparison.Ordinal)
                    ? "RecoveryRedispatch"
                    : "InitialDispatch");
        }

        private static bool IsWorkEvent(
            AiRuntimeLifecycleEvent item)
        {
            return string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkAssigned, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkReassigned, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkReleased, StringComparison.Ordinal);
        }

        private static HashSet<string> ResolveDemonstratedFailureIncidentIds(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> events)
        {
            var incidentIds = events
                .Where(item =>
                    string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkReleased, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId))
                .Select(item => item.RuntimeFailureIncidentId!)
                .ToHashSet(StringComparer.Ordinal);

            if (incidentIds.Count > 0)
            {
                return incidentIds;
            }

            return events
                .Where(item =>
                    string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkReassigned, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId))
                .Select(item => item.RuntimeFailureIncidentId!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static HashSet<string> ResolveDemonstratedFailureHostIds(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> events,
            IReadOnlySet<string> demonstratedFailureIncidentIds)
        {
            if (demonstratedFailureIncidentIds.Count == 0)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return events
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId) &&
                    demonstratedFailureIncidentIds.Contains(item.RuntimeFailureIncidentId!) &&
                    IsFailureSideEvent(item))
                .SelectMany(item => new[] { item.HostId, item.KubernetesPodUid })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static HashSet<string> ResolveDemonstratedFailurePodUids(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> events,
            IReadOnlySet<string> demonstratedFailureIncidentIds)
        {
            if (demonstratedFailureIncidentIds.Count == 0)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var terminalFailurePodUids =
                events
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId) &&
                        demonstratedFailureIncidentIds.Contains(item.RuntimeFailureIncidentId!) &&
                        (string.Equals(item.EventType, AiRuntimeLifecycleEvents.HostDeleted, StringComparison.Ordinal) ||
                         string.Equals(item.EventType, AiRuntimeLifecycleEvents.HostDisappeared, StringComparison.Ordinal)) &&
                        !string.IsNullOrWhiteSpace(item.KubernetesPodUid))
                    .Select(item => item.KubernetesPodUid!)
                    .ToHashSet(StringComparer.Ordinal);

            if (terminalFailurePodUids.Count > 0)
            {
                return terminalFailurePodUids;
            }

            var runtimeFailurePodUids =
                events
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId) &&
                        demonstratedFailureIncidentIds.Contains(item.RuntimeFailureIncidentId!) &&
                        IsRuntimeFailureStatusEvent(item) &&
                        !string.IsNullOrWhiteSpace(item.KubernetesPodUid))
                    .Select(item => item.KubernetesPodUid!)
                    .ToHashSet(StringComparer.Ordinal);

            if (runtimeFailurePodUids.Count > 0)
            {
                return runtimeFailurePodUids;
            }

            // Compatibility fallback for older lifecycle histories that predate explicit
            // host/runtime terminal events. WorkReleased is deliberately last because a
            // recovered run may later be released from replacement capacity while retaining
            // the original failure incident identity.
            return events
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId) &&
                    demonstratedFailureIncidentIds.Contains(item.RuntimeFailureIncidentId!) &&
                    string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkReleased, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(item.KubernetesPodUid))
                .Select(item => item.KubernetesPodUid!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static HashSet<string> ResolveDemonstratedFailureRuntimeIds(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> events,
            IReadOnlySet<string> demonstratedFailureIncidentIds,
            IReadOnlySet<string> demonstratedFailurePodUids)
        {
            if (demonstratedFailureIncidentIds.Count == 0 ||
                demonstratedFailurePodUids.Count == 0)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return events
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId) &&
                    demonstratedFailureIncidentIds.Contains(item.RuntimeFailureIncidentId!) &&
                    IsRuntimeFailureStatusEvent(item) &&
                    !string.IsNullOrWhiteSpace(item.RuntimeInstanceId) &&
                    !string.IsNullOrWhiteSpace(item.KubernetesPodUid) &&
                    demonstratedFailurePodUids.Contains(item.KubernetesPodUid!))
                .Select(item => item.RuntimeInstanceId!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool IsRuntimeFailureStatusEvent(
            AiRuntimeLifecycleEvent item)
        {
            return string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeSuppressed, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeUnhealthy, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeStopped, StringComparison.Ordinal);
        }

        private static bool ShouldIncludeHistoricalRuntime(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> runtimeEvents,
            IReadOnlySet<string> demonstratedFailureIncidentIds,
            IReadOnlySet<string> demonstratedFailureHostIds)
        {
            if (demonstratedFailureIncidentIds.Count == 0)
            {
                return true;
            }

            var belongsToDemonstratedHost = runtimeEvents
                .SelectMany(item => new[] { item.HostId, item.KubernetesPodUid })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Any(value => demonstratedFailureHostIds.Contains(value!));

            if (belongsToDemonstratedHost)
            {
                return true;
            }

            return runtimeEvents.Any(item =>
                !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId) &&
                demonstratedFailureIncidentIds.Contains(item.RuntimeFailureIncidentId!) &&
                IsFailureSideEvent(item));
        }

        private static bool IsFailureSideEvent(
            AiRuntimeLifecycleEvent item)
        {
            return string.Equals(item.EventType, AiRuntimeLifecycleEvents.HostDeleted, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.HostDisappeared, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeSuppressed, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeUnhealthy, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.WorkReleased, StringComparison.Ordinal);
        }

        private static bool IsRuntimeStatusEvent(
            AiRuntimeLifecycleEvent item)
        {
            return string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeRegistered, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeReady, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeDraining, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeSuppressed, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeUnhealthy, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeStopped, StringComparison.Ordinal) ||
                   string.Equals(item.EventType, AiRuntimeLifecycleEvents.RuntimeReplacementRegistered, StringComparison.Ordinal);
        }

        private static AiRuntimeInstanceStatus ResolveRuntimeStatus(
            AiRuntimeLifecycleEvent? statusEvent)
        {
            if (statusEvent is null)
            {
                return AiRuntimeInstanceStatus.Unknown;
            }

            return statusEvent.EventType switch
            {
                AiRuntimeLifecycleEvents.RuntimeReady => AiRuntimeInstanceStatus.Ready,
                AiRuntimeLifecycleEvents.RuntimeReplacementRegistered => AiRuntimeInstanceStatus.Ready,
                AiRuntimeLifecycleEvents.RuntimeDraining => AiRuntimeInstanceStatus.Draining,
                AiRuntimeLifecycleEvents.RuntimeSuppressed => AiRuntimeInstanceStatus.Unhealthy,
                AiRuntimeLifecycleEvents.RuntimeUnhealthy => AiRuntimeInstanceStatus.Unhealthy,
                AiRuntimeLifecycleEvents.RuntimeStopped => AiRuntimeInstanceStatus.Stopped,
                _ => AiRuntimeInstanceStatus.Unknown
            };
        }

        private static bool IsDurablyCurrent(
            AiRuntimeInstanceStatus status)
        {
            return status == AiRuntimeInstanceStatus.Ready ||
                   status == AiRuntimeInstanceStatus.Busy ||
                   status == AiRuntimeInstanceStatus.Paused ||
                   status == AiRuntimeInstanceStatus.Draining;
        }

        /// <summary>
        /// Keeps a runtime alias addressable after its normally completed work has been released.
        /// Some KubernetesPool dispatch identities are visible through durable work ownership but
        /// do not emit an independent runtime.ready event. A normal work.released event must not
        /// erase that runtime-to-host fact from the final topology projection.
        /// </summary>
        private static bool HasPlacementHistoryWithoutFailure(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> runtimeEvents)
        {
            var hasPlacement = runtimeEvents.Any(item =>
                string.Equals(
                    item.EventType,
                    AiRuntimeLifecycleEvents.WorkAssigned,
                    StringComparison.Ordinal) ||
                string.Equals(
                    item.EventType,
                    AiRuntimeLifecycleEvents.WorkReassigned,
                    StringComparison.Ordinal));

            if (!hasPlacement)
            {
                return false;
            }

            return !runtimeEvents.Any(item =>
                string.Equals(
                    item.EventType,
                    AiRuntimeLifecycleEvents.RuntimeSuppressed,
                    StringComparison.Ordinal) ||
                string.Equals(
                    item.EventType,
                    AiRuntimeLifecycleEvents.RuntimeUnhealthy,
                    StringComparison.Ordinal) ||
                string.Equals(
                    item.EventType,
                    AiRuntimeLifecycleEvents.RuntimeStopped,
                    StringComparison.Ordinal) ||
                string.Equals(
                    item.EventType,
                    AiRuntimeLifecycleEvents.HostDeleted,
                    StringComparison.Ordinal) ||
                string.Equals(
                    item.EventType,
                    AiRuntimeLifecycleEvents.HostDisappeared,
                    StringComparison.Ordinal) ||
                (string.Equals(
                     item.EventType,
                     AiRuntimeLifecycleEvents.WorkReleased,
                     StringComparison.Ordinal) &&
                 !string.IsNullOrWhiteSpace(item.RuntimeFailureIncidentId)));
        }

        private static string? LastNonEmpty(
            IReadOnlyList<AiRuntimeLifecycleEvent> events,
            Func<AiRuntimeLifecycleEvent, string?> selector)
        {
            for (var index = events.Count - 1; index >= 0; index--)
            {
                var value = selector(events[index]);

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string? ResolveFirstMetadata(
            IReadOnlyDictionary<string, string> metadata,
            IReadOnlyCollection<string> keys)
        {
            foreach (var key in keys)
            {
                var value = ResolveMetadata(metadata, key);

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string? ResolveMetadata(
            IReadOnlyDictionary<string, string> metadata,
            string key)
        {
            if (metadata.TryGetValue(key, out var value))
            {
                return value;
            }

            foreach (var pair in metadata)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        private static string FirstNonEmpty(
            params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private sealed record TenantSharedRunKey(
            string TenantId,
            string SharedRunId);

        private sealed class TenantSharedRunKeyComparer : IEqualityComparer<TenantSharedRunKey>
        {
            public static TenantSharedRunKeyComparer Instance { get; } = new();

            public bool Equals(
                TenantSharedRunKey? left,
                TenantSharedRunKey? right)
            {
                return left is not null &&
                       right is not null &&
                       string.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal) &&
                       string.Equals(left.SharedRunId, right.SharedRunId, StringComparison.Ordinal);
            }

            public int GetHashCode(
                TenantSharedRunKey value)
            {
                return HashCode.Combine(
                    StringComparer.Ordinal.GetHashCode(value.TenantId),
                    StringComparer.Ordinal.GetHashCode(value.SharedRunId));
            }
        }
    }

    /// <summary>
    /// Contains the durable topology projection consumed by the existing summary formatter.
    /// </summary>
    internal sealed record ProductionRuntimeLifecycleTopologyProjection(
        IReadOnlyCollection<AiRuntimeInstanceSnapshot> CurrentRuntimeSnapshots,
        IReadOnlyCollection<AiRuntimeInstanceSnapshot> HistoricalRuntimeSnapshots,
        IReadOnlyCollection<ProductionRuntimeRunPlacement> RunPlacements,
        int? DeletedKubernetesPodCount,
        int? HistoricalRuntimeCount);
}
