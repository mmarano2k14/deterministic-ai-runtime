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
        [InlineData(5, 5, 2, 2, 1)]
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
