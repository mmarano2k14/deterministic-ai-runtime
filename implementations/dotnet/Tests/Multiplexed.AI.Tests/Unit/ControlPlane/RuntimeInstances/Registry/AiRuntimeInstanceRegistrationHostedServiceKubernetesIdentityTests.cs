using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Tests first-class Kubernetes identity publication by the runtime registration hosted service.
    /// </summary>
    public sealed class AiRuntimeInstanceRegistrationHostedServiceKubernetesIdentityTests
    {
        /// <summary>
        /// Verifies that Kubernetes provider metadata is promoted to the typed registry fields used
        /// by runtime-pool lifecycle and pod-failure handling.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Publish_FirstClass_Kubernetes_Identity_From_Provider_Metadata()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var service = CreateService(
                registry,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [AiKubernetesRuntimeHostMetadataKeys.Namespace] = "ai-runtime",
                    [AiKubernetesRuntimeHostMetadataKeys.PodName] = "runtime-pool-pod-1",
                    [AiKubernetesRuntimeHostMetadataKeys.NodeName] = "minikube"
                });

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                var snapshot = Assert.Single(registry.RuntimeInstances);

                Assert.Equal("ai-runtime", snapshot.KubernetesNamespace);
                Assert.Equal("runtime-pool-pod-1", snapshot.KubernetesPodName);
                Assert.Equal("minikube", snapshot.KubernetesNodeName);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Verifies that providers without Kubernetes metadata retain the previous null field behavior.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Leave_Kubernetes_Identity_Null_When_Metadata_Is_Absent()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var service = CreateService(
                registry,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                var snapshot = Assert.Single(registry.RuntimeInstances);

                Assert.Null(snapshot.KubernetesNamespace);
                Assert.Null(snapshot.KubernetesPodName);
                Assert.Null(snapshot.KubernetesNodeName);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Verifies that a fresh in-Pod replacement identity inherits one surviving sibling's
        /// externally projected Gateway transport so the replacement remains dispatchable from
        /// the control plane without reusing the failed RuntimeInstanceId.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Preserve_KubernetesPool_Sibling_Gateway_Transport_For_New_Replacement_Identity()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();

            await capacityStore
                .PublishAsync(
                    new AiRuntimeInstanceCapacityDescriptor
                    {
                        RuntimeInstanceId = "runtime-pool-member-1",
                        PoolId = "runtime-pool-1",
                        HostId = "pod-uid-1",
                        ProviderName = "grpc",
                        Status = AiRuntimeInstanceStatus.Ready,
                        AvailableRunSlots = 1,
                        CanAcceptRun = true,
                        Metadata =
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["provider.name"] = "grpc",
                                ["host.provider"] = "kubernetes",
                                ["host.creation.mode"] = "KubernetesPool",
                                ["deployment"] = "kubernetes-pool",
                                ["transport.endpoint"] = "http://127.0.0.1:52695",
                                ["transport.endpoint.scope"] = "control-plane",
                                ["gateway.routing.header"] = "x-ai-runtime-instance-id",
                                ["gateway.routing.value"] = "runtime-pool-member-1",
                                ["kubernetes.pod.uid"] = "pod-uid-1",
                                ["kubernetes.pod.name"] = "runtime-pool-pod-1"
                            }
                    })
                .ConfigureAwait(false);

            var replacementRuntimeInstanceId =
                "runtime-pool-replacement-6-test";

            var service = CreateService(
                registry,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["provider.name"] = "grpc",
                    ["transport.name"] = "grpc",
                    ["host.provider"] = "kubernetes",
                    ["host.creation.mode"] = "KubernetesPool",
                    ["hostType"] = "runtime-instance-kubernetes-pool",
                    ["deployment"] = "kubernetes-pool",
                    ["transport.endpoint"] = "http://127.0.0.1:19081",
                    ["transport.endpoint.scope"] = "pod-internal",
                    [AiKubernetesRuntimeHostMetadataKeys.Namespace] = "ai-runtime",
                    [AiKubernetesRuntimeHostMetadataKeys.PodName] = "runtime-pool-pod-1"
                },
                capacityStore,
                replacementRuntimeInstanceId);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                var descriptor =
                    Assert.Single(
                        capacityStore.PublishedDescriptors,
                        item => string.Equals(
                            item.RuntimeInstanceId,
                            replacementRuntimeInstanceId,
                            StringComparison.Ordinal));

                Assert.True(descriptor.CanAcceptRun);
                Assert.Equal(
                    "http://127.0.0.1:52695",
                    descriptor.Metadata["transport.endpoint"]);
                Assert.Equal(
                    "http://127.0.0.1:19081",
                    descriptor.Metadata["transport.endpoint.internal"]);
                Assert.Equal(
                    "control-plane",
                    descriptor.Metadata["transport.endpoint.scope"]);
                Assert.Equal(
                    "preserved-sibling-capacity-descriptor",
                    descriptor.Metadata["transport.endpoint.source"]);
                Assert.Equal(
                    "runtime-pool-member-1",
                    descriptor.Metadata["gateway.routing.value"]);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static AiRuntimeInstanceRegistrationHostedService CreateService(
            FakeRuntimeInstanceRegistry registry,
            IReadOnlyDictionary<string, string> providerMetadata,
            IAiRuntimeInstanceCapacityStore? capacityStore = null,
            string runtimeInstanceId = "runtime-pool-member-1")
        {
            var options = new AiRuntimeInstanceRegistrationOptions
            {
                Enabled = true,
                RuntimeInstanceId = runtimeInstanceId,
                PoolId = "runtime-pool-1",
                HostId = "pod-uid-1",
                ProviderName = "kubernetes",
                WorkerCount = 1,
                QueueCapacity = 2,
                MaxConcurrentRuns = 1,
                RuntimeVersion = "test-runtime",
                HeartbeatInterval = TimeSpan.FromHours(1),
                RegistryTtl = TimeSpan.FromMinutes(5),
                CapacityTtl = TimeSpan.FromMinutes(5)
            };

            return new AiRuntimeInstanceRegistrationHostedService(
                registry,
                new StaticRuntimeEnvironmentProvider(
                    providerMetadata,
                    runtimeInstanceId),
                new FakeRuntimePipelineBackgroundController(),
                new StaticControlPlaneIdResolver("control-plane-1"),
                new IAiRuntimeInstanceCapacityStore[]
                {
                    capacityStore ?? new FakeRuntimeInstanceCapacityStore()
                },
                Options.Create(options),
                NullLogger<AiRuntimeInstanceRegistrationHostedService>.Instance);
        }

        private sealed class StaticRuntimeEnvironmentProvider : IAiRuntimeEnvironmentProvider
        {
            private readonly IReadOnlyDictionary<string, string> providerMetadata;
            private readonly string runtimeInstanceId;

            public StaticRuntimeEnvironmentProvider(
                IReadOnlyDictionary<string, string> providerMetadata,
                string runtimeInstanceId)
            {
                this.providerMetadata = providerMetadata;
                this.runtimeInstanceId = runtimeInstanceId;
            }

            public Task<AiRuntimeEnvironmentSnapshot> GetSnapshotAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new AiRuntimeEnvironmentSnapshot
                    {
                        ProviderName = "kubernetes",
                        RuntimeInstanceId = this.runtimeInstanceId,
                        HostId = "pod-uid-1",
                        RuntimeId = this.runtimeInstanceId,
                        ControlPlaneHostId = "control-plane-host-1",
                        HostName = "runtime-pool-pod-1",
                        ProcessId = 123,
                        ProviderMetadata = this.providerMetadata
                    });
            }
        }
    }
}
