using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides unit tests for <see cref="FakeAiKubernetesRuntimeHostClient"/>.
    /// </summary>
    public sealed class FakeAiKubernetesRuntimeHostClientTests
    {
        /// <summary>
        /// Verifies that the fake client creates a runtime host and reports it ready.
        /// </summary>
        [Fact]
        public async Task Create_Then_Readiness_Should_Return_Ready()
        {
            var client = new FakeAiKubernetesRuntimeHostClient();
            var podSpec = CreatePodSpec();

            var createResult = await client.CreateRuntimeHostAsync(podSpec);
            var readinessResult = await client.WaitUntilHostReadyAsync(podSpec);

            Assert.True(createResult.Success);
            Assert.True(readinessResult.Success);
            Assert.Equal("ai-runtime", createResult.Namespace);
            Assert.Equal("runtime-001", createResult.PodName);
            Assert.Equal("runtime-001-svc", createResult.ServiceName);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Equal(1, client.ReadinessCallCount);
            Assert.Same(podSpec, client.LastCreatedPodSpec);
            Assert.Same(podSpec, client.LastReadinessPodSpec);
        }

        /// <summary>
        /// Verifies that readiness fails when the pod was not created first.
        /// </summary>
        [Fact]
        public async Task Readiness_Should_Fail_When_Pod_Was_Not_Created()
        {
            var client = new FakeAiKubernetesRuntimeHostClient();
            var podSpec = CreatePodSpec();

            var result = await client.WaitUntilHostReadyAsync(podSpec);

            Assert.False(result.Success);
            Assert.False(result.TimedOut);
            Assert.False(result.Retryable);
            Assert.Equal("fake-kubernetes-pod-not-created", result.FailureReason);
            Assert.Equal("runtime-001", result.PodName);
            Assert.Equal("runtime-001-svc", result.ServiceName);
            Assert.Equal(1, client.ReadinessCallCount);
        }

        /// <summary>
        /// Verifies that the fake client can simulate create failures.
        /// </summary>
        [Fact]
        public async Task Create_Should_Return_Rejected_When_Create_Failure_Is_Configured()
        {
            var client =
                new FakeAiKubernetesRuntimeHostClient
                {
                    FailCreate = true
                };

            var podSpec = CreatePodSpec();

            var result = await client.CreateRuntimeHostAsync(podSpec);

            Assert.False(result.Success);
            Assert.True(result.Retryable);
            Assert.Equal("fake-kubernetes-create-failed", result.FailureReason);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Same(podSpec, client.LastCreatedPodSpec);
        }

        /// <summary>
        /// Verifies that the fake client can simulate readiness failures.
        /// </summary>
        [Fact]
        public async Task Readiness_Should_Return_Failed_When_Readiness_Failure_Is_Configured()
        {
            var client =
                new FakeAiKubernetesRuntimeHostClient
                {
                    FailReadiness = true,
                    ReadinessTimedOut = true
                };

            var podSpec = CreatePodSpec();

            await client.CreateRuntimeHostAsync(podSpec);
            var result = await client.WaitUntilHostReadyAsync(podSpec);

            Assert.False(result.Success);
            Assert.True(result.Retryable);
            Assert.True(result.TimedOut);
            Assert.Equal("fake-kubernetes-readiness-failed", result.FailureReason);
            Assert.Equal(1, client.ReadinessCallCount);
            Assert.Same(podSpec, client.LastReadinessPodSpec);
        }

        /// <summary>
        /// Verifies that the fake client deletes a created runtime host.
        /// </summary>
        [Fact]
        public async Task Delete_Should_Remove_Created_Runtime_Host()
        {
            var client = new FakeAiKubernetesRuntimeHostClient();
            var podSpec = CreatePodSpec();

            await client.CreateRuntimeHostAsync(podSpec);
            var deleteResult = await client.DeleteRuntimeHostAsync(podSpec);
            var readinessResult = await client.WaitUntilHostReadyAsync(podSpec);

            Assert.True(deleteResult.Success);
            Assert.Equal("runtime-001", deleteResult.PodName);
            Assert.Equal("runtime-001-svc", deleteResult.ServiceName);
            Assert.False(readinessResult.Success);
            Assert.Equal("fake-kubernetes-pod-not-created", readinessResult.FailureReason);
            Assert.Equal(1, client.DeleteCallCount);
            Assert.Same(podSpec, client.LastDeletedPodSpec);
        }

        /// <summary>
        /// Verifies that the fake client can simulate delete failures.
        /// </summary>
        [Fact]
        public async Task Delete_Should_Return_Failed_When_Delete_Failure_Is_Configured()
        {
            var client =
                new FakeAiKubernetesRuntimeHostClient
                {
                    FailDelete = true
                };

            var podSpec = CreatePodSpec();

            await client.CreateRuntimeHostAsync(podSpec);
            var result = await client.DeleteRuntimeHostAsync(podSpec);

            Assert.False(result.Success);
            Assert.True(result.Retryable);
            Assert.Equal("fake-kubernetes-delete-failed", result.FailureReason);
            Assert.Equal(1, client.DeleteCallCount);
            Assert.Same(podSpec, client.LastDeletedPodSpec);
        }

        /// <summary>
        /// Creates a test Kubernetes runtime pod specification.
        /// </summary>
        /// <returns>The test Kubernetes runtime pod specification.</returns>
        private static AiKubernetesRuntimePodSpec CreatePodSpec()
        {
            return new AiKubernetesRuntimePodSpec
            {
                Namespace = "ai-runtime",
                PodName = "runtime-001",
                RuntimeImage = "multiplexed-ai-runtime:test",
                ContainerName = "runtime-instance",
                ContainerPort = 8080,
                ServiceAccountName = "runtime-service-account",
                Labels = new Dictionary<string, string>
                {
                    ["multiplexed.ai/provider"] = "grpc",
                    ["multiplexed.ai/host-provider"] = "kubernetes"
                },
                Annotations = new Dictionary<string, string>
                {
                    ["host.provider"] = "kubernetes",
                    ["provider.name"] = "grpc"
                },
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["AiMcpHost__Mode"] = "RuntimeInstanceOnly",
                    ["AiRuntimeInstanceRegistration__ProviderName"] = "grpc"
                }
            };
        }
    }
}