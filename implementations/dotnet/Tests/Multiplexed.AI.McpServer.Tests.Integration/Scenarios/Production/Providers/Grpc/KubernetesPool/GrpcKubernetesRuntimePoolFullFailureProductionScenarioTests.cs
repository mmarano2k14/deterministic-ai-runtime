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
        /// reuses the converged warm pool across cycles, and cleans only after the final cycle.
        /// </summary>
        [Theory]
        [InlineData(5, 5, 5, 2)]
        public Task Grpc_KubernetesPool_Should_Recover_Child_Runtime_Then_Distinct_Pod_And_Reuse_Warm_Capacity(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount)
        {
            return ExecuteFullFailureProductionScenarioAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount);
        }

        /// <summary>
        /// Keeps the same hierarchical proof but waits for an operator to externally kill the exact distinct busy Pod.
        /// Before starting the test, keep this PowerShell watcher open for both cycles:
        /// <code>Get-Content "$env:TEMP\multiplexed-ai-manual-kubernetes-kill.txt" -Wait</code>
        /// </summary>
        [Theory]
        [Trait("Category", "ManualExternalFailure")]
        [InlineData(5, 5, 5, 2)]
        public Task Grpc_KubernetesPool_Should_Recover_Child_Runtime_Then_Wait_For_External_Distinct_Pod_Kill_And_Reuse_Warm_Capacity(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount)
        {
            return ExecuteFullFailureProductionScenarioAwaitExternalPodFailureAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount);
        }
    }
}
