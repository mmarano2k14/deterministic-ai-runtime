using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Enumerates one exact Kubernetes Runtime Pool Pod from the shared runtime registry.
    /// </summary>
    /// <remarks>
    /// The shared registry is the cross-process membership authority. The pool route registry is
    /// intentionally not used because routes are local to the pool host and disappear with the Pod.
    /// </remarks>
    public sealed class AiKubernetesRuntimePoolPodMembershipEnumerator :
        IAiKubernetesRuntimePoolPodMembershipEnumerator
    {
        private readonly IAiRuntimePoolMembershipReader membershipReader;

        public AiKubernetesRuntimePoolPodMembershipEnumerator(
            IAiRuntimePoolMembershipReader membershipReader)
        {
            this.membershipReader =
                membershipReader
                ?? throw new ArgumentNullException(nameof(membershipReader));
        }

        public async Task<AiKubernetesRuntimePoolPodMembership> EnumerateAsync(
            string poolId,
            string podUid,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(podUid);
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedPoolId = poolId.Trim();
            var normalizedPodUid = podUid.Trim();

            var snapshots =
                await this.membershipReader
                    .ListByHostIdAsync(
                        normalizedPodUid,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (snapshots.Count == 0)
            {
                throw CreateAuthorityException(
                    normalizedPoolId,
                    normalizedPodUid,
                    AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                        .MembershipNotFound,
                    $"Kubernetes Runtime Pool Pod '{normalizedPodUid}' has no current shared-registry membership.");
            }

            var runtimeInstanceIds =
                new HashSet<string>(StringComparer.Ordinal);

            var members =
                new List<AiKubernetesRuntimePoolPodMember>(snapshots.Count);

            foreach (var snapshot in snapshots)
            {
                ValidateSnapshotIdentity(
                    normalizedPoolId,
                    normalizedPodUid,
                    snapshot);

                if (!StringComparer.Ordinal.Equals(
                        snapshot.PoolId,
                        normalizedPoolId))
                {
                    throw CreateAuthorityException(
                        normalizedPoolId,
                        normalizedPodUid,
                        AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                            .PoolBoundaryViolation,
                        $"Runtime instance '{snapshot.RuntimeInstanceId}' belongs to Runtime Pool '{snapshot.PoolId}' instead of '{normalizedPoolId}'.");
                }

                if (!StringComparer.Ordinal.Equals(
                        snapshot.HostId,
                        normalizedPodUid))
                {
                    throw CreateAuthorityException(
                        normalizedPoolId,
                        normalizedPodUid,
                        AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                            .PodBoundaryViolation,
                        $"Runtime instance '{snapshot.RuntimeInstanceId}' belongs to host '{snapshot.HostId}' instead of Kubernetes Pod UID '{normalizedPodUid}'.");
                }

                if (!runtimeInstanceIds.Add(snapshot.RuntimeInstanceId))
                {
                    throw CreateAuthorityException(
                        normalizedPoolId,
                        normalizedPodUid,
                        AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                            .DuplicateRuntimeInstanceId,
                        $"Kubernetes Pod UID '{normalizedPodUid}' returned duplicate runtime instance '{snapshot.RuntimeInstanceId}'.");
                }

                members.Add(
                    new AiKubernetesRuntimePoolPodMember
                    {
                        PoolId = snapshot.PoolId!,
                        PodUid = snapshot.HostId!,
                        RuntimeInstanceId = snapshot.RuntimeInstanceId,
                        RuntimeId = snapshot.RuntimeId,
                        Status = snapshot.Status,
                        CanAcceptRun = snapshot.CanAcceptRun,
                        RegisteredAtUtc = snapshot.RegisteredAtUtc,
                        LastHeartbeatAtUtc = snapshot.LastHeartbeatAtUtc
                    });
            }

            return new AiKubernetesRuntimePoolPodMembership
            {
                PoolId = normalizedPoolId,
                PodUid = normalizedPodUid,
                EnumeratedAtUtc = DateTimeOffset.UtcNow,
                Members = members
                    .OrderBy(
                        member => member.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .ToArray()
            };
        }

        private static void ValidateSnapshotIdentity(
            string poolId,
            string podUid,
            AiRuntimeInstanceSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (string.IsNullOrWhiteSpace(snapshot.RuntimeInstanceId) ||
                string.IsNullOrWhiteSpace(snapshot.PoolId) ||
                string.IsNullOrWhiteSpace(snapshot.HostId))
            {
                throw CreateAuthorityException(
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                        .InvalidRegistryIdentity,
                    $"Kubernetes Pod UID '{podUid}' returned a registry member with incomplete first-class identity.");
            }
        }

        private static AiKubernetesRuntimePoolPodMembershipAuthorityException
            CreateAuthorityException(
                string poolId,
                string podUid,
                AiKubernetesRuntimePoolPodMembershipAuthorityFailure reason,
                string message)
        {
            return new AiKubernetesRuntimePoolPodMembershipAuthorityException(
                poolId,
                podUid,
                reason,
                message);
        }
    }
}
