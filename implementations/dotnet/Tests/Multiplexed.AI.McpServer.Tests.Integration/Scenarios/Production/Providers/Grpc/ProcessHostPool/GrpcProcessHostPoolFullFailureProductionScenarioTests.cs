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
        [InlineData(7, 5, 20, 2)]
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
    }
}
