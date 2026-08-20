using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.ProcessHostPool
{
    /// <summary>
    /// Exposes the shared multi-host ProcessPool production proof through gRPC.
    /// </summary>
    [Collection(ProcessHostPoolProductionCollection.Name)]
    public sealed class GrpcProcessHostPoolProductionScenarioTests :
        ProcessHostPoolProductionScenarioTestsBase
    {
        /// <summary>
        /// Initializes the gRPC Process Host Pool proof.
        /// </summary>
        public GrpcProcessHostPoolProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                ProcessHostPoolProductionScenarioProfile.CreateGrpc())
        {
        }

        [Theory]
        [InlineData(3, 5, 5)]
        public Task Grpc_ProcessHostPool_Should_Measure_Machine_Limit_With_Bounded_Capacity(
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
        [InlineData(4, 6, 50)]
        public Task Grpc_ProcessHostPool_Should_Recover_After_Forced_Parent_Host_Crash(
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
        [InlineData(5, 5, 2, 2)]
        public Task Grpc_ProcessHostPool_Should_Reuse_Warm_Capacity_Across_Sequential_Production_Recovery_Cycles(
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
