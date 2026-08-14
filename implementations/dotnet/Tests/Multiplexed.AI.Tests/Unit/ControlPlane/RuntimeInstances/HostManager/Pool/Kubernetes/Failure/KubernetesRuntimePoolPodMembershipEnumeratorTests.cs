using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    public sealed class KubernetesRuntimePoolPodMembershipEnumeratorTests
    {
        [Fact]
        public async Task Enumerate_Should_Use_SharedRegistry_Membership_And_Keep_Unhealthy_Members()
        {
            var reader =
                new FakeMembershipReader(
                    CreateSnapshot("runtime-03", AiRuntimeInstanceStatus.Unhealthy, false),
                    CreateSnapshot("runtime-01", AiRuntimeInstanceStatus.Ready, true),
                    CreateSnapshot("runtime-02", AiRuntimeInstanceStatus.Draining, false));

            var enumerator =
                new AiKubernetesRuntimePoolPodMembershipEnumerator(reader);

            var membership =
                await enumerator.EnumerateAsync("pool-01", "pod-uid-01");

            Assert.Equal("pool-01", membership.PoolId);
            Assert.Equal("pod-uid-01", membership.PodUid);
            Assert.Equal(
                new[] { "runtime-01", "runtime-02", "runtime-03" },
                membership.Members
                    .Select(member => member.RuntimeInstanceId)
                    .ToArray());
            Assert.Equal(
                AiRuntimeInstanceStatus.Draining,
                membership.Members[1].Status);
            Assert.False(membership.Members[1].CanAcceptRun);
            Assert.Equal(1, reader.ListByHostCallCount);
        }

        [Fact]
        public async Task Enumerate_Should_Reject_CrossPool_Member()
        {
            var snapshot = CreateSnapshot(
                "runtime-01",
                AiRuntimeInstanceStatus.Ready,
                true,
                poolId: "pool-other");

            var enumerator =
                new AiKubernetesRuntimePoolPodMembershipEnumerator(
                    new FakeMembershipReader(snapshot));

            var exception =
                await Assert.ThrowsAsync<
                    AiKubernetesRuntimePoolPodMembershipAuthorityException>(
                    () => enumerator.EnumerateAsync(
                        "pool-01",
                        "pod-uid-01"));

            Assert.Equal(
                AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                    .PoolBoundaryViolation,
                exception.Reason);
        }

        [Fact]
        public async Task Enumerate_Should_Reject_Duplicate_RuntimeIdentity()
        {
            var enumerator =
                new AiKubernetesRuntimePoolPodMembershipEnumerator(
                    new FakeMembershipReader(
                        CreateSnapshot(
                            "runtime-01",
                            AiRuntimeInstanceStatus.Ready,
                            true),
                        CreateSnapshot(
                            "runtime-01",
                            AiRuntimeInstanceStatus.Draining,
                            false)));

            var exception =
                await Assert.ThrowsAsync<
                    AiKubernetesRuntimePoolPodMembershipAuthorityException>(
                    () => enumerator.EnumerateAsync(
                        "pool-01",
                        "pod-uid-01"));

            Assert.Equal(
                AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                    .DuplicateRuntimeInstanceId,
                exception.Reason);
        }

        [Fact]
        public async Task Enumerate_Should_Fail_When_SharedRegistry_Has_No_Membership()
        {
            var enumerator =
                new AiKubernetesRuntimePoolPodMembershipEnumerator(
                    new FakeMembershipReader());

            var exception =
                await Assert.ThrowsAsync<
                    AiKubernetesRuntimePoolPodMembershipAuthorityException>(
                    () => enumerator.EnumerateAsync(
                        "pool-01",
                        "pod-uid-01"));

            Assert.Equal(
                AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                    .MembershipNotFound,
                exception.Reason);
        }

        private static AiRuntimeInstanceSnapshot CreateSnapshot(
            string runtimeInstanceId,
            AiRuntimeInstanceStatus status,
            bool canAcceptRun,
            string poolId = "pool-01")
        {
            var now = DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                PoolId = poolId,
                HostId = "pod-uid-01",
                RuntimeId = runtimeInstanceId.Replace("instance", "logical"),
                Status = status,
                CanAcceptRun = canAcceptRun,
                RegisteredAtUtc = now.AddMinutes(-1),
                LastHeartbeatAtUtc = now
            };
        }

        private sealed class FakeMembershipReader :
            IAiRuntimePoolMembershipReader
        {
            private readonly IReadOnlyList<AiRuntimeInstanceSnapshot> snapshots;

            public FakeMembershipReader(
                params AiRuntimeInstanceSnapshot[] snapshots)
            {
                this.snapshots = snapshots;
            }

            public int ListByHostCallCount { get; private set; }

            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>>
                ListByPoolIdAsync(
                    string poolId,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(
                    this.snapshots
                        .Where(item => item.PoolId == poolId)
                        .ToArray());
            }

            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>>
                ListByHostIdAsync(
                    string hostId,
                    CancellationToken cancellationToken = default)
            {
                this.ListByHostCallCount++;
                return Task.FromResult(this.snapshots);
            }

            public Task<IReadOnlyList<string>> ListHostIdsByPoolIdAsync(
                string poolId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<string>>(
                    this.snapshots
                        .Where(item => item.PoolId == poolId)
                        .Select(item => item.HostId!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            }
        }
    }
}
