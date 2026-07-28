using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Restores one Kubernetes Runtime Pool Pod through the existing host strategy and validates
    /// that the replacement Pod and shared-registry runtime identities are fresh incarnations.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodReplacementCoordinator :
        IAiKubernetesRuntimePoolPodReplacementCoordinator
    {
        private readonly IReadOnlyList<IAiRuntimeHostCreationStrategy>
            hostCreationStrategies;

        private readonly IAiKubernetesRuntimePoolPodMembershipEnumerator
            membershipEnumerator;

        private readonly AiKubernetesRuntimePoolOptions poolOptions;
        private readonly AiKubernetesRuntimePoolHostOptions hostOptions;

        /// <summary>
        /// Initializes a new replacement coordinator.
        /// </summary>
        public AiKubernetesRuntimePoolPodReplacementCoordinator(
            IEnumerable<IAiRuntimeHostCreationStrategy>
                hostCreationStrategies,
            IAiKubernetesRuntimePoolPodMembershipEnumerator
                membershipEnumerator,
            IOptions<AiKubernetesRuntimePoolOptions> poolOptions,
            IOptions<AiKubernetesRuntimePoolHostOptions> hostOptions)
        {
            this.hostCreationStrategies =
                hostCreationStrategies?.ToArray()
                ?? throw new ArgumentNullException(
                    nameof(hostCreationStrategies));

            this.membershipEnumerator =
                membershipEnumerator
                ?? throw new ArgumentNullException(
                    nameof(membershipEnumerator));

            this.poolOptions =
                poolOptions?.Value
                ?? throw new ArgumentNullException(nameof(poolOptions));

            this.hostOptions =
                hostOptions?.Value
                ?? throw new ArgumentNullException(nameof(hostOptions));
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimePoolPodReplacement>
            CreateReplacementAsync(
                AiKubernetesRuntimePoolPodReplacementRequest request,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(
                request.ClaimedAssignedWork);
            ArgumentNullException.ThrowIfNull(
                request.HostStartTemplate);
            cancellationToken.ThrowIfCancellationRequested();

            var claimed = request.ClaimedAssignedWork;
            var inventory = claimed.Inventory;
            var claim = claimed.Claim;

            ValidateActiveClaim(claimed);
            var leaseId = claimed.Lease!.LeaseId;
            ValidateClaimAuthority(inventory, claim);
            this.ValidateHostTemplate(
                request.HostStartTemplate,
                claim);

            var strategy = this.ResolveKubernetesPoolStrategy(claim);
            var replacementRequestId =
                AiKubernetesRuntimePoolPodReplacementIdentityFactory
                    .CreateRequestId(
                        claim);

            var primaryRuntimeInstanceId =
                AiKubernetesRuntimePoolPodReplacementIdentityFactory
                    .CreatePrimaryRuntimeInstanceId(
                        this.poolOptions.RuntimeInstanceIdPrefix,
                        claim);

            var failedRuntimeInstanceIds =
                inventory.RuntimeInventories
                    .Select(item => item.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            if (failedRuntimeInstanceIds.Contains(
                    primaryRuntimeInstanceId))
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .StaleRuntimeIdentityReused,
                    "The deterministic replacement primary runtime identity collides with the failed Pod membership.");
            }

            var replacementHostRequest =
                request.HostStartTemplate with
                {
                    RequestId = replacementRequestId,
                    HostCreationMode =
                        AiRuntimeHostCreationMode.KubernetesPool,
                    PoolId = claim.PoolId,
                    HostId = null,
                    RuntimeInstanceId = primaryRuntimeInstanceId,
                    RuntimeInstanceIdPrefix =
                        this.poolOptions.RuntimeInstanceIdPrefix,
                    ProviderName = this.poolOptions.ProviderName,
                    TransportName = this.poolOptions.TransportName,
                    TransportEndpoint = null,
                    Metadata =
                        new Dictionary<string, string>()
                };

            var startResult =
                await strategy
                    .StartAsync(
                        replacementHostRequest,
                        cancellationToken)
                    .ConfigureAwait(false);

            ValidateActiveClaim(claimed);

            if (!startResult.Success)
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .HostStartRejected,
                    startResult.FailureReason
                    ?? "Kubernetes Runtime Pool replacement host creation was rejected.",
                    startResult.Retryable);
            }

            if (!StringComparer.Ordinal.Equals(
                    startResult.RuntimeInstanceId,
                    primaryRuntimeInstanceId))
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .PrimaryRuntimeMissing,
                    "The KubernetesPool host strategy returned a different primary runtime identity.");
            }

            var replacementPodUid =
                ResolveReplacementPodUid(
                    claim,
                    startResult);

            if (StringComparer.Ordinal.Equals(
                    replacementPodUid,
                    inventory.PodUid))
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .FailedPodUidReused,
                    "Kubernetes returned the failed Pod UID for replacement capacity.");
            }

            ValidateStartMetadata(
                claim,
                replacementPodUid,
                startResult);

            var replacementMembership =
                await this.WaitForReadyMembershipAsync(
                        claim,
                        replacementPodUid,
                        primaryRuntimeInstanceId,
                        failedRuntimeInstanceIds,
                        cancellationToken)
                    .ConfigureAwait(false);

            ValidateActiveClaim(claimed);

            return new AiKubernetesRuntimePoolPodReplacement
            {
                FailureId = claim.FailureId,
                PoolId = claim.PoolId,
                FailedPodUid = inventory.PodUid,
                ReplacementPodUid = replacementPodUid,
                ReplacementRequestId = replacementRequestId,
                PrimaryRuntimeInstanceId =
                    primaryRuntimeInstanceId,
                RecoveryLeaseId = leaseId,
                HostStartResult = startResult,
                Membership = replacementMembership,
                ReadyAtUtc = DateTimeOffset.UtcNow
            };
        }

        private IAiRuntimeHostCreationStrategy ResolveKubernetesPoolStrategy(
            AiRuntimePoolRecoveryMembershipClaim claim)
        {
            var strategies =
                this.hostCreationStrategies
                    .Where(
                        item =>
                            item.Mode ==
                            AiRuntimeHostCreationMode.KubernetesPool)
                    .ToArray();

            if (strategies.Length != 1)
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .HostCreationStrategyUnavailable,
                    string.Concat(
                        "Expected exactly one KubernetesPool host creation strategy, but found ",
                        strategies.Length,
                        "."));
            }

            return strategies[0];
        }

        private async Task<AiKubernetesRuntimePoolPodMembership>
            WaitForReadyMembershipAsync(
                AiRuntimePoolRecoveryMembershipClaim claim,
                string replacementPodUid,
                string primaryRuntimeInstanceId,
                IReadOnlySet<string> failedRuntimeInstanceIds,
                CancellationToken cancellationToken)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(
                    this.hostOptions.StartupTimeout);

            string? lastPendingReason = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var membership =
                        await this.membershipEnumerator
                            .EnumerateAsync(
                                claim.PoolId,
                                replacementPodUid,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (TryValidateReadyMembership(
                            claim,
                            replacementPodUid,
                            primaryRuntimeInstanceId,
                            this.poolOptions.InitialRuntimeInstanceCount,
                            failedRuntimeInstanceIds,
                            membership,
                            out lastPendingReason))
                    {
                        return membership;
                    }
                }
                catch (
                    AiKubernetesRuntimePoolPodMembershipAuthorityException
                    exception)
                    when (exception.Reason ==
                        AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                            .MembershipNotFound)
                {
                    lastPendingReason = exception.Message;
                }
                catch (
                    AiKubernetesRuntimePoolPodMembershipAuthorityException
                    exception)
                {
                    throw CreateException(
                        claim,
                        AiKubernetesRuntimePoolPodReplacementFailure
                            .MembershipAuthorityMismatch,
                        exception.Message,
                        retryable: false,
                        exception);
                }

                await Task
                    .Delay(
                        this.hostOptions.ReadinessPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw CreateException(
                claim,
                AiKubernetesRuntimePoolPodReplacementFailure
                    .MembershipReadinessTimeout,
                lastPendingReason
                ?? "Replacement Pod membership did not become exactly ready before the configured deadline.",
                retryable: true);
        }

        private static bool TryValidateReadyMembership(
            AiRuntimePoolRecoveryMembershipClaim claim,
            string replacementPodUid,
            string primaryRuntimeInstanceId,
            int expectedMemberCount,
            IReadOnlySet<string> failedRuntimeInstanceIds,
            AiKubernetesRuntimePoolPodMembership membership,
            out string? pendingReason)
        {
            ArgumentNullException.ThrowIfNull(membership);

            if (!StringComparer.Ordinal.Equals(
                    membership.PoolId,
                    claim.PoolId) ||
                !StringComparer.Ordinal.Equals(
                    membership.PodUid,
                    replacementPodUid))
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .MembershipAuthorityMismatch,
                    "Replacement membership crossed its exact Pool or Pod UID boundary.");
            }

            foreach (var member in membership.Members)
            {
                if (!StringComparer.Ordinal.Equals(
                        member.PoolId,
                        claim.PoolId) ||
                    !StringComparer.Ordinal.Equals(
                        member.PodUid,
                        replacementPodUid))
                {
                    throw CreateException(
                        claim,
                        AiKubernetesRuntimePoolPodReplacementFailure
                            .MembershipAuthorityMismatch,
                        "Replacement membership contains a runtime outside the exact Pool or Pod UID boundary.");
                }

                if (failedRuntimeInstanceIds.Contains(
                        member.RuntimeInstanceId))
                {
                    throw CreateException(
                        claim,
                        AiKubernetesRuntimePoolPodReplacementFailure
                            .StaleRuntimeIdentityReused,
                        string.Concat(
                            "Replacement Pod reused failed runtime identity '",
                            member.RuntimeInstanceId,
                            "'."));
                }
            }

            if (membership.Members.Count > expectedMemberCount)
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .MembershipAuthorityMismatch,
                    string.Concat(
                        "Replacement Pod registered ",
                        membership.Members.Count,
                        " members while exactly ",
                        expectedMemberCount,
                        " were planned."));
            }

            if (membership.Members.Count < expectedMemberCount)
            {
                pendingReason =
                    string.Concat(
                        "Replacement Pod has ",
                        membership.Members.Count,
                        " of ",
                        expectedMemberCount,
                        " registered members.");
                return false;
            }

            if (membership.Members.Any(
                    member =>
                        member.Status != AiRuntimeInstanceStatus.Ready ||
                        !member.CanAcceptRun))
            {
                pendingReason =
                    "Replacement Pod membership exists but not every registered runtime is Ready and selectable.";
                return false;
            }

            if (!membership.Members.Any(
                    member =>
                        StringComparer.Ordinal.Equals(
                            member.RuntimeInstanceId,
                            primaryRuntimeInstanceId)))
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .PrimaryRuntimeMissing,
                    "The provider-selected fresh primary runtime did not register in replacement membership.");
            }

            pendingReason = null;
            return true;
        }

        private void ValidateHostTemplate(
            AiRuntimeHostStartRequest template,
            AiRuntimePoolRecoveryMembershipClaim claim)
        {
            ArgumentNullException.ThrowIfNull(
                template.ExecutionContextSnapshot);

            var valid =
                template.HostCreationMode ==
                    AiRuntimeHostCreationMode.KubernetesPool &&
                StringComparer.Ordinal.Equals(
                    template.PoolId,
                    claim.PoolId) &&
                !string.IsNullOrWhiteSpace(
                    template.ControlPlaneId) &&
                StringComparer.OrdinalIgnoreCase.Equals(
                    template.ProviderName,
                    this.poolOptions.ProviderName) &&
                StringComparer.OrdinalIgnoreCase.Equals(
                    template.TransportName,
                    this.poolOptions.TransportName);

            if (!valid)
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .InvalidHostTemplate,
                    "The replacement host template does not match KubernetesPool, PoolId, control-plane, provider, or transport authority.");
            }
        }

        private static void ValidateActiveClaim(
            AiKubernetesRuntimePoolPodClaimedAssignedWork claimed)
        {
            var claim = claimed.Claim;
            var lease = claimed.Lease;

            if (claimed.Status !=
                    AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired ||
                lease is null ||
                lease.IsReleased ||
                !ClaimsMatch(claim, lease.Claim))
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .InactiveRecoveryLease,
                    "Replacement capacity requires the only active exact-membership recovery lease.");
            }
        }

        private static void ValidateClaimAuthority(
            AiKubernetesRuntimePoolPodAssignedWorkInventory inventory,
            AiRuntimePoolRecoveryMembershipClaim claim)
        {
            ArgumentNullException.ThrowIfNull(inventory);
            ArgumentNullException.ThrowIfNull(claim);

            var authorityMatches =
                StringComparer.Ordinal.Equals(
                    inventory.FailureId,
                    claim.FailureId) &&
                StringComparer.Ordinal.Equals(
                    inventory.PoolId,
                    claim.PoolId) &&
                StringComparer.Ordinal.Equals(
                    inventory.PodUid,
                    claim.HostId) &&
                inventory.RuntimeInventories.Count ==
                    claim.MemberCount &&
                inventory.Candidates.Count ==
                    claim.CandidateCount &&
                StringComparer.Ordinal.Equals(
                    AiKubernetesRuntimePoolPodRecoveryInventoryFingerprint
                        .CalculateMembership(inventory),
                    claim.MembershipFingerprint) &&
                StringComparer.Ordinal.Equals(
                    AiKubernetesRuntimePoolPodRecoveryInventoryFingerprint
                        .CalculateInventory(inventory),
                    claim.InventoryFingerprint);

            if (!authorityMatches)
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .ClaimAuthorityMismatch,
                    "The claimed failed-Pod inventory no longer matches its deterministic recovery authority.");
            }
        }

        private static string ResolveReplacementPodUid(
            AiRuntimePoolRecoveryMembershipClaim claim,
            AiRuntimeHostStartResult startResult)
        {
            if (!startResult.Metadata.TryGetValue(
                    AiRuntimeHostMetadataKeys.HostId,
                    out var hostId) ||
                string.IsNullOrWhiteSpace(hostId))
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .ReplacementPodUidMissing,
                    "The KubernetesPool host strategy did not return the replacement Pod UID.",
                    retryable: true);
            }

            return hostId.Trim();
        }

        private static void ValidateStartMetadata(
            AiRuntimePoolRecoveryMembershipClaim claim,
            string replacementPodUid,
            AiRuntimeHostStartResult startResult)
        {
            if (!startResult.Metadata.TryGetValue(
                    "runtime.pool.id",
                    out var metadataPoolId) ||
                !StringComparer.Ordinal.Equals(
                    metadataPoolId,
                    claim.PoolId))
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .MembershipAuthorityMismatch,
                    "Replacement host metadata does not preserve the exact PoolId authority.");
            }

            if (startResult.Metadata.TryGetValue(
                    "kubernetes.pod.uid",
                    out var metadataPodUid) &&
                !StringComparer.Ordinal.Equals(
                    metadataPodUid,
                    replacementPodUid))
            {
                throw CreateException(
                    claim,
                    AiKubernetesRuntimePoolPodReplacementFailure
                        .MembershipAuthorityMismatch,
                    "Replacement host metadata returned conflicting Kubernetes Pod UIDs.");
            }
        }

        private static bool ClaimsMatch(
            AiRuntimePoolRecoveryMembershipClaim first,
            AiRuntimePoolRecoveryMembershipClaim second)
        {
            return
                StringComparer.Ordinal.Equals(
                    first.ClaimId,
                    second.ClaimId) &&
                StringComparer.Ordinal.Equals(
                    first.FailureId,
                    second.FailureId) &&
                StringComparer.Ordinal.Equals(
                    first.PoolId,
                    second.PoolId) &&
                StringComparer.Ordinal.Equals(
                    first.HostId,
                    second.HostId) &&
                StringComparer.Ordinal.Equals(
                    first.MembershipFingerprint,
                    second.MembershipFingerprint) &&
                first.MemberCount == second.MemberCount &&
                StringComparer.Ordinal.Equals(
                    first.InventoryFingerprint,
                    second.InventoryFingerprint) &&
                first.CandidateCount == second.CandidateCount;
        }

        private static AiKubernetesRuntimePoolPodReplacementException
            CreateException(
                AiRuntimePoolRecoveryMembershipClaim claim,
                AiKubernetesRuntimePoolPodReplacementFailure reason,
                string message,
                bool retryable = false,
                Exception? innerException = null)
        {
            return new AiKubernetesRuntimePoolPodReplacementException(
                claim.FailureId,
                claim.PoolId,
                claim.HostId,
                reason,
                message,
                retryable,
                innerException);
        }
    }
}
