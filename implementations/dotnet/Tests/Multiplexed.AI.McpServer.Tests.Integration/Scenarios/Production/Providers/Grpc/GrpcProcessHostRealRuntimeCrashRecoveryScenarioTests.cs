using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc
{
    /// <summary>
    /// Proves real gRPC process-host runtime crash recovery without synthetic DAG reseeding.
    /// </summary>
    public sealed class GrpcProcessHostRealRuntimeCrashRecoveryScenarioTests
        : ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrpcProcessHostRealRuntimeCrashRecoveryScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcProcessHostRealRuntimeCrashRecoveryScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcProcessHostScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Verifies that a real runtime process crash is detected and the in-flight DAG execution resumes on a replacement runtime through gRPC.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public Task Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill()
        {
            return ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill();
        }
    }
}