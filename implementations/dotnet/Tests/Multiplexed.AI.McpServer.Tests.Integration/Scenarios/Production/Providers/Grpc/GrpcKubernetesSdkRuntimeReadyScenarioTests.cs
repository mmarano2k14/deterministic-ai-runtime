using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc
{
    /// <summary>
    /// Validates real Kubernetes SDK host-manager scale-out with runtime readiness enabled.
    /// </summary>
    /// <remarks>
    /// This test proves the full lifecycle boundary:
    /// gRPC remains the runtime provider and command transport,
    /// Kubernetes creates the runtime host,
    /// the pod starts,
    /// the local runtime pool registers capacity,
    /// and the control plane only fulfills scale-out after runtime readiness succeeds.
    /// </remarks>
    public sealed class GrpcKubernetesSdkRuntimeReadyScenarioTests :
        HostManagerScaleOutScenarioTestsBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrpcKubernetesSdkRuntimeReadyScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesSdkRuntimeReadyScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesSdkRuntimeReadyScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Verifies that Kubernetes SDK host-manager scale-out waits for real runtime readiness
        /// without changing the gRPC runtime provider or command transport.
        /// </summary>
        /// <returns>The asynchronous test operation.</returns>
        [Fact]
        public Task Grpc_KubernetesSdkHostManager_Should_Fulfill_ScaleOut_After_Runtime_Readiness_Without_Changing_Grpc_Runtime_Transport()
        {
            return this.HostManager_Should_Fulfill_ScaleOut_Without_Changing_Runtime_Transport();
        }
    }
}