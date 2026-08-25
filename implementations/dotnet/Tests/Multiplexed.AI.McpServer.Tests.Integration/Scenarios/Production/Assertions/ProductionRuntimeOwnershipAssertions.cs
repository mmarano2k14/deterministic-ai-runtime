using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Observability.Events;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    internal static class ProductionRuntimeOwnershipAssertions
    {
        private const string AssignedStatus = "assigned";

        public static ProductionRuntimeOwnershipProof AssertExactValidOwnershipHandoff(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> lifecycleEvents,
            IReadOnlyCollection<AiSharedRunRecord> completedParentRuns,
            IReadOnlySet<string> recoveredSharedRunIds,
            string proofName)
        {
            ArgumentNullException.ThrowIfNull(completedParentRuns);

            return AssertExactValidOwnershipHandoff(
                lifecycleEvents,
                completedParentRuns
                    .Select(run => ProductionRuntimeOwnershipProofTarget.FromSharedRun(run))
                    .ToArray(),
                recoveredSharedRunIds,
                proofName);
        }

        internal static ProductionRuntimeOwnershipProof AssertExactValidOwnershipHandoff(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> lifecycleEvents,
            IReadOnlyCollection<ProductionRuntimeOwnershipProofTarget> completedParentRuns,
            IReadOnlySet<string> recoveredSharedRunIds,
            string proofName)
        {
            ArgumentNullException.ThrowIfNull(lifecycleEvents);
            ArgumentNullException.ThrowIfNull(completedParentRuns);
            ArgumentNullException.ThrowIfNull(recoveredSharedRunIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(proofName);

            Assert.NotEmpty(completedParentRuns);

            var parentBySharedRunId =
                completedParentRuns.ToDictionary(
                    parent => parent.SharedRunId,
                    StringComparer.Ordinal);

            Assert.Equal(
                completedParentRuns.Count,
                parentBySharedRunId.Count);

            Assert.True(
                recoveredSharedRunIds.IsSubsetOf(
                    parentBySharedRunId.Keys.ToHashSet(StringComparer.Ordinal)),
                $"{proofName} contains recovered SharedRunIds that are not part of the completed parent-run proof set. " +
                $"Unexpected='{string.Join(",", recoveredSharedRunIds.Where(id => !parentBySharedRunId.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal))}'.");

            var recoveryOwnershipEvents =
                lifecycleEvents
                    .Where(IsRecoveryOwnershipWorkEvent)
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(item.SharedRunId) &&
                        parentBySharedRunId.ContainsKey(item.SharedRunId!))
                    .ToArray();

            Assert.Equal(
                recoveryOwnershipEvents.Length,
                recoveryOwnershipEvents
                    .Select(item => item.EventId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            var observedRecoveredSharedRunIds =
                new HashSet<string>(StringComparer.Ordinal);
            var recoveryReleaseCount = 0;
            var recoveryReassignmentCount = 0;
            var recoveryTransitionCount = 0;

            foreach (var parent in completedParentRuns.OrderBy(
                         item => item.SharedRunId,
                         StringComparer.Ordinal))
            {
                var events =
                    recoveryOwnershipEvents
                        .Where(item =>
                            string.Equals(
                                item.SharedRunId,
                                parent.SharedRunId,
                                StringComparison.Ordinal) &&
                            string.Equals(
                                item.TenantId,
                                parent.TenantId,
                                StringComparison.Ordinal))
                        .ToArray();

                var releases =
                    events
                        .Where(item => string.Equals(
                            item.EventType,
                            AiRuntimeLifecycleEvents.WorkReleased,
                            StringComparison.Ordinal))
                        .ToArray();
                var reassignments =
                    events
                        .Where(item => string.Equals(
                            item.EventType,
                            AiRuntimeLifecycleEvents.WorkReassigned,
                            StringComparison.Ordinal))
                        .ToArray();
                var expectedRecovery =
                    recoveredSharedRunIds.Contains(parent.SharedRunId);

                if (!expectedRecovery)
                {
                    Assert.Empty(releases);
                    Assert.Empty(reassignments);
                    continue;
                }

                Assert.NotEmpty(releases);
                Assert.Equal(releases.Length, reassignments.Length);

                var releaseByIncidentId =
                    CreateIncidentMap(
                        releases,
                        proofName,
                        parent.SharedRunId,
                        AiRuntimeLifecycleEvents.WorkReleased);
                var reassignmentByIncidentId =
                    CreateIncidentMap(
                        reassignments,
                        proofName,
                        parent.SharedRunId,
                        AiRuntimeLifecycleEvents.WorkReassigned);

                Assert.Equal(
                    releaseByIncidentId.Keys.OrderBy(
                        value => value,
                        StringComparer.Ordinal),
                    reassignmentByIncidentId.Keys.OrderBy(
                        value => value,
                        StringComparer.Ordinal));

                var ownershipEdges =
                    new List<ProductionRuntimeOwnershipEdge>();

                foreach (var incidentId in releaseByIncidentId.Keys.OrderBy(
                             value => value,
                             StringComparer.Ordinal))
                {
                    var release = releaseByIncidentId[incidentId];
                    var reassignment = reassignmentByIncidentId[incidentId];

                    AssertOwnershipIdentity(
                        parent,
                        release,
                        proofName,
                        AiRuntimeLifecycleEvents.WorkReleased);
                    AssertOwnershipIdentity(
                        parent,
                        reassignment,
                        proofName,
                        AiRuntimeLifecycleEvents.WorkReassigned);

                    Assert.Equal(AssignedStatus, release.PreviousStatus);
                    Assert.Equal(
                        AiRuntimeRecoveryTransitionStatuses.ReleasedForRecovery,
                        release.CurrentStatus);
                    Assert.Equal(
                        AiRuntimeRecoveryTransitionStatuses.ReleasedForRecovery,
                        reassignment.PreviousStatus);
                    Assert.Equal(AssignedStatus, reassignment.CurrentStatus);

                    Assert.False(
                        string.IsNullOrWhiteSpace(release.RuntimeInstanceId),
                        $"{proofName} release event for SharedRunId='{parent.SharedRunId}', RuntimeFailureIncidentId='{incidentId}' has no RuntimeInstanceId.");
                    Assert.False(
                        string.IsNullOrWhiteSpace(release.LocalRunId),
                        $"{proofName} release event for SharedRunId='{parent.SharedRunId}', RuntimeFailureIncidentId='{incidentId}' has no LocalRunId.");
                    Assert.False(
                        string.IsNullOrWhiteSpace(reassignment.RuntimeInstanceId),
                        $"{proofName} reassignment event for SharedRunId='{parent.SharedRunId}', RuntimeFailureIncidentId='{incidentId}' has no RuntimeInstanceId.");
                    Assert.False(
                        string.IsNullOrWhiteSpace(reassignment.LocalRunId),
                        $"{proofName} reassignment event for SharedRunId='{parent.SharedRunId}', RuntimeFailureIncidentId='{incidentId}' has no LocalRunId.");

                    Assert.NotEqual(
                        release.RuntimeInstanceId,
                        reassignment.RuntimeInstanceId);
                    Assert.NotEqual(
                        release.LocalRunId,
                        reassignment.LocalRunId);

                    Assert.False(
                        string.IsNullOrWhiteSpace(release.ForensicsId),
                        $"{proofName} release event for RuntimeFailureIncidentId='{incidentId}' has no ForensicsId.");
                    Assert.Equal(
                        release.ForensicsId,
                        reassignment.ForensicsId);

                    ownershipEdges.Add(
                        new ProductionRuntimeOwnershipEdge(
                            incidentId,
                            new ProductionRuntimeOwnershipOwner(
                                release.RuntimeInstanceId!,
                                release.LocalRunId!,
                                release.ExecutionId!),
                            new ProductionRuntimeOwnershipOwner(
                                reassignment.RuntimeInstanceId!,
                                reassignment.LocalRunId!,
                                reassignment.ExecutionId!)));

                    recoveryReleaseCount++;
                    recoveryReassignmentCount++;
                    recoveryTransitionCount++;
                }

                AssertSingleLinearOwnershipChain(
                    ownershipEdges,
                    parent.SharedRunId,
                    proofName);

                observedRecoveredSharedRunIds.Add(parent.SharedRunId);
            }

            Assert.Equal(
                recoveredSharedRunIds.OrderBy(
                    value => value,
                    StringComparer.Ordinal),
                observedRecoveredSharedRunIds.OrderBy(
                    value => value,
                    StringComparer.Ordinal));

            return new ProductionRuntimeOwnershipProof(
                recoveredSharedRunIds.Count,
                observedRecoveredSharedRunIds.Count,
                recoveryReleaseCount,
                recoveryReassignmentCount,
                recoveryTransitionCount,
                ValidRuntimeOwnershipOverlapCount: 0);
        }

        private static void AssertSingleLinearOwnershipChain(
            IReadOnlyCollection<ProductionRuntimeOwnershipEdge> edges,
            string sharedRunId,
            string proofName)
        {
            Assert.NotEmpty(edges);

            var edgeByReleasedOwner =
                new Dictionary<ProductionRuntimeOwnershipOwner, ProductionRuntimeOwnershipEdge>();
            var edgeByReplacementOwner =
                new Dictionary<ProductionRuntimeOwnershipOwner, ProductionRuntimeOwnershipEdge>();

            foreach (var edge in edges)
            {
                Assert.NotEqual(edge.ReleasedOwner, edge.ReplacementOwner);

                Assert.True(
                    edgeByReleasedOwner.TryAdd(
                        edge.ReleasedOwner,
                        edge),
                    $"{proofName} observed more than one ownership transition leaving the same valid owner. " +
                    $"SharedRunId='{sharedRunId}', RuntimeInstanceId='{edge.ReleasedOwner.RuntimeInstanceId}', LocalRunId='{edge.ReleasedOwner.LocalRunId}'.");

                Assert.True(
                    edgeByReplacementOwner.TryAdd(
                        edge.ReplacementOwner,
                        edge),
                    $"{proofName} observed more than one ownership transition entering the same replacement owner. " +
                    $"SharedRunId='{sharedRunId}', RuntimeInstanceId='{edge.ReplacementOwner.RuntimeInstanceId}', LocalRunId='{edge.ReplacementOwner.LocalRunId}'.");
            }

            var chainStarts =
                edgeByReleasedOwner.Keys
                    .Where(owner => !edgeByReplacementOwner.ContainsKey(owner))
                    .ToArray();
            var chainEnds =
                edgeByReplacementOwner.Keys
                    .Where(owner => !edgeByReleasedOwner.ContainsKey(owner))
                    .ToArray();

            Assert.True(
                chainStarts.Length == 1,
                $"{proofName} ownership recovery graph must contain exactly one chain start. " +
                $"SharedRunId='{sharedRunId}', ChainStartCount='{chainStarts.Length}', EdgeCount='{edges.Count}'.");
            Assert.True(
                chainEnds.Length == 1,
                $"{proofName} ownership recovery graph must contain exactly one chain end. " +
                $"SharedRunId='{sharedRunId}', ChainEndCount='{chainEnds.Length}', EdgeCount='{edges.Count}'.");

            var visitedIncidentIds =
                new HashSet<string>(StringComparer.Ordinal);
            var currentOwner = chainStarts[0];

            while (edgeByReleasedOwner.TryGetValue(
                       currentOwner,
                       out var edge))
            {
                Assert.True(
                    visitedIncidentIds.Add(edge.RuntimeFailureIncidentId),
                    $"{proofName} ownership recovery graph contains a cycle. " +
                    $"SharedRunId='{sharedRunId}', RuntimeFailureIncidentId='{edge.RuntimeFailureIncidentId}'.");

                currentOwner = edge.ReplacementOwner;
            }

            Assert.Equal(edges.Count, visitedIncidentIds.Count);
            Assert.Equal(chainEnds[0], currentOwner);
        }

        private static IReadOnlyDictionary<string, AiRuntimeLifecycleEvent> CreateIncidentMap(
            IReadOnlyCollection<AiRuntimeLifecycleEvent> events,
            string proofName,
            string sharedRunId,
            string eventType)
        {
            var result =
                new Dictionary<string, AiRuntimeLifecycleEvent>(
                    StringComparer.Ordinal);

            foreach (var lifecycleEvent in events)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        lifecycleEvent.RuntimeFailureIncidentId),
                    $"{proofName} {eventType} event for SharedRunId='{sharedRunId}' has no RuntimeFailureIncidentId.");
                Assert.True(
                    result.TryAdd(
                        lifecycleEvent.RuntimeFailureIncidentId!,
                        lifecycleEvent),
                    $"{proofName} observed more than one {eventType} event for SharedRunId='{sharedRunId}', " +
                    $"RuntimeFailureIncidentId='{lifecycleEvent.RuntimeFailureIncidentId}'.");
            }

            return result;
        }

        private static void AssertOwnershipIdentity(
            ProductionRuntimeOwnershipProofTarget parent,
            AiRuntimeLifecycleEvent lifecycleEvent,
            string proofName,
            string eventType)
        {
            Assert.Equal(parent.SharedRunId, lifecycleEvent.SharedRunId);
            Assert.Equal(parent.TenantId, lifecycleEvent.TenantId);
            Assert.False(
                string.IsNullOrWhiteSpace(parent.ExecutionId),
                $"{proofName} parent SharedRunId='{parent.SharedRunId}' has no resolved ExecutionId.");
            Assert.Equal(parent.ExecutionId, lifecycleEvent.ExecutionId);
            Assert.False(
                string.IsNullOrWhiteSpace(lifecycleEvent.ExecutionId),
                $"{proofName} {eventType} event for SharedRunId='{parent.SharedRunId}' has no ExecutionId.");
        }

        private static bool IsRecoveryOwnershipWorkEvent(
            AiRuntimeLifecycleEvent lifecycleEvent)
        {
            return string.Equals(
                       lifecycleEvent.EventType,
                       AiRuntimeLifecycleEvents.WorkReleased,
                       StringComparison.Ordinal) ||
                   string.Equals(
                       lifecycleEvent.EventType,
                       AiRuntimeLifecycleEvents.WorkReassigned,
                       StringComparison.Ordinal);
        }
    }

    internal sealed record ProductionRuntimeOwnershipProofTarget(
        string SharedRunId,
        string TenantId,
        string? ExecutionId)
    {
        public static ProductionRuntimeOwnershipProofTarget FromSharedRun(
            AiSharedRunRecord sharedRun,
            string? resolvedExecutionId = null)
        {
            ArgumentNullException.ThrowIfNull(sharedRun);

            return new ProductionRuntimeOwnershipProofTarget(
                sharedRun.SharedRunId,
                sharedRun.ExecutionContextSnapshot.TenantId,
                !string.IsNullOrWhiteSpace(resolvedExecutionId)
                    ? resolvedExecutionId
                    : sharedRun.ExecutionId);
        }
    }

    internal sealed record ProductionRuntimeOwnershipProof(
        int ExpectedRecoveredSharedRunCount,
        int ObservedRecoveredSharedRunCount,
        int RecoveryReleaseCount,
        int RecoveryReassignmentCount,
        int RecoveryTransitionCount,
        int ValidRuntimeOwnershipOverlapCount);

    internal sealed record ProductionRuntimeOwnershipOwner(
        string RuntimeInstanceId,
        string LocalRunId,
        string ExecutionId);

    internal sealed record ProductionRuntimeOwnershipEdge(
        string RuntimeFailureIncidentId,
        ProductionRuntimeOwnershipOwner ReleasedOwner,
        ProductionRuntimeOwnershipOwner ReplacementOwner);
}
