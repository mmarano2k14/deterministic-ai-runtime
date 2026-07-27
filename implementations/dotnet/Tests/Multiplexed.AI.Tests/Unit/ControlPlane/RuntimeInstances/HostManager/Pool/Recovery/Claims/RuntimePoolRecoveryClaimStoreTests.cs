using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Validates atomic deterministic recovery-claim storage.
    /// </summary>
    public sealed class RuntimePoolRecoveryClaimStoreTests
    {
        /// <summary>
        /// Verifies that concurrent coordinators receive one lease only.
        /// </summary>
        [Fact]
        public async Task TryAcquireAsync_Should_Grant_Exactly_One_Lease_Under_Concurrency()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var acquisitions =
                await Task.WhenAll(
                    Enumerable
                        .Range(1, 20)
                        .Select(
                            index =>
                                store.TryAcquireAsync(
                                    CreateRequest(
                                        claimedBy:
                                            string.Concat(
                                                "coordinator-",
                                                index)))));

            var acquired =
                Assert.Single(
                    acquisitions.Where(
                        result =>
                            result.Status ==
                            AiRuntimePoolRecoveryClaimAcquisitionStatus
                                .Acquired));

            Assert.NotNull(acquired.Lease);

            Assert.Equal(
                19,
                acquisitions.Count(
                    result =>
                        result.Status ==
                        AiRuntimePoolRecoveryClaimAcquisitionStatus
                            .AlreadyClaimed));

            Assert.All(
                acquisitions,
                result =>
                    Assert.Equal(
                        acquired.Claim.ClaimId,
                        result.Claim.ClaimId));

            Assert.All(
                acquisitions.Where(
                    result =>
                        result.Status ==
                        AiRuntimePoolRecoveryClaimAcquisitionStatus
                            .AlreadyClaimed),
                result =>
                    Assert.Null(result.Lease));

            await acquired.Lease!.DisposeAsync();
        }

        /// <summary>
        /// Verifies that another inventory cannot reuse the same failure authority.
        /// </summary>
        [Fact]
        public async Task TryAcquireAsync_Should_Reject_Inventory_Rebinding()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var acquired =
                await store.TryAcquireAsync(
                    CreateRequest(
                        claimedBy: "coordinator-01"));

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolRecoveryClaimConflictException>(
                    () =>
                        store.TryAcquireAsync(
                            CreateRequest(
                                claimedBy: "coordinator-02")
                            with
                            {
                                InventoryFingerprint =
                                    "different-fingerprint"
                            }));

            Assert.Equal(
                "failure-a1",
                exception.FailureId);

            await acquired.Lease!.DisposeAsync();
        }

        /// <summary>
        /// Verifies that lease disposal releases the exact claim and allows deterministic retry.
        /// </summary>
        [Fact]
        public async Task Lease_Disposal_Should_Allow_A_New_Acquisition()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var first =
                await store.TryAcquireAsync(
                    CreateRequest(
                        claimedBy: "coordinator-01"));

            var firstClaimId =
                first.Claim.ClaimId;

            await first.Lease!.DisposeAsync();
            await first.Lease.DisposeAsync();

            Assert.Null(
                await store.GetByFailureIdAsync(
                    "failure-a1"));

            var second =
                await store.TryAcquireAsync(
                    CreateRequest(
                        claimedBy: "coordinator-02"));

            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                second.Status);

            Assert.Equal(
                firstClaimId,
                second.Claim.ClaimId);

            Assert.Equal(
                "coordinator-02",
                second.Claim.ClaimedBy);

            await second.Lease!.DisposeAsync();
        }

        /// <summary>
        /// Verifies that a released lease generation cannot authorize a later acquisition with the
        /// same deterministic ClaimId.
        /// </summary>
        [Fact]
        public async Task Released_Lease_Should_Not_Be_Active_After_Reacquisition()
        {
            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var first =
                await store.TryAcquireAsync(
                    CreateRequest(
                        claimedBy: "coordinator-01"));

            var firstLeaseId =
                first.Lease!.LeaseId;

            Assert.True(
                await store.IsActiveLeaseAsync(
                    first.Claim.FailureId,
                    first.Claim.ClaimId,
                    firstLeaseId));

            await first.Lease.DisposeAsync();

            var second =
                await store.TryAcquireAsync(
                    CreateRequest(
                        claimedBy: "coordinator-02"));

            Assert.Equal(
                first.Claim.ClaimId,
                second.Claim.ClaimId);

            Assert.NotEqual(
                firstLeaseId,
                second.Lease!.LeaseId);

            Assert.False(
                await store.IsActiveLeaseAsync(
                    first.Claim.FailureId,
                    first.Claim.ClaimId,
                    firstLeaseId));

            Assert.True(
                await store.IsActiveLeaseAsync(
                    second.Claim.FailureId,
                    second.Claim.ClaimId,
                    second.Lease.LeaseId));

            await second.Lease.DisposeAsync();
        }

        /// <summary>
        /// Creates one exact deterministic claim request.
        /// </summary>
        internal static AiRuntimePoolRecoveryClaimRequest
            CreateRequest(
                string claimedBy)
        {
            return new AiRuntimePoolRecoveryClaimRequest
            {
                FailureId = "failure-a1",
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId = "runtime-a1",
                RouteId = "route-a1",
                InventoryFingerprint =
                    "inventory-fingerprint-a1",
                CandidateCount = 3,
                ClaimedBy = claimedBy
            };
        }
    }
}
