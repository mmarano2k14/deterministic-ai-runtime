using System;
using System.Linq;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Validates the opt-in Kubernetes Runtime Pool topology contract.
    /// </summary>
    public sealed class RuntimeKubernetesPoolFoundationTests
    {
        /// <summary>
        /// Verifies that the new mode is additive and does not renumber existing host modes.
        /// </summary>
        [Fact]
        public void HostCreationMode_Should_Add_KubernetesPool_Without_Renumbering_ExistingModes()
        {
            Assert.Equal(0, (int)AiRuntimeHostCreationMode.Fixture);
            Assert.Equal(1, (int)AiRuntimeHostCreationMode.Process);
            Assert.Equal(2, (int)AiRuntimeHostCreationMode.Kubernetes);
            Assert.Equal(3, (int)AiRuntimeHostCreationMode.Attach);
            Assert.Equal(4, (int)AiRuntimeHostCreationMode.KubernetesPool);
        }

        /// <summary>
        /// Verifies safe disabled fixed-size defaults.
        /// </summary>
        [Fact]
        public void Options_Should_Default_To_Disabled_FixedSizePool()
        {
            var options = new AiKubernetesRuntimePoolOptions();

            Assert.False(options.Enabled);
            Assert.Equal(3, options.InitialRuntimeInstanceCount);
            Assert.Equal(3, options.MinimumRuntimeInstanceCount);
            Assert.Equal(3, options.MaximumRuntimeInstanceCount);
            Assert.Equal(1, options.StartupParallelism);
            Assert.Equal(8080, options.StableTransportPort);
            Assert.Equal(8081, options.ReadinessPort);
            Assert.Equal(18080, options.FirstChildTransportPort);
        }

        /// <summary>
        /// Verifies that disabled options do not require a partially configured pool identity.
        /// </summary>
        [Fact]
        public void Validate_Should_Allow_Disabled_UnconfiguredPool()
        {
            AiKubernetesRuntimePoolOptionsValidator.Validate(
                new AiKubernetesRuntimePoolOptions());
        }

        /// <summary>
        /// Verifies that an enabled pool requires a logical pool identity.
        /// </summary>
        [Fact]
        public void Validate_Should_Reject_EnabledPool_Without_PoolId()
        {
            var options = CreateValidOptions();
            options.PoolId = " ";

            var exception =
                Assert.Throws<ArgumentException>(
                    () => AiKubernetesRuntimePoolOptionsValidator.Validate(options));

            Assert.Contains(
                "PoolId",
                exception.Message,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Verifies invalid runtime count boundaries are rejected.
        /// </summary>
        /// <param name="initialCount">The initial runtime count.</param>
        /// <param name="minimumCount">The minimum runtime count.</param>
        /// <param name="maximumCount">The maximum runtime count.</param>
        [Theory]
        [InlineData(1, 2, 3)]
        [InlineData(4, 2, 3)]
        [InlineData(0, 0, 0)]
        public void Validate_Should_Reject_Invalid_RuntimeCountBoundaries(
            int initialCount,
            int minimumCount,
            int maximumCount)
        {
            var options = CreateValidOptions();
            options.InitialRuntimeInstanceCount = initialCount;
            options.MinimumRuntimeInstanceCount = minimumCount;
            options.MaximumRuntimeInstanceCount = maximumCount;

            Assert.Throws<ArgumentException>(
                () => AiKubernetesRuntimePoolOptionsValidator.Validate(options));
        }

        /// <summary>
        /// Verifies that the stable pool port cannot overlap a planned child port.
        /// </summary>
        [Fact]
        public void Validate_Should_Reject_StablePort_Inside_ChildPortRange()
        {
            var options = CreateValidOptions();
            options.StableTransportPort = 18081;

            var exception =
                Assert.Throws<ArgumentException>(
                    () => AiKubernetesRuntimePoolOptionsValidator.Validate(options));

            Assert.Contains(
                "overlap",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies that readiness cannot share the clear-text stable transport endpoint.
        /// </summary>
        [Fact]
        public void Validate_Should_Reject_ReadinessPort_Equal_To_StablePort()
        {
            var options = CreateValidOptions();
            options.ReadinessPort = options.StableTransportPort;

            var exception =
                Assert.Throws<ArgumentException>(
                    () => AiKubernetesRuntimePoolOptionsValidator.Validate(options));

            Assert.Contains(
                "distinct",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies that readiness cannot overlap a planned child transport port.
        /// </summary>
        [Fact]
        public void Validate_Should_Reject_ReadinessPort_Inside_ChildPortRange()
        {
            var options = CreateValidOptions();
            options.ReadinessPort = 18082;

            var exception =
                Assert.Throws<ArgumentException>(
                    () => AiKubernetesRuntimePoolOptionsValidator.Validate(options));

            Assert.Contains(
                "overlap",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies that one Pod plan contains independently identifiable child runtimes.
        /// </summary>
        [Fact]
        public void Create_Should_Build_Independent_RuntimePlans_Inside_OnePod()
        {
            var options = CreateValidOptions();

            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001");

            Assert.Equal(options.PoolId, plan.PoolId);
            Assert.Equal("request-0001", plan.PodRequestId);
            Assert.Equal(options.Namespace, plan.Namespace);
            Assert.Equal(options.ProviderName, plan.ProviderName);
            Assert.Equal(options.TransportName, plan.TransportName);
            Assert.Equal(options.StableTransportPort, plan.StableTransportPort);
            Assert.Equal(options.ReadinessPort, plan.ReadinessPort);
            Assert.Equal(3, plan.RuntimeInstances.Count);
            Assert.Equal(
                3,
                plan.RuntimeInstances
                    .Select(item => item.RuntimeInstanceId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.Equal(
                new[] { 18080, 18081, 18082 },
                plan.RuntimeInstances
                    .Select(item => item.TransportPort)
                    .ToArray());
            Assert.All(
                plan.RuntimeInstances,
                item =>
                {
                    Assert.Equal(plan.PoolId, item.PoolId);
                    Assert.Equal(plan.PodRequestId, item.PodRequestId);
                    Assert.Equal(plan.ProviderName, item.ProviderName);
                    Assert.Equal(plan.TransportName, item.TransportName);
                });
        }

        /// <summary>
        /// Verifies that the Pod name is DNS-label safe and request-specific.
        /// </summary>
        [Fact]
        public void Create_Should_Build_DnsSafe_RequestSpecific_PodName()
        {
            var options = CreateValidOptions();
            options.PodNamePrefix = "Runtime_POOL";
            options.PoolId = "Tenant A / Shared";

            var first =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001");

            var second =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0002");

            Assert.NotEqual(first.PodName, second.PodName);
            Assert.True(first.PodName.Length <= 63);
            Assert.Matches(
                "^[a-z0-9]([-a-z0-9]*[a-z0-9])?$",
                first.PodName);
        }

        /// <summary>
        /// Verifies that a new Pod plan receives fresh runtime instance identities.
        /// </summary>
        [Fact]
        public void Create_Should_Not_Reuse_RuntimeInstanceIds_Across_PodRequests()
        {
            var options = CreateValidOptions();

            var first =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001");

            var second =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0002");

            Assert.Empty(
                first.RuntimeInstances
                    .Select(item => item.RuntimeInstanceId)
                    .Intersect(
                        second.RuntimeInstances.Select(item => item.RuntimeInstanceId),
                        StringComparer.Ordinal));
        }

        /// <summary>
        /// Verifies that HostId becomes authoritative only after Kubernetes returns the Pod UID.
        /// </summary>
        [Fact]
        public void CreateHostIdentity_Should_Map_Exact_PodUid_To_HostId()
        {
            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    CreateValidOptions(),
                    "request-0001");

            var identity =
                AiKubernetesRuntimePoolHostIdentityFactory.Create(
                    plan,
                    "pod-uid-123");

            Assert.Equal(plan.PoolId, identity.PoolId);
            Assert.Equal("pod-uid-123", identity.HostId);
            Assert.Equal(plan.PodName, identity.PodName);
            Assert.Equal(plan.Namespace, identity.Namespace);
            Assert.Equal(plan.PodRequestId, identity.PodRequestId);
        }

        /// <summary>
        /// Verifies that a Pod plan cannot be created while the mode remains disabled.
        /// </summary>
        [Fact]
        public void Create_Should_Reject_Disabled_KubernetesPool()
        {
            var options = CreateValidOptions();
            options.Enabled = false;

            Assert.Throws<InvalidOperationException>(
                () => AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "request-0001"));
        }

        /// <summary>
        /// Creates valid enabled options used by the topology tests.
        /// </summary>
        private static AiKubernetesRuntimePoolOptions CreateValidOptions()
        {
            return new AiKubernetesRuntimePoolOptions
            {
                Enabled = true,
                PoolId = "pool-shared-01",
                Namespace = "runtime-tests",
                PodNamePrefix = "runtime-pool",
                RuntimeInstanceIdPrefix = "runtime-pool",
                ProviderName = "http",
                TransportName = "http",
                InitialRuntimeInstanceCount = 3,
                MinimumRuntimeInstanceCount = 3,
                MaximumRuntimeInstanceCount = 3,
                StartupParallelism = 1,
                StableTransportPort = 8080,
                ReadinessPort = 8081,
                FirstChildTransportPort = 18080,
                ChildTransportPortStride = 1,
                ShutdownTimeoutSeconds = 30
            };
        }
    }
}
