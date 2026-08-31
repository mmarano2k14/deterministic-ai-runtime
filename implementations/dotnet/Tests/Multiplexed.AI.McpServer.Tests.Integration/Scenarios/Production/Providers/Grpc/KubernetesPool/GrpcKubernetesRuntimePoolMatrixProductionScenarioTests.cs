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

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "baseline")]
        public Task Grpc_KubernetesPool_Matrix_Baseline_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.Baseline);
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
        /// Kills the runtime that owns the accepted parent continuation only while the durable child relation is
        /// Completed/Scheduled, the child call-site has monotonic post-schedule progress, and that call-site is
        /// still non-terminal. Recovery must consume the same durable child result without a new child generation.
        /// </summary>
        /// <returns>A task that completes after the continuation-consume matrix row converges.</returns>
        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "continuation-consume")]
        public Task Grpc_KubernetesPool_Matrix_ContinuationConsume_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.ContinuationConsume);
        }

        /// <summary>
        /// Holds the exact Depth 2 child at its deterministic child checkpoint, destroys the Linux process
        /// incarnation that owns that intermediate recursive execution, and proves recovery preserves the same
        /// child ExecutionId while Depth 3 and every upward continuation still converge exactly.
        /// </summary>
        /// <returns>A task that completes after the depth2-runtime-failure matrix row converges.</returns>
        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "depth2-runtime-failure")]
        public Task Grpc_KubernetesPool_Matrix_Depth2RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.Depth2RuntimeFailure);
        }

        /// <summary>
        /// Holds the exact deepest Depth 3 child at its deterministic durable checkpoint, destroys the Linux
        /// process incarnation that owns that execution, and proves the same child ExecutionId resumes while both
        /// upward continuations and the root converge without duplicate logical child work.
        /// </summary>
        /// <returns>A task that completes after the depth3-runtime-failure matrix row converges.</returns>
        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "depth3-runtime-failure")]
        public Task Grpc_KubernetesPool_Matrix_Depth3RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.Depth3RuntimeFailure);
        }

        /// <summary>
        /// Executes deterministic interleaving seed A by reversing logical run submission invocation order in
        /// every admission segment while preserving the historical workload, parent runtime failure boundary,
        /// Pod failure, durable identities, and frozen proof contract.
        /// </summary>
        /// <returns>A task that completes after the seed-a matrix row converges.</returns>
        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "seed-a")]
        public Task Grpc_KubernetesPool_Matrix_SeedA_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.SeedA);
        }

        /// <summary>
        /// Executes deterministic interleaving seed B by alternating logical run submission from the low and
        /// high edges of every admission segment while preserving the historical workload, parent runtime
        /// failure boundary, Pod failure, durable identities, and frozen proof contract.
        /// </summary>
        /// <returns>A task that completes after the seed-b matrix row converges.</returns>
        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "seed-b")]
        public Task Grpc_KubernetesPool_Matrix_SeedB_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.SeedB);
        }

        /// <summary>
        /// Executes deterministic interleaving seed C by starting logical run submission at the center of every
        /// admission segment and expanding toward both edges while preserving the historical workload, parent
        /// runtime failure boundary, Pod failure, durable identities, and frozen proof contract.
        /// </summary>
        /// <returns>A task that completes after the seed-c matrix row converges.</returns>
        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "seed-c")]
        public Task Grpc_KubernetesPool_Matrix_SeedC_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.SeedC);
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
