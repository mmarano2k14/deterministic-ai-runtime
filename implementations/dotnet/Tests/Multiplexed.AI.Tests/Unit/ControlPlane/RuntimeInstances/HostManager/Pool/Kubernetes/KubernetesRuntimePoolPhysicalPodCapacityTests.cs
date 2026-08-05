using k8s.Models;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Validates the physical Kubernetes Pod capacity boundary.
    /// </summary>
    public sealed class KubernetesRuntimePoolPhysicalPodCapacityTests
    {
        [Fact]
        public async Task CountRuntimePoolPodsAsync_Should_Filter_By_Exact_Pool_Annotation()
        {
            var sdk = new FakeAiKubernetesSdkClient();
            sdk.Pods.Add(CreatePhysicalPod("pod-a-1", "pool-a"));
            sdk.Pods.Add(CreatePhysicalPod("pod-a-2", "pool-a"));
            sdk.Pods.Add(CreatePhysicalPod("pod-a-3", "pool-a"));
            sdk.Pods.Add(CreatePhysicalPod("pod-b-1", "pool-b"));

            var client = CreateClient(sdk);

            var count =
                await client.CountRuntimePoolPodsAsync(
                    "runtime-tests",
                    "pool-a");

            Assert.Equal(3, count);
            Assert.Equal(1, sdk.ListPodsCallCount);
        }

        [Fact]
        public async Task CreateRuntimePoolHostAsync_Should_Reject_Before_Kubernetes_Mutation_At_Physical_Limit()
        {
            var sdk = new FakeAiKubernetesSdkClient();
            sdk.Pods.Add(CreatePhysicalPod("pod-a-1", "pool-a"));
            sdk.Pods.Add(CreatePhysicalPod("pod-a-2", "pool-a"));
            sdk.Pods.Add(CreatePhysicalPod("pod-a-3", "pool-a"));

            var client = CreateClient(sdk);
            var spec = CreatePodSpec(maximumPodCount: 3);

            var result =
                await client.CreateRuntimePoolHostAsync(spec);

            Assert.False(result.Success);
            Assert.True(result.Retryable);
            Assert.Equal(
                "kubernetes-runtime-pool-physical-pod-capacity-already-satisfied",
                result.FailureReason);
            Assert.Equal(0, sdk.CreatePodCallCount);
            Assert.Equal("3", result.Metadata["runtime.pool.physicalPodCount"]);
            Assert.Equal("3", result.Metadata["runtime.pool.maximumPodCount"]);
        }

        private static KubernetesSdkAiKubernetesRuntimePoolHostClient
            CreateClient(FakeAiKubernetesSdkClient sdk)
        {
            var hostOptions =
                new AiKubernetesRuntimePoolHostOptions
                {
                    RuntimeImage = "runtime:test",
                    CreateService = false
                };

            return new KubernetesSdkAiKubernetesRuntimePoolHostClient(
                new FakeKubernetesClientFactory(sdk),
                new AiKubernetesRuntimePoolSdkResourceFactory(
                    hostOptions),
                hostOptions);
        }

        private static AiKubernetesRuntimePoolPodSpec CreatePodSpec(
            int maximumPodCount)
        {
            var poolOptions =
                new AiKubernetesRuntimePoolOptions
                {
                    Enabled = true,
                    PoolId = "pool-a",
                    Namespace = "runtime-tests",
                    ProviderName = "grpc",
                    TransportName = "grpc",
                    MaximumPodCount = maximumPodCount,
                    InitialRuntimeInstanceCount = 5,
                    MinimumRuntimeInstanceCount = 5,
                    MaximumRuntimeInstanceCount = 5
                };

            var hostOptions =
                new AiKubernetesRuntimePoolHostOptions
                {
                    RuntimeImage = "runtime:test",
                    CreateService = false
                };

            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    poolOptions,
                    "request-4");

            return new AiKubernetesRuntimePoolPodSpecBuilder(
                poolOptions,
                hostOptions)
                .Build(plan);
        }

        private static V1Pod CreatePhysicalPod(
            string podName,
            string poolId)
        {
            return new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = podName,
                    NamespaceProperty = "runtime-tests",
                    Labels =
                        new Dictionary<string, string>
                        {
                            ["multiplexed.ai/runtime-pool"] =
                                "true"
                        },
                    Annotations =
                        new Dictionary<string, string>
                        {
                            ["multiplexed.ai/pool-id"] =
                                poolId
                        }
                }
            };
        }
    }
}
