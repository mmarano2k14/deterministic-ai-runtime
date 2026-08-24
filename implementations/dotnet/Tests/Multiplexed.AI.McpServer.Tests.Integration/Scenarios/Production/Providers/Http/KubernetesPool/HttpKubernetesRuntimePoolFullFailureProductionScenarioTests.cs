using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.KubernetesPool
{
    /// <summary>
    /// Exposes the final hierarchical KubernetesPool failure and warm-reuse proof through HTTP.
    /// </summary>
    [Collection(HttpKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "HttpKubernetesRuntimePoolFullFailureProduction")]
    public sealed class HttpKubernetesRuntimePoolFullFailureProductionScenarioTests :
        KubernetesRuntimePoolProductionScenarioTestsBase
    {
        /// <summary>
        /// Initializes the final HTTP KubernetesPool production proof.
        /// </summary>
        public HttpKubernetesRuntimePoolFullFailureProductionScenarioTests(
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

        /// <summary>
        /// Kills one exact in-Pod runtime after durable progress, then one distinct busy Pod,
        /// reuses the converged warm pool across cycles, and cleans only after the final cycle.
        /// </summary>
        [Theory]
        [InlineData(5, 5, 5, 2)]
        public Task Http_KubernetesPool_Should_Recover_Child_Runtime_Then_Distinct_Pod_And_Reuse_Warm_Capacity(
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
        /// Runs the same full hierarchical Kubernetes Runtime Pool proof through HTTP with canonical
        /// event-driven post-kill recovery synchronization and the configured recursive Child DAG depth.
        /// The shared harness remains the single authority for admission, failure injection, warm reuse,
        /// replay, ledger, recovery-forensics, lifecycle, and exact terminal DAG assertions.
        /// </summary>
        /// <param name="maximumPodCount">The maximum number of Kubernetes Runtime Pool Pods.</param>
        /// <param name="runtimeCountPerPod">The exact number of independently registered runtimes per Pod.</param>
        /// <param name="submissionIterationCount">The number of full-capacity submission waves per cycle.</param>
        /// <param name="executionCycleCount">The number of sequential warm-pool execution cycles.</param>
        /// <param name="childDepth">The number of nested child DAG levels composed by every submitted parent DAG.</param>
        /// <returns>A task that completes after the HTTP EventDriven full-failure proof converges.</returns>
        [Theory]
        [Trait("ObservationMode", "EventDriven")]
        [Trait("ValidationProfile", "Canary")]
        [InlineData(3, 3, 3, 2, 3)]
        public Task Http_KubernetesPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario(
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
        [Theory]
        [Trait("Category", "ManualExternalFailure")]
        [InlineData(5, 5, 5, 2)]
        public Task Http_KubernetesPool_Should_Recover_Child_Runtime_Then_Wait_For_External_Distinct_Pod_Kill_And_Reuse_Warm_Capacity(
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
