using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Composes the existing Pod membership, capacity, claim, host strategy, and transition boundaries.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodFailureRecoveryCoordinator :
        IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator
    {
        private readonly IAiRuntimePoolFailureObserver failureObserver;
        private readonly IAiRuntimePoolFailureReader failureReader;
        private readonly IAiKubernetesRuntimePoolPodCapacitySuppressor
            capacitySuppressor;
        private readonly IAiKubernetesRuntimePoolPodRecoveryClaimCoordinator
            claimCoordinator;
        private readonly IAiKubernetesRuntimePoolPodReplacementCoordinator
            replacementCoordinator;
        private readonly IAiKubernetesRuntimePoolPodClaimedRecoveryExecutor
            recoveryExecutor;

        public AiKubernetesRuntimePoolPodFailureRecoveryCoordinator(
            IAiRuntimePoolFailureObserver failureObserver,
            IAiRuntimePoolFailureReader failureReader,
            IAiKubernetesRuntimePoolPodCapacitySuppressor capacitySuppressor,
            IAiKubernetesRuntimePoolPodRecoveryClaimCoordinator
                claimCoordinator,
            IAiKubernetesRuntimePoolPodReplacementCoordinator
                replacementCoordinator,
            IAiKubernetesRuntimePoolPodClaimedRecoveryExecutor recoveryExecutor)
        {
            this.failureObserver =
                failureObserver
                ?? throw new ArgumentNullException(nameof(failureObserver));
            this.failureReader =
                failureReader
                ?? throw new ArgumentNullException(nameof(failureReader));
            this.capacitySuppressor =
                capacitySuppressor
                ?? throw new ArgumentNullException(
                    nameof(capacitySuppressor));
            this.claimCoordinator =
                claimCoordinator
                ?? throw new ArgumentNullException(nameof(claimCoordinator));
            this.replacementCoordinator =
                replacementCoordinator
                ?? throw new ArgumentNullException(
                    nameof(replacementCoordinator));
            this.recoveryExecutor =
                recoveryExecutor
                ?? throw new ArgumentNullException(nameof(recoveryExecutor));
        }

        public async Task<AiKubernetesRuntimePoolPodFailureRecoveryResult>
            RecoverAsync(
                AiKubernetesRuntimePoolPodFailureRecoveryRequest request,
                CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var failureId = request.FailureId.Trim();
            var poolId = request.PoolId.Trim();
            var podUid = request.PodUid.Trim();

            var failure =
                await this.GetOrRecordFailureAsync(
                        failureId,
                        poolId,
                        podUid,
                        request.FailureMessage,
                        cancellationToken)
                    .ConfigureAwait(false);

            var suppression =
                await this.capacitySuppressor
                    .SuppressAsync(
                        new AiKubernetesRuntimePoolPodCapacitySuppressionRequest
                        {
                            FailureId = failureId,
                            PoolId = poolId,
                            PodUid = podUid
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            var claimed =
                await this.claimCoordinator
                    .TryAcquireAsync(
                        new AiKubernetesRuntimePoolPodAssignedWorkRequest
                        {
                            FailureId = failureId,
                            PoolId = poolId,
                            PodUid = podUid
                        },
                        request.ClaimedBy,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (claimed.Status ==
                AiRuntimePoolRecoveryClaimAcquisitionStatus.AlreadyClaimed)
            {
                return new AiKubernetesRuntimePoolPodFailureRecoveryResult
                {
                    FailureId = failureId,
                    PoolId = poolId,
                    FailedPodUid = podUid,
                    Status = claimed.Status,
                    Failure = failure,
                    Suppression = suppression,
                    ClaimedAssignedWork = claimed,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                };
            }

            if (claimed.Lease is null)
            {
                throw new InvalidOperationException(
                    "An acquired failed-Pod recovery claim must include its active membership lease.");
            }

            await using var lease = claimed.Lease;

            var replacement =
                await this.replacementCoordinator
                    .CreateReplacementAsync(
                        new AiKubernetesRuntimePoolPodReplacementRequest
                        {
                            ClaimedAssignedWork = claimed,
                            HostStartTemplate = request.HostStartTemplate
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            var recovery =
                await this.recoveryExecutor
                    .ExecuteAsync(
                        claimed,
                        cancellationToken)
                    .ConfigureAwait(false);

            return new AiKubernetesRuntimePoolPodFailureRecoveryResult
            {
                FailureId = failureId,
                PoolId = poolId,
                FailedPodUid = podUid,
                Status = claimed.Status,
                Failure = failure,
                Suppression = suppression,
                ClaimedAssignedWork = claimed,
                Replacement = replacement,
                Recovery = recovery,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }


        private async Task<AiRuntimePoolFailureObservation>
            GetOrRecordFailureAsync(
                string failureId,
                string poolId,
                string podUid,
                string? failureMessage,
                CancellationToken cancellationToken)
        {
            var existing =
                await this.failureReader
                    .GetByFailureIdAsync(
                        failureId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (existing is not null)
            {
                ValidateFailureAuthority(
                    existing,
                    failureId,
                    poolId,
                    podUid);

                return existing;
            }

            try
            {
                return await this.failureObserver
                    .RecordAsync(
                        new AiRuntimePoolFailureObservation
                        {
                            FailureId = failureId,
                            Scope = AiRuntimePoolFailureScope.Host,
                            PoolId = poolId,
                            HostId = podUid,
                            RuntimeInstanceId = null,
                            RouteId = null,
                            Kind =
                                AiRuntimePoolFailureKind
                                    .UnexpectedPodDeletion,
                            ObservedAtUtc = DateTimeOffset.UtcNow,
                            FailureMessage = failureMessage
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AiRuntimePoolFailureConflictException)
            {
                existing =
                    await this.failureReader
                        .GetByFailureIdAsync(
                            failureId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (existing is null)
                {
                    throw;
                }

                ValidateFailureAuthority(
                    existing,
                    failureId,
                    poolId,
                    podUid);

                return existing;
            }
        }

        private static void ValidateFailureAuthority(
            AiRuntimePoolFailureObservation failure,
            string failureId,
            string poolId,
            string podUid)
        {
            var matches =
                StringComparer.Ordinal.Equals(
                    failure.FailureId,
                    failureId) &&
                failure.Scope == AiRuntimePoolFailureScope.Host &&
                StringComparer.Ordinal.Equals(
                    failure.PoolId,
                    poolId) &&
                StringComparer.Ordinal.Equals(
                    failure.HostId,
                    podUid) &&
                failure.RuntimeInstanceId is null &&
                failure.RouteId is null &&
                failure.Kind ==
                    AiRuntimePoolFailureKind.UnexpectedPodDeletion;

            if (!matches)
            {
                throw new AiRuntimePoolFailureConflictException(failureId);
            }
        }

        private static void ValidateRequest(
            AiKubernetesRuntimePoolPodFailureRecoveryRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PodUid);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ClaimedBy);
            ArgumentNullException.ThrowIfNull(request.HostStartTemplate);
        }
    }
}
