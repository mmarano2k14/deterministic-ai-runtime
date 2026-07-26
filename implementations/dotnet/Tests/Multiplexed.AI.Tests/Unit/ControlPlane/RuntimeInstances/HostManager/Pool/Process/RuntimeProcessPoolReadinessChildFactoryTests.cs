using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates the provider-neutral readiness boundary around RuntimeInstanceOnly children.
    /// </summary>
    public sealed class RuntimeProcessPoolReadinessChildFactoryTests
    {
        /// <summary>
        /// Verifies that a child is returned only after registry, capacity, and transport readiness.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Return_Child_After_Readiness_Succeeds()
        {
            var request =
                RuntimeProcessPoolRuntimeInstanceProjectionTests.CreateRequest();
            var lease = new TrackingPortLease(5941);
            var child = new FakeChild(request);
            var readinessWaiter = new FakeReadinessWaiter(success: true);

            var factory =
                new RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory(
                    new FakePlanFactory(
                        CreatePlan(request, lease)),
                    new FakeProcessLauncher(child),
                    readinessWaiter);

            var started = await factory.StartAsync(request);

            Assert.Equal(request.RuntimeInstanceId, started.RuntimeInstanceId);
            Assert.Equal(request.RuntimeInstanceId, readinessWaiter.Request?.RuntimeInstanceId);
            Assert.False(lease.Released);

            await started.StopAsync();
            await started.Completion;

            Assert.True(lease.Released);
        }

        /// <summary>
        /// Verifies that failed readiness stops the child and releases its transport port.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Stop_Child_And_Release_Port_When_Readiness_Fails()
        {
            var request =
                RuntimeProcessPoolRuntimeInstanceProjectionTests.CreateRequest();
            var lease = new TrackingPortLease(5942);
            var child = new FakeChild(request);

            var factory =
                new RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory(
                    new FakePlanFactory(
                        CreatePlan(request, lease)),
                    new FakeProcessLauncher(child),
                    new FakeReadinessWaiter(success: false));

            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => factory.StartAsync(request));

            Assert.Contains("did not become ready", exception.Message, StringComparison.Ordinal);
            Assert.Equal(AiRuntimeProcessPoolChildStatus.Stopped, child.Status);
            Assert.True(lease.Released);
        }

        /// <summary>
        /// Creates a deterministic child start plan.
        /// </summary>
        private static AiRuntimeProcessPoolRuntimeInstanceStartPlan CreatePlan(
            AiRuntimeProcessPoolChildStartRequest request,
            IAiRuntimeProcessPoolPortLease lease)
        {
            return new AiRuntimeProcessPoolRuntimeInstanceStartPlan
            {
                PortLease = lease,
                TransportEndpoint = "http://127.0.0.1:5941",
                ProcessOptions = new AiRuntimeProcessPoolChildProcessOptions
                {
                    ExecutablePath = "dotnet",
                    Arguments =
                    {
                        "runtime-host.dll"
                    }
                },
                ReadinessRequest = new AiRuntimeInstanceReadinessRequest
                {
                    ControlPlaneId = "control-plane-01",
                    ExecutionContextSnapshot =
                        RuntimeProcessPoolRuntimeInstanceProjectionTests
                            .CreateExecutionContextSnapshot(),
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    ProviderName = "http",
                    TransportName = "http",
                    RequireTransportEndpoint = true,
                    TransportEndpoint = "http://127.0.0.1:5941"
                }
            };
        }

        /// <summary>
        /// Provides one deterministic start plan.
        /// </summary>
        private sealed class FakePlanFactory :
            IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory
        {
            private readonly AiRuntimeProcessPoolRuntimeInstanceStartPlan plan;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakePlanFactory"/> class.
            /// </summary>
            public FakePlanFactory(
                AiRuntimeProcessPoolRuntimeInstanceStartPlan plan)
            {
                this.plan = plan;
            }

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolRuntimeInstanceStartPlan> CreateAsync(
                AiRuntimeProcessPoolChildStartRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.plan);
            }
        }

        /// <summary>
        /// Returns one deterministic fake child.
        /// </summary>
        private sealed class FakeProcessLauncher :
            IAiRuntimeProcessPoolChildProcessLauncher
        {
            private readonly IAiRuntimeProcessPoolChild child;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeProcessLauncher"/> class.
            /// </summary>
            public FakeProcessLauncher(
                IAiRuntimeProcessPoolChild child)
            {
                this.child = child;
            }

            /// <inheritdoc />
            public Task<IAiRuntimeProcessPoolChild> StartAsync(
                AiRuntimeProcessPoolChildStartRequest request,
                AiRuntimeProcessPoolChildProcessOptions options,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.child);
            }
        }

        /// <summary>
        /// Provides a deterministic readiness result.
        /// </summary>
        private sealed class FakeReadinessWaiter :
            IAiRuntimeInstanceReadinessWaiter
        {
            private readonly bool success;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeReadinessWaiter"/> class.
            /// </summary>
            public FakeReadinessWaiter(
                bool success)
            {
                this.success = success;
            }

            /// <summary>
            /// Gets the observed readiness request.
            /// </summary>
            public AiRuntimeInstanceReadinessRequest? Request { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
                AiRuntimeInstanceReadinessRequest request,
                CancellationToken cancellationToken = default)
            {
                this.Request = request;

                return Task.FromResult(
                    new AiRuntimeInstanceReadinessResult
                    {
                        Success = this.success,
                        ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        ProviderName = request.ProviderName,
                        TransportName = request.TransportName,
                        TransportEndpoint = request.TransportEndpoint,
                        FailureReason = this.success ? null : "synthetic-readiness-failure",
                        TimedOut = !this.success
                    });
            }
        }

        /// <summary>
        /// Provides one deterministic runtime child lifecycle.
        /// </summary>
        private sealed class FakeChild : IAiRuntimeProcessPoolChild
        {
            private readonly TaskCompletionSource<AiRuntimeProcessPoolChildExit> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private AiRuntimeProcessPoolChildStatus status =
                AiRuntimeProcessPoolChildStatus.Running;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeChild"/> class.
            /// </summary>
            public FakeChild(
                AiRuntimeProcessPoolChildStartRequest request)
            {
                this.PoolId = request.PoolId;
                this.HostId = request.HostId;
                this.RuntimeInstanceId = request.RuntimeInstanceId;
                this.Ordinal = request.Ordinal;
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
            public AiRuntimeProcessPoolChildStatus Status => this.status;

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolChildExit> Completion => this.completion.Task;

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                this.status = AiRuntimeProcessPoolChildStatus.Stopped;
                this.completion.TrySetResult(
                    new AiRuntimeProcessPoolChildExit
                    {
                        Kind = AiRuntimeProcessPoolChildExitKind.Requested,
                        ExitCode = 0
                    });

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Tracks deterministic release of one fake port lease.
        /// </summary>
        private sealed class TrackingPortLease : IAiRuntimeProcessPoolPortLease
        {
            private int released;

            /// <summary>
            /// Initializes a new instance of the <see cref="TrackingPortLease"/> class.
            /// </summary>
            public TrackingPortLease(
                int port)
            {
                this.Port = port;
            }

            /// <inheritdoc />
            public int Port { get; }

            /// <summary>
            /// Gets a value indicating whether the lease was released.
            /// </summary>
            public bool Released => Volatile.Read(ref this.released) == 1;

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref this.released, 1);
                return ValueTask.CompletedTask;
            }
        }
    }
}
