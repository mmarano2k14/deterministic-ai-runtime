using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Validates exact failure publication from routed process-pool child lifecycle.
    /// </summary>
    public sealed class RuntimePoolFailureLifecycleBindingTests
    {
        /// <summary>
        /// Verifies that an unexpected A1 exit records only A1 and leaves sibling routes intact.
        /// </summary>
        [Fact]
        public async Task Unexpected_A1_Exit_Should_Record_Only_A1_Failure()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            var routeA1 =
                await RegisterRouteAsync(
                    registry,
                    "runtime-a1",
                    "route-a1",
                    6101);

            await RegisterRouteAsync(
                registry,
                "runtime-a2",
                "route-a2",
                6102);

            await RegisterRouteAsync(
                registry,
                "runtime-a3",
                "route-a3",
                6103);

            var innerA1 =
                new FakeChild(
                    "runtime-a1",
                    ordinal: 1);

            var routedA1 =
                new AiRuntimeProcessPoolRoutedChild(
                    innerA1,
                    registry,
                    routeA1,
                    journal);

            innerA1.ExitUnexpectedly();

            var exit =
                await routedA1.Completion;

            var hostFailures =
                await journal.ListByHostIdAsync(
                    "host-01");

            var routeA1After =
                await ResolveAsync(
                    registry,
                    "runtime-a1");

            var routeA2After =
                await ResolveAsync(
                    registry,
                    "runtime-a2");

            var routeA3After =
                await ResolveAsync(
                    registry,
                    "runtime-a3");

            Assert.Equal(
                AiRuntimeProcessPoolChildExitKind.Unexpected,
                exit.Kind);

            var failure =
                Assert.Single(hostFailures);

            Assert.Equal(
                "runtime-a1",
                failure.RuntimeInstanceId);

            Assert.Equal(
                "route-a1",
                failure.RouteId);

            Assert.Equal(
                AiRuntimePoolFailureScope.RuntimeInstance,
                failure.Scope);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.NotFound,
                routeA1After.Status);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                routeA2After.Status);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                routeA3After.Status);
        }

        /// <summary>
        /// Verifies that failure publication completes before route removal and manager-visible
        /// child completion.
        /// </summary>
        [Fact]
        public async Task Failure_Should_Be_Recorded_Before_Route_Removal_And_Completion()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            var blockingObserver =
                new BlockingFailureObserver(
                    journal);

            var route =
                await RegisterRouteAsync(
                    registry,
                    "runtime-a1",
                    "route-a1",
                    6101);

            var inner =
                new FakeChild(
                    "runtime-a1",
                    ordinal: 1);

            var routed =
                new AiRuntimeProcessPoolRoutedChild(
                    inner,
                    registry,
                    route,
                    blockingObserver);

            inner.ExitUnexpectedly();

            await blockingObserver.Entered;

            var routeWhileRecording =
                await ResolveAsync(
                    registry,
                    "runtime-a1");

            Assert.False(
                routed.Completion.IsCompleted);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                routeWhileRecording.Status);

            blockingObserver.Release();

            await routed.Completion;

            var routeAfterCompletion =
                await ResolveAsync(
                    registry,
                    "runtime-a1");

            var failures =
                await journal.ListByRuntimeInstanceIdAsync(
                    "runtime-a1");

            Assert.Single(failures);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.NotFound,
                routeAfterCompletion.Status);
        }

        /// <summary>
        /// Verifies that requested shutdown does not create a failure observation.
        /// </summary>
        [Fact]
        public async Task Requested_Stop_Should_Not_Record_Failure()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            var route =
                await RegisterRouteAsync(
                    registry,
                    "runtime-a1",
                    "route-a1",
                    6101);

            var routed =
                new AiRuntimeProcessPoolRoutedChild(
                    new FakeChild(
                        "runtime-a1",
                        ordinal: 1),
                    registry,
                    route,
                    journal);

            await routed.StopAsync();
            await routed.Completion;

            var failures =
                await journal.ListByRuntimeInstanceIdAsync(
                    "runtime-a1");

            Assert.Empty(failures);
        }

        /// <summary>
        /// Registers one exact ready route.
        /// </summary>
        private static Task<AiRuntimePoolRouteDescriptor>
            RegisterRouteAsync(
                IAiRuntimePoolRouteRegistry registry,
                string runtimeInstanceId,
                string routeId,
                int port)
        {
            return registry.RegisterAsync(
                new AiRuntimePoolRouteRegistration
                {
                    RouteId = routeId,
                    PoolId = "pool-01",
                    HostId = "host-01",
                    RuntimeInstanceId =
                        runtimeInstanceId,
                    TransportName = "http",
                    TransportEndpoint =
                        string.Concat(
                            "http://127.0.0.1:",
                            port)
                });
        }

        /// <summary>
        /// Resolves one exact test route.
        /// </summary>
        private static Task<AiRuntimePoolRouteResolutionResult>
            ResolveAsync(
                IAiRuntimePoolRouteRegistry registry,
                string runtimeInstanceId)
        {
            return registry.ResolveAsync(
                new AiRuntimePoolRouteResolutionRequest
                {
                    PoolId = "pool-01",
                    HostId = "host-01",
                    RuntimeInstanceId =
                        runtimeInstanceId,
                    TransportName = "http"
                });
        }

        /// <summary>
        /// Provides one controllable child process.
        /// </summary>
        private sealed class FakeChild :
            IAiRuntimeProcessPoolChild
        {
            private readonly TaskCompletionSource<
                AiRuntimeProcessPoolChildExit> completion =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

            private AiRuntimeProcessPoolChildStatus status =
                AiRuntimeProcessPoolChildStatus.Running;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeChild"/> class.
            /// </summary>
            public FakeChild(
                string runtimeInstanceId,
                int ordinal)
            {
                this.RuntimeInstanceId =
                    runtimeInstanceId;
                this.Ordinal = ordinal;
            }

            /// <inheritdoc />
            public string PoolId => "pool-01";

            /// <inheritdoc />
            public string HostId => "host-01";

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
            /// Completes the child as an unexpected operating-system exit.
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
                        ExitCode = 137
                    });
            }

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                this.status =
                    AiRuntimeProcessPoolChildStatus.Stopped;

                this.completion.TrySetResult(
                    new AiRuntimeProcessPoolChildExit
                    {
                        Kind =
                            AiRuntimeProcessPoolChildExitKind.Requested,
                        ExitCode = 0
                    });

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Blocks failure publication to prove lifecycle ordering.
        /// </summary>
        private sealed class BlockingFailureObserver :
            IAiRuntimePoolFailureObserver
        {
            private readonly IAiRuntimePoolFailureObserver inner;
            private readonly TaskCompletionSource<bool> entered =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> release =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="BlockingFailureObserver"/> class.
            /// </summary>
            public BlockingFailureObserver(
                IAiRuntimePoolFailureObserver inner)
            {
                this.inner = inner;
            }

            /// <summary>
            /// Gets a task completed when observation starts.
            /// </summary>
            public Task Entered => this.entered.Task;

            /// <summary>
            /// Releases the blocked observation.
            /// </summary>
            public void Release()
            {
                this.release.TrySetResult(true);
            }

            /// <inheritdoc />
            public async Task<AiRuntimePoolFailureObservation>
                RecordAsync(
                    AiRuntimePoolFailureObservation observation,
                    CancellationToken cancellationToken = default)
            {
                this.entered.TrySetResult(true);

                await this.release.Task
                    .WaitAsync(cancellationToken);

                return await this.inner
                    .RecordAsync(
                        observation,
                        cancellationToken);
            }
        }
    }
}
