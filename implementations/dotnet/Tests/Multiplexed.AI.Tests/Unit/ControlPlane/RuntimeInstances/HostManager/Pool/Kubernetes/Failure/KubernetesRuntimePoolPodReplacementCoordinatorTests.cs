using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    public sealed class KubernetesRuntimePoolPodReplacementCoordinatorTests
    {
        [Fact]
        public async Task CreateReplacementAsync_Should_Use_Existing_Strategy_And_SharedRegistry_Membership()
        {
            var claimed = CreateClaimedAssignedWork();
            var strategy =
                new RecordingKubernetesPoolStrategy("pod-uid-02");
            var enumerator =
                new DelegateMembershipEnumerator(
                    (_, podUid, _) =>
                        Task.FromResult(
                            CreateReadyMembership(
                                podUid,
                                strategy.LastRequest!.RuntimeInstanceId)));
            var coordinator = CreateCoordinator(strategy, enumerator);

            var replacement =
                await coordinator.CreateReplacementAsync(
                    CreateReplacementRequest(claimed));

            var hostRequest = Assert.Single(strategy.Requests);
            Assert.Equal(
                AiRuntimeHostCreationMode.KubernetesPool,
                hostRequest.HostCreationMode);
            Assert.Equal("pool-01", hostRequest.PoolId);
            Assert.Null(hostRequest.HostId);
            Assert.Null(hostRequest.TransportEndpoint);
            Assert.Empty(hostRequest.Metadata);
            Assert.Equal(
                replacement.ReplacementRequestId,
                hostRequest.RequestId);
            Assert.Equal(
                replacement.PrimaryRuntimeInstanceId,
                hostRequest.RuntimeInstanceId);
            Assert.Equal("pod-uid-01", replacement.FailedPodUid);
            Assert.Equal("pod-uid-02", replacement.ReplacementPodUid);
            Assert.Equal(3, replacement.Membership.Members.Count);
            Assert.All(
                replacement.Membership.Members,
                member =>
                {
                    Assert.Equal("pool-01", member.PoolId);
                    Assert.Equal("pod-uid-02", member.PodUid);
                    Assert.Equal(
                        AiRuntimeInstanceStatus.Ready,
                        member.Status);
                    Assert.True(member.CanAcceptRun);
                    Assert.DoesNotContain(
                        member.RuntimeInstanceId,
                        new[]
                        {
                            "runtime-a",
                            "runtime-b",
                            "runtime-c"
                        });
                });
        }

        [Fact]
        public async Task CreateReplacementAsync_Should_Be_RetryStable_For_Same_Claim()
        {
            var claimed = CreateClaimedAssignedWork();
            var strategy =
                new RecordingKubernetesPoolStrategy("pod-uid-02");
            var enumerator =
                new DelegateMembershipEnumerator(
                    (_, podUid, _) =>
                        Task.FromResult(
                            CreateReadyMembership(
                                podUid,
                                strategy.LastRequest!.RuntimeInstanceId)));
            var coordinator = CreateCoordinator(strategy, enumerator);
            var request = CreateReplacementRequest(claimed);

            var first = await coordinator.CreateReplacementAsync(request);
            var second = await coordinator.CreateReplacementAsync(request);

            Assert.Equal(
                first.ReplacementRequestId,
                second.ReplacementRequestId);
            Assert.Equal(
                first.PrimaryRuntimeInstanceId,
                second.PrimaryRuntimeInstanceId);
            Assert.Equal(first.ReplacementPodUid, second.ReplacementPodUid);
            Assert.Equal(2, strategy.Requests.Count);
        }

        [Fact]
        public async Task CreateReplacementAsync_Should_Reuse_Logical_Replacement_Across_Lease_Reacquisition()
        {
            var firstClaimed =
                CreateClaimedAssignedWork("lease-generation-01");
            var secondClaimed =
                CreateClaimedAssignedWork("lease-generation-02");
            var strategy =
                new RecordingKubernetesPoolStrategy("pod-uid-02");
            var enumerator =
                new DelegateMembershipEnumerator(
                    (_, podUid, _) =>
                        Task.FromResult(
                            CreateReadyMembership(
                                podUid,
                                strategy.LastRequest!.RuntimeInstanceId)));
            var coordinator = CreateCoordinator(strategy, enumerator);

            var first =
                await coordinator.CreateReplacementAsync(
                    CreateReplacementRequest(firstClaimed));
            var second =
                await coordinator.CreateReplacementAsync(
                    CreateReplacementRequest(secondClaimed));

            Assert.NotEqual(
                firstClaimed.Lease!.LeaseId,
                secondClaimed.Lease!.LeaseId);
            Assert.Equal(
                first.ReplacementRequestId,
                second.ReplacementRequestId);
            Assert.Equal(
                first.PrimaryRuntimeInstanceId,
                second.PrimaryRuntimeInstanceId);
            Assert.Equal(
                first.ReplacementPodUid,
                second.ReplacementPodUid);
            Assert.Equal(2, strategy.Requests.Count);
        }

        [Fact]
        public async Task CreateReplacementAsync_Should_Reject_Failed_PodUid_Reuse()
        {
            var claimed = CreateClaimedAssignedWork();
            var strategy =
                new RecordingKubernetesPoolStrategy("pod-uid-01");
            var coordinator =
                CreateCoordinator(
                    strategy,
                    new DelegateMembershipEnumerator(
                        (_, _, _) =>
                            throw new InvalidOperationException(
                                "Membership must not be enumerated.")));

            var exception =
                await Assert.ThrowsAsync<
                    AiKubernetesRuntimePoolPodReplacementException>(
                    () => coordinator.CreateReplacementAsync(
                        CreateReplacementRequest(claimed)));

            Assert.Equal(
                AiKubernetesRuntimePoolPodReplacementFailure
                    .FailedPodUidReused,
                exception.Reason);
        }

        [Fact]
        public async Task CreateReplacementAsync_Should_Reject_Stale_Runtime_Identity()
        {
            var claimed = CreateClaimedAssignedWork();
            var strategy =
                new RecordingKubernetesPoolStrategy("pod-uid-02");
            var enumerator =
                new DelegateMembershipEnumerator(
                    (_, podUid, _) =>
                        Task.FromResult(
                            CreateReadyMembership(
                                podUid,
                                strategy.LastRequest!.RuntimeInstanceId,
                                secondRuntimeInstanceId: "runtime-b")));
            var coordinator = CreateCoordinator(strategy, enumerator);

            var exception =
                await Assert.ThrowsAsync<
                    AiKubernetesRuntimePoolPodReplacementException>(
                    () => coordinator.CreateReplacementAsync(
                        CreateReplacementRequest(claimed)));

            Assert.Equal(
                AiKubernetesRuntimePoolPodReplacementFailure
                    .StaleRuntimeIdentityReused,
                exception.Reason);
        }

        [Fact]
        public async Task CreateReplacementAsync_Should_Wait_Until_All_Registry_Members_Are_Ready_And_Selectable()
        {
            var claimed = CreateClaimedAssignedWork();
            var strategy =
                new RecordingKubernetesPoolStrategy("pod-uid-02");
            var callCount = 0;
            var enumerator =
                new DelegateMembershipEnumerator(
                    (_, podUid, _) =>
                    {
                        callCount++;
                        var complete =
                            CreateReadyMembership(
                                podUid,
                                strategy.LastRequest!.RuntimeInstanceId);

                        if (callCount == 1)
                        {
                            return Task.FromResult(
                                complete with
                                {
                                    Members =
                                        complete.Members
                                            .Select(
                                                (member, index) =>
                                                    index == 0
                                                        ? member with
                                                        {
                                                            Status =
                                                                AiRuntimeInstanceStatus
                                                                    .Draining,
                                                            CanAcceptRun = false
                                                        }
                                                        : member)
                                            .ToArray()
                                });
                        }

                        return Task.FromResult(complete);
                    });
            var coordinator =
                CreateCoordinator(
                    strategy,
                    enumerator,
                    TimeSpan.FromMilliseconds(1));

            var replacement =
                await coordinator.CreateReplacementAsync(
                    CreateReplacementRequest(claimed));

            Assert.True(callCount >= 2);
            Assert.All(
                replacement.Membership.Members,
                member =>
                {
                    Assert.Equal(
                        AiRuntimeInstanceStatus.Ready,
                        member.Status);
                    Assert.True(member.CanAcceptRun);
                });
        }

        private static AiKubernetesRuntimePoolPodReplacementCoordinator
            CreateCoordinator(
                IAiRuntimeHostCreationStrategy strategy,
                IAiKubernetesRuntimePoolPodMembershipEnumerator enumerator,
                TimeSpan? readinessPollInterval = null)
        {
            return new AiKubernetesRuntimePoolPodReplacementCoordinator(
                new[] { strategy },
                enumerator,
                Options.Create(
                    new AiKubernetesRuntimePoolOptions
                    {
                        Enabled = true,
                        PoolId = "pool-01",
                        RuntimeInstanceIdPrefix = "runtime-pool",
                        ProviderName = "http",
                        TransportName = "http",
                        InitialRuntimeInstanceCount = 3,
                        MinimumRuntimeInstanceCount = 3,
                        MaximumRuntimeInstanceCount = 3
                    }),
                Options.Create(
                    new AiKubernetesRuntimePoolHostOptions
                    {
                        StartupTimeout = TimeSpan.FromSeconds(1),
                        ReadinessPollInterval =
                            readinessPollInterval
                            ?? TimeSpan.FromMilliseconds(10)
                    }));
        }

        private static AiKubernetesRuntimePoolPodReplacementRequest
            CreateReplacementRequest(
                AiKubernetesRuntimePoolPodClaimedAssignedWork claimed)
        {
            return new AiKubernetesRuntimePoolPodReplacementRequest
            {
                ClaimedAssignedWork = claimed,
                HostStartTemplate =
                    new AiRuntimeHostStartRequest
                    {
                        RequestId = "original-request",
                        ControlPlaneId = "control-plane-01",
                        ExecutionContextSnapshot =
                            new ExecutionContextSnapshot
                            {
                                ContextKey = "context-01",
                                Project = "replacement-tests",
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
                        RuntimeInstanceIdPrefix = "old-prefix",
                        ProviderName = "http",
                        TransportName = "http",
                        TransportEndpoint = "http://failed-pod/",
                        WorkerCountPerInstance = 2,
                        MaxConcurrentRunsPerInstance = 2,
                        LocalQueueCapacity = 16,
                        Metadata =
                            new Dictionary<string, string>
                            {
                                ["kubernetes.pod.uid"] = "pod-uid-01"
                            }
                    }
            };
        }

        private static AiKubernetesRuntimePoolPodClaimedAssignedWork
            CreateClaimedAssignedWork(
                string leaseId = "lease-01")
        {
            var inventory = CreateInventory();
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
                Lease = new FakeMembershipClaimLease(claim, leaseId)
            };
        }

        private static AiKubernetesRuntimePoolPodAssignedWorkInventory
            CreateInventory()
        {
            return new AiKubernetesRuntimePoolPodAssignedWorkInventory
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

        private static AiKubernetesRuntimePoolPodMembership
            CreateReadyMembership(
                string podUid,
                string primaryRuntimeInstanceId,
                string secondRuntimeInstanceId = "runtime-replacement-b")
        {
            return new AiKubernetesRuntimePoolPodMembership
            {
                PoolId = "pool-01",
                PodUid = podUid,
                EnumeratedAtUtc = DateTimeOffset.UtcNow,
                Members =
                    new[]
                    {
                        CreateMember(podUid, primaryRuntimeInstanceId),
                        CreateMember(podUid, secondRuntimeInstanceId),
                        CreateMember(podUid, "runtime-replacement-c")
                    }
            };
        }

        private static AiKubernetesRuntimePoolPodMember CreateMember(
            string podUid,
            string runtimeInstanceId)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiKubernetesRuntimePoolPodMember
            {
                PoolId = "pool-01",
                PodUid = podUid,
                RuntimeInstanceId = runtimeInstanceId,
                RuntimeId = runtimeInstanceId,
                Status = AiRuntimeInstanceStatus.Ready,
                CanAcceptRun = true,
                RegisteredAtUtc = now.AddSeconds(-1),
                LastHeartbeatAtUtc = now
            };
        }

        private sealed class RecordingKubernetesPoolStrategy :
            IAiRuntimeHostCreationStrategy
        {
            private readonly string replacementPodUid;

            public RecordingKubernetesPoolStrategy(string replacementPodUid)
            {
                this.replacementPodUid = replacementPodUid;
            }

            public AiRuntimeHostCreationMode Mode =>
                AiRuntimeHostCreationMode.KubernetesPool;

            public IList<AiRuntimeHostStartRequest> Requests { get; } =
                new List<AiRuntimeHostStartRequest>();

            public AiRuntimeHostStartRequest? LastRequest =>
                this.Requests.LastOrDefault();

            public Task<AiRuntimeHostStartResult> StartAsync(
                AiRuntimeHostStartRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.Requests.Add(request);

                return Task.FromResult(
                    AiRuntimeHostStartResult.Started(
                        request.ExecutionContextSnapshot,
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        "http://replacement-service/",
                        new Dictionary<string, string>
                        {
                            [AiRuntimeHostMetadataKeys.HostId] =
                                this.replacementPodUid,
                            ["kubernetes.pod.uid"] =
                                this.replacementPodUid,
                            ["runtime.pool.id"] = request.PoolId!
                        }));
            }
        }

        private sealed class DelegateMembershipEnumerator :
            IAiKubernetesRuntimePoolPodMembershipEnumerator
        {
            private readonly Func<
                string,
                string,
                CancellationToken,
                Task<AiKubernetesRuntimePoolPodMembership>> handler;

            public DelegateMembershipEnumerator(
                Func<
                    string,
                    string,
                    CancellationToken,
                    Task<AiKubernetesRuntimePoolPodMembership>> handler)
            {
                this.handler = handler;
            }

            public Task<AiKubernetesRuntimePoolPodMembership>
                EnumerateAsync(
                    string poolId,
                    string podUid,
                    CancellationToken cancellationToken = default)
            {
                return this.handler(poolId, podUid, cancellationToken);
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
