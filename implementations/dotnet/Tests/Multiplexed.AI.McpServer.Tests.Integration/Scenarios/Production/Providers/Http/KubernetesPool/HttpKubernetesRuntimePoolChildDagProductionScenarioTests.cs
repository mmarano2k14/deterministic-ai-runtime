using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.KubernetesPool
{
    /// <summary>
    /// Proves deterministic Child DAG composition through a real HTTP Kubernetes Runtime Pool.
    /// </summary>
    [Collection(HttpKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "HttpKubernetesRuntimePoolChildDag")]
    public sealed class HttpKubernetesRuntimePoolChildDagProductionScenarioTests
    {
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes the focused HTTP Kubernetes Runtime Pool Child DAG proofs.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpKubernetesRuntimePoolChildDagProductionScenarioTests(
            ITestOutputHelper output)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Proves one Child DAG level through one real in-Pod RuntimeInstanceOnly process.
        /// </summary>
        /// <returns>A task that completes when the parent and child converge.</returns>
        [Fact]
        public async Task Http_KubernetesPool_Should_Run_SingleTenant_Shared_Runtime_Mode_With_Child_Dag_Depth_One()
        {
            var scenario =
                ProductionChildDagScenarioFactory.CreateDepthOneScenario();
            var runner =
                new HttpKubernetesRuntimePoolProductionScenarioRunner(this.output);

            var result = await runner.RunAsync(scenario);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(
                scenario,
                result);
        }

        /// <summary>
        /// Proves nominal P to C1 to C2 cascading convergence through one real in-Pod runtime.
        /// </summary>
        /// <returns>A task that completes when the nested composition converges.</returns>
        [Fact]
        public async Task Http_KubernetesPool_Should_Run_SingleTenant_Shared_Runtime_Mode_With_Child_Dag_Depth_Two()
        {
            var scenario =
                ProductionChildDagScenarioFactory.CreateDepthTwoScenario();
            var runner =
                new HttpKubernetesRuntimePoolProductionScenarioRunner(this.output);

            var result = await runner.RunAsync(scenario);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(
                scenario,
                result);
        }
        /// <summary>
        /// Proves depth-one Child DAG recovery after killing the exact RuntimeInstanceOnly process inside the Pod.
        /// </summary>
        /// <returns>A task that completes when the recovered child and parent converge.</returns>
        [Fact]
        public async Task Http_KubernetesPool_Should_Recover_Child_Dag_Depth_One_After_Real_Child_Runtime_Process_Kill()
        {
            var scenario =
                ProductionChildDagScenarioFactory.CreateDepthOneRuntimeCrashRecoveryScenario();
            var runner =
                new HttpKubernetesRuntimePoolProductionScenarioRunner(this.output);

            var result = await runner.RunAsync(scenario);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(
                scenario,
                result);
            ProductionChildDagAssertions.AssertKubernetesRuntimeProcessFailureBoundary(
                result);
        }

        /// <summary>
        /// Proves cascading P to C1 to C2 convergence after killing the intermediate C1 runtime process inside the Pod.
        /// </summary>
        /// <returns>A task that completes when C1 recovers, C2 completes, and convergence reaches the root parent.</returns>
        [Fact]
        public async Task Http_KubernetesPool_Should_Recover_Intermediate_Child_Dag_Depth_Two_After_Real_Runtime_Process_Kill()
        {
            var scenario =
                ProductionChildDagScenarioFactory.CreateDepthTwoIntermediateRuntimeCrashRecoveryScenario();
            var runner =
                new HttpKubernetesRuntimePoolProductionScenarioRunner(this.output);

            var result = await runner.RunAsync(scenario);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(
                scenario,
                result);
            ProductionChildDagAssertions.AssertKubernetesRuntimeProcessFailureBoundary(
                result);
        }

        /// <summary>
        /// Proves depth-one Child DAG recovery after deleting the complete Kubernetes Runtime Pool Pod that owns C1.
        /// </summary>
        /// <returns>A task that completes when a fresh Pod recovers the same child execution and the parent converges.</returns>
        [Fact]
        public async Task Http_KubernetesPool_Should_Recover_Child_Dag_Depth_One_After_Real_Pod_Kill()
        {
            var scenario =
                ProductionChildDagScenarioFactory.CreateDepthOneRuntimeCrashRecoveryScenario();
            var runner =
                new HttpKubernetesRuntimePoolProductionScenarioRunner(
                    this.output,
                    KubernetesRuntimePoolChildFailureBoundary.KubernetesPod);

            var result = await runner.RunAsync(scenario);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(
                scenario,
                result);
            ProductionChildDagAssertions.AssertKubernetesPodFailureBoundary(
                result);
        }

        /// <summary>
        /// Proves cascading P to C1 to C2 convergence after deleting the complete Pod that owns intermediate C1.
        /// </summary>
        /// <returns>A task that completes when a fresh Pod recovers C1, C2 completes, and convergence reaches P.</returns>
        [Fact]
        public async Task Http_KubernetesPool_Should_Recover_Intermediate_Child_Dag_Depth_Two_After_Real_Pod_Kill()
        {
            var scenario =
                ProductionChildDagScenarioFactory.CreateDepthTwoIntermediateRuntimeCrashRecoveryScenario();
            var runner =
                new HttpKubernetesRuntimePoolProductionScenarioRunner(
                    this.output,
                    KubernetesRuntimePoolChildFailureBoundary.KubernetesPod);

            var result = await runner.RunAsync(scenario);

            ProductionRuntimeScenarioAssertions.AssertConfiguredScenario(
                scenario,
                result);
            ProductionChildDagAssertions.AssertKubernetesPodFailureBoundary(
                result);
        }

        /// <summary>
        /// Proves the canonical Step 9 parent-failure path: P parks and releases capacity, its original Pod is
        /// destroyed while C1 remains active on a distinct Pod, then C1 completes and the same parent converges.
        /// </summary>
        /// <returns>A task that completes when the child survives the parent-Pod failure and the parent completes.</returns>
        [Fact]
        public async Task Http_KubernetesPool_Should_Complete_Child_And_Resume_Parent_After_Parent_Pod_Kill()
        {
            var scenario =
                ProductionChildDagScenarioFactory.CreateParentRuntimeCrashWhileChildRunsScenario();
            var runner =
                new HttpKubernetesRuntimePoolProductionScenarioRunner(
                    this.output,
                    KubernetesRuntimePoolChildFailureBoundary.KubernetesPod,
                    maximumPodCount: 2);

            var result = await runner.RunAsync(scenario);

            ProductionChildDagAssertions.AssertFinalProductionProof(
                scenario,
                result);

            this.output.WriteLine(string.Empty);
            this.output.WriteLine("[STEP 9 CHILD DAG FINAL PROOF]");
            this.output.WriteLine("ParentExecutionCount='1'");
            this.output.WriteLine("ChildExecutionCount='1'");
            this.output.WriteLine("ChildInvocationGeneration='0'");
            this.output.WriteLine("ChildResultCount='1'");
            this.output.WriteLine("DuplicateChildCount='0'");
            this.output.WriteLine("DuplicateEffectiveContinuationCount='0'");
            this.output.WriteLine("ParentCompleted='true'");
            this.output.WriteLine("ChildCompleted='true'");
            this.output.WriteLine("ParentCapacityReleasedWhileWaiting='true'");
            this.output.WriteLine("SameParentExecutionIdAfterRecovery='true'");
            this.output.WriteLine("SameChildExecutionIdAfterRecovery='true'");
            this.output.WriteLine("ReplayValidated='true'");
            this.output.WriteLine("LedgerValidated='true'");
            this.output.WriteLine("TraceValidated='true'");
            this.output.WriteLine("TenantIsolationValidated='true'");
        }
    }
}
