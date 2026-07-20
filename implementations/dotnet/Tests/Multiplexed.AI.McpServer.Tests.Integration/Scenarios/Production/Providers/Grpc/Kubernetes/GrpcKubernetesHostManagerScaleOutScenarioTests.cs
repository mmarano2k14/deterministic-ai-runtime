using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Kubernetes
{
    /// <summary>
    /// Proves that gRPC runtime scale-out can delegate runtime lifecycle creation to Kubernetes
    /// while preserving gRPC as the runtime provider and command transport.
    /// </summary>
    public sealed class GrpcKubernetesHostManagerScaleOutScenarioTests
        : HostManagerScaleOutScenarioTestsBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrpcKubernetesHostManagerScaleOutScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesHostManagerScaleOutScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesHostScenarioRuntimeProfile())
        {
        }

       
    }
}