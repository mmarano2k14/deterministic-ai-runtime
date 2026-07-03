using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Unit tests for gRPC runtime provider routing.
    /// </summary>
    public sealed class GrpcRuntimeInstanceProviderRouterTests
    {
        /// <summary>
        /// Verifies that the runtime provider router selects the gRPC dispatch provider when provider metadata is grpc.
        /// </summary>
        [Fact]
        public void GetRequiredProvider_Should_Select_Grpc_Dispatch_Provider_When_ProviderName_Is_Grpc()
        {
            var services =
                CreateServices();

            services.AddAiGrpcRuntimeInstanceProvider();

            using var serviceProvider =
                services.BuildServiceProvider();

            var router =
                new AiRuntimeInstanceProviderRouter(
                    serviceProvider.GetServices<IAiRuntimeInstanceProvider>());

            var descriptor =
                CreateDescriptor(
                    "grpc-runtime-test-1",
                    AiGrpcRuntimeProviderConstants.ProviderName,
                    AiGrpcRuntimeProviderConstants.TransportName,
                    "http://127.0.0.1:50051");

            var resolvedProvider =
                router.GetRequiredProvider<IAiRuntimeInstanceDispatchProvider>(
                    descriptor);

            Assert.IsType<AiGrpcRuntimeInstanceProvider>(resolvedProvider);
        }

        /// <summary>
        /// Verifies that the runtime provider router exposes the gRPC provider name when the provider is registered.
        /// </summary>
        [Fact]
        public void ProviderNames_Should_Contain_Grpc_When_Grpc_Provider_Is_Registered()
        {
            var services =
                CreateServices();

            services.AddAiGrpcRuntimeInstanceProvider();

            using var serviceProvider =
                services.BuildServiceProvider();

            var router =
                new AiRuntimeInstanceProviderRouter(
                    serviceProvider.GetServices<IAiRuntimeInstanceProvider>());

            Assert.Contains(
                AiGrpcRuntimeProviderConstants.ProviderName,
                router.ProviderNames);
        }

        /// <summary>
        /// Verifies that the gRPC provider does not handle descriptors for another provider.
        /// </summary>
        [Fact]
        public void CanHandle_Should_Return_False_When_ProviderName_Is_Not_Grpc()
        {
            var services =
                CreateServices();

            services.AddAiGrpcRuntimeInstanceProvider();

            using var serviceProvider =
                services.BuildServiceProvider();

            var grpcProvider =
                serviceProvider.GetRequiredService<AiGrpcRuntimeInstanceProvider>();

            var descriptor =
                CreateDescriptor(
                    "http-runtime-test-1",
                    "http",
                    "http",
                    "http://127.0.0.1:5000");

            Assert.False(
                grpcProvider.CanHandle(
                    descriptor));
        }

        /// <summary>
        /// Verifies that the runtime provider router does not resolve the gRPC dispatch provider for another provider name.
        /// </summary>
        [Fact]
        public void TryGetProvider_Should_Return_False_When_ProviderName_Is_Not_Grpc()
        {
            var services =
                CreateServices();

            services.AddAiGrpcRuntimeInstanceProvider();

            using var serviceProvider =
                services.BuildServiceProvider();

            var router =
                new AiRuntimeInstanceProviderRouter(
                    serviceProvider.GetServices<IAiRuntimeInstanceProvider>());

            var descriptor =
                CreateDescriptor(
                    "http-runtime-test-1",
                    "http",
                    "http",
                    "http://127.0.0.1:5000");

            var resolved =
                router.TryGetProvider<IAiRuntimeInstanceDispatchProvider>(
                    descriptor,
                    out var provider);

            Assert.False(resolved);
            Assert.Null(provider);
        }

        /// <summary>
        /// Verifies that the runtime provider router selects the gRPC scale-out provider when provider metadata is grpc.
        /// </summary>
        [Fact]
        public void GetRequiredProvider_Should_Select_Grpc_ScaleOut_Provider_When_ProviderName_Is_Grpc()
        {
            var services =
                CreateServices();

            services.AddAiGrpcRuntimeInstanceProvider();

            using var serviceProvider =
                services.BuildServiceProvider();

            var router =
                new AiRuntimeInstanceProviderRouter(
                    serviceProvider.GetServices<IAiRuntimeInstanceProvider>());

            var descriptor =
                CreateDescriptor(
                    "grpc-runtime-test-1",
                    AiGrpcRuntimeProviderConstants.ProviderName,
                    AiGrpcRuntimeProviderConstants.TransportName,
                    "http://127.0.0.1:50051");

            var resolvedProvider =
                router.GetRequiredProvider<IAiRuntimeScaleOutProvider>(
                    descriptor);

            Assert.IsType<AiGrpcRuntimeInstanceProvider>(resolvedProvider);
        }

        /// <summary>
        /// Creates a service collection for gRPC provider router tests.
        /// </summary>
        /// <returns>The service collection.</returns>
        private static ServiceCollection CreateServices()
        {
            var services =
                new ServiceCollection();

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

            services.AddLogging(
                static builder =>
                    builder.AddDebug());

            services.AddSingleton<IAiGrpcRuntimeScaleOutProvisioner, FakeGrpcRuntimeScaleOutProvisioner>();

            return services;
        }

        /// <summary>
        /// Creates a runtime instance capacity descriptor for provider routing tests.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="providerName">The provider name.</param>
        /// <param name="transportName">The transport name.</param>
        /// <param name="endpoint">The transport endpoint.</param>
        /// <returns>The capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId,
            string providerName,
            string transportName,
            string endpoint)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 1,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = 1,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                MaxConcurrentRuns = 1,
                MaxRunSlots = 1,
                AvailableRunSlots = 1,
                EffectiveAvailableRunSlots = 1,
                ReservedRunSlots = 0,
                CanAcceptRun = true,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] =
                        providerName,

                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] =
                        transportName,

                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                        endpoint
                }
            };
        }
    }
}