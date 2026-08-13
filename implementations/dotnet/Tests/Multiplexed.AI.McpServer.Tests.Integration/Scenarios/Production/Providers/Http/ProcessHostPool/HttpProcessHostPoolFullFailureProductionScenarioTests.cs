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

        /// <summary>
        /// Keeps the same hierarchical proof but waits for an operator to externally kill the exact distinct busy parent Process Host.
        /// Before starting the test, keep this PowerShell watcher open for both cycles:
        /// <code>Get-Content "$env:TEMP\multiplexed-ai-manual-processhost-kill.txt" -Wait</code>
        /// </summary>
        [Theory]
        [Trait("Category", "ManualExternalFailure")]
        [InlineData(3, 5, 5, 2)]
        public Task Http_ProcessHostPool_Should_Recover_Child_Runtime_Then_Wait_For_External_Distinct_Parent_Kill_And_Reuse_Warm_Capacity(
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
