using k8s.Models;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides unit tests for <see cref="KubernetesSdkAiKubernetesRuntimeHostClient"/>.
    /// </summary>
    public sealed class KubernetesSdkAiKubernetesRuntimeHostClientTests
    {
        /// <summary>
        /// Verifies that creating a runtime host creates a pod and service when service-per-runtime is enabled.
        /// </summary>
        [Fact]
        public async Task CreateRuntimeHostAsync_Should_Create_Pod_And_Service_When_Service_Per_Runtime_Is_Enabled()
        {
            var sdkClient = new FakeAiKubernetesSdkClient();
            var client = CreateClient(sdkClient);
            var podSpec = CreatePodSpec();

            var result = await client.CreateRuntimeHostAsync(podSpec);

            Assert.True(result.Success);
            Assert.Equal("ai-runtime", result.Namespace);
            Assert.Equal("runtime-tenant-a-001", result.PodName);
            Assert.Equal("runtime-tenant-a-001-svc", result.ServiceName);
            Assert.Equal(1, sdkClient.CreatePodCallCount);
            Assert.Equal(1, sdkClient.CreateServiceCallCount);
            Assert.Equal("runtime-tenant-a-001", sdkClient.LastCreatedPod?.Metadata.Name);
            Assert.Equal("runtime-tenant-a-001-svc", sdkClient.LastCreatedService?.Metadata.Name);
            AssertKubernetesLifecycleMetadata(result.Metadata, podSpec, expectedServiceName: "runtime-tenant-a-001-svc");
        }

        /// <summary>
        /// Verifies that creating a runtime host creates only a pod when service-per-runtime is disabled.
        /// </summary>
        [Fact]
        public async Task CreateRuntimeHostAsync_Should_Create_Only_Pod_When_Service_Per_Runtime_Is_Disabled()
        {
            var sdkClient = new FakeAiKubernetesSdkClient();
            var client = CreateClient(
                sdkClient,
                options =>
                {
                    options.UseServicePerRuntime = false;
                });
            var podSpec = CreatePodSpec();

            var result = await client.CreateRuntimeHostAsync(podSpec);

            Assert.True(result.Success);
            Assert.Null(result.ServiceName);
            Assert.Equal(1, sdkClient.CreatePodCallCount);
            Assert.Equal(0, sdkClient.CreateServiceCallCount);
            AssertKubernetesLifecycleMetadata(result.Metadata, podSpec, expectedServiceName: null);
        }

        /// <summary>
        /// Verifies that create failure returns a rejected result.
        /// </summary>
        [Fact]
        public async Task CreateRuntimeHostAsync_Should_Return_Rejected_When_Pod_Create_Fails()
        {
            var sdkClient =
                new FakeAiKubernetesSdkClient
                {
                    CreatePodException = new InvalidOperationException("pod-create-failed")
                };
            var client = CreateClient(sdkClient);
            var podSpec = CreatePodSpec();

            var result = await client.CreateRuntimeHostAsync(podSpec);

            Assert.False(result.Success);
            Assert.True(result.Retryable);
            Assert.Equal("pod-create-failed", result.FailureReason);
            Assert.Equal(1, sdkClient.CreatePodCallCount);
            Assert.Equal(0, sdkClient.CreateServiceCallCount);
        }

        /// <summary>
        /// Verifies that readiness succeeds when the Kubernetes pod is ready.
        /// </summary>
        [Fact]
        public async Task WaitUntilHostReadyAsync_Should_Return_Ready_When_Pod_Is_Ready()
        {
            var sdkClient =
                new FakeAiKubernetesSdkClient
                {
                    PodStatus = CreateReadyPod()
                };
            var client = CreateClient(sdkClient);
            var podSpec = CreatePodSpec();

            var result = await client.WaitUntilHostReadyAsync(podSpec);

            Assert.True(result.Success);
            Assert.False(result.TimedOut);
            Assert.Equal("runtime-tenant-a-001-svc", result.ServiceName);
            Assert.Equal(1, sdkClient.ReadPodStatusCallCount);
            AssertKubernetesLifecycleMetadata(result.Metadata, podSpec, expectedServiceName: "runtime-tenant-a-001-svc");
        }

        /// <summary>
        /// Verifies that readiness returns failed when pod status cannot be read.
        /// </summary>
        [Fact]
        public async Task WaitUntilHostReadyAsync_Should_Return_Failed_When_Status_Read_Fails()
        {
            var sdkClient =
                new FakeAiKubernetesSdkClient
                {
                    ReadPodStatusException = new InvalidOperationException("read-status-failed")
                };
            var client = CreateClient(sdkClient);
            var podSpec = CreatePodSpec();

            var result = await client.WaitUntilHostReadyAsync(podSpec);

            Assert.False(result.Success);
            Assert.False(result.TimedOut);
            Assert.True(result.Retryable);
            Assert.Equal("read-status-failed", result.FailureReason);
            Assert.Equal(1, sdkClient.ReadPodStatusCallCount);
        }

        /// <summary>
        /// Verifies that readiness times out when the pod never becomes ready.
        /// </summary>
        [Fact]
        public async Task WaitUntilHostReadyAsync_Should_Return_Timeout_When_Pod_Never_Becomes_Ready()
        {
            var sdkClient =
                new FakeAiKubernetesSdkClient
                {
                    PodStatus = new V1Pod()
                };
            var client = CreateClient(
                sdkClient,
                options =>
                {
                    options.StartupTimeout = TimeSpan.FromMilliseconds(5);
                    options.ReadinessPollInterval = TimeSpan.FromMilliseconds(1);
                });
            var podSpec = CreatePodSpec();

            var result = await client.WaitUntilHostReadyAsync(podSpec);

            Assert.False(result.Success);
            Assert.True(result.TimedOut);
            Assert.True(result.Retryable);
            Assert.Equal("kubernetes-runtime-host-readiness-timeout", result.FailureReason);
            Assert.True(sdkClient.ReadPodStatusCallCount >= 1);
        }

        /// <summary>
        /// Verifies that deleting a runtime host deletes service and pod.
        /// </summary>
        [Fact]
        public async Task DeleteRuntimeHostAsync_Should_Delete_Service_And_Pod()
        {
            var sdkClient = new FakeAiKubernetesSdkClient();
            var client = CreateClient(sdkClient);
            var podSpec = CreatePodSpec();

            var result = await client.DeleteRuntimeHostAsync(podSpec);

            Assert.True(result.Success);
            Assert.Equal("runtime-tenant-a-001-svc", result.ServiceName);
            Assert.Equal(1, sdkClient.DeleteServiceCallCount);
            Assert.Equal(1, sdkClient.DeletePodCallCount);
            AssertKubernetesLifecycleMetadata(result.Metadata, podSpec, expectedServiceName: "runtime-tenant-a-001-svc");
        }

        /// <summary>
        /// Verifies that deleting a runtime host deletes only the pod when service-per-runtime is disabled.
        /// </summary>
        [Fact]
        public async Task DeleteRuntimeHostAsync_Should_Delete_Only_Pod_When_Service_Per_Runtime_Is_Disabled()
        {
            var sdkClient = new FakeAiKubernetesSdkClient();
            var client = CreateClient(
                sdkClient,
                options =>
                {
                    options.UseServicePerRuntime = false;
                });
            var podSpec = CreatePodSpec();

            var result = await client.DeleteRuntimeHostAsync(podSpec);

            Assert.True(result.Success);
            Assert.Null(result.ServiceName);
            Assert.Equal(0, sdkClient.DeleteServiceCallCount);
            Assert.Equal(1, sdkClient.DeletePodCallCount);
            AssertKubernetesLifecycleMetadata(result.Metadata, podSpec, expectedServiceName: null);
        }

        /// <summary>
        /// Verifies that delete failure returns a failed lifecycle result.
        /// </summary>
        [Fact]
        public async Task DeleteRuntimeHostAsync_Should_Return_Failed_When_Pod_Delete_Fails()
        {
            var sdkClient =
                new FakeAiKubernetesSdkClient
                {
                    DeletePodException = new InvalidOperationException("pod-delete-failed")
                };
            var client = CreateClient(sdkClient);
            var podSpec = CreatePodSpec();

            var result = await client.DeleteRuntimeHostAsync(podSpec);

            Assert.False(result.Success);
            Assert.True(result.Retryable);
            Assert.Equal("pod-delete-failed", result.FailureReason);
            Assert.Equal(1, sdkClient.DeleteServiceCallCount);
            Assert.Equal(1, sdkClient.DeletePodCallCount);
        }

        private static KubernetesSdkAiKubernetesRuntimeHostClient CreateClient(
            FakeAiKubernetesSdkClient sdkClient,
            Action<AiKubernetesRuntimeHostOptions>? configure = null)
        {
            var options = new AiKubernetesRuntimeHostOptions
            {
                Enabled = true,
                RuntimeImage = "multiplexed-ai-runtime:test",
                UseServicePerRuntime = true,
                StartupTimeout = TimeSpan.FromMilliseconds(50),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(1)
            };

            configure?.Invoke(options);

            return new KubernetesSdkAiKubernetesRuntimeHostClient(
                new FakeKubernetesClientFactory(sdkClient),
                new AiKubernetesSdkResourceFactory(Options.Create(options)),
                Options.Create(options));
        }

        private static AiKubernetesRuntimePodSpec CreatePodSpec()
        {
            return new AiKubernetesRuntimePodSpec
            {
                Namespace = "ai-runtime",
                PodName = "runtime-tenant-a-001",
                RuntimeImage = "multiplexed-ai-runtime:test",
                ContainerName = "runtime-instance",
                ContainerPort = 8080,
                Labels =
                    new Dictionary<string, string>
                    {
                        ["multiplexed.ai/runtime-instance-id"] = "tenant-a-runtime-001",
                        ["multiplexed.ai/provider"] = "grpc",
                        ["multiplexed.ai/transport"] = "grpc"
                    },
                Annotations =
                    new Dictionary<string, string>
                    {
                        ["host.provider"] = "kubernetes"
                    },
                EnvironmentVariables =
                    new Dictionary<string, string>
                    {
                        ["AiMcpHost__Mode"] = "RuntimeInstanceOnly"
                    }
            };
        }

        private static V1Pod CreateReadyPod()
        {
            return new V1Pod
            {
                Status =
                    new V1PodStatus
                    {
                        Conditions =
                            new List<V1PodCondition>
                            {
                                new()
                                {
                                    Type = "Ready",
                                    Status = "True"
                                }
                            }
                    }
            };
        }

        private static void AssertKubernetesLifecycleMetadata(
            IReadOnlyDictionary<string, string> metadata,
            AiKubernetesRuntimePodSpec podSpec,
            string? expectedServiceName)
        {
            if (metadata.TryGetValue(AiKubernetesRuntimeHostMetadataKeys.Namespace, out var namespaceName))
            {
                Assert.Equal(podSpec.Namespace, namespaceName);
            }

            if (metadata.TryGetValue(AiKubernetesRuntimeHostMetadataKeys.PodName, out var podName))
            {
                Assert.Equal(podSpec.PodName, podName);
            }

            if (expectedServiceName is null)
            {
                Assert.False(metadata.ContainsKey(AiKubernetesRuntimeHostMetadataKeys.ServiceName));
                return;
            }

            if (metadata.TryGetValue(AiKubernetesRuntimeHostMetadataKeys.ServiceName, out var serviceName))
            {
                Assert.Equal(expectedServiceName, serviceName);
            }
        }
    }
}