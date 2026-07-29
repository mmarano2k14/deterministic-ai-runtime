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

        private static AiRuntimeInstanceRegistrationHostedService CreateService(
            FakeRuntimeInstanceRegistry registry,
            IReadOnlyDictionary<string, string> providerMetadata)
        {
            var options = new AiRuntimeInstanceRegistrationOptions
            {
                Enabled = true,
                RuntimeInstanceId = "runtime-pool-member-1",
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
                new StaticRuntimeEnvironmentProvider(providerMetadata),
                new FakeRuntimePipelineBackgroundController(),
                new StaticControlPlaneIdResolver("control-plane-1"),
                new IAiRuntimeInstanceCapacityStore[]
                {
                    new FakeRuntimeInstanceCapacityStore()
                },
                Options.Create(options),
                NullLogger<AiRuntimeInstanceRegistrationHostedService>.Instance);
        }

        private sealed class StaticRuntimeEnvironmentProvider : IAiRuntimeEnvironmentProvider
        {
            private readonly IReadOnlyDictionary<string, string> providerMetadata;

            public StaticRuntimeEnvironmentProvider(
                IReadOnlyDictionary<string, string> providerMetadata)
            {
                this.providerMetadata = providerMetadata;
            }

            public Task<AiRuntimeEnvironmentSnapshot> GetSnapshotAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new AiRuntimeEnvironmentSnapshot
                    {
                        ProviderName = "kubernetes",
                        RuntimeInstanceId = "runtime-pool-member-1",
                        HostId = "pod-uid-1",
                        RuntimeId = "runtime-member-1",
                        ControlPlaneHostId = "control-plane-host-1",
                        HostName = "runtime-pool-pod-1",
                        ProcessId = 123,
                        ProviderMetadata = this.providerMetadata
                    });
            }
        }
    }
}
