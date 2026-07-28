using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Atomically suppresses all and only shared-registry members of one failed Pod UID.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodCapacitySuppressor :
        IAiKubernetesRuntimePoolPodCapacitySuppressor
    {
        private readonly IAiKubernetesRuntimePoolPodMembershipEnumerator
            membershipEnumerator;
        private readonly IAiRuntimePoolCapacitySafetyBatchWriter batchWriter;
        private readonly IAiRuntimePoolCapacitySafetyReader safetyReader;
        private readonly SemaphoreSlim suppressionGate = new(1, 1);


        public AiKubernetesRuntimePoolPodCapacitySuppressor(
            IAiKubernetesRuntimePoolPodMembershipEnumerator membershipEnumerator,
            IAiRuntimePoolCapacitySafetyBatchWriter batchWriter,
            IAiRuntimePoolCapacitySafetyReader safetyReader)
        {
            this.membershipEnumerator =
                membershipEnumerator
                ?? throw new ArgumentNullException(nameof(membershipEnumerator));
            this.batchWriter =
                batchWriter
                ?? throw new ArgumentNullException(nameof(batchWriter));
            this.safetyReader =
                safetyReader
                ?? throw new ArgumentNullException(nameof(safetyReader));
        }

        public async Task<AiKubernetesRuntimePoolPodCapacitySuppression>
            SuppressAsync(
                AiKubernetesRuntimePoolPodCapacitySuppressionRequest request,
                CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var failureId = request.FailureId.Trim();
            var poolId = request.PoolId.Trim();
            var podUid = request.PodUid.Trim();

            await this.suppressionGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var membership =
                    await this.membershipEnumerator
                        .EnumerateAsync(
                            poolId,
                            podUid,
                            cancellationToken)
                        .ConfigureAwait(false);

                var existing =
                    await this.safetyReader
                        .ListByHostIdAsync(
                            podUid,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (existing.Count > 0)
                {
                    var reconstructed =
                        ReconstructExisting(
                            failureId,
                            poolId,
                            podUid,
                            membership,
                            existing);

                    return reconstructed;
                }

                var suppressedAtUtc = DateTimeOffset.UtcNow;
                var planned =
                    membership.Members
                        .OrderBy(
                            member => member.RuntimeInstanceId,
                            StringComparer.Ordinal)
                        .Select(
                            member =>
                                new AiRuntimePoolCapacitySuppression
                                {
                                    FailureId = failureId,
                                    Scope =
                                        AiRuntimePoolCapacitySuppressionScope
                                            .HostMembership,
                                    PoolId = poolId,
                                    HostId = podUid,
                                    RuntimeInstanceId =
                                        member.RuntimeInstanceId,
                                    RouteId = null,
                                    SuppressedAtUtc = suppressedAtUtc
                                })
                        .ToArray();

                IReadOnlyList<AiRuntimePoolCapacitySuppression> stored;

                try
                {
                    stored =
                        await this.batchWriter
                            .SuppressBatchAsync(
                                planned,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (AiRuntimePoolCapacitySuppressionConflictException exception)
                {
                    throw CreateException(
                        failureId,
                        poolId,
                        podUid,
                        AiKubernetesRuntimePoolPodCapacitySuppressionFailure
                            .AtomicCapacityConflict,
                        string.Concat(
                            "Kubernetes Pod UID '",
                            podUid,
                            "' could not atomically suppress its exact membership because RuntimeInstanceId '",
                            exception.RuntimeInstanceId,
                            "' is already bound to another immutable suppression."),
                        exception);
                }

                ValidateExactSet(
                    failureId,
                    poolId,
                    podUid,
                    membership,
                    stored,
                    AiKubernetesRuntimePoolPodCapacitySuppressionFailure
                        .AtomicWriteVerificationFailed);

                var persisted =
                    await this.safetyReader
                        .ListByHostIdAsync(
                            podUid,
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateExactSet(
                    failureId,
                    poolId,
                    podUid,
                    membership,
                    persisted,
                    AiKubernetesRuntimePoolPodCapacitySuppressionFailure
                        .AtomicWriteVerificationFailed);

                var result =
                    CreateResult(
                        failureId,
                        poolId,
                        podUid,
                        membership,
                        persisted);

                return result;
            }
            finally
            {
                this.suppressionGate.Release();
            }
        }

        private static AiKubernetesRuntimePoolPodCapacitySuppression
            ReconstructExisting(
                string failureId,
                string poolId,
                string podUid,
                AiKubernetesRuntimePoolPodMembership membership,
                IReadOnlyList<AiRuntimePoolCapacitySuppression> existing)
        {
            if (existing.Any(
                    suppression =>
                        !StringComparer.Ordinal.Equals(
                            suppression.PoolId,
                            poolId)))
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodCapacitySuppressionFailure
                        .PoolBoundaryViolation,
                    $"Kubernetes Pod UID '{podUid}' has suppression state owned by another Runtime Pool.");
            }

            var failureIds =
                existing
                    .Select(suppression => suppression.FailureId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            if (failureIds.Length != 1 ||
                !StringComparer.Ordinal.Equals(failureIds[0], failureId))
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    AiKubernetesRuntimePoolPodCapacitySuppressionFailure
                        .FailureIdentityConflict,
                    $"Kubernetes Pod UID '{podUid}' is already bound to another failure identity.");
            }

            ValidateExactSet(
                failureId,
                poolId,
                podUid,
                membership,
                existing,
                AiKubernetesRuntimePoolPodCapacitySuppressionFailure
                    .ExistingSuppressionSetMismatch);

            return CreateResult(
                failureId,
                poolId,
                podUid,
                membership,
                existing);
        }

        private static AiKubernetesRuntimePoolPodCapacitySuppression
            CreateResult(
                string failureId,
                string poolId,
                string podUid,
                AiKubernetesRuntimePoolPodMembership membership,
                IReadOnlyList<AiRuntimePoolCapacitySuppression> suppressions)
        {
            var ordered =
                suppressions
                    .OrderBy(
                        suppression => suppression.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .ToArray();

            return new AiKubernetesRuntimePoolPodCapacitySuppression
            {
                FailureId = failureId,
                PoolId = poolId,
                PodUid = podUid,
                MembershipEnumeratedAtUtc = membership.EnumeratedAtUtc,
                SuppressedAtUtc = ordered[0].SuppressedAtUtc,
                Suppressions = ordered
            };
        }

        private static void ValidateExactSet(
            string failureId,
            string poolId,
            string podUid,
            AiKubernetesRuntimePoolPodMembership membership,
            IReadOnlyList<AiRuntimePoolCapacitySuppression> suppressions,
            AiKubernetesRuntimePoolPodCapacitySuppressionFailure reason)
        {
            var expected =
                membership.Members
                    .Select(member => member.RuntimeInstanceId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            var actual =
                suppressions
                    .Where(
                        suppression =>
                            suppression.Scope ==
                                AiRuntimePoolCapacitySuppressionScope
                                    .HostMembership &&
                            suppression.RouteId is null &&
                            StringComparer.Ordinal.Equals(
                                suppression.PoolId,
                                poolId) &&
                            StringComparer.Ordinal.Equals(
                                suppression.HostId,
                                podUid) &&
                            StringComparer.Ordinal.Equals(
                                suppression.FailureId,
                                failureId))
                    .Select(suppression => suppression.RuntimeInstanceId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            var timestamps =
                suppressions
                    .Select(suppression => suppression.SuppressedAtUtc)
                    .Distinct()
                    .ToArray();

            if (expected.Length == 0 ||
                suppressions.Count != expected.Length ||
                !expected.SequenceEqual(actual, StringComparer.Ordinal) ||
                timestamps.Length != 1 ||
                timestamps[0] == default)
            {
                throw CreateException(
                    failureId,
                    poolId,
                    podUid,
                    reason,
                    $"Kubernetes Pod UID '{podUid}' does not have one complete exact atomic host-membership suppression set.");
            }
        }

        private static void ValidateRequest(
            AiKubernetesRuntimePoolPodCapacitySuppressionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PodUid);
        }

        private static AiKubernetesRuntimePoolPodCapacitySuppressionException
            CreateException(
                string failureId,
                string poolId,
                string podUid,
                AiKubernetesRuntimePoolPodCapacitySuppressionFailure reason,
                string message,
                Exception? innerException = null)
        {
            return new AiKubernetesRuntimePoolPodCapacitySuppressionException(
                failureId,
                poolId,
                podUid,
                reason,
                message,
                innerException);
        }
    }
}
