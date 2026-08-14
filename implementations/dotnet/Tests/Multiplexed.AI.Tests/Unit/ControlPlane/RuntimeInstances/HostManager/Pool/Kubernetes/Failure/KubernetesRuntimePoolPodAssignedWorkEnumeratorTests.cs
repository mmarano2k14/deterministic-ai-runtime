using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    public sealed class KubernetesRuntimePoolPodAssignedWorkEnumeratorTests
    {
        [Fact]
        public async Task Enumerate_Should_Aggregate_All_HostMembers_Deterministically()
        {
            var suppressions = new[]
            {
                CreateSuppression("runtime-02"),
                CreateSuppression("runtime-01")
            };

            var enumerator =
                new AiKubernetesRuntimePoolPodAssignedWorkEnumerator(
                    new FixedSafetyReader(suppressions),
                    new FixedSuppressedEnumerator());

            var result =
                await enumerator.EnumerateAsync(
                    new AiKubernetesRuntimePoolPodAssignedWorkRequest
                    {
                        FailureId = "failure-pod-01",
                        PoolId = "pool-01",
                        PodUid = "pod-uid-01"
                    });

            Assert.Equal(
                new[] { "runtime-01", "runtime-02" },
                result.RuntimeInventories
                    .Select(item => item.RuntimeInstanceId)
                    .ToArray());
            Assert.All(
                result.RuntimeInventories,
                inventory => Assert.Null(inventory.RouteId));
            Assert.Equal(2, result.Candidates.Count);
        }

        [Fact]
        public async Task Enumerate_Should_Reject_RouteScoped_Suppression()
        {
            var routeScoped =
                CreateSuppression("runtime-01") with
                {
                    Scope =
                        AiRuntimePoolCapacitySuppressionScope
                            .RuntimeInstanceRoute,
                    RouteId = "route-01"
                };

            var enumerator =
                new AiKubernetesRuntimePoolPodAssignedWorkEnumerator(
                    new FixedSafetyReader(new[] { routeScoped }),
                    new FixedSuppressedEnumerator());

            await Assert.ThrowsAsync<
                AiKubernetesRuntimePoolPodAssignedWorkException>(
                () => enumerator.EnumerateAsync(
                    new AiKubernetesRuntimePoolPodAssignedWorkRequest
                    {
                        FailureId = "failure-pod-01",
                        PoolId = "pool-01",
                        PodUid = "pod-uid-01"
                    }));
        }

        private static AiRuntimePoolCapacitySuppression CreateSuppression(
            string runtimeInstanceId)
        {
            return new AiRuntimePoolCapacitySuppression
            {
                FailureId = "failure-pod-01",
                Scope =
                    AiRuntimePoolCapacitySuppressionScope.HostMembership,
                PoolId = "pool-01",
                HostId = "pod-uid-01",
                RuntimeInstanceId = runtimeInstanceId,
                RouteId = null,
                SuppressedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private sealed class FixedSafetyReader :
            IAiRuntimePoolCapacitySafetyReader
        {
            private readonly IReadOnlyList<AiRuntimePoolCapacitySuppression>
                suppressions;

            public FixedSafetyReader(
                IReadOnlyList<AiRuntimePoolCapacitySuppression> suppressions)
            {
                this.suppressions = suppressions;
            }

            public Task<AiRuntimePoolCapacitySuppression?> GetSuppressionAsync(
                string poolId,
                string hostId,
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    this.suppressions.SingleOrDefault(
                        item =>
                            item.PoolId == poolId &&
                            item.HostId == hostId &&
                            item.RuntimeInstanceId == runtimeInstanceId));
            }

            public Task<IReadOnlyList<AiRuntimePoolCapacitySuppression>>
                ListByHostIdAsync(
                    string hostId,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.suppressions);
            }
        }

        private sealed class FixedSuppressedEnumerator :
            IAiRuntimePoolSuppressedAssignedWorkEnumerator
        {
            public Task<AiRuntimePoolAssignedWorkInventory> EnumerateAsync(
                AiRuntimePoolCapacitySuppression suppression,
                CancellationToken cancellationToken = default)
            {
                var candidate =
                    new AiRuntimePoolAssignedWorkCandidate
                    {
                        FailureId = suppression.FailureId,
                        PoolId = suppression.PoolId,
                        HostId = suppression.HostId,
                        RuntimeInstanceId =
                            suppression.RuntimeInstanceId,
                        RouteId = suppression.RouteId,
                        LocalRunId =
                            string.Concat(
                                "run-",
                                suppression.RuntimeInstanceId),
                        ExecutionId =
                            string.Concat(
                                "execution-",
                                suppression.RuntimeInstanceId),
                        Status = "running",
                        TenantId = "tenant-01",
                        TenantGroupId = "group-01",
                        Kind = AiRuntimePoolAssignedWorkKind.InFlight,
                        CreatedAtUtc = DateTimeOffset.UtcNow
                    };

                return Task.FromResult(
                    new AiRuntimePoolAssignedWorkInventory
                    {
                        FailureId = suppression.FailureId,
                        PoolId = suppression.PoolId,
                        HostId = suppression.HostId,
                        RuntimeInstanceId =
                            suppression.RuntimeInstanceId,
                        RouteId = suppression.RouteId,
                        EnumeratedAtUtc = DateTimeOffset.UtcNow,
                        Candidates = new[] { candidate }
                    });
            }
        }
    }
}
