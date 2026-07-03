using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
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
    /// Integration-style unit tests for gRPC runtime scale-out provider routing.
    /// </summary>
    public sealed class GrpcRuntimeScaleOutRouterIntegrationTests
    {
        /// <summary>
        /// Verifies that the provider router resolves the gRPC scale-out provider and provisions metadata-only capacity.
        /// </summary>
        [Fact]
        public async Task Router_Should_Resolve_Grpc_ScaleOut_Provider_And_Provision_MetadataOnly_Capacity()
        {
            var registry =
                new FakeRuntimeInstanceRegistry();

            var capacityStore =
                new FakeRuntimeInstanceCapacityStore();

            var services =
                CreateServices(registry, capacityStore);

            services.AddAiGrpcRuntimeInstanceProvider();

            using var serviceProvider =
                services.BuildServiceProvider();

            var router =
                new AiRuntimeInstanceProviderRouter(
                    serviceProvider.GetServices<IAiRuntimeInstanceProvider>());

            var descriptor =
                CreateDescriptor();

            var scaleOutProvider =
                router.GetRequiredProvider<IAiRuntimeScaleOutProvider>(
                    descriptor);

            var result =
                await scaleOutProvider
                    .RequestScaleOutAsync(
                        new AiRuntimeScaleOutProviderRequest
                        {
                            RequestId = "grpc-router-scaleout-request-1",
                            ControlPlaneId = "control-plane-test-1",
                            SharedRunId = "shared-run-test-1",
                            TenantId = "tenant-a",
                            TenantGroupId = "group-a",
                            RequestedTargetInstanceCount = 1,
                            CurrentInstanceCount = 0,
                            WorkerCountPerInstance = 2,
                            MaxConcurrentRunsPerInstance = 3,
                            LocalQueueCapacity = 40,
                            PreferDedicatedCapacity = true,
                            AllowSharedFallback = false,
                            IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated,
                            ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create()
                        })
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal("control-plane-test-1:grpc-runtime-1", result.RuntimeInstanceId);
            Assert.Single(registry.RuntimeInstances);
            Assert.Single(capacityStore.PublishedDescriptors);

            var publishedDescriptor =
                capacityStore.PublishedDescriptors.Single();

            Assert.Equal("control-plane-test-1:grpc-runtime-1", publishedDescriptor.RuntimeInstanceId);
            Assert.Equal("tenant-a", publishedDescriptor.TenantId);
            Assert.Equal("group-a", publishedDescriptor.TenantGroupId);
            Assert.Equal(AiGrpcRuntimeProviderConstants.ProviderName, publishedDescriptor.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal(AiGrpcRuntimeProviderConstants.TransportName, publishedDescriptor.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);
            Assert.Equal("http://127.0.0.1:50051/control-plane-test-1:grpc-runtime-1", publishedDescriptor.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint]);
        }

        /// <summary>
        /// Verifies that the provider router does not resolve the gRPC scale-out provider for another provider name.
        /// </summary>
        [Fact]
        public void Router_Should_Not_Resolve_Grpc_ScaleOut_Provider_When_ProviderName_Is_Not_Grpc()
        {
            var registry =
                new FakeRuntimeInstanceRegistry();

            var capacityStore =
                new FakeRuntimeInstanceCapacityStore();

            var services =
                CreateServices(registry, capacityStore);

            services.AddAiGrpcRuntimeInstanceProvider();

            using var serviceProvider =
                services.BuildServiceProvider();

            var router =
                new AiRuntimeInstanceProviderRouter(
                    serviceProvider.GetServices<IAiRuntimeInstanceProvider>());

            var descriptor =
                CreateDescriptor(
                    "http",
                    "http",
                    "http://127.0.0.1:5000");

            var resolved =
                router.TryGetProvider<IAiRuntimeScaleOutProvider>(
                    descriptor,
                    out var provider);

            Assert.False(resolved);
            Assert.Null(provider);
            Assert.Empty(registry.RuntimeInstances);
            Assert.Empty(capacityStore.PublishedDescriptors);
        }

        /// <summary>
        /// Creates a configured service collection for gRPC router scale-out tests.
        /// </summary>
        /// <param name="registry">The fake runtime instance registry.</param>
        /// <param name="capacityStore">The fake runtime instance capacity store.</param>
        /// <returns>The service collection.</returns>
        private static ServiceCollection CreateServices(
            FakeRuntimeInstanceRegistry registry,
            FakeRuntimeInstanceCapacityStore capacityStore)
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
                            ["AiGrpcRuntimeScaleOut:Mode"] = AiGrpcRuntimeScaleOutModes.MetadataOnly,
                            ["AiGrpcRuntimeScaleOut:EndpointTemplate"] = "http://127.0.0.1:50051/{runtimeInstanceId}",
                            ["AiGrpcRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix"] = "grpc-runtime",
                            ["AiGrpcRuntimeScaleOut:RequireReadiness"] = "false"
                        })
                    .Build();

            services.AddSingleton<IConfiguration>(configuration);

            services.AddLogging(
                static builder =>
                    builder.AddDebug());

            services.AddSingleton(registry);
            services.AddSingleton<IAiRuntimeInstanceRegistry>(registry);
            services.AddSingleton(capacityStore);
            services.AddSingleton<IAiRuntimeInstanceCapacityStore>(capacityStore);
            services.AddSingleton<IAiRuntimeHostManager, FakeRuntimeHostManager>();
            services.AddSingleton<IAiRuntimeInstanceReadinessWaiter, FakeRuntimeInstanceReadinessWaiter>();
            services.AddSingleton<IAiTenantRuntimeSettingsProvider>(
                new FakeTenantRuntimeSettingsProvider
                {
                    WorkerCountPerInstance = 2,
                    MaxConcurrentRunsPerInstance = 3,
                    LocalQueueCapacity = 40,
                    RuntimeInstanceIdPrefix = "grpc-runtime"
                });

            services.Configure<AiGrpcRuntimeScaleOutOptions>(
                options =>
                {
                    options.Enabled = true;
                    options.Mode = AiGrpcRuntimeScaleOutModes.MetadataOnly;
                    options.EndpointTemplate = "http://127.0.0.1:50051/{runtimeInstanceId}";
                    options.DefaultRuntimeInstanceIdPrefix = "grpc-runtime";
                    options.RequireReadiness = false;
                });

            return services;
        }

        /// <summary>
        /// Creates a runtime instance descriptor for router resolution.
        /// </summary>
        /// <param name="providerName">The provider name.</param>
        /// <param name="transportName">The transport name.</param>
        /// <param name="endpoint">The transport endpoint.</param>
        /// <returns>The runtime instance capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string providerName = AiGrpcRuntimeProviderConstants.ProviderName,
            string transportName = AiGrpcRuntimeProviderConstants.TransportName,
            string endpoint = "http://127.0.0.1:50051")
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = "grpc-runtime-router-test-1",
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