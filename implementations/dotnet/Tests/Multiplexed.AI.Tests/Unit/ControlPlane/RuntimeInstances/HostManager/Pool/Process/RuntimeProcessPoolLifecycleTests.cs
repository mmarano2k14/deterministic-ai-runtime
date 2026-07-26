using System.Collections.Concurrent;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates the deterministic in-memory process-host Runtime Pool Manager lifecycle.
    /// </summary>
    public sealed class RuntimeProcessPoolLifecycleTests
    {
        /// <summary>
        /// Verifies that the initial fixed-size capacity creates three independently identified
        /// sibling runtime processes.
        /// </summary>
        [Fact]
        public async Task EnsureInitialCapacityAsync_Should_Create_Three_Independent_Siblings()
        {
            var factory = new FakeChildFactory();
            var manager = CreateManager(factory);

            var snapshot =
                await manager.EnsureInitialCapacityAsync();

            Assert.Equal(AiRuntimeProcessPoolManagerStatus.Running, snapshot.Status);
            Assert.Equal(3, snapshot.MinimumProcessCount);
            Assert.Equal(3, snapshot.MaximumProcessCount);
            Assert.False(snapshot.IsBelowMinimumCapacity);
            Assert.Equal(3, snapshot.Children.Count);
            Assert.Equal(3, snapshot.Children.Select(child => child.RuntimeInstanceId).Distinct().Count());
            Assert.All(
                snapshot.Children,
                child =>
                {
                    Assert.Equal(manager.Identity.PoolId, child.PoolId);
                    Assert.Equal(manager.Identity.HostId, child.HostId);
                    Assert.Equal(AiRuntimeProcessPoolChildStatus.Running, child.Status);
                });
            Assert.Equal(new[] { 1, 2, 3 }, snapshot.Children.Select(child => child.Ordinal));
            Assert.Equal(3, factory.StartAttemptCount);
        }

        /// <summary>
        /// Verifies that repeated and concurrent reconciliation does not over-create child
        /// processes.
        /// </summary>
        [Fact]
        public async Task EnsureInitialCapacityAsync_Should_Be_Idempotent_Under_Concurrency()
        {
            var factory = new FakeChildFactory();
            var manager = CreateManager(factory);

            await Task.WhenAll(
                Enumerable
                    .Range(0, 12)
                    .Select(
                        _ => manager.EnsureInitialCapacityAsync()));

            var snapshot =
                await manager.GetSnapshotAsync();

            Assert.Equal(3, snapshot.Children.Count);
            Assert.Equal(3, factory.StartAttemptCount);
        }

        /// <summary>
        /// Verifies that a partial startup remains tracked and a later reconciliation fills only
        /// the missing capacity.
        /// </summary>
        [Fact]
        public async Task EnsureInitialCapacityAsync_Should_Preserve_PartialStart_And_Retry_MissingCapacity()
        {
            var factory = new FakeChildFactory
            {
                FailOnStartAttempt = 2
            };

            var manager = CreateManager(factory);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureInitialCapacityAsync());

            var partialSnapshot =
                await manager.GetSnapshotAsync();

            Assert.Equal(AiRuntimeProcessPoolManagerStatus.Degraded, partialSnapshot.Status);
            Assert.True(partialSnapshot.IsBelowMinimumCapacity);
            Assert.Single(partialSnapshot.Children);

            factory.FailOnStartAttempt = null;

            var completedSnapshot =
                await manager.EnsureInitialCapacityAsync();

            Assert.Equal(AiRuntimeProcessPoolManagerStatus.Running, completedSnapshot.Status);
            Assert.False(completedSnapshot.IsBelowMinimumCapacity);
            Assert.Equal(3, completedSnapshot.Children.Count);
            Assert.Equal(4, factory.StartAttemptCount);
        }

        /// <summary>
        /// Verifies that capacity cannot exceed the configured host boundary.
        /// </summary>
        [Fact]
        public async Task EnsureCapacityAsync_Should_Reject_Count_Above_Maximum()
        {
            var manager = CreateManager(new FakeChildFactory());

            var exception =
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                    () => manager.EnsureCapacityAsync(4));

            Assert.Equal("requiredProcessCount", exception.ParamName);
        }

        /// <summary>
        /// Verifies deterministic reverse-start shutdown and idempotent repeated shutdown.
        /// </summary>
        [Fact]
        public async Task StopAsync_Should_Stop_Children_In_ReverseOrder_ExactlyOnce()
        {
            var factory = new FakeChildFactory();
            var manager = CreateManager(factory);

            var running =
                await manager.EnsureInitialCapacityAsync();

            var expectedStopOrder =
                running.Children
                    .OrderByDescending(child => child.Ordinal)
                    .Select(child => child.RuntimeInstanceId)
                    .ToArray();

            await manager.StopAsync();
            await manager.StopAsync();

            var stopped =
                await manager.GetSnapshotAsync();

            Assert.Equal(expectedStopOrder, factory.StopOrder);
            Assert.Equal(AiRuntimeProcessPoolManagerStatus.Stopped, stopped.Status);
            Assert.Empty(stopped.Children);
        }

        /// <summary>
        /// Verifies that failed child shutdown remains first-class and retryable.
        /// </summary>
        [Fact]
        public async Task StopAsync_Should_Retain_FailedChild_For_Retry()
        {
            var factory = new FakeChildFactory
            {
                FailStopForOrdinal = 2
            };

            var manager = CreateManager(factory);

            await manager.EnsureInitialCapacityAsync();

            await Assert.ThrowsAsync<AggregateException>(
                () => manager.StopAsync());

            var failedSnapshot =
                await manager.GetSnapshotAsync();

            var remainingChild =
                Assert.Single(failedSnapshot.Children);

            Assert.Equal(2, remainingChild.Ordinal);
            Assert.Equal(AiRuntimeProcessPoolManagerStatus.Faulted, failedSnapshot.Status);

            factory.FailStopForOrdinal = null;

            await manager.StopAsync();

            var stoppedSnapshot =
                await manager.GetSnapshotAsync();

            Assert.Equal(AiRuntimeProcessPoolManagerStatus.Stopped, stoppedSnapshot.Status);
            Assert.Empty(stoppedSnapshot.Children);
        }

        /// <summary>
        /// Verifies that capacity cannot be recreated after deterministic shutdown.
        /// </summary>
        [Fact]
        public async Task EnsureCapacityAsync_Should_Reject_After_Shutdown()
        {
            var manager = CreateManager(new FakeChildFactory());

            await manager.EnsureInitialCapacityAsync();
            await manager.StopAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.EnsureInitialCapacityAsync());
        }

        /// <summary>
        /// Verifies that the manager rejects a child handle that changes authoritative identity.
        /// </summary>
        [Fact]
        public async Task EnsureCapacityAsync_Should_Reject_Child_With_Different_HostId()
        {
            var factory = new FakeChildFactory
            {
                OverrideHostId = "wrong-host"
            };

            var manager = CreateManager(factory);

            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => manager.EnsureCapacityAsync(1));

            Assert.Contains("HostId", exception.Message, StringComparison.Ordinal);
            Assert.Single(factory.StopOrder);
        }

        /// <summary>
        /// Creates a manager configured for the first fixed-size lifecycle proof.
        /// </summary>
        /// <param name="factory">The deterministic fake child factory.</param>
        /// <returns>The created process pool manager.</returns>
        internal static AiRuntimeProcessPoolManager CreateManager(
            FakeChildFactory factory)
        {
            return new AiRuntimeProcessPoolManager(
                new AiRuntimeProcessPoolOptions
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
                },
                factory);
        }

        /// <summary>
        /// Provides a deterministic child factory for lifecycle and replacement tests.
        /// </summary>
        internal sealed class FakeChildFactory : IAiRuntimeProcessPoolChildFactory
        {
            private readonly ConcurrentQueue<string> stopOrder = new();
            private readonly ConcurrentDictionary<int, FakeChild> children = new();
            private int startAttemptCount;

            /// <summary>
            /// Gets or sets the one-based start attempt that should fail.
            /// </summary>
            public int? FailOnStartAttempt { get; set; }

            /// <summary>
            /// Gets or sets the child ordinal whose stop operation should fail.
            /// </summary>
            public int? FailStopForOrdinal { get; set; }

            /// <summary>
            /// Gets or sets a host identity that replaces the authoritative request identity.
            /// </summary>
            public string? OverrideHostId { get; set; }

            /// <summary>
            /// Gets the number of child start attempts.
            /// </summary>
            public int StartAttemptCount => Volatile.Read(ref this.startAttemptCount);

            /// <summary>
            /// Gets the deterministic child stop order.
            /// </summary>
            public IReadOnlyList<string> StopOrder => this.stopOrder.ToArray();

            /// <summary>
            /// Gets all successfully created children ordered by ordinal.
            /// </summary>
            public IReadOnlyList<FakeChild> Children =>
                this.children.Values.OrderBy(child => child.Ordinal).ToArray();

            /// <summary>
            /// Gets a successfully created child by ordinal.
            /// </summary>
            /// <param name="ordinal">The child ordinal.</param>
            /// <returns>The matching fake child.</returns>
            public FakeChild GetChild(
                int ordinal)
            {
                return this.children[ordinal];
            }

            /// <inheritdoc />
            public Task<IAiRuntimeProcessPoolChild> StartAsync(
                AiRuntimeProcessPoolChildStartRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attempt =
                    Interlocked.Increment(ref this.startAttemptCount);

                if (this.FailOnStartAttempt == attempt)
                {
                    throw new InvalidOperationException(
                        $"Synthetic child startup failure at attempt {attempt}.");
                }

                var child =
                    new FakeChild(
                        request.PoolId,
                        this.OverrideHostId ?? request.HostId,
                        request.RuntimeInstanceId,
                        request.Ordinal,
                        this.stopOrder,
                        () => this.FailStopForOrdinal == request.Ordinal);

                this.children[request.Ordinal] = child;
                return Task.FromResult<IAiRuntimeProcessPoolChild>(child);
            }
        }

        /// <summary>
        /// Provides a deterministic in-memory runtime child handle.
        /// </summary>
        internal sealed class FakeChild : IAiRuntimeProcessPoolChild
        {
            private readonly ConcurrentQueue<string> stopOrder;
            private readonly Func<bool> shouldFailStop;
            private readonly TaskCompletionSource<AiRuntimeProcessPoolChildExit> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private AiRuntimeProcessPoolChildStatus status =
                AiRuntimeProcessPoolChildStatus.Running;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeChild"/> class.
            /// </summary>
            public FakeChild(
                string poolId,
                string hostId,
                string runtimeInstanceId,
                int ordinal,
                ConcurrentQueue<string> stopOrder,
                Func<bool> shouldFailStop)
            {
                this.PoolId = poolId;
                this.HostId = hostId;
                this.RuntimeInstanceId = runtimeInstanceId;
                this.Ordinal = ordinal;
                this.stopOrder = stopOrder;
                this.shouldFailStop = shouldFailStop;
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

            /// <summary>
            /// Completes the child as an unexpected process exit.
            /// </summary>
            /// <param name="exitCode">The synthetic process exit code.</param>
            public void ExitUnexpectedly(
                int exitCode = 1)
            {
                if (this.status == AiRuntimeProcessPoolChildStatus.Stopped ||
                    this.status == AiRuntimeProcessPoolChildStatus.Faulted)
                {
                    return;
                }

                this.status = AiRuntimeProcessPoolChildStatus.Faulted;
                this.completion.TrySetResult(
                    new AiRuntimeProcessPoolChildExit
                    {
                        Kind = AiRuntimeProcessPoolChildExitKind.Unexpected,
                        ExitCode = exitCode
                    });
            }

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (this.status == AiRuntimeProcessPoolChildStatus.Stopped)
                {
                    return Task.CompletedTask;
                }

                this.status = AiRuntimeProcessPoolChildStatus.Stopping;
                this.stopOrder.Enqueue(this.RuntimeInstanceId);

                if (this.shouldFailStop())
                {
                    this.status = AiRuntimeProcessPoolChildStatus.Faulted;

                    throw new InvalidOperationException(
                        $"Synthetic stop failure for child ordinal {this.Ordinal}.");
                }

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
    }
}
