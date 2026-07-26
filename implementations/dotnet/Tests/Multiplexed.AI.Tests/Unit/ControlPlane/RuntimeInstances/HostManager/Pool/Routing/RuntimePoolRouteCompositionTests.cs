using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http;
using Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Validates opt-in composition of the local runtime route registry.
    /// </summary>
    public sealed class RuntimePoolRouteCompositionTests
    {
        /// <summary>
        /// Verifies that process-pool composition registers one route-aware production chain.
        /// </summary>
        [Fact]
        public void AddAiRuntimeProcessPool_Should_Register_Route_Registry()
        {
            var services =
                new ServiceCollection();

            services.AddSingleton<
                IAiRuntimeInstanceReadinessWaiter,
                FakeReadinessWaiter>();

            services.AddAiRuntimeProcessPool(
                new AiRuntimeProcessPoolOptions
                {
                    Enabled = true,
                    PoolId = "pool-01",
                    HostIdPrefix = "host",
                    RuntimeInstanceIdPrefix = "runtime",
                    InitialProcessCount = 1,
                    MinimumProcessCount = 1,
                    MaximumProcessCount = 1,
                    StartupParallelism = 1,
                    ShutdownTimeoutSeconds = 10
                },
                new AiRuntimeProcessPoolRuntimeInstanceOptions
                {
                    RuntimeHostAssemblyPath =
                        "runtime-host.dll",
                    ControlPlaneId =
                        "control-plane-01",
                    ExecutionContextSnapshot =
                        RuntimeProcessPoolRuntimeInstanceProjectionTests
                            .CreateExecutionContextSnapshot(),
                    BasePort = 6100,
                    MaxPort = 6110,
                    ProviderName = "http",
                    TransportName = "http"
                });

            using var provider =
                services.BuildServiceProvider(
                    new ServiceProviderOptions
                    {
                        ValidateOnBuild = true,
                        ValidateScopes = true
                    });

            Assert.IsType<
                InMemoryAiRuntimePoolRouteRegistry>(
                    provider.GetRequiredService<
                        IAiRuntimePoolRouteRegistry>());

            Assert.IsType<
                AiRuntimePoolRouteForwarder>(
                    provider.GetRequiredService<
                        IAiRuntimePoolRouteForwarder>());

            Assert.IsType<
                AiRuntimePoolHttpTransportForwarder>(
                    provider.GetRequiredService<
                        IAiRuntimePoolHttpTransportForwarder>());

            Assert.IsType<
                AiRuntimePoolHttpCommandHandler>(
                    provider.GetRequiredService<
                        IAiRuntimePoolHttpCommandHandler>());

            Assert.IsType<
                AiRuntimePoolGrpcClientFactory>(
                    provider.GetRequiredService<
                        IAiRuntimePoolGrpcClientFactory>());

            Assert.IsType<
                AiRuntimePoolGrpcTransportForwarder>(
                    provider.GetRequiredService<
                        IAiRuntimePoolGrpcTransportForwarder>());

            Assert.IsType<
                AiRuntimePoolGrpcCommandHandler>(
                    provider.GetRequiredService<
                        IAiRuntimePoolGrpcCommandHandler>());

            Assert.IsType<
                RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory>(
                    provider.GetRequiredService<
                        IAiRuntimeProcessPoolChildFactory>());
        }

        /// <summary>
        /// Provides deterministic readiness for composition validation.
        /// </summary>
        private sealed class FakeReadinessWaiter :
            IAiRuntimeInstanceReadinessWaiter
        {
            /// <inheritdoc />
            public Task<AiRuntimeInstanceReadinessResult>
                WaitUntilReadyAsync(
                    AiRuntimeInstanceReadinessRequest request,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeInstanceReadinessResult
                    {
                        Success = true,
                        ExecutionContextSnapshot =
                            request.ExecutionContextSnapshot,
                        RuntimeInstanceId =
                            request.RuntimeInstanceId,
                        ProviderName =
                            request.ProviderName,
                        TransportName =
                            request.TransportName,
                        TransportEndpoint =
                            request.TransportEndpoint
                    });
            }
        }
    }
}
