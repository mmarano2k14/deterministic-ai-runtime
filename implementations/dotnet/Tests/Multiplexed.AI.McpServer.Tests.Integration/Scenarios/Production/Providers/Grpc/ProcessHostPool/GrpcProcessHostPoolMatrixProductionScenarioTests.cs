using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.ProcessHostPool
{
    /// <summary>
    /// Projects the deterministic adversarial Recursive Child DAG matrix through gRPC ProcessHostPool.
    /// </summary>
    [Collection(ProcessHostPoolProductionCollection.Name)]
    [Trait("Category", "GrpcProcessHostPoolMatrixProduction")]
    public sealed class GrpcProcessHostPoolMatrixProductionScenarioTests :
        ProcessHostPoolProductionScenarioTestsBase
    {
        public GrpcProcessHostPoolMatrixProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                ProcessHostPoolProductionScenarioProfile.CreateGrpc())
        {
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "baseline")]
        public Task Grpc_ProcessHostPool_Matrix_Baseline_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return this.ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.Baseline);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "crash-early")]
        public Task Grpc_ProcessHostPool_Matrix_CrashEarly_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return this.ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.CrashEarly);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "child-invocation-boundary")]
        public Task Grpc_ProcessHostPool_Matrix_ChildInvocationBoundary_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return this.ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.ChildInvocationBoundary);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "continuation-consume")]
        public Task Grpc_ProcessHostPool_Matrix_ContinuationConsume_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return this.ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.ContinuationConsume);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "depth2-runtime-failure")]
        public Task Grpc_ProcessHostPool_Matrix_Depth2RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return this.ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.Depth2RuntimeFailure);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "depth3-runtime-failure")]
        public Task Grpc_ProcessHostPool_Matrix_Depth3RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return this.ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.Depth3RuntimeFailure);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "seed-a")]
        public Task Grpc_ProcessHostPool_Matrix_SeedA_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return this.ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.SeedA);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "seed-b")]
        public Task Grpc_ProcessHostPool_Matrix_SeedB_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return this.ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.SeedB);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "seed-c")]
        public Task Grpc_ProcessHostPool_Matrix_SeedC_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return this.ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.SeedC);
        }

        private Task ExecuteMatrixRowAsync(
            ProductionChildDagAdversarialScheduleDefinition schedule)
        {
            return this.ExecuteFullFailureProductionScenarioAsync(
                maximumProcessHostCount: 3,
                runtimeCountPerHost: 3,
                submissionIterationCount: 2,
                executionCycleCount: 2,
                childDepth: 3,
                recoveryObservationMode: ProductionRecoveryObservationMode.EventDriven,
                adversarialSchedule: schedule);
        }
    }
}
