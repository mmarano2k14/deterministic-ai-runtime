using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Verifies deterministic Runtime Pool scenario identity creation.
    /// </summary>
    public sealed class RuntimePoolCrashRecoveryScenarioIdentityTests
    {
        /// <summary>
        /// Verifies that the same profile and control-plane identity produce the same pool identifier.
        /// </summary>
        [Fact]
        public void CreatePoolId_Should_Be_Deterministic()
        {
            var first =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    "mcp-grpc-kubernetes-pool",
                    "control-plane-a");

            var second =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    "mcp-grpc-kubernetes-pool",
                    "control-plane-a");

            Assert.Equal(first, second);
        }

        /// <summary>
        /// Verifies that different control-plane identities cannot share the same pool identifier.
        /// </summary>
        [Fact]
        public void CreatePoolId_Should_Isolate_ControlPlanes()
        {
            var first =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    "mcp-grpc-kubernetes-pool",
                    "control-plane-a");

            var second =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    "mcp-grpc-kubernetes-pool",
                    "control-plane-b");

            Assert.NotEqual(first, second);
        }

        /// <summary>
        /// Verifies that the generated identifier is bounded and Kubernetes-safe.
        /// </summary>
        [Fact]
        public void CreatePoolId_Should_Create_Bounded_Kubernetes_Safe_Identity()
        {
            var poolId =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    "MCP GRPC Kubernetes Pool With A Deliberately Very Long Prefix",
                    "control-plane-a");

            Assert.InRange(poolId.Length, 1, 63);
            Assert.Matches("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", poolId);
        }
    }
}
