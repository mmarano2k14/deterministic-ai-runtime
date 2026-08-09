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
        [InlineData(3, 5, 5, 2)]
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
    }
}
