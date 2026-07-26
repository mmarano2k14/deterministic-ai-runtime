using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates the first process-host Runtime Pool Manager foundation.
    /// </summary>
    public sealed class RuntimeProcessPoolFoundationTests
    {
        /// <summary>
        /// Verifies that pool and host identities are carried as typed host-start fields.
        /// </summary>
        [Fact]
        public void HostStartRequest_Should_Carry_FirstClass_PoolAndHostIdentity()
        {
            var request = new AiRuntimeHostStartRequest
            {
                ExecutionContextSnapshot = null!,
                PoolId = "pool-shared-01",
                HostId = "runtime-pool-host-incarnation-01",
                RuntimeInstanceId = "runtime-a1",
                Metadata = new Dictionary<string, string>
                {
                    ["pool.id"] = "diagnostic-pool",
                    ["host.id"] = "diagnostic-host"
                }
            };

            Assert.Equal("pool-shared-01", request.PoolId);
            Assert.Equal("runtime-pool-host-incarnation-01", request.HostId);
            Assert.NotEqual(request.PoolId, request.Metadata["pool.id"]);
            Assert.NotEqual(request.HostId, request.Metadata["host.id"]);
        }

        /// <summary>
        /// Verifies the safe fixed-size defaults used by the first process pool milestone.
        /// </summary>
        [Fact]
        public void Options_Should_Default_To_Disabled_FixedSizePool()
        {
            var options = new AiRuntimeProcessPoolOptions();

            Assert.False(options.Enabled);
            Assert.Equal(3, options.InitialProcessCount);
            Assert.Equal(3, options.MinimumProcessCount);
            Assert.Equal(3, options.MaximumProcessCount);
            Assert.Equal(1, options.StartupParallelism);
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
                    () => AiRuntimeProcessPoolOptionsValidator.Validate(options));

            Assert.Contains("PoolId", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Verifies that invalid process count boundaries are rejected.
        /// </summary>
        /// <param name="initialProcessCount">The configured initial process count.</param>
        /// <param name="minimumProcessCount">The configured minimum process count.</param>
        /// <param name="maximumProcessCount">The configured maximum process count.</param>
        [Theory]
        [InlineData(1, 2, 3)]
        [InlineData(4, 2, 3)]
        [InlineData(0, 0, 0)]
        public void Validate_Should_Reject_Invalid_ProcessCountBoundaries(
            int initialProcessCount,
            int minimumProcessCount,
            int maximumProcessCount)
        {
            var options = CreateValidOptions();
            options.InitialProcessCount = initialProcessCount;
            options.MinimumProcessCount = minimumProcessCount;
            options.MaximumProcessCount = maximumProcessCount;

            Assert.Throws<ArgumentException>(
                () => AiRuntimeProcessPoolOptionsValidator.Validate(options));
        }

        /// <summary>
        /// Verifies that each manager startup receives a distinct immutable host incarnation.
        /// </summary>
        [Fact]
        public void CreatePoolIdentity_Should_Generate_New_HostId_Per_Startup()
        {
            var options = CreateValidOptions();

            var first =
                AiRuntimeProcessPoolIdentityFactory.CreatePoolIdentity(options);

            var second =
                AiRuntimeProcessPoolIdentityFactory.CreatePoolIdentity(options);

            Assert.Equal(options.PoolId, first.PoolId);
            Assert.Equal(options.PoolId, second.PoolId);
            Assert.NotEqual(first.HostId, second.HostId);
            Assert.StartsWith(
                options.HostIdPrefix + "-",
                first.HostId,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Verifies that sibling runtime processes share pool and host identity but keep distinct
        /// runtime instance identities.
        /// </summary>
        [Fact]
        public void CreateRuntimeInstanceId_Should_Create_Independent_SiblingIdentities()
        {
            var identity =
                AiRuntimeProcessPoolIdentityFactory.CreatePoolIdentity(
                    CreateValidOptions());

            var runtimeA1 =
                AiRuntimeProcessPoolIdentityFactory.CreateRuntimeInstanceId(
                    identity,
                    ordinal: 1);

            var runtimeA2 =
                AiRuntimeProcessPoolIdentityFactory.CreateRuntimeInstanceId(
                    identity,
                    ordinal: 2);

            Assert.NotEqual(runtimeA1, runtimeA2);
            Assert.StartsWith(
                identity.RuntimeInstanceIdPrefix + "-1-",
                runtimeA1,
                StringComparison.Ordinal);
            Assert.StartsWith(
                identity.RuntimeInstanceIdPrefix + "-2-",
                runtimeA2,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Creates valid enabled options used by process pool foundation tests.
        /// </summary>
        /// <returns>The valid process pool options.</returns>
        private static AiRuntimeProcessPoolOptions CreateValidOptions()
        {
            return new AiRuntimeProcessPoolOptions
            {
                Enabled = true,
                PoolId = "pool-shared-01",
                HostIdPrefix = "runtime-pool-host",
                RuntimeInstanceIdPrefix = "runtime-pool",
                InitialProcessCount = 3,
                MinimumProcessCount = 3,
                MaximumProcessCount = 3,
                StartupParallelism = 1,
                ShutdownTimeoutSeconds = 30
            };
        }
    }
}
