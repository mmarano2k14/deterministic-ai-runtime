using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.KubernetesPool
{
    /// <summary>
    /// Defines the non-parallel collection used by destructive HTTP Kubernetes Runtime Pool proofs.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class HttpKubernetesRuntimePoolCrashRecoveryCollection
    {
        /// <summary>
        /// Gets the collection name.
        /// </summary>
        public const string Name =
            "HTTP Kubernetes Runtime Pool crash recovery collection";
    }

    /// <summary>
    /// Executes the transport-neutral bounded production harness through HTTP Kubernetes Runtime Pool capacity.
    /// </summary>
    [Collection(HttpKubernetesRuntimePoolCrashRecoveryCollection.Name)]
    [Trait("Category", "HttpKubernetesRuntimePoolCrashRecovery")]
    public sealed class HttpKubernetesRuntimePoolRealRuntimeCrashRecoveryScenarioTests :
        KubernetesRuntimePoolProductionScenarioTestsBase
    {
        /// <summary>
        /// Initializes the HTTP Kubernetes Runtime Pool production proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpKubernetesRuntimePoolRealRuntimeCrashRecoveryScenarioTests(
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
        /// Measures pure machine capacity with a bounded HTTP Kubernetes Runtime Pool.
        /// </summary>
        [Theory]
        [InlineData(3, 5, 5)]
        public Task Http_KubernetesPool_Should_Measure_Machine_Limit_With_Bounded_Capacity(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount)
        {
            return ExecuteBoundedCapacityMachineLimitScenarioAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount);
        }

        /// <summary>
        /// Verifies bounded HTTP capacity while one fully busy Runtime Pool Pod is force-deleted.
        /// </summary>
        [Theory]
        [InlineData(3, 5, 5)]
        public Task Http_KubernetesPool_Should_Recover_Bounded_Capacity_After_Forced_Pod_Deletion(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount)
        {
            return ExecuteBoundedCapacityPodFailureMachineLimitScenarioAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount);
        }

        /// <summary>
        /// Verifies warm HTTP Runtime Pool reuse across sequential production recovery cycles.
        /// </summary>
        [Theory]
        [InlineData(3, 5, 5, 2)]
        public Task Http_KubernetesPool_Should_Reuse_Warm_Capacity_Across_Sequential_Production_Recovery_Cycles(
            int maximumPodCount,
            int runtimeCountPerPod,
            int submissionIterationCount,
            int executionCycleCount)
        {
            return ExecuteReusableBoundedCapacityPodFailureProductionScenarioAsync(
                maximumPodCount,
                runtimeCountPerPod,
                submissionIterationCount,
                executionCycleCount);
        }
    }
}
