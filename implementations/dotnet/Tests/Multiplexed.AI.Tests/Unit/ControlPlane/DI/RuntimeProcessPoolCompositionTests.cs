using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.DI
{
    /// <summary>
    /// Validates additive, opt-in production composition of the process-host Runtime Pool Manager.
    /// </summary>
    public sealed class RuntimeProcessPoolCompositionTests
    {
        /// <summary>
        /// Verifies that the normal control-plane registration does not enable the new process pool.
        /// </summary>
        [Fact]
        public void AddAiControlPlane_Should_Not_Register_ProcessPool_By_Default()
        {
            var services = new ServiceCollection();

            services.AddAiControlPlane();

            Assert.DoesNotContain(
                services,
                descriptor =>
                    descriptor.ServiceType ==
                    typeof(IAiRuntimeProcessPoolManager));

            Assert.DoesNotContain(
                services,
                descriptor =>
                    descriptor.ImplementationType ==
                    typeof(AiRuntimeProcessPoolHostedService));
        }

        /// <summary>
        /// Verifies the complete opt-in process-pool production registration chain.
        /// </summary>
        [Fact]
        public void AddAiRuntimeProcessPool_Should_Register_Production_Chain()
        {
            var services = new ServiceCollection();

            services.AddSingleton<
                IAiRuntimeInstanceReadinessWaiter,
                FakeReadinessWaiter>();

            services.AddSingleton<
                IAiSharedRunOwnershipResolver,
                FakeSharedRunOwnershipResolver>();

            services.AddSingleton<
                IAiRuntimeExecutionRecoveryTransitionService,
                FakeRecoveryTransitionService>();

            services.AddAiRuntimeProcessPool(
                CreatePoolOptions(),
                CreateRuntimeInstanceOptions());

            using var serviceProvider =
                services.BuildServiceProvider(
                    new ServiceProviderOptions
                    {
                        ValidateOnBuild = true,
                        ValidateScopes = true
                    });

            var manager =
                serviceProvider.GetRequiredService<
                    IAiRuntimeProcessPoolManager>();

            var childFactory =
                serviceProvider.GetRequiredService<
                    IAiRuntimeProcessPoolChildFactory>();

            var hostedServices =
                serviceProvider.GetServices<IHostedService>().ToArray();

            var processCreationExecutor =
                serviceProvider.GetRequiredService<
                    IAiRuntimePoolProcessCreationExecutor>();

            Assert.Equal("pool-shared-01", manager.Identity.PoolId);
            Assert.NotNull(processCreationExecutor);
            Assert.IsType<
                RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory>(
                    childFactory);

            Assert.Single(
                hostedServices,
                service =>
                    service is AiRuntimeProcessPoolHostedService);
        }

        /// <summary>
        /// Verifies that explicit process-pool composition rejects disabled configuration.
        /// </summary>
        [Fact]
        public void AddAiRuntimeProcessPool_Should_Reject_Disabled_Options()
        {
            var services = new ServiceCollection();
            var options = CreatePoolOptions();
            options.Enabled = false;

            var exception =
                Assert.Throws<InvalidOperationException>(
                    () =>
                        services.AddAiRuntimeProcessPool(
                            options,
                            CreateRuntimeInstanceOptions()));

            Assert.Contains(
                "Enabled",
                exception.Message,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Verifies that a second process-pool composition cannot silently replace the first
        /// authoritative configuration.
        /// </summary>
        [Fact]
        public void AddAiRuntimeProcessPool_Should_Reject_Duplicate_Registration()
        {
            var services = new ServiceCollection();

            services.AddSingleton<
                IAiRuntimeInstanceReadinessWaiter,
                FakeReadinessWaiter>();

            services.AddAiRuntimeProcessPool(
                CreatePoolOptions(),
                CreateRuntimeInstanceOptions());

            var exception =
                Assert.Throws<InvalidOperationException>(
                    () =>
                        services.AddAiRuntimeProcessPool(
                            CreatePoolOptions(),
                            CreateRuntimeInstanceOptions()));

            Assert.Contains(
                "already been registered",
                exception.Message,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Creates valid fixed-size process-pool options.
        /// </summary>
        private static AiRuntimeProcessPoolOptions CreatePoolOptions()
        {
            return new AiRuntimeProcessPoolOptions
            {
                Enabled = true,
                PoolId = "pool-shared-01",
                HostIdPrefix = "runtime-pool-host",
                RuntimeInstanceIdPrefix = "runtime-pool",
                InitialProcessCount = 3,
                MinimumProcessCount = 3,
                MaximumProcessCount = 3,
                StartupParallelism = 1,
                ShutdownTimeoutSeconds = 30
            };
        }

        /// <summary>
        /// Creates valid RuntimeInstanceOnly child options.
        /// </summary>
        private static AiRuntimeProcessPoolRuntimeInstanceOptions
            CreateRuntimeInstanceOptions()
        {
            return new AiRuntimeProcessPoolRuntimeInstanceOptions
            {
                RuntimeHostAssemblyPath = "runtime-host.dll",
                ControlPlaneId = "control-plane-01",
                ExecutionContextSnapshot =
                    Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances
                        .HostManager.Pool.Process
                        .RuntimeProcessPoolRuntimeInstanceProjectionTests
                        .CreateExecutionContextSnapshot(),
                BasePort = 5900,
                MaxPort = 5999,
                ProviderName = "http",
                TransportName = "http"
            };
        }

        /// <summary>
        /// Provides a deterministic readiness waiter for service-provider validation.
        /// </summary>
        private sealed class FakeReadinessWaiter :
            IAiRuntimeInstanceReadinessWaiter
        {
            /// <inheritdoc />
            public Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
                AiRuntimeInstanceReadinessRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeInstanceReadinessResult
                    {
                        Success = true,
                        ExecutionContextSnapshot =
                            request.ExecutionContextSnapshot,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        ProviderName = request.ProviderName,
                        TransportName = request.TransportName,
                        TransportEndpoint = request.TransportEndpoint
                    });
            }
        }

        private sealed class FakeSharedRunOwnershipResolver :
            IAiSharedRunOwnershipResolver
        {
            public Task<AiSharedRunOwnershipResolutionResult> ResolveAsync(
                AiSharedRunOwnershipResolutionRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Composition-only dependency.");
            }
        }

        private sealed class FakeRecoveryTransitionService :
            IAiRuntimeExecutionRecoveryTransitionService
        {
            public Task<AiRuntimeExecutionRecoveryTransitionResult> ApplyAsync(
                AiRuntimeExecutionRecoveryTransitionRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Composition-only dependency.");
            }
        }
    }
}
