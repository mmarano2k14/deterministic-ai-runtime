using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.KubernetesPool
{
    /// <summary>
    /// Hosts deterministic adversarial Recursive Child DAG matrix rows for the gRPC Kubernetes Runtime Pool.
    /// Historical full-failure and canary scenarios remain in their existing test class.
    /// </summary>
    [Collection(GrpcKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "GrpcKubernetesRuntimePoolMatrixProduction")]
    public sealed class GrpcKubernetesRuntimePoolMatrixProductionScenarioTests :
        GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase
    {
        /// <summary>
        /// Initializes the gRPC Kubernetes Runtime Pool adversarial matrix.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcKubernetesRuntimePoolMatrixProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Kills the selected runtime after the first durable parent step and proves that recursive composition,
        /// recovery, ownership, replay, and durable dispatch remain exact.
        /// </summary>
        /// <returns>A task that completes after the crash-early matrix row converges.</returns>
        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "crash-early")]
        public Task Grpc_KubernetesPool_Matrix_CrashEarly_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.CrashEarly);
        }

        /// <summary>
        /// Kills the selected parent at the final ordinary root checkpoint, before ExecuteChildDag can become
        /// runnable, then proves that recovery creates exactly one durable child invocation generation and that
        /// the complete recursive DAG still converges with the frozen proof invariants.
        /// </summary>
        /// <returns>A task that completes after the child-invocation-boundary matrix row converges.</returns>
        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "child-invocation-boundary")]
        public Task Grpc_KubernetesPool_Matrix_ChildInvocationBoundary_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.ChildInvocationBoundary);
        }

        /// <summary>
        /// Executes one deterministic matrix row against the same reduced production topology used by the
        /// reference gRPC Kubernetes canary. Only the schedule coordinate varies between rows.
        /// </summary>
        /// <param name="schedule">The deterministic adversarial schedule.</param>
        /// <returns>A task that completes after the matrix row converges.</returns>
        private Task ExecuteMatrixRowAsync(
            ProductionChildDagAdversarialScheduleDefinition schedule)
        {
            ArgumentNullException.ThrowIfNull(schedule);

            return ExecuteFullFailureProductionScenarioAsync(
                maximumPodCount: 3,
                runtimeCountPerPod: 3,
                submissionIterationCount: 2,
                executionCycleCount: 2,
                childDepth: 3,
                recoveryObservationMode: ProductionRecoveryObservationMode.EventDriven,
                adversarialSchedule: schedule);
        }
    }
}
