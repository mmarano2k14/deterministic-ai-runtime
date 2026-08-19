using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Process
{
    /// <summary>
    /// Contains production gRPC process-host proofs for deterministic nested child DAG composition and recovery.
    /// </summary>
    public sealed class GrpcProcessHostChildDagProductionScenarioTests
    {
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrpcProcessHostChildDagProductionScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcProcessHostChildDagProductionScenarioTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Verifies one nested child DAG level through the gRPC process-host transport using the same production
        /// scenario contract as the already-proven HTTP process-host path.
        /// </summary>
        [Fact]
        public async Task Grpc_ProcessHost_Should_Run_SingleTenant_Shared_Runtime_Mode_With_Child_Dag_Depth_One()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthOneScenario();
            var runner = new GrpcProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
            ProductionChildDagAssertions.AssertNestedComposition(scenario, result);
        }

        /// <summary>
        /// Verifies two nested child DAG levels and cascading durable continuation convergence through the gRPC
        /// process-host transport using the same production scenario contract as HTTP.
        /// </summary>
        [Fact]
        public async Task Grpc_ProcessHost_Should_Run_SingleTenant_Shared_Runtime_Mode_With_Child_Dag_Depth_Two()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthTwoScenario();
            var runner = new GrpcProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
            ProductionChildDagAssertions.AssertNestedComposition(scenario, result);
        }

        /// <summary>
        /// Verifies that the depth-one child execution survives a real process kill through gRPC, preserves the same
        /// ChildExecutionId, resumes on replacement runtime capacity, and converges its parent continuation.
        /// </summary>
        [Fact]
        public async Task Grpc_ProcessHost_Should_Recover_Child_Dag_Depth_One_After_Real_Child_Runtime_Process_Kill()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthOneRuntimeCrashRecoveryScenario();
            var runner = new GrpcProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
            ProductionChildDagAssertions.AssertNestedComposition(scenario, result);
            ProductionChildDagAssertions.AssertRuntimeFailureRecovery(scenario, result);
        }

        /// <summary>
        /// Verifies that a real process kill of the intermediate first-level child in a depth-two chain recovers
        /// through gRPC, composes the second-level child, and cascades both continuations back to the root parent.
        /// </summary>
        [Fact]
        public async Task Grpc_ProcessHost_Should_Recover_Intermediate_Child_Dag_Depth_Two_After_Real_Runtime_Process_Kill()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthTwoIntermediateRuntimeCrashRecoveryScenario();
            var runner = new GrpcProcessHostProductionScenarioRunner(this.output);
            var result = await runner.RunAsync(scenario).ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(scenario, result);
            ProductionChildDagAssertions.AssertNestedComposition(scenario, result);
            ProductionChildDagAssertions.AssertRuntimeFailureRecovery(scenario, result);
        }
    }
}
