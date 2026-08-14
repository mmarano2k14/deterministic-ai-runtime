using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    public sealed class KubernetesRuntimePoolPodFailureRecoveryCoordinatorTests
    {
        [Fact]
        public async Task RecoverAsync_Should_Sequence_Forensics_Suppression_Claim_Replacement_And_Recovery()
        {
            var sequence = new List<string>();
            var claimed =
                CreateClaimedAssignedWork(
                    AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired);
            var failureObserver = new RecordingFailureObserver(sequence);
            var suppressor = new RecordingCapacitySuppressor(sequence);
            var claimCoordinator =
                new RecordingClaimCoordinator(sequence, claimed);
            var replacementCoordinator =
                new RecordingReplacementCoordinator(sequence);
            var recoveryExecutor =
                new RecordingRecoveryExecutor(sequence);
            var coordinator =
                new AiKubernetesRuntimePoolPodFailureRecoveryCoordinator(
                    failureObserver,
                    new FixedFailureReader(),
                    suppressor,
                    claimCoordinator,
                    replacementCoordinator,
                    recoveryExecutor);

            var result =
                await coordinator.RecoverAsync(CreateRequest());

            Assert.Equal(
                new[]
                {
                    "failure",
                    "suppression",
                    "claim",
                    "replacement",
                    "recovery"
                },
                sequence);
            Assert.Equal(
                AiRuntimePoolFailureScope.Host,
                result.Failure.Scope);
            Assert.Equal(
                AiRuntimePoolFailureKind.UnexpectedPodDeletion,
                result.Failure.Kind);
            Assert.Null(result.Failure.RuntimeInstanceId);
            Assert.Null(result.Failure.RouteId);
            Assert.Equal(3, result.Suppression.Suppressions.Count);
            Assert.All(
                result.Suppression.Suppressions,
                suppression =>
                {
                    Assert.Equal(
                        AiRuntimePoolCapacitySuppressionScope
                            .HostMembership,
                        suppression.Scope);
                    Assert.Null(suppression.RouteId);
                });
            Assert.NotNull(result.Replacement);
            Assert.NotNull(result.Recovery);
            Assert.True(claimed.Lease!.IsReleased);
        }

        [Fact]
        public async Task RecoverAsync_Should_Not_Create_Replacement_Or_Execute_Recovery_When_Claim_Is_Already_Owned()
        {
            var sequence = new List<string>();
            var claimed =
                CreateClaimedAssignedWork(
                    AiRuntimePoolRecoveryClaimAcquisitionStatus
                        .AlreadyClaimed);
            var replacementCoordinator =
                new RecordingReplacementCoordinator(sequence);
            var recoveryExecutor =
                new RecordingRecoveryExecutor(sequence);
            var coordinator =
                new AiKubernetesRuntimePoolPodFailureRecoveryCoordinator(
                    new RecordingFailureObserver(sequence),
                    new FixedFailureReader(),
                    new RecordingCapacitySuppressor(sequence),
                    new RecordingClaimCoordinator(sequence, claimed),
                    replacementCoordinator,
                    recoveryExecutor);

            var result =
                await coordinator.RecoverAsync(CreateRequest());

            Assert.Equal(
                new[] { "failure", "suppression", "claim" },
                sequence);
            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus
                    .AlreadyClaimed,
                result.Status);
            Assert.Null(result.Replacement);
            Assert.Null(result.Recovery);
            Assert.Equal(0, replacementCoordinator.CallCount);
            Assert.Equal(0, recoveryExecutor.CallCount);
        }

        [Fact]
        public async Task RecoverAsync_Should_Reuse_Existing_Exact_Failure_Fact_On_Retry()
        {
            var sequence = new List<string>();
            var existingFailure =
                new AiRuntimePoolFailureObservation
                {
                    FailureId = "failure-pod-01",
                    Scope = AiRuntimePoolFailureScope.Host,
                    PoolId = "pool-01",
                    HostId = "pod-uid-01",
                    RuntimeInstanceId = null,
                    RouteId = null,
                    Kind =
                        AiRuntimePoolFailureKind.UnexpectedPodDeletion,
                    ObservedAtUtc =
                        new DateTimeOffset(
                            2026,
                            7,
                            28,
                            0,
                            0,
                            0,
                            TimeSpan.Zero),
                    FailureMessage = "first observation"
                };
            var failureObserver =
                new RecordingFailureObserver(sequence);
            var claimed =
                CreateClaimedAssignedWork(
                    AiRuntimePoolRecoveryClaimAcquisitionStatus
                        .AlreadyClaimed);
            var coordinator =
                new AiKubernetesRuntimePoolPodFailureRecoveryCoordinator(
                    failureObserver,
                    new FixedFailureReader(existingFailure),
                    new RecordingCapacitySuppressor(sequence),
                    new RecordingClaimCoordinator(sequence, claimed),
                    new RecordingReplacementCoordinator(sequence),
                    new RecordingRecoveryExecutor(sequence));

            var result =
                await coordinator.RecoverAsync(CreateRequest());

            Assert.Same(existingFailure, result.Failure);
            Assert.Equal(0, failureObserver.CallCount);
            Assert.Equal(
                new[] { "suppression", "claim" },
                sequence);
        }

        private static AiKubernetesRuntimePoolPodFailureRecoveryRequest
            CreateRequest()
        {
            return new AiKubernetesRuntimePoolPodFailureRecoveryRequest
            {
                FailureId = "failure-pod-01",
                PoolId = "pool-01",
                PodUid = "pod-uid-01",
                ClaimedBy = "reconciler-01",
                FailureMessage = "pod deleted by failure proof",
                HostStartTemplate =
                    new AiRuntimeHostStartRequest
                    {
                        RequestId = "host-template-01",
                        ControlPlaneId = "control-plane-01",
                        ExecutionContextSnapshot =
                            new ExecutionContextSnapshot
                            {
                                ContextKey = "context-01",
                                Project = "pod-failure-tests",
                                UserId = "system",
                                TenantId = "tenant-01",
                                TenantGroupId = "tenant-group-01",
                                CurrentNamespace = "tests",
                                Namespaces = new List<NamespaceEntry>()
                            },
                        HostCreationMode =
                            AiRuntimeHostCreationMode.KubernetesPool,
                        PoolId = "pool-01",
                        HostId = "pod-uid-01",
                        RuntimeInstanceId = "runtime-a",
                        RuntimeInstanceIdPrefix = "runtime-pool",
                        ProviderName = "http",
                        TransportName = "http",
                        WorkerCountPerInstance = 2,
                        MaxConcurrentRunsPerInstance = 2,
                        LocalQueueCapacity = 16,
                        Metadata = new Dictionary<string, string>()
                    }
            };
        }

        private static AiKubernetesRuntimePoolPodClaimedAssignedWork
            CreateClaimedAssignedWork(
                AiRuntimePoolRecoveryClaimAcquisitionStatus status)
        {
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
                            CreateRuntimeInventory("runtime-a"),
                            CreateRuntimeInventory("runtime-b"),
                            CreateRuntimeInventory("runtime-c")
                        },
                    Candidates =
                        Array.Empty<AiRuntimePoolAssignedWorkCandidate>()
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
                Status = status,
                Claim = claim,
                Lease = status ==
                    AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired
                    ? new FakeMembershipClaimLease(claim, "lease-01")
                    : null
            };
        }

        private static AiRuntimePoolAssignedWorkInventory
            CreateRuntimeInventory(string runtimeInstanceId)
        {
            return new AiRuntimePoolAssignedWorkInventory
            {
                FailureId = "failure-pod-01",
                PoolId = "pool-01",
                HostId = "pod-uid-01",
                RuntimeInstanceId = runtimeInstanceId,
                RouteId = null,
                EnumeratedAtUtc = DateTimeOffset.UtcNow,
                Candidates =
                    Array.Empty<AiRuntimePoolAssignedWorkCandidate>()
            };
        }

        private sealed class RecordingFailureObserver :
            IAiRuntimePoolFailureObserver
        {
            private readonly IList<string> sequence;

            public RecordingFailureObserver(IList<string> sequence)
            {
                this.sequence = sequence;
            }

            public int CallCount { get; private set; }

            public Task<AiRuntimePoolFailureObservation> RecordAsync(
                AiRuntimePoolFailureObservation observation,
                CancellationToken cancellationToken = default)
            {
                this.CallCount++;
                this.sequence.Add("failure");
                return Task.FromResult(observation);
            }
        }

        private sealed class FixedFailureReader :
            IAiRuntimePoolFailureReader
        {
            private readonly AiRuntimePoolFailureObservation? failure;

            public FixedFailureReader(
                AiRuntimePoolFailureObservation? failure = null)
            {
                this.failure = failure;
            }

            public Task<AiRuntimePoolFailureObservation?>
                GetByFailureIdAsync(
                    string failureId,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.failure);
            }

            public Task<IReadOnlyList<AiRuntimePoolFailureObservation>>
                ListByHostIdAsync(
                    string hostId,
                    CancellationToken cancellationToken = default)
            {
                IReadOnlyList<AiRuntimePoolFailureObservation> values =
                    this.failure is null
                        ? Array.Empty<AiRuntimePoolFailureObservation>()
                        : new[] { this.failure };

                return Task.FromResult(values);
            }

            public Task<IReadOnlyList<AiRuntimePoolFailureObservation>>
                ListByRuntimeInstanceIdAsync(
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult<
                    IReadOnlyList<AiRuntimePoolFailureObservation>>(
                    Array.Empty<AiRuntimePoolFailureObservation>());
            }
        }

        private sealed class RecordingCapacitySuppressor :
            IAiKubernetesRuntimePoolPodCapacitySuppressor
        {
            private readonly IList<string> sequence;

            public RecordingCapacitySuppressor(IList<string> sequence)
            {
                this.sequence = sequence;
            }

            public Task<AiKubernetesRuntimePoolPodCapacitySuppression>
                SuppressAsync(
                    AiKubernetesRuntimePoolPodCapacitySuppressionRequest request,
                    CancellationToken cancellationToken = default)
            {
                this.sequence.Add("suppression");
                var now = DateTimeOffset.UtcNow;
                return Task.FromResult(
                    new AiKubernetesRuntimePoolPodCapacitySuppression
                    {
                        FailureId = request.FailureId,
                        PoolId = request.PoolId,
                        PodUid = request.PodUid,
                        MembershipEnumeratedAtUtc = now,
                        SuppressedAtUtc = now,
                        Suppressions =
                            new[]
                            {
                                CreateSuppression("runtime-a", now),
                                CreateSuppression("runtime-b", now),
                                CreateSuppression("runtime-c", now)
                            }
                    });
            }

            private static AiRuntimePoolCapacitySuppression
                CreateSuppression(
                    string runtimeInstanceId,
                    DateTimeOffset suppressedAtUtc)
            {
                return new AiRuntimePoolCapacitySuppression
                {
                    FailureId = "failure-pod-01",
                    PoolId = "pool-01",
                    HostId = "pod-uid-01",
                    Scope =
                        AiRuntimePoolCapacitySuppressionScope
                            .HostMembership,
                    RuntimeInstanceId = runtimeInstanceId,
                    RouteId = null,
                    SuppressedAtUtc = suppressedAtUtc
                };
            }
        }

        private sealed class RecordingClaimCoordinator :
            IAiKubernetesRuntimePoolPodRecoveryClaimCoordinator
        {
            private readonly IList<string> sequence;
            private readonly AiKubernetesRuntimePoolPodClaimedAssignedWork
                result;

            public RecordingClaimCoordinator(
                IList<string> sequence,
                AiKubernetesRuntimePoolPodClaimedAssignedWork result)
            {
                this.sequence = sequence;
                this.result = result;
            }

            public Task<AiKubernetesRuntimePoolPodClaimedAssignedWork>
                TryAcquireAsync(
                    AiKubernetesRuntimePoolPodAssignedWorkRequest request,
                    string claimedBy,
                    CancellationToken cancellationToken = default)
            {
                this.sequence.Add("claim");
                return Task.FromResult(this.result);
            }
        }

        private sealed class RecordingReplacementCoordinator :
            IAiKubernetesRuntimePoolPodReplacementCoordinator
        {
            private readonly IList<string> sequence;

            public RecordingReplacementCoordinator(IList<string> sequence)
            {
                this.sequence = sequence;
            }

            public int CallCount { get; private set; }

            public Task<AiKubernetesRuntimePoolPodReplacement>
                CreateReplacementAsync(
                    AiKubernetesRuntimePoolPodReplacementRequest request,
                    CancellationToken cancellationToken = default)
            {
                this.CallCount++;
                this.sequence.Add("replacement");
                var now = DateTimeOffset.UtcNow;
                var primaryRuntimeInstanceId = "replacement-runtime-a";
                return Task.FromResult(
                    new AiKubernetesRuntimePoolPodReplacement
                    {
                        FailureId = request.ClaimedAssignedWork.Claim.FailureId,
                        PoolId = request.ClaimedAssignedWork.Claim.PoolId,
                        FailedPodUid =
                            request.ClaimedAssignedWork.Inventory.PodUid,
                        ReplacementPodUid = "pod-uid-02",
                        ReplacementRequestId = "replacement-request-01",
                        PrimaryRuntimeInstanceId =
                            primaryRuntimeInstanceId,
                        RecoveryLeaseId =
                            request.ClaimedAssignedWork.Lease!.LeaseId,
                        HostStartResult =
                            AiRuntimeHostStartResult.Started(
                                request.HostStartTemplate
                                    .ExecutionContextSnapshot,
                                primaryRuntimeInstanceId,
                                "http",
                                "http",
                                "http://replacement-service/",
                                new Dictionary<string, string>
                                {
                                    [AiRuntimeHostMetadataKeys.HostId] =
                                        "pod-uid-02",
                                    ["runtime.pool.id"] = "pool-01"
                                }),
                        Membership =
                            new AiKubernetesRuntimePoolPodMembership
                            {
                                PoolId = "pool-01",
                                PodUid = "pod-uid-02",
                                EnumeratedAtUtc = now,
                                Members =
                                    new[]
                                    {
                                        CreateMember(
                                            primaryRuntimeInstanceId,
                                            now),
                                        CreateMember(
                                            "replacement-runtime-b",
                                            now),
                                        CreateMember(
                                            "replacement-runtime-c",
                                            now)
                                    }
                            },
                        ReadyAtUtc = now
                    });
            }

            private static AiKubernetesRuntimePoolPodMember CreateMember(
                string runtimeInstanceId,
                DateTimeOffset now)
            {
                return new AiKubernetesRuntimePoolPodMember
                {
                    PoolId = "pool-01",
                    PodUid = "pod-uid-02",
                    RuntimeInstanceId = runtimeInstanceId,
                    RuntimeId = runtimeInstanceId,
                    Status = AiRuntimeInstanceStatus.Ready,
                    CanAcceptRun = true,
                    RegisteredAtUtc = now,
                    LastHeartbeatAtUtc = now
                };
            }
        }

        private sealed class RecordingRecoveryExecutor :
            IAiKubernetesRuntimePoolPodClaimedRecoveryExecutor
        {
            private readonly IList<string> sequence;

            public RecordingRecoveryExecutor(IList<string> sequence)
            {
                this.sequence = sequence;
            }

            public int CallCount { get; private set; }

            public Task<
                AiKubernetesRuntimePoolPodClaimedRecoveryExecutionResult>
                ExecuteAsync(
                    AiKubernetesRuntimePoolPodClaimedAssignedWork claimedWork,
                    CancellationToken cancellationToken = default)
            {
                this.CallCount++;
                this.sequence.Add("recovery");
                return Task.FromResult(
                    new AiKubernetesRuntimePoolPodClaimedRecoveryExecutionResult
                    {
                        ClaimId = claimedWork.Claim.ClaimId,
                        FailureId = claimedWork.Claim.FailureId,
                        PoolId = claimedWork.Claim.PoolId,
                        PodUid = claimedWork.Claim.HostId,
                        MemberCount = claimedWork.Claim.MemberCount,
                        CandidateCount = 0,
                        AcceptedCount = 0,
                        ChangedCount = 0,
                        RejectedCount = 0,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    });
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
