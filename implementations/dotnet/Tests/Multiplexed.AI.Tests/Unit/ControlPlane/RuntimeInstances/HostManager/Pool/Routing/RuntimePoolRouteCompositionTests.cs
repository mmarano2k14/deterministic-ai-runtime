using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http;
using Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Tests.Fixtures;

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

            services.AddSingleton<
                IAiRuntimeRunExecutionIndex,
                FakeRuntimeRunExecutionIndex>();

            services.AddSingleton<
                IAiSharedRunOwnershipResolver,
                FakeSharedRunOwnershipResolver>();

            services.AddSingleton<
                IAiRuntimeExecutionRecoveryTransitionService,
                FakeRuntimeExecutionRecoveryTransitionService>();

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

            var failureObserver =
                provider.GetRequiredService<
                    IAiRuntimePoolFailureObserver>();

            var failureReader =
                provider.GetRequiredService<
                    IAiRuntimePoolFailureReader>();

            Assert.IsType<
                AiRuntimePoolFailureSafetyObserver>(
                    failureObserver);

            Assert.IsType<
                InMemoryAiRuntimePoolFailureJournal>(
                    failureReader);

            var safetyRegistry =
                provider.GetRequiredService<
                    IAiRuntimePoolCapacitySafetyRegistry>();

            var safetyWriter =
                provider.GetRequiredService<
                    IAiRuntimePoolCapacitySafetyWriter>();

            var safetyReader =
                provider.GetRequiredService<
                    IAiRuntimePoolCapacitySafetyReader>();

            Assert.IsType<
                InMemoryAiRuntimePoolCapacitySafetyRegistry>(
                    safetyRegistry);

            Assert.Same(
                safetyRegistry,
                safetyWriter);

            Assert.Same(
                safetyRegistry,
                safetyReader);

            Assert.IsType<
                InMemoryAiRuntimePoolRouteRegistry>(
                    provider.GetRequiredService<
                        IAiRuntimePoolRouteRegistry>());

            Assert.IsType<
                AiRuntimePoolRouteForwarder>(
                    provider.GetRequiredService<
                        IAiRuntimePoolRouteForwarder>());

            Assert.IsType<
                AiRuntimePoolAssignedWorkEnumerator>(
                    provider.GetRequiredService<
                        IAiRuntimePoolAssignedWorkEnumerator>());

            Assert.IsType<
                InMemoryAiRuntimePoolRecoveryClaimStore>(
                    provider.GetRequiredService<
                        IAiRuntimePoolRecoveryClaimStore>());

            Assert.IsType<
                AiRuntimePoolRecoveryClaimCoordinator>(
                    provider.GetRequiredService<
                        IAiRuntimePoolRecoveryClaimCoordinator>());

            Assert.IsType<
                AiRuntimePoolClaimedRecoveryExecutor>(
                    provider.GetRequiredService<
                        IAiRuntimePoolClaimedRecoveryExecutor>());

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
        /// Provides deterministic ownership resolution for composition validation.
        /// </summary>
        private sealed class FakeSharedRunOwnershipResolver :
            IAiSharedRunOwnershipResolver
        {
            /// <inheritdoc />
            public Task<AiSharedRunOwnershipResolutionResult>
                ResolveAsync(
                    AiSharedRunOwnershipResolutionRequest request,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiSharedRunOwnershipResolutionResult
                    {
                        Resolved = false,
                        RuntimeInstanceId =
                            request.RuntimeInstanceId,
                        LocalRunId =
                            request.LocalRunId,
                        ExecutionId =
                            request.ExecutionId,
                        TenantId =
                            request.TenantId,
                        TenantGroupId =
                            request.TenantGroupId,
                        SharedRunId =
                            request.SharedRunId,
                        CanRecover = false,
                        Reason =
                            "composition-only"
                    });
            }
        }

        /// <summary>
        /// Provides deterministic transition behavior for composition validation.
        /// </summary>
        private sealed class FakeRuntimeExecutionRecoveryTransitionService :
            IAiRuntimeExecutionRecoveryTransitionService
        {
            /// <inheritdoc />
            public Task<AiRuntimeExecutionRecoveryTransitionResult>
                ApplyAsync(
                    AiRuntimeExecutionRecoveryTransitionRequest request,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeExecutionRecoveryTransitionResult
                    {
                        Accepted = false,
                        Changed = false,
                        RuntimeInstanceId =
                            request.Ownership.RuntimeInstanceId,
                        LocalRunId =
                            request.Ownership.LocalRunId,
                        ExecutionId =
                            request.Ownership.ExecutionId,
                        SharedRunId =
                            request.Ownership.SharedRunId,
                        Action = "none",
                        Reason =
                            "composition-only"
                    });
            }
        }

        /// <summary>
        /// Provides an empty durable runtime-run index for composition validation.
        /// </summary>
        private sealed class FakeRuntimeRunExecutionIndex :
            RuntimeRunExecutionIndexTestFixture
        {
            /// <inheritdoc />
            public override Task RegisterQueuedAsync(
                AiRuntimeRunExecutionIndexEntry entry,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override Task MarkStartedAsync(
                string runId,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override Task MarkCompletedAsync(
                string runId,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override Task MarkFailedAsync(
                string runId,
                string? executionId,
                string failureReason,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override Task MarkCancelledAsync(
                string runId,
                string? executionId,
                string? reason,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override Task<bool> MarkRequeuedForRecoveryAsync(
                string runId,
                string executionId,
                string reason,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public override Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
                string runId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<
                    AiRuntimeRunExecutionIndexEntry?>(
                    null);
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>>
                ListUnfinishedByRuntimeInstanceAsync(
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default)
            {
                return EmptyAsync();
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>>
                ListUnfinishedAsync(
                    CancellationToken cancellationToken = default)
            {
                return EmptyAsync();
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>>
                ListRecoverableByRuntimeInstanceAsync(
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default)
            {
                return EmptyAsync();
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>>
                ListRecoverableAsync(
                    CancellationToken cancellationToken = default)
            {
                return EmptyAsync();
            }

            /// <summary>
            /// Returns one empty durable index result.
            /// </summary>
            private static Task<IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>>
                EmptyAsync()
            {
                IReadOnlyList<
                    AiRuntimeRunExecutionIndexEntry> entries =
                    Array.Empty<
                        AiRuntimeRunExecutionIndexEntry>();

                return Task.FromResult(entries);
            }
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
