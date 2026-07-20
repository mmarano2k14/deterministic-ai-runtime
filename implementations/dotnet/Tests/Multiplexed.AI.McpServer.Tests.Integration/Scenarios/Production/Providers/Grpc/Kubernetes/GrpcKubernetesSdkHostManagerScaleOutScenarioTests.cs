using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Kubernetes
{
    /// <summary>
    /// Proves that gRPC runtime scale-out can delegate runtime lifecycle creation to the real Kubernetes SDK client
    /// while preserving gRPC as the runtime provider and command transport.
    /// </summary>
    public sealed class GrpcKubernetesSdkHostManagerScaleOutScenarioTests
        : HostManagerScaleOutScenarioTestsBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrpcKubernetesSdkHostManagerScaleOutScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesSdkHostManagerScaleOutScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesSdkHostScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Verifies that the Kubernetes SDK client is used as the runtime host lifecycle provider while gRPC remains the runtime transport.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Grpc_KubernetesSdkHostManager_Should_Fulfill_ScaleOut_Without_Changing_Grpc_Runtime_Transport()
        {
            return HostManager_Should_Fulfill_ScaleOut_Without_Changing_Runtime_Transport();
        }
    }
}