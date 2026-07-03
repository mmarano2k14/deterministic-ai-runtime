using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Unit tests for gRPC runtime instance provider scale-out behavior.
    /// </summary>
    public sealed class GrpcRuntimeInstanceProviderScaleOutTests
    {
        /// <summary>
        /// Verifies that the gRPC runtime provider delegates scale-out requests to the gRPC scale-out provisioner.
        /// </summary>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Delegate_To_Grpc_ScaleOut_Provisioner()
        {
            var provisioner =
                new CapturingGrpcRuntimeScaleOutProvisioner();

            var provider =
                new AiGrpcRuntimeInstanceProvider(
                    NullLogger<AiGrpcRuntimeInstanceProvider>.Instance,
                    Options.Create(new AiGrpcRuntimeInstanceProviderOptions()),
                    provisioner);

            var request =
                new AiRuntimeScaleOutProviderRequest
                {
                    RequestId = "grpc-provider-scaleout-request-1",
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
                };

            var result =
                await provider
                    .RequestScaleOutAsync(request)
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal("grpc-runtime-provider-scaleout-test-1", result.RuntimeInstanceId);
            Assert.Single(provisioner.Requests);
            Assert.Same(request, provisioner.Requests.Single());
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

            var scaleOutProvider =
                serviceProvider.GetRequiredService<IAiRuntimeScaleOutProvider>();

            Assert.NotNull(provider);
            Assert.IsType<AiGrpcRuntimeInstanceProvider>(scaleOutProvider);
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

        /// <summary>
        /// Capturing gRPC runtime scale-out provisioner used by provider delegation tests.
        /// </summary>
        private sealed class CapturingGrpcRuntimeScaleOutProvisioner : IAiGrpcRuntimeScaleOutProvisioner
        {
            /// <summary>
            /// Gets the captured scale-out requests.
            /// </summary>
            public List<AiRuntimeScaleOutProviderRequest> Requests { get; } = [];

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> ProvisionAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                Requests.Add(request);

                return Task.FromResult(
                    new AiRuntimeScaleOutProviderResult
                    {
                        Success = true,
                        Rejected = false,
                        RuntimeInstanceId = "grpc-runtime-provider-scaleout-test-1",
                        ProviderOperationId = "grpc-provider-scaleout-delegated"
                    });
            }
        }
    }
}