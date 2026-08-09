using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.ProcessHostPool
{
    /// <summary>
    /// Exposes the final hierarchical ProcessHostPool failure and warm-reuse proof through HTTP.
    /// </summary>
    [Collection(ProcessHostPoolProductionCollection.Name)]
    public sealed class HttpProcessHostPoolFullFailureProductionScenarioTests :
        ProcessHostPoolProductionScenarioTestsBase
    {
        /// <summary>
        /// Initializes the final HTTP ProcessHostPool production proof.
        /// </summary>
        public HttpProcessHostPoolFullFailureProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                ProcessHostPoolProductionScenarioProfile.CreateHttp())
        {
        }

        /// <summary>
        /// Kills one exact child runtime after durable progress, then one distinct busy parent,
        /// reuses the converged warm pool across cycles, and cleans only after the final cycle.
        /// </summary>
        [Theory]
        [InlineData(3, 5, 5, 2)]
        public Task Http_ProcessHostPool_Should_Recover_Child_Runtime_Then_Distinct_Parent_And_Reuse_Warm_Capacity(
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
