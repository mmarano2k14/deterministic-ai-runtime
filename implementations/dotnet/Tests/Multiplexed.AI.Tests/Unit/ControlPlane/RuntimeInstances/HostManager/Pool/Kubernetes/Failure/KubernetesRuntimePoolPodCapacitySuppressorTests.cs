using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    public sealed class KubernetesRuntimePoolPodCapacitySuppressorTests
    {
        [Fact]
        public async Task Suppress_Should_Atomically_Write_All_HostMembers_Without_Local_Routes()
        {
            var registry = new InMemoryAiRuntimePoolCapacitySafetyRegistry();
            var suppressor =
                new AiKubernetesRuntimePoolPodCapacitySuppressor(
                    new FixedMembershipEnumerator(
                        CreateMembership(
                            "runtime-01",
                            "runtime-02",
                            "runtime-03")),
                    registry,
                    registry);

            var result =
                await suppressor.SuppressAsync(
                    new AiKubernetesRuntimePoolPodCapacitySuppressionRequest
                    {
                        FailureId = "failure-pod-01",
                        PoolId = "pool-01",
                        PodUid = "pod-uid-01"
                    });

            Assert.Equal(3, result.Suppressions.Count);
            Assert.All(
                result.Suppressions,
                suppression =>
                {
                    Assert.Equal(
                        AiRuntimePoolCapacitySuppressionScope.HostMembership,
                        suppression.Scope);
                    Assert.Null(suppression.RouteId);
                    Assert.Equal("pod-uid-01", suppression.HostId);
                    Assert.Equal("failure-pod-01", suppression.FailureId);
                });
            Assert.Single(
                result.Suppressions
                    .Select(item => item.SuppressedAtUtc)
                    .Distinct());
        }

        [Fact]
        public async Task Suppress_Should_Be_Idempotent_For_Same_Failure()
        {
            var registry = new InMemoryAiRuntimePoolCapacitySafetyRegistry();
            var membership = CreateMembership("runtime-01", "runtime-02");
            var suppressor =
                new AiKubernetesRuntimePoolPodCapacitySuppressor(
                    new FixedMembershipEnumerator(membership),
                    registry,
                    registry);
            var request =
                new AiKubernetesRuntimePoolPodCapacitySuppressionRequest
                {
                    FailureId = "failure-pod-01",
                    PoolId = "pool-01",
                    PodUid = "pod-uid-01"
                };

            var first = await suppressor.SuppressAsync(request);
            var second = await suppressor.SuppressAsync(request);

            Assert.NotSame(first, second);
            Assert.Equal(first.FailureId, second.FailureId);
            Assert.Equal(first.PoolId, second.PoolId);
            Assert.Equal(first.PodUid, second.PodUid);
            Assert.Equal(first.SuppressedAtUtc, second.SuppressedAtUtc);
            Assert.Equal(
                first.Suppressions.ToArray(),
                second.Suppressions.ToArray());
            Assert.Equal(2, (await registry.ListByHostIdAsync("pod-uid-01")).Count);
        }

        [Fact]
        public async Task Suppress_Should_Not_Touch_Another_Pod()
        {
            var registry = new InMemoryAiRuntimePoolCapacitySafetyRegistry();
            await registry.SuppressAsync(
                new AiRuntimePoolCapacitySuppression
                {
                    FailureId = "failure-other",
                    Scope =
                        AiRuntimePoolCapacitySuppressionScope
                            .RuntimeInstanceRoute,
                    PoolId = "pool-01",
                    HostId = "pod-uid-02",
                    RuntimeInstanceId = "safe-runtime",
                    RouteId = "safe-route",
                    SuppressedAtUtc = DateTimeOffset.UtcNow
                });

            var suppressor =
                new AiKubernetesRuntimePoolPodCapacitySuppressor(
                    new FixedMembershipEnumerator(
                        CreateMembership("runtime-01", "runtime-02")),
                    registry,
                    registry);

            await suppressor.SuppressAsync(
                new AiKubernetesRuntimePoolPodCapacitySuppressionRequest
                {
                    FailureId = "failure-pod-01",
                    PoolId = "pool-01",
                    PodUid = "pod-uid-01"
                });

            var safe =
                await registry.GetSuppressionAsync(
                    "pool-01",
                    "pod-uid-02",
                    "safe-runtime");

            Assert.NotNull(safe);
            Assert.Equal("safe-route", safe!.RouteId);
        }

        private static AiKubernetesRuntimePoolPodMembership CreateMembership(
            params string[] runtimeInstanceIds)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiKubernetesRuntimePoolPodMembership
            {
                PoolId = "pool-01",
                PodUid = "pod-uid-01",
                EnumeratedAtUtc = now,
                Members = runtimeInstanceIds
                    .Select(
                        runtimeInstanceId =>
                            new AiKubernetesRuntimePoolPodMember
                            {
                                PoolId = "pool-01",
                                PodUid = "pod-uid-01",
                                RuntimeInstanceId = runtimeInstanceId,
                                RuntimeId = runtimeInstanceId,
                                Status = AiRuntimeInstanceStatus.Ready,
                                CanAcceptRun = true,
                                RegisteredAtUtc = now.AddMinutes(-1),
                                LastHeartbeatAtUtc = now
                            })
                    .ToArray()
            };
        }

        private sealed class FixedMembershipEnumerator :
            IAiKubernetesRuntimePoolPodMembershipEnumerator
        {
            private readonly AiKubernetesRuntimePoolPodMembership membership;

            public FixedMembershipEnumerator(
                AiKubernetesRuntimePoolPodMembership membership)
            {
                this.membership = membership;
            }

            public Task<AiKubernetesRuntimePoolPodMembership> EnumerateAsync(
                string poolId,
                string podUid,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.membership);
            }
        }
    }
}
