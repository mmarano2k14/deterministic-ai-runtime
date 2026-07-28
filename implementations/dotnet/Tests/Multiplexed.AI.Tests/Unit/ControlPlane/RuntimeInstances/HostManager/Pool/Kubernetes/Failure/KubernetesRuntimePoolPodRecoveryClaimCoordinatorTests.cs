using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    public sealed class KubernetesRuntimePoolPodRecoveryClaimCoordinatorTests
    {
        [Fact]
        public async Task Concurrent_Coordinators_Should_Produce_One_Membership_Lease()
        {
            var inventory = CreateInventory();
            var store = new InMemoryAiRuntimePoolRecoveryClaimStore();
            var coordinator =
                new AiKubernetesRuntimePoolPodRecoveryClaimCoordinator(
                    new FixedAssignedWorkEnumerator(inventory),
                    store);
            var request =
                new AiKubernetesRuntimePoolPodAssignedWorkRequest
                {
                    FailureId = inventory.FailureId,
                    PoolId = inventory.PoolId,
                    PodUid = inventory.PodUid
                };

            var results =
                await Task.WhenAll(
                    coordinator.TryAcquireAsync(
                        request,
                        "coordinator-01"),
                    coordinator.TryAcquireAsync(
                        request,
                        "coordinator-02"));

            Assert.Single(
                results.Where(
                    item =>
                        item.Status ==
                            AiRuntimePoolRecoveryClaimAcquisitionStatus
                                .Acquired));
            Assert.Single(
                results.Where(
                    item =>
                        item.Status ==
                            AiRuntimePoolRecoveryClaimAcquisitionStatus
                                .AlreadyClaimed));
            Assert.Single(results.Where(item => item.Lease is not null));

            var acquired = results.Single(item => item.Lease is not null);
            await acquired.Lease!.DisposeAsync();
        }

        [Fact]
        public async Task Claim_Should_Ignore_Local_Route_Identity()
        {
            var inventory = CreateInventory();
            Assert.All(
                inventory.RuntimeInventories,
                item => Assert.Null(item.RouteId));

            var store = new InMemoryAiRuntimePoolRecoveryClaimStore();
            var coordinator =
                new AiKubernetesRuntimePoolPodRecoveryClaimCoordinator(
                    new FixedAssignedWorkEnumerator(inventory),
                    store);

            var result =
                await coordinator.TryAcquireAsync(
                    new AiKubernetesRuntimePoolPodAssignedWorkRequest
                    {
                        FailureId = inventory.FailureId,
                        PoolId = inventory.PoolId,
                        PodUid = inventory.PodUid
                    },
                    "coordinator-01");

            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                result.Status);
            Assert.Equal(2, result.Claim.MemberCount);
            Assert.Equal(2, result.Claim.CandidateCount);
            await result.Lease!.DisposeAsync();
        }

        private static AiKubernetesRuntimePoolPodAssignedWorkInventory
            CreateInventory()
        {
            var runtimeOne = CreateRuntimeInventory("runtime-01", 1);
            var runtimeTwo = CreateRuntimeInventory("runtime-02", 2);

            return new AiKubernetesRuntimePoolPodAssignedWorkInventory
            {
                FailureId = "failure-pod-01",
                PoolId = "pool-01",
                PodUid = "pod-uid-01",
                EnumeratedAtUtc = DateTimeOffset.UtcNow,
                RuntimeInventories = new[] { runtimeOne, runtimeTwo },
                Candidates = runtimeOne.Candidates
                    .Concat(runtimeTwo.Candidates)
                    .OrderBy(item => item.Kind)
                    .ThenBy(item => item.CreatedAtUtc)
                    .ThenBy(item => item.RuntimeInstanceId)
                    .ThenBy(item => item.LocalRunId)
                    .ToArray()
            };
        }

        private static AiRuntimePoolAssignedWorkInventory
            CreateRuntimeInventory(
                string runtimeInstanceId,
                int ordinal)
        {
            var candidate =
                new AiRuntimePoolAssignedWorkCandidate
                {
                    FailureId = "failure-pod-01",
                    PoolId = "pool-01",
                    HostId = "pod-uid-01",
                    RuntimeInstanceId = runtimeInstanceId,
                    RouteId = null,
                    LocalRunId = $"run-{ordinal}",
                    ExecutionId = $"execution-{ordinal}",
                    Status = "running",
                    TenantId = "tenant-01",
                    TenantGroupId = "group-01",
                    Kind = AiRuntimePoolAssignedWorkKind.InFlight,
                    CreatedAtUtc =
                        DateTimeOffset.UtcNow.AddSeconds(ordinal)
                };

            return new AiRuntimePoolAssignedWorkInventory
            {
                FailureId = candidate.FailureId,
                PoolId = candidate.PoolId,
                HostId = candidate.HostId,
                RuntimeInstanceId = candidate.RuntimeInstanceId,
                RouteId = null,
                EnumeratedAtUtc = DateTimeOffset.UtcNow,
                Candidates = new[] { candidate }
            };
        }

        private sealed class FixedAssignedWorkEnumerator :
            IAiKubernetesRuntimePoolPodAssignedWorkEnumerator
        {
            private readonly AiKubernetesRuntimePoolPodAssignedWorkInventory
                inventory;

            public FixedAssignedWorkEnumerator(
                AiKubernetesRuntimePoolPodAssignedWorkInventory inventory)
            {
                this.inventory = inventory;
            }

            public Task<AiKubernetesRuntimePoolPodAssignedWorkInventory>
                EnumerateAsync(
                    AiKubernetesRuntimePoolPodAssignedWorkRequest request,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.inventory);
            }
        }
    }
}
