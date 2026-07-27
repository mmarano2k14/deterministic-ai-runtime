using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Validates exact route registration, draining, removal, and replacement binding.
    /// </summary>
    public sealed class RuntimePoolRouteLifecycleBindingTests
    {
        /// <summary>
        /// Verifies that a child is returned only after its exact ready route is registered.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Register_Exact_Route_After_Readiness()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var launcher =
                new FakeLauncher();

            var request =
                CreateRequest(ordinal: 1);

            var factory =
                CreateFactory(
                    launcher,
                    registry);

            var child =
                await factory.StartAsync(request);

            var routedChild =
                Assert.IsAssignableFrom<
                    IAiRuntimeProcessPoolRoutedChild>(child);

            var resolution =
                await registry.ResolveAsync(
                    CreateResolutionRequest(
                        request.RuntimeInstanceId,
                        "http"));

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                resolution.Status);

            Assert.Equal(
                routedChild.RouteId,
                resolution.Route?.RouteId);

            Assert.Equal(
                "http://127.0.0.1:6101",
                routedChild.TransportEndpoint);
        }

        /// <summary>
        /// Verifies that stop drains the exact route before stopping the process and removes the
        /// route before completion reaches the manager.
        /// </summary>
        [Fact]
        public async Task StopAsync_Should_Drain_Before_Process_Stop_And_Remove_Before_Completion()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            AiRuntimePoolRouteResolutionStatus? statusDuringStop =
                null;

            var launcher =
                new FakeLauncher(
                    onStopAsync:
                        async child =>
                        {
                            var resolution =
                                await registry.ResolveAsync(
                                    CreateResolutionRequest(
                                        child.RuntimeInstanceId,
                                        "http"));

                            statusDuringStop =
                                resolution.Status;
                        });

            var request =
                CreateRequest(ordinal: 1);

            var child =
                await CreateFactory(
                        launcher,
                        registry)
                    .StartAsync(request);

            await child.StopAsync();
            await child.Completion;

            var afterCompletion =
                await registry.ResolveAsync(
                    CreateResolutionRequest(
                        request.RuntimeInstanceId,
                        "http"));

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Draining,
                statusDuringStop);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.NotFound,
                afterCompletion.Status);
        }

        /// <summary>
        /// Verifies that an unexpected A1 exit removes only A1 while A2 and A3 remain routable.
        /// </summary>
        [Fact]
        public async Task UnexpectedExit_Should_Remove_Only_Exact_Route()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var launcher =
                new FakeLauncher();

            var factory =
                CreateFactory(
                    launcher,
                    registry);

            var childA1 =
                await factory.StartAsync(
                    CreateRequest(ordinal: 1));

            await factory.StartAsync(
                CreateRequest(ordinal: 2));

            await factory.StartAsync(
                CreateRequest(ordinal: 3));

            launcher.GetChild(1)
                .ExitUnexpectedly();

            await childA1.Completion;

            var routeA1 =
                await registry.ResolveAsync(
                    CreateResolutionRequest(
                        "runtime-a1",
                        "http"));

            var routeA2 =
                await registry.ResolveAsync(
                    CreateResolutionRequest(
                        "runtime-a2",
                        "http"));

            var routeA3 =
                await registry.ResolveAsync(
                    CreateResolutionRequest(
                        "runtime-a3",
                        "http"));

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.NotFound,
                routeA1.Status);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                routeA2.Status);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                routeA3.Status);
        }

        /// <summary>
        /// Verifies manager replacement after A1 failure while A2/A3 keep their original route
        /// incarnations and A4 receives a fresh route.
        /// </summary>
        [Fact]
        public async Task Replacement_Should_Preserve_Sibling_Routes_And_Register_Fresh_A4()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var launcher =
                new FakeLauncher();

            var manager =
                new AiRuntimeProcessPoolManager(
                    new AiRuntimeProcessPoolOptions
                    {
                        Enabled = true,
                        PoolId = "pool-01",
                        HostIdPrefix = "host",
                        RuntimeInstanceIdPrefix = "runtime",
                        InitialProcessCount = 3,
                        MinimumProcessCount = 3,
                        MaximumProcessCount = 3,
                        StartupParallelism = 1,
                        ShutdownTimeoutSeconds = 10
                    },
                    CreateFactory(
                        launcher,
                        registry));

            var initial =
                await manager.EnsureInitialCapacityAsync();

            var initialRoutes =
                await registry.ListByHostIdAsync(
                    initial.HostId);

            var routeA2 =
                initialRoutes.Single(
                    route =>
                        route.RuntimeInstanceId ==
                        initial.Children.Single(
                            child => child.Ordinal == 2)
                            .RuntimeInstanceId);

            var routeA3 =
                initialRoutes.Single(
                    route =>
                        route.RuntimeInstanceId ==
                        initial.Children.Single(
                            child => child.Ordinal == 3)
                            .RuntimeInstanceId);

            launcher.GetChild(1)
                .ExitUnexpectedly();

            var recovered =
                await WaitForReplacementAsync(
                    manager,
                    registry);

            var recoveredRoutes =
                await registry.ListByHostIdAsync(
                    recovered.HostId);

            Assert.Equal(3, recoveredRoutes.Count);

            Assert.Equal(
                routeA2.RouteId,
                recoveredRoutes.Single(
                    route =>
                        route.RuntimeInstanceId ==
                        recovered.Children.Single(
                            child => child.Ordinal == 2)
                            .RuntimeInstanceId)
                    .RouteId);

            Assert.Equal(
                routeA3.RouteId,
                recoveredRoutes.Single(
                    route =>
                        route.RuntimeInstanceId ==
                        recovered.Children.Single(
                            child => child.Ordinal == 3)
                            .RuntimeInstanceId)
                    .RouteId);

            var runtimeA4 =
                recovered.Children.Single(
                    child => child.Ordinal == 4);

            var routeA4 =
                recoveredRoutes.Single(
                    route =>
                        route.RuntimeInstanceId ==
                        runtimeA4.RuntimeInstanceId);

            Assert.NotEqual(
                routeA2.RouteId,
                routeA4.RouteId);

            Assert.NotEqual(
                routeA3.RouteId,
                routeA4.RouteId);

            Assert.Equal(
                AiRuntimePoolRouteStatus.Ready,
                routeA4.Status);

            await manager.StopAsync();
        }

        /// <summary>
        /// Creates the route-aware RuntimeInstanceOnly child factory.
        /// </summary>
        private static RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory
            CreateFactory(
                FakeLauncher launcher,
                IAiRuntimePoolRouteRegistry registry)
        {
            return new RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory(
                new FakePlanFactory(),
                launcher,
                new SuccessfulReadinessWaiter(),
                registry);
        }

        /// <summary>
        /// Creates one deterministic child start request.
        /// </summary>
        private static AiRuntimeProcessPoolChildStartRequest
            CreateRequest(
                int ordinal)
        {
            return new AiRuntimeProcessPoolChildStartRequest
            {
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId =
                    string.Concat(
                        "runtime-a",
                        ordinal),
                Ordinal = ordinal
            };
        }

        /// <summary>
        /// Creates one exact route resolution request.
        /// </summary>
        private static AiRuntimePoolRouteResolutionRequest
            CreateResolutionRequest(
                string runtimeInstanceId,
                string transportName)
        {
            return new AiRuntimePoolRouteResolutionRequest
            {
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId =
                    runtimeInstanceId,
                TransportName =
                    transportName
            };
        }

        /// <summary>
        /// Waits for A4 and its exact route.
        /// </summary>
        private static async Task<AiRuntimeProcessPoolSnapshot>
            WaitForReplacementAsync(
                IAiRuntimeProcessPoolManager manager,
                IAiRuntimePoolRouteRegistry registry)
        {
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(3));

            try
            {
                while (true)
                {
                    var snapshot =
                        await manager.GetSnapshotAsync(
                            timeout.Token);

                    var routes =
                        await registry.ListByHostIdAsync(
                            snapshot.HostId,
                            timeout.Token);

                    if (snapshot.Status ==
                            AiRuntimeProcessPoolManagerStatus.Running &&
                        snapshot.Children.Count == 3 &&
                        snapshot.Children.Any(
                            child => child.Ordinal == 4) &&
                        routes.Count == 3 &&
                        routes.Any(
                            route =>
                                route.RuntimeInstanceId ==
                                snapshot.Children.Single(
                                    child => child.Ordinal == 4)
                                    .RuntimeInstanceId))
                    {
                        return snapshot;
                    }

                    await Task.Delay(
                        TimeSpan.FromMilliseconds(10),
                        timeout.Token);
                }
            }
            catch (OperationCanceledException)
                when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The replacement runtime route was not observed.");
            }
        }

        /// <summary>
        /// Creates deterministic start plans from the requested ordinal.
        /// </summary>
        private sealed class FakePlanFactory :
            IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory
        {
            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolRuntimeInstanceStartPlan>
                CreateAsync(
                    AiRuntimeProcessPoolChildStartRequest request,
                    CancellationToken cancellationToken = default)
            {
                var endpoint =
                    string.Concat(
                        "http://127.0.0.1:",
                        6100 + request.Ordinal);

                return Task.FromResult(
                    new AiRuntimeProcessPoolRuntimeInstanceStartPlan
                    {
                        PortLease =
                            new FakePortLease(
                                6100 + request.Ordinal),
                        TransportEndpoint = endpoint,
                        ProcessOptions =
                            new AiRuntimeProcessPoolChildProcessOptions
                            {
                                ExecutablePath = "dotnet",
                                Arguments =
                                {
                                    "runtime-host.dll"
                                }
                            },
                        ReadinessRequest =
                            new AiRuntimeInstanceReadinessRequest
                            {
                                ControlPlaneId =
                                    "control-plane-01",
                                ExecutionContextSnapshot =
                                    RuntimeProcessPoolRuntimeInstanceProjectionTests
                                        .CreateExecutionContextSnapshot(),
                                RuntimeInstanceId =
                                    request.RuntimeInstanceId,
                                ProviderName = "http",
                                TransportName = "http",
                                RequireTransportEndpoint = true,
                                TransportEndpoint = endpoint
                            }
                    });
            }
        }

        /// <summary>
        /// Returns successful deterministic readiness.
        /// </summary>
        private sealed class SuccessfulReadinessWaiter :
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

        /// <summary>
        /// Creates and tracks deterministic fake child processes.
        /// </summary>
        private sealed class FakeLauncher :
            IAiRuntimeProcessPoolChildProcessLauncher
        {
            private readonly Dictionary<int, FakeChild> children =
                new();

            private readonly Func<FakeChild, Task>? onStopAsync;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeLauncher"/> class.
            /// </summary>
            public FakeLauncher(
                Func<FakeChild, Task>? onStopAsync = null)
            {
                this.onStopAsync = onStopAsync;
            }

            /// <summary>
            /// Gets one created child by ordinal.
            /// </summary>
            public FakeChild GetChild(
                int ordinal)
            {
                return this.children[ordinal];
            }

            /// <inheritdoc />
            public Task<IAiRuntimeProcessPoolChild> StartAsync(
                AiRuntimeProcessPoolChildStartRequest request,
                AiRuntimeProcessPoolChildProcessOptions options,
                CancellationToken cancellationToken = default)
            {
                var child =
                    new FakeChild(
                        request,
                        this.onStopAsync);

                this.children[request.Ordinal] =
                    child;

                return Task.FromResult<
                    IAiRuntimeProcessPoolChild>(child);
            }
        }

        /// <summary>
        /// Provides one controllable fake process child.
        /// </summary>
        private sealed class FakeChild :
            IAiRuntimeProcessPoolChild
        {
            private readonly TaskCompletionSource<
                AiRuntimeProcessPoolChildExit> completion =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

            private readonly Func<FakeChild, Task>? onStopAsync;

            private AiRuntimeProcessPoolChildStatus status =
                AiRuntimeProcessPoolChildStatus.Running;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeChild"/> class.
            /// </summary>
            public FakeChild(
                AiRuntimeProcessPoolChildStartRequest request,
                Func<FakeChild, Task>? onStopAsync)
            {
                this.PoolId = request.PoolId;
                this.HostId = request.HostId;
                this.RuntimeInstanceId =
                    request.RuntimeInstanceId;
                this.Ordinal = request.Ordinal;
                this.onStopAsync = onStopAsync;
            }

            /// <inheritdoc />
            public string PoolId { get; }

            /// <inheritdoc />
            public string HostId { get; }

            /// <inheritdoc />
            public string RuntimeInstanceId { get; }

            /// <inheritdoc />
            public int Ordinal { get; }

            /// <inheritdoc />
            public AiRuntimeProcessPoolChildStatus Status =>
                this.status;

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolChildExit> Completion =>
                this.completion.Task;

            /// <summary>
            /// Completes the child as an unexpected failure.
            /// </summary>
            public void ExitUnexpectedly()
            {
                this.status =
                    AiRuntimeProcessPoolChildStatus.Faulted;

                this.completion.TrySetResult(
                    new AiRuntimeProcessPoolChildExit
                    {
                        Kind =
                            AiRuntimeProcessPoolChildExitKind.Unexpected,
                        ExitCode = 1
                    });
            }

            /// <inheritdoc />
            public async Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                if (this.onStopAsync is not null)
                {
                    await this.onStopAsync(this);
                }

                this.status =
                    AiRuntimeProcessPoolChildStatus.Stopped;

                this.completion.TrySetResult(
                    new AiRuntimeProcessPoolChildExit
                    {
                        Kind =
                            AiRuntimeProcessPoolChildExitKind.Requested,
                        ExitCode = 0
                    });
            }
        }

        /// <summary>
        /// Provides one deterministic transport port lease.
        /// </summary>
        private sealed class FakePortLease :
            IAiRuntimeProcessPoolPortLease
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="FakePortLease"/> class.
            /// </summary>
            public FakePortLease(
                int port)
            {
                this.Port = port;
            }

            /// <inheritdoc />
            public int Port { get; }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
