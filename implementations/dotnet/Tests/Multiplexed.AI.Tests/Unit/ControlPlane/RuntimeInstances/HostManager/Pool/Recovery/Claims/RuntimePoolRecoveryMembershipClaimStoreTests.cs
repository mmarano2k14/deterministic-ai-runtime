using System;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Validates atomic and cross-scope recovery claim ownership.
    /// </summary>
    public sealed class RuntimePoolRecoveryMembershipClaimStoreTests
    {
        /// <summary>
        /// Verifies the same exact membership can only be actively acquired once.
        /// </summary>
        [Fact]
        public async Task TryAcquireMembershipAsync_Should_Deduplicate_Exact_Authority()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var first =
                await store.TryAcquireMembershipAsync(
                    CreateMembershipRequest("coordinator-a"));

            var second =
                await store.TryAcquireMembershipAsync(
                    CreateMembershipRequest("coordinator-b"));

            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                first.Status);
            Assert.NotNull(first.Lease);
            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.AlreadyClaimed,
                second.Status);
            Assert.Null(second.Lease);
            Assert.Equal(
                first.Claim.ClaimId,
                second.Claim.ClaimId);

            var lease =
                first.Lease
                ?? throw new InvalidOperationException(
                    "The acquired claim did not expose its lease.");

            Assert.True(
                await store.IsActiveMembershipLeaseAsync(
                    first.Claim.FailureId,
                    first.Claim.ClaimId,
                    lease.LeaseId));

            await lease.DisposeAsync();
        }

        /// <summary>
        /// Verifies changed membership under the same failure identity is rejected.
        /// </summary>
        [Fact]
        public async Task TryAcquireMembershipAsync_Should_Reject_Changed_Membership()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var first =
                await store.TryAcquireMembershipAsync(
                    CreateMembershipRequest("coordinator-a"));

            var changed =
                CreateMembershipRequest("coordinator-b") with
                {
                    MembershipFingerprint = "membership-changed"
                };

            await Assert.ThrowsAsync<
                AiRuntimePoolRecoveryClaimConflictException>(
                () => store.TryAcquireMembershipAsync(changed));

            await first.Lease!.DisposeAsync();
        }

        /// <summary>
        /// Verifies one failure identity cannot be owned simultaneously by runtime and membership claims.
        /// </summary>
        [Fact]
        public async Task Store_Should_Reject_CrossScope_Duplicate_Failure()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var membership =
                await store.TryAcquireMembershipAsync(
                    CreateMembershipRequest("coordinator-a"));

            await Assert.ThrowsAsync<
                AiRuntimePoolRecoveryClaimConflictException>(
                () =>
                    store.TryAcquireAsync(
                        new AiRuntimePoolRecoveryClaimRequest
                        {
                            FailureId = "failure-pod-01",
                            PoolId = "pool-01",
                            HostId = "pod-uid-01",
                            RuntimeInstanceId = "runtime-a",
                            RouteId = "route-a",
                            InventoryFingerprint = "inventory-runtime",
                            CandidateCount = 1,
                            ClaimedBy = "runtime-coordinator"
                        }));

            await membership.Lease!.DisposeAsync();
        }

        /// <summary>
        /// Verifies releasing the only lease permits a later exact reacquisition.
        /// </summary>
        [Fact]
        public async Task MembershipLease_Should_Release_Exact_Claim()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var first =
                await store.TryAcquireMembershipAsync(
                    CreateMembershipRequest("coordinator-a"));

            var firstLease =
                first.Lease
                ?? throw new InvalidOperationException(
                    "The acquired claim did not expose its lease.");

            await firstLease.DisposeAsync();

            Assert.False(
                await store.IsActiveMembershipLeaseAsync(
                    first.Claim.FailureId,
                    first.Claim.ClaimId,
                    firstLease.LeaseId));

            var second =
                await store.TryAcquireMembershipAsync(
                    CreateMembershipRequest("coordinator-b"));

            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                second.Status);
            Assert.Equal(
                first.Claim.ClaimId,
                second.Claim.ClaimId);

            await second.Lease!.DisposeAsync();
        }

        private static AiRuntimePoolRecoveryMembershipClaimRequest
            CreateMembershipRequest(string claimedBy)
        {
            return new AiRuntimePoolRecoveryMembershipClaimRequest
            {
                FailureId = "failure-pod-01",
                PoolId = "pool-01",
                HostId = "pod-uid-01",
                MembershipFingerprint = "membership-01",
                MemberCount = 3,
                InventoryFingerprint = "inventory-01",
                CandidateCount = 4,
                ClaimedBy = claimedBy
            };
        }
    }
}
