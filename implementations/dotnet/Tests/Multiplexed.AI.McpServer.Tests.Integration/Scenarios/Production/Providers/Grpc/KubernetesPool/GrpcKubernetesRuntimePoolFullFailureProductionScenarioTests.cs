using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.KubernetesPool
{
    /// <summary>
    /// Exposes the final hierarchical KubernetesPool failure and warm-reuse proof through gRPC.
    /// </summary>
    [Collection(GrpcKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "GrpcKubernetesRuntimePoolFullFailureProduction")]
    public sealed class GrpcKubernetesRuntimePoolFullFailureProductionScenarioTests :
        GrpcKubernetesRuntimePoolCrashRecoveryScenarioTestsBase
    {
        /// <summary>
        /// Initializes the final gRPC KubernetesPool production proof.
        /// </summary>
        public GrpcKubernetesRuntimePoolFullFailureProductionScenarioTests(
            ITestOutputHelper output)
            : base(
                output,
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile())
        {
        }

        /// <summary>
        /// Kills one exact in-Pod runtime after durable progress, then one distinct busy Pod,
        /// reuses the converged warm pool across cycles, and executes the configured nested child DAG depth
        /// before cleanup after the final cycle.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves per cycle.</param>
        /// <param name="executionCycleCount">The number of sequential warm-pool execution cycles.</param>
        /// <param name="childDepth">The number of nested child DAG levels composed by every submitted parent DAG.</param>
        /// <returns>A task that completes after the automatic full-failure proof converges.</returns>
        [Theory]
        [InlineData(5, 5, 2, 2, 0)]
        public Task Grpc_KubernetesPool_Should_Recover_Child_Runtime_Then_Distinct_Pod_And_Reuse_Warm_Capacity(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount,
            int childDepth)
        {
            return ExecuteFullFailureProductionScenarioAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount,
                childDepth);
        }

        /// <summary>
        /// Runs the same production failure scenario with a reduced but still three-boundary topology as a fast EventDriven
        /// canary before the full 5-by-5 certification. No scenario logic is duplicated: this test
        /// calls the same production harness, failure injectors, recovery assertions, and audit path.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves per cycle.</param>
        /// <param name="executionCycleCount">The number of sequential warm-pool execution cycles.</param>
        /// <param name="childDepth">The number of nested child DAG levels composed by every submitted parent DAG.</param>
        /// <returns>A task that completes after the reduced EventDriven warm-reuse proof converges.</returns>
        [Theory]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "Canary")]
        //[InlineData(5, 5, 5, 2, 3)]
        [InlineData(3, 3, 2, 2, 3)]
        public Task Grpc_KubernetesPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount,
            int childDepth)
        {
            return ExecuteFullFailureProductionScenarioAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount,
                childDepth,
                ProductionRecoveryObservationMode.EventDriven);
        }

        /// <summary>
        /// STEP11B A1 deterministic adversarial row. Reuses the exact green hierarchical production harness,
        /// but kills the selected runtime after the first durable parent step instead of the A0 midpoint.
        /// The later broad Pod failure keeps its historical independent hold coordinate.
        /// </summary>
        /// <returns>A task that completes after the crash-early production proof converges.</returns>
        [Fact]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "Adversarial")]
        [Trait("MatrixScenarioId", "crash-early")]
        public Task Grpc_KubernetesPool_Matrix_CrashEarly_Should_Preserve_Recursive_Child_Dag_Exactness()
        {
            return ExecuteFullFailureProductionScenarioAsync(
                maximumPodCount: 3,
                runtimeCountPerPod: 3,
                submissionIterationCount: 2,
                executionCycleCount: 2,
                childDepth: 3,
                recoveryObservationMode: ProductionRecoveryObservationMode.EventDriven,
                adversarialSchedule:
                    ProductionChildDagAdversarialScheduleDefinition.CrashEarly);
        }

        /// <summary>
        /// Runs the positive-depth Kubernetes Runtime Pool scenario with canonical event-driven
        /// post-kill recovery synchronization. The shared harness keeps one deterministic root execution
        /// active across full pool convergence so the same proof remains valid for any configured ChildDepth.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves per cycle.</param>
        /// <param name="executionCycleCount">The number of sequential warm-pool execution cycles.</param>
        /// <param name="childDepth">The number of nested child DAG levels composed by every submitted parent DAG.</param>
        /// <returns>A task that completes after the event-driven recovery proof converges.</returns>
        [Theory]
        [Trait("ObservationMode", "EventDriven")]
        [InlineData(5, 5, 2, 2, 1)]
        public Task Grpc_KubernetesPool_Should_Recover_Child_Runtime_Using_Canonical_Events_Then_Recover_Distinct_Pod_And_Reuse_Warm_Capacity(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount,
            int childDepth)
        {
            return ExecuteFullFailureProductionScenarioAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount,
                childDepth,
                ProductionRecoveryObservationMode.EventDriven);
        }

        /// <summary>
        /// Keeps the same hierarchical proof but waits for an operator to externally kill the exact distinct busy Pod.
        /// Before starting the test, keep this PowerShell watcher open for both cycles:
        /// <code>Get-Content "$env:TEMP\multiplexed-ai-manual-kubernetes-kill.txt" -Wait</code>
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves per cycle.</param>
        /// <param name="executionCycleCount">The number of sequential warm-pool execution cycles.</param>
        /// <param name="childDepth">The number of nested child DAG levels composed by every submitted parent DAG.</param>
        /// <returns>A task that completes after the externally triggered full-failure proof converges.</returns>
        [Theory]
        [Trait("Category", "ManualExternalFailure")]
        [InlineData(5, 5, 5, 2, 2)]
        public Task Grpc_KubernetesPool_Should_Recover_Child_Runtime_Then_Wait_For_External_Distinct_Pod_Kill_And_Reuse_Warm_Capacity(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount,
            int childDepth)
        {
            return ExecuteFullFailureProductionScenarioAwaitExternalPodFailureAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount,
                childDepth);
        }
    }
}
