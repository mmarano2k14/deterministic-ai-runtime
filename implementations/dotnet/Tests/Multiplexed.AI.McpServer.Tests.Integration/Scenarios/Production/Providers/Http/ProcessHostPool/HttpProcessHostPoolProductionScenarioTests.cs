using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.ProcessHostPool
{
    /// <summary>
    /// Exposes the shared multi-host ProcessPool production proof through HTTP.
    /// </summary>
    [Collection(ProcessHostPoolProductionCollection.Name)]
    public sealed class HttpProcessHostPoolProductionScenarioTests :
        ProcessHostPoolProductionScenarioTestsBase
    {
        /// <summary>
        /// Initializes the HTTP Process Host Pool proof.
        /// </summary>
        public HttpProcessHostPoolProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                ProcessHostPoolProductionScenarioProfile.CreateHttp())
        {
        }

        [Theory]
        [InlineData(3, 5, 5)]
        public Task Http_ProcessHostPool_Should_Measure_Machine_Limit_With_Bounded_Capacity(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount)
        {
            return this.ExecuteBoundedCapacityMachineLimitScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount);
        }

        [Theory]
        [InlineData(3, 5, 5)]
        public Task Http_ProcessHostPool_Should_Recover_After_Forced_Parent_Host_Crash(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount)
        {
            return this.ExecuteForcedParentHostFailureScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount);
        }

        [Theory]
        [InlineData(3, 5, 5, 2)]
        public Task Http_ProcessHostPool_Should_Reuse_Warm_Capacity_Across_Sequential_Production_Recovery_Cycles(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount)
        {
            return this.ExecuteWarmReuseScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount);
        }
    }
}
