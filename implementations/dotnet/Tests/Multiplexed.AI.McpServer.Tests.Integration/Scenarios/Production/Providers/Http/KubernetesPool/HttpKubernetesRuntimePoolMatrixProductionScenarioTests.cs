using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.KubernetesPool
{
    /// <summary>
    /// Projects the complete deterministic adversarial Recursive Child DAG matrix through HTTP while reusing
    /// the same Kubernetes Runtime Pool harness, schedules, failure injectors, recovery path, and proof contract.
    /// </summary>
    [Collection(HttpKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "HttpKubernetesRuntimePoolMatrixProduction")]
    public sealed class HttpKubernetesRuntimePoolMatrixProductionScenarioTests :
        KubernetesRuntimePoolProductionScenarioTestsBase
    {
        /// <summary>
        /// Initializes the HTTP Kubernetes Runtime Pool adversarial matrix.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpKubernetesRuntimePoolMatrixProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new HttpKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(),
                static (maximumPodCount, runtimeCountPerPod) =>
                    new HttpKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(
                        maximumPodCount,
                        runtimeCountPerPod))
        {
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "baseline")]
        public Task Http_KubernetesPool_Matrix_Baseline_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.Baseline);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "crash-early")]
        public Task Http_KubernetesPool_Matrix_CrashEarly_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.CrashEarly);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "child-invocation-boundary")]
        public Task Http_KubernetesPool_Matrix_ChildInvocationBoundary_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.ChildInvocationBoundary);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "continuation-consume")]
        public Task Http_KubernetesPool_Matrix_ContinuationConsume_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.ContinuationConsume);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "depth2-runtime-failure")]
        public Task Http_KubernetesPool_Matrix_Depth2RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.Depth2RuntimeFailure);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "depth3-runtime-failure")]
        public Task Http_KubernetesPool_Matrix_Depth3RuntimeFailure_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.Depth3RuntimeFailure);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "seed-a")]
        public Task Http_KubernetesPool_Matrix_SeedA_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.SeedA);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "seed-b")]
        public Task Http_KubernetesPool_Matrix_SeedB_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.SeedB);
        }

        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "AdversarialMatrix")]
        [Trait("MatrixScenarioId", "seed-c")]
        public Task Http_KubernetesPool_Matrix_SeedC_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteMatrixRowAsync(
                ProductionChildDagAdversarialScheduleDefinition.SeedC);
        }

        /// <summary>
        /// Executes one deterministic matrix row against the same reduced topology as the reference matrix.
        /// The HTTP runtime profile is the only transport-specific input.
        /// </summary>
        /// <param name="schedule">The already-hardened deterministic adversarial schedule.</param>
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
