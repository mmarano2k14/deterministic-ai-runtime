using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using Multiplexed.AI.Tests.Fixtures;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides unit tests for <see cref="AiKubernetesRuntimeHostServiceCollectionExtensions"/>.
    /// </summary>
    public sealed class AiKubernetesRuntimeHostServiceCollectionExtensionsTests
    {
        /// <summary>
        /// Verifies that Kubernetes runtime host services are registered explicitly.
        /// </summary>
        [Fact]
        public void AddAiKubernetesRuntimeHostProvider_Should_Register_Kubernetes_Host_Services()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeInstanceReadinessWaiter, FakeRuntimeInstanceReadinessWaiter>();
            services.AddAiKubernetesRuntimeHostProvider(
                options =>
                {
                    options.Enabled = true;
                    options.Namespace = "ai-runtime";
                    options.RuntimeImage = "multiplexed-ai-runtime:test";
                    options.ContainerName = "runtime-instance";
                    options.ContainerPort = 8081;
                    options.TransportName = "grpc";
                    options.ClientMode = AiKubernetesRuntimeHostClientMode.Fake;
                });

            using var provider = services.BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<AiKubernetesRuntimeHostOptions>>().Value;
            var metadataBuilder = provider.GetRequiredService<AiKubernetesRuntimePodMetadataBuilder>();
            var podSpecBuilder = provider.GetRequiredService<AiKubernetesRuntimePodSpecBuilder>();
            var resourceFactory = provider.GetRequiredService<AiKubernetesSdkResourceFactory>();
            var clientFactory = provider.GetRequiredService<IKubernetesClientFactory>();
            var sdkClient = provider.GetRequiredService<KubernetesSdkAiKubernetesRuntimeHostClient>();
            var client = provider.GetRequiredService<IAiKubernetesRuntimeHostClient>();
            var strategies = provider.GetServices<IAiRuntimeHostCreationStrategy>().ToArray();

            Assert.True(options.Enabled);
            Assert.Equal("ai-runtime", options.Namespace);
            Assert.Equal("multiplexed-ai-runtime:test", options.RuntimeImage);
            Assert.Equal("runtime-instance", options.ContainerName);
            Assert.Equal(8081, options.ContainerPort);
            Assert.Equal("grpc", options.TransportName);
            Assert.Equal(AiKubernetesRuntimeHostClientMode.Fake, options.ClientMode);
            Assert.NotNull(metadataBuilder);
            Assert.NotNull(podSpecBuilder);
            Assert.NotNull(resourceFactory);
            Assert.IsType<DefaultKubernetesClientFactory>(clientFactory);
            Assert.NotNull(sdkClient);
            Assert.IsType<FakeAiKubernetesRuntimeHostClient>(client);
            Assert.Contains(strategies, strategy => strategy.GetType() == typeof(KubernetesAiRuntimeHostCreationStrategy));
        }

        /// <summary>
        /// Verifies that Kubernetes runtime host options can be bound from configuration.
        /// </summary>
        [Fact]
        public void AddAiKubernetesRuntimeHostProvider_Should_Bind_Options_From_Configuration()
        {
            var configuration =
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AiKubernetesRuntimeHost:Enabled"] = "true",
                            ["AiKubernetesRuntimeHost:Namespace"] = "ai-runtime",
                            ["AiKubernetesRuntimeHost:RuntimeImage"] = "multiplexed-ai-runtime:configured",
                            ["AiKubernetesRuntimeHost:ContainerName"] = "runtime-instance",
                            ["AiKubernetesRuntimeHost:ContainerPort"] = "9090",
                            ["AiKubernetesRuntimeHost:TransportName"] = "grpc",
                            ["AiKubernetesRuntimeHost:PodNamePrefix"] = "runtime",
                            ["AiKubernetesRuntimeHost:ClientMode"] = "Fake",
                            ["AiKubernetesRuntimeHost:DeleteResourcesOnFailure"] = "true"
                        })
                    .Build();

            var services = new ServiceCollection();

            services.AddAiKubernetesRuntimeHostProvider(configuration);

            using var provider = services.BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<AiKubernetesRuntimeHostOptions>>().Value;

            Assert.True(options.Enabled);
            Assert.Equal("ai-runtime", options.Namespace);
            Assert.Equal("multiplexed-ai-runtime:configured", options.RuntimeImage);
            Assert.Equal("runtime-instance", options.ContainerName);
            Assert.Equal(9090, options.ContainerPort);
            Assert.Equal("grpc", options.TransportName);
            Assert.Equal("runtime", options.PodNamePrefix);
            Assert.Equal(AiKubernetesRuntimeHostClientMode.Fake, options.ClientMode);
            Assert.True(options.DeleteResourcesOnFailure);
        }

        /// <summary>
        /// Verifies that Kubernetes registration adds a host creation strategy without replacing existing strategies.
        /// </summary>
        [Fact]
        public void AddAiKubernetesRuntimeHostProvider_Should_Add_Strategy_Without_Replacing_Existing_Strategies()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeHostCreationStrategy, ExistingRuntimeHostCreationStrategy>();
            services.AddSingleton<IAiRuntimeInstanceReadinessWaiter, FakeRuntimeInstanceReadinessWaiter>();
            services.AddAiKubernetesRuntimeHostProvider(
                options =>
                {
                    options.Enabled = true;
                    options.RuntimeImage = "multiplexed-ai-runtime:test";
                    options.ClientMode = AiKubernetesRuntimeHostClientMode.Fake;
                });

            using var provider = services.BuildServiceProvider();

            var strategies = provider.GetServices<IAiRuntimeHostCreationStrategy>().ToArray();

            Assert.Contains(strategies, strategy => strategy.GetType() == typeof(ExistingRuntimeHostCreationStrategy));
            Assert.Contains(strategies, strategy => strategy.GetType() == typeof(KubernetesAiRuntimeHostCreationStrategy));
        }

        /// <summary>
        /// Verifies that Kubernetes SDK client mode resolves the SDK-backed runtime host client.
        /// </summary>
        [Fact]
        public void AddAiKubernetesRuntimeHostProvider_Should_Resolve_KubernetesSdk_Client_When_ClientMode_Is_KubernetesSdk()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeInstanceReadinessWaiter, FakeRuntimeInstanceReadinessWaiter>();
            services.AddAiKubernetesRuntimeHostProvider(
                options =>
                {
                    options.Enabled = true;
                    options.RuntimeImage = "multiplexed-ai-runtime:test";
                    options.ClientMode = AiKubernetesRuntimeHostClientMode.KubernetesSdk;
                });

            using var provider = services.BuildServiceProvider();

            var client = provider.GetRequiredService<IAiKubernetesRuntimeHostClient>();
            var resourceFactory = provider.GetRequiredService<AiKubernetesSdkResourceFactory>();
            var clientFactory = provider.GetRequiredService<IKubernetesClientFactory>();

            Assert.IsType<KubernetesSdkAiKubernetesRuntimeHostClient>(client);
            Assert.NotNull(resourceFactory);
            Assert.IsType<DefaultKubernetesClientFactory>(clientFactory);
        }
    }
}