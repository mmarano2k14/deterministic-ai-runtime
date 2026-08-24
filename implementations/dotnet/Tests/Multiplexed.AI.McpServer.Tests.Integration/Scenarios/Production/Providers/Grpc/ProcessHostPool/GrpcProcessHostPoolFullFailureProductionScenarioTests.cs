using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.ProcessHostPool
{
    /// <summary>
    /// Exposes the final hierarchical ProcessHostPool failure and warm-reuse proof through gRPC.
    /// </summary>
    [Collection(ProcessHostPoolProductionCollection.Name)]
    public sealed class GrpcProcessHostPoolFullFailureProductionScenarioTests :
        ProcessHostPoolProductionScenarioTestsBase
    {
        /// <summary>
        /// Initializes the final gRPC ProcessHostPool production proof.
        /// </summary>
        public GrpcProcessHostPoolFullFailureProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                ProcessHostPoolProductionScenarioProfile.CreateGrpc())
        {
        }

        /// <summary>
        /// Kills one exact child runtime after durable progress, then one distinct busy parent,
        /// reuses the converged warm pool across cycles, and cleans only after the final cycle.
        /// </summary>
        [Theory]
        [InlineData(5, 5, 5, 2)]
        public Task Grpc_ProcessHostPool_Should_Recover_Child_Runtime_Then_Distinct_Parent_And_Reuse_Warm_Capacity(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount)
        {
            return this.ExecuteFullFailureProductionScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount);
        }

        /// <summary>
        /// Certifies ChildDepth3 and canonical EventDriven recovery over the same bounded warm ProcessHostPool proof.
        /// </summary>
        [Theory]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "Canary")]
        [InlineData(5, 5, 5, 2, 3)]
        public Task Grpc_ProcessHostPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount,
            int childDepth)
        {
            return this.ExecuteFullFailureProductionScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount,
                childDepth,
                ProductionRecoveryObservationMode.EventDriven);
        }

        /// <summary>
        /// Keeps the same hierarchical proof but waits for an operator to externally kill the exact distinct busy parent Process Host.
        /// Before starting the test, keep this PowerShell watcher open for both cycles:
        /// <code>Get-Content "$env:TEMP\multiplexed-ai-manual-processhost-kill.txt" -Wait</code>
        /// </summary>
        [Theory]
        [Trait("Category", "ManualExternalFailure")]
        [InlineData(7, 5, 20, 2)]
        public Task Grpc_ProcessHostPool_Should_Recover_Child_Runtime_Then_Wait_For_External_Distinct_Parent_Kill_And_Reuse_Warm_Capacity(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount)
        {
            return this.ExecuteFullFailureProductionScenarioAwaitExternalParentFailureAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount);
        }
    }
}
