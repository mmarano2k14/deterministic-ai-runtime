using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Enumerates durable work across all and only atomically suppressed children of one Pod UID.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodAssignedWorkEnumerator :
        IAiKubernetesRuntimePoolPodAssignedWorkEnumerator
    {
        private readonly IAiRuntimePoolCapacitySafetyReader safetyReader;
        private readonly IAiRuntimePoolSuppressedAssignedWorkEnumerator
            assignedWorkEnumerator;

        public AiKubernetesRuntimePoolPodAssignedWorkEnumerator(
            IAiRuntimePoolCapacitySafetyReader safetyReader,
            IAiRuntimePoolSuppressedAssignedWorkEnumerator assignedWorkEnumerator)
        {
            this.safetyReader =
                safetyReader
                ?? throw new ArgumentNullException(nameof(safetyReader));
            this.assignedWorkEnumerator =
                assignedWorkEnumerator
                ?? throw new ArgumentNullException(
                    nameof(assignedWorkEnumerator));
        }

        public async Task<AiKubernetesRuntimePoolPodAssignedWorkInventory>
            EnumerateAsync(
                AiKubernetesRuntimePoolPodAssignedWorkRequest request,
                CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var failureId = request.FailureId.Trim();
            var poolId = request.PoolId.Trim();
            var podUid = request.PodUid.Trim();

            var suppressions =
                await this.safetyReader
                    .ListByHostIdAsync(
                        podUid,
                        cancellationToken)
                    .ConfigureAwait(false);

            var orderedSuppressions =
                ValidateAndOrderSuppressions(
                    failureId,
                    poolId,
                    podUid,
                    suppressions);

            var runtimeInventories =
                new List<AiRuntimePoolAssignedWorkInventory>(
                    orderedSuppressions.Length);

            foreach (var suppression in orderedSuppressions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var inventory =
                    await this.assignedWorkEnumerator
                        .EnumerateAsync(
                            suppression,
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateRuntimeInventory(
                    failureId,
                    poolId,
                    podUid,
                    suppression,
                    inventory);

                runtimeInventories.Add(inventory);
            }

            var candidates =
                runtimeInventories
                    .SelectMany(inventory => inventory.Candidates)
                    .OrderBy(candidate => candidate.Kind)
                    .ThenBy(candidate => candidate.CreatedAtUtc)
                    .ThenBy(
                        candidate => candidate.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .ThenBy(
                        candidate => candidate.LocalRunId,
                        StringComparer.Ordinal)
                    .ToArray();

            ValidateCandidateUniqueness(
                failureId,
                poolId,
                podUid,
                candidates);

            return new AiKubernetesRuntimePoolPodAssignedWorkInventory
            {
                FailureId = failureId,
                PoolId = poolId,
                PodUid = podUid,
                EnumeratedAtUtc = DateTimeOffset.UtcNow,
                RuntimeInventories = runtimeInventories.ToArray(),
                Candidates = candidates
            };
        }

        private static AiRuntimePoolCapacitySuppression[]
            ValidateAndOrderSuppressions(
                string failureId,
                string poolId,
                string podUid,
                IReadOnlyList<AiRuntimePoolCapacitySuppression> suppressions)
        {
            if (suppressions.Count == 0)
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodAssignedWorkFailure
                        .SuppressionSetMissing,
                    $"Kubernetes Pod UID '{podUid}' has no authoritative suppressed child membership.");
            }

            if (suppressions.Any(
                    suppression =>
                        !StringComparer.Ordinal.Equals(
                            suppression.FailureId,
                            failureId)))
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodAssignedWorkFailure
                        .FailureIdentityMismatch,
                    $"Kubernetes Pod UID '{podUid}' contains suppression state from another failure identity.");
            }

            if (suppressions.Any(
                    suppression =>
                        !StringComparer.Ordinal.Equals(
                            suppression.PoolId,
                            poolId)))
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodAssignedWorkFailure
                        .PoolBoundaryViolation,
                    $"Kubernetes Pod UID '{podUid}' contains suppression state from another Runtime Pool.");
            }

            if (suppressions.Any(
                    suppression =>
                        !StringComparer.Ordinal.Equals(
                            suppression.HostId,
                            podUid)))
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodAssignedWorkFailure
                        .PodBoundaryViolation,
                    $"Kubernetes Pod UID '{podUid}' contains suppression state from another host incarnation.");
            }

            if (suppressions.Any(
                    suppression =>
                        suppression.Scope !=
                            AiRuntimePoolCapacitySuppressionScope
                                .HostMembership ||
                        suppression.RouteId is not null))
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodAssignedWorkFailure
                        .RuntimeInventoryMismatch,
                    $"Kubernetes Pod UID '{podUid}' contains route-scoped suppression instead of host-membership authority.");
            }

            if (suppressions
                .GroupBy(
                    suppression => suppression.RuntimeInstanceId,
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodAssignedWorkFailure
                        .DuplicateRuntimeIdentity,
                    $"Kubernetes Pod UID '{podUid}' contains duplicate suppressed RuntimeInstanceId values.");
            }

            return suppressions
                .OrderBy(
                    suppression => suppression.RuntimeInstanceId,
                    StringComparer.Ordinal)
                .ToArray();
        }

        private static void ValidateRuntimeInventory(
            string failureId,
            string poolId,
            string podUid,
            AiRuntimePoolCapacitySuppression suppression,
            AiRuntimePoolAssignedWorkInventory inventory)
        {
            ArgumentNullException.ThrowIfNull(inventory);

            var inventoryMatches =
                StringComparer.Ordinal.Equals(
                    inventory.FailureId,
                    suppression.FailureId) &&
                StringComparer.Ordinal.Equals(
                    inventory.PoolId,
                    suppression.PoolId) &&
                StringComparer.Ordinal.Equals(
                    inventory.HostId,
                    suppression.HostId) &&
                StringComparer.Ordinal.Equals(
                    inventory.RuntimeInstanceId,
                    suppression.RuntimeInstanceId) &&
                inventory.RouteId is null &&
                inventory.Candidates.All(
                    candidate =>
                        StringComparer.Ordinal.Equals(
                            candidate.FailureId,
                            suppression.FailureId) &&
                        StringComparer.Ordinal.Equals(
                            candidate.PoolId,
                            suppression.PoolId) &&
                        StringComparer.Ordinal.Equals(
                            candidate.HostId,
                            suppression.HostId) &&
                        StringComparer.Ordinal.Equals(
                            candidate.RuntimeInstanceId,
                            suppression.RuntimeInstanceId) &&
                        candidate.RouteId is null);

            if (!inventoryMatches)
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodAssignedWorkFailure
                        .RuntimeInventoryMismatch,
                    $"Assigned-work inventory for RuntimeInstanceId '{suppression.RuntimeInstanceId}' does not match its authoritative Pod suppression.");
            }
        }

        private static void ValidateCandidateUniqueness(
            string failureId,
            string poolId,
            string podUid,
            IReadOnlyList<AiRuntimePoolAssignedWorkCandidate> candidates)
        {
            if (candidates
                .GroupBy(
                    candidate => candidate.LocalRunId,
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodAssignedWorkFailure
                        .DuplicateLocalRunIdentity,
                    $"Kubernetes Pod UID '{podUid}' exposes the same LocalRunId under more than one failed child runtime.");
            }

            if (candidates
                .Where(
                    candidate =>
                        !string.IsNullOrWhiteSpace(candidate.ExecutionId))
                .GroupBy(
                    candidate => candidate.ExecutionId!,
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodAssignedWorkFailure
                        .DuplicateExecutionIdentity,
                    $"Kubernetes Pod UID '{podUid}' exposes the same durable ExecutionId under more than one failed child runtime.");
            }
        }

        private static void ValidateRequest(
            AiKubernetesRuntimePoolPodAssignedWorkRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PodUid);
        }

        private static AiKubernetesRuntimePoolPodAssignedWorkException
            CreateException(
                string failureId,
                string poolId,
                string podUid,
                AiKubernetesRuntimePoolPodAssignedWorkFailure reason,
                string message)
        {
            return new AiKubernetesRuntimePoolPodAssignedWorkException(
                failureId,
                poolId,
                podUid,
                reason,
                message);
        }
    }
}
