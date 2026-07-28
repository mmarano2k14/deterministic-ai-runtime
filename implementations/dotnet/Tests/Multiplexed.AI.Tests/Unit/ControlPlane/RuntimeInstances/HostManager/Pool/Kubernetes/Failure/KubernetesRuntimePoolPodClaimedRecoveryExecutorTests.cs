using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    public sealed class KubernetesRuntimePoolPodClaimedRecoveryExecutorTests
    {
        [Fact]
        public async Task ExecuteAsync_Should_Reuse_Shared_Candidate_Transition_Core_For_All_Pod_Members()
        {
            var claimed = CreateClaimedAssignedWork();
            var claimStore =
                new ActiveMembershipClaimStore(
                    claimed.Claim,
                    claimed.Lease!.LeaseId);
            var candidateExecutor =
                new RecordingCandidateTransitionExecutor();
            var executor =
                new AiKubernetesRuntimePoolPodClaimedRecoveryExecutor(
                    claimStore,
                    candidateExecutor);

            var result = await executor.ExecuteAsync(claimed);

            Assert.Equal(3, result.MemberCount);
            Assert.Equal(2, result.CandidateCount);
            Assert.Equal(2, result.AcceptedCount);
            Assert.Equal(2, result.ChangedCount);
            Assert.Equal(0, result.RejectedCount);
            Assert.Equal(1, candidateExecutor.CallCount);
            Assert.Equal(2, candidateExecutor.AuthorizedCandidateCount);
            Assert.True(claimStore.ActiveLeaseCheckCount >= 3);
            Assert.Equal(
                new[] { "runtime-a", "runtime-b" },
                candidateExecutor.Candidates
                    .Select(item => item.RuntimeInstanceId)
                    .ToArray());
        }

        [Fact]
        public async Task ExecuteAsync_Should_Reject_When_Membership_Lease_Is_Not_Active()
        {
            var claimed = CreateClaimedAssignedWork();
            var claimStore =
                new ActiveMembershipClaimStore(
                    claimed.Claim,
                    claimed.Lease!.LeaseId)
                {
                    IsActive = false
                };
            var candidateExecutor =
                new RecordingCandidateTransitionExecutor();
            var executor =
                new AiKubernetesRuntimePoolPodClaimedRecoveryExecutor(
                    claimStore,
                    candidateExecutor);

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolRecoveryExecutionAuthorityException>(
                    () => executor.ExecuteAsync(claimed));

            Assert.Equal(
                AiRuntimePoolRecoveryExecutionAuthorityFailure
                    .ClaimNotActive,
                exception.Reason);
            Assert.Equal(0, candidateExecutor.CallCount);
        }

        private static AiKubernetesRuntimePoolPodClaimedAssignedWork
            CreateClaimedAssignedWork()
        {
            var candidates =
                new[]
                {
                    CreateCandidate(
                        "runtime-a",
                        "local-a",
                        "execution-a",
                        AiRuntimePoolAssignedWorkKind.InFlight),
                    CreateCandidate(
                        "runtime-b",
                        "local-b",
                        null,
                        AiRuntimePoolAssignedWorkKind.LocalQueued)
                };
            var inventory =
                new AiKubernetesRuntimePoolPodAssignedWorkInventory
                {
                    FailureId = "failure-pod-01",
                    PoolId = "pool-01",
                    PodUid = "pod-uid-01",
                    EnumeratedAtUtc = DateTimeOffset.UtcNow,
                    RuntimeInventories =
                        new[]
                        {
                            CreateRuntimeInventory("runtime-a", candidates[0]),
                            CreateRuntimeInventory("runtime-b", candidates[1]),
                            CreateRuntimeInventory("runtime-c")
                        },
                    Candidates = candidates
                };
            var request =
                new AiRuntimePoolRecoveryMembershipClaimRequest
                {
                    FailureId = inventory.FailureId,
                    PoolId = inventory.PoolId,
                    HostId = inventory.PodUid,
                    MembershipFingerprint =
                        AiKubernetesRuntimePoolPodRecoveryInventoryFingerprint
                            .CalculateMembership(inventory),
                    MemberCount = inventory.RuntimeInventories.Count,
                    InventoryFingerprint =
                        AiKubernetesRuntimePoolPodRecoveryInventoryFingerprint
                            .CalculateInventory(inventory),
                    CandidateCount = inventory.Candidates.Count,
                    ClaimedBy = "reconciler-01"
                };
            var claim =
                new AiRuntimePoolRecoveryMembershipClaim
                {
                    ClaimId =
                        AiRuntimePoolRecoveryMembershipClaimIdentityFactory
                            .CreateClaimId(request),
                    FailureId = request.FailureId,
                    PoolId = request.PoolId,
                    HostId = request.HostId,
                    MembershipFingerprint = request.MembershipFingerprint,
                    MemberCount = request.MemberCount,
                    InventoryFingerprint = request.InventoryFingerprint,
                    CandidateCount = request.CandidateCount,
                    ClaimedBy = request.ClaimedBy,
                    ClaimedAtUtc = DateTimeOffset.UtcNow
                };

            return new AiKubernetesRuntimePoolPodClaimedAssignedWork
            {
                Inventory = inventory,
                Status = AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                Claim = claim,
                Lease = new FakeMembershipClaimLease(claim, "lease-01")
            };
        }

        private static AiRuntimePoolAssignedWorkCandidate CreateCandidate(
            string runtimeInstanceId,
            string localRunId,
            string? executionId,
            AiRuntimePoolAssignedWorkKind kind)
        {
            return new AiRuntimePoolAssignedWorkCandidate
            {
                FailureId = "failure-pod-01",
                PoolId = "pool-01",
                HostId = "pod-uid-01",
                RuntimeInstanceId = runtimeInstanceId,
                RouteId = null,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                Status = kind == AiRuntimePoolAssignedWorkKind.InFlight
                    ? "running"
                    : "queued",
                TenantId = "tenant-01",
                TenantGroupId = "tenant-group-01",
                SharedRunId = string.Concat("shared-", localRunId),
                Kind = kind,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>()
            };
        }

        private static AiRuntimePoolAssignedWorkInventory
            CreateRuntimeInventory(
                string runtimeInstanceId,
                params AiRuntimePoolAssignedWorkCandidate[] candidates)
        {
            return new AiRuntimePoolAssignedWorkInventory
            {
                FailureId = "failure-pod-01",
                PoolId = "pool-01",
                HostId = "pod-uid-01",
                RuntimeInstanceId = runtimeInstanceId,
                RouteId = null,
                EnumeratedAtUtc = DateTimeOffset.UtcNow,
                Candidates = candidates
            };
        }

        private sealed class ActiveMembershipClaimStore :
            IAiRuntimePoolRecoveryMembershipClaimStore
        {
            private readonly AiRuntimePoolRecoveryMembershipClaim claim;
            private readonly string leaseId;

            public ActiveMembershipClaimStore(
                AiRuntimePoolRecoveryMembershipClaim claim,
                string leaseId)
            {
                this.claim = claim;
                this.leaseId = leaseId;
            }

            public bool IsActive { get; set; } = true;

            public int ActiveLeaseCheckCount { get; private set; }

            public Task<AiRuntimePoolRecoveryMembershipClaimAcquisition>
                TryAcquireMembershipAsync(
                    AiRuntimePoolRecoveryMembershipClaimRequest request,
                    CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<AiRuntimePoolRecoveryMembershipClaim?>
                GetMembershipByFailureIdAsync(
                    string failureId,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimePoolRecoveryMembershipClaim?>(
                    this.claim);
            }

            public Task<bool> IsActiveMembershipLeaseAsync(
                string failureId,
                string claimId,
                string leaseId,
                CancellationToken cancellationToken = default)
            {
                this.ActiveLeaseCheckCount++;
                return Task.FromResult(
                    this.IsActive &&
                    StringComparer.Ordinal.Equals(
                        failureId,
                        this.claim.FailureId) &&
                    StringComparer.Ordinal.Equals(
                        claimId,
                        this.claim.ClaimId) &&
                    StringComparer.Ordinal.Equals(
                        leaseId,
                        this.leaseId));
            }
        }

        private sealed class RecordingCandidateTransitionExecutor :
            IAiRuntimePoolRecoveryCandidateTransitionExecutor
        {
            public int CallCount { get; private set; }

            public int AuthorizedCandidateCount { get; private set; }

            public IReadOnlyList<AiRuntimePoolAssignedWorkCandidate>
                Candidates { get; private set; } =
                Array.Empty<AiRuntimePoolAssignedWorkCandidate>();

            public async Task<
                IReadOnlyList<AiRuntimePoolRecoveryCandidateOutcome>>
                ExecuteAsync(
                    string failureId,
                    IReadOnlyList<AiRuntimePoolAssignedWorkCandidate>
                        candidates,
                    Func<AiRuntimePoolAssignedWorkCandidate, bool>
                        isAuthorized,
                    Func<CancellationToken, Task> ensureActiveLeaseAsync,
                    CancellationToken cancellationToken = default)
            {
                this.CallCount++;
                this.Candidates = candidates.ToArray();
                var outcomes =
                    new List<AiRuntimePoolRecoveryCandidateOutcome>();

                foreach (var candidate in candidates)
                {
                    if (isAuthorized(candidate))
                    {
                        this.AuthorizedCandidateCount++;
                    }

                    await ensureActiveLeaseAsync(cancellationToken);

                    outcomes.Add(
                        new AiRuntimePoolRecoveryCandidateOutcome
                        {
                            Candidate = candidate,
                            Transition =
                                new AiRuntimeExecutionRecoveryTransitionResult
                                {
                                    Accepted = true,
                                    Changed = true,
                                    SharedRunId = candidate.SharedRunId,
                                    RuntimeInstanceId =
                                        candidate.RuntimeInstanceId,
                                    LocalRunId = candidate.LocalRunId,
                                    ExecutionId = candidate.ExecutionId,
                                    Action = "recovered",
                                    Reason = "test"
                                },
                            CompletedAtUtc = DateTimeOffset.UtcNow
                        });
                }

                return outcomes;
            }
        }

        private sealed class FakeMembershipClaimLease :
            IAiRuntimePoolRecoveryMembershipClaimLease
        {
            public FakeMembershipClaimLease(
                AiRuntimePoolRecoveryMembershipClaim claim,
                string leaseId)
            {
                this.Claim = claim;
                this.LeaseId = leaseId;
            }

            public AiRuntimePoolRecoveryMembershipClaim Claim { get; }

            public string LeaseId { get; }

            public bool IsReleased { get; private set; }

            public ValueTask DisposeAsync()
            {
                this.IsReleased = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
