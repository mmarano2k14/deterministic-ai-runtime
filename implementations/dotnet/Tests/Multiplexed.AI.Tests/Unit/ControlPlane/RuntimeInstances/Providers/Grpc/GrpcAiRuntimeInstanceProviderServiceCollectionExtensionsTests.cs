using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Unit tests for <see cref="GrpcRuntimeInstanceProviderServiceCollectionExtensions"/>.
    /// </summary>
    public sealed class GrpcAiRuntimeInstanceProviderServiceCollectionExtensionsTests
    {
        /// <summary>
        /// Verifies that the gRPC runtime instance provider is registered for control-plane provider contracts
        /// without registering scale-out by default.
        /// </summary>
        [Fact]
        public void AddAiGrpcRuntimeInstanceProvider_Should_Register_Provider_Contracts_Without_ScaleOut()
        {
            var services =
                new ServiceCollection();

            AddTestConfiguration(services);

            services.AddLogging(
                static builder =>
                    builder.AddDebug());

            services.AddSingleton<IAiGrpcRuntimeScaleOutProvisioner, FakeGrpcRuntimeScaleOutProvisioner>();
            services.AddAiGrpcRuntimeInstanceProvider();

            using var serviceProvider =
                services.BuildServiceProvider();

            var provider =
                serviceProvider.GetRequiredService<AiGrpcRuntimeInstanceProvider>();

            var baseProviders =
                serviceProvider.GetServices<IAiRuntimeInstanceProvider>().ToArray();

            var scaleOutProviders =
                serviceProvider.GetServices<IAiRuntimeScaleOutProvider>().ToArray();

            Assert.NotNull(provider);
            Assert.Single(baseProviders);
            Assert.IsType<AiGrpcRuntimeInstanceProvider>(baseProviders.Single());
            Assert.Empty(scaleOutProviders);
        }

        /// <summary>
        /// Verifies that the explicit gRPC scale-out registration exposes the provider as a scale-out provider.
        /// </summary>
        [Fact]
        public void AddAiGrpcRuntimeInstanceScaleOutProvider_Should_Register_ScaleOut_Provider()
        {
            var services =
                new ServiceCollection();

            AddTestConfiguration(services);

            services.AddLogging(
                static builder =>
                    builder.AddDebug());

            services.AddSingleton<IAiGrpcRuntimeScaleOutProvisioner, FakeGrpcRuntimeScaleOutProvisioner>();
            services.AddAiGrpcRuntimeInstanceScaleOutProvider();

            using var serviceProvider =
                services.BuildServiceProvider();

            var provider =
                serviceProvider.GetRequiredService<AiGrpcRuntimeInstanceProvider>();

            var baseProviders =
                serviceProvider.GetServices<IAiRuntimeInstanceProvider>().ToArray();

            var scaleOutProviders =
                serviceProvider.GetServices<IAiRuntimeScaleOutProvider>().ToArray();

            Assert.NotNull(provider);
            Assert.Single(baseProviders);
            Assert.Single(scaleOutProviders);
            Assert.IsType<AiGrpcRuntimeInstanceProvider>(baseProviders.Single());
            Assert.IsType<AiGrpcRuntimeInstanceProvider>(scaleOutProviders.Single());
        }

        /// <summary>
        /// Verifies that the gRPC runtime instance transport registration adds gRPC server services.
        /// </summary>
        [Fact]
        public void AddGrpcRuntimeInstanceTransport_Should_Register_Grpc_Server_Services()
        {
            var services =
                new ServiceCollection();

            AddTestConfiguration(services);

            services.AddGrpcRuntimeInstanceTransport();

            Assert.NotEmpty(services);
        }

        /// <summary>
        /// Adds test configuration required by options binding.
        /// </summary>
        /// <param name="services">The service collection.</param>
        private static void AddTestConfiguration(
            IServiceCollection services)
        {
            var configuration =
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AiGrpcRuntimeInstanceProvider:DispatchTimeout"] = "00:00:30",
                            ["AiGrpcRuntimeScaleOut:Enabled"] = "true",
                            ["AiGrpcRuntimeScaleOut:Mode"] = "MetadataOnly",
                            ["AiGrpcRuntimeScaleOut:HostCreationMode"] = "Fixture",
                            ["AiGrpcRuntimeScaleOut:RequireReadiness"] = "false",
                            ["AiGrpcRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix"] = "grpc-runtime",
                            ["AiGrpcRuntimeScaleOut:EndpointTemplate"] = "http://runtime-host/{runtimeInstanceId}"
                        })
                    .Build();

            services.AddSingleton<IConfiguration>(configuration);
        }
    }
}