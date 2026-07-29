using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Validates bounded and idempotent Step 7D process creation through the existing
    /// Runtime Pool Manager authority.
    /// </summary>
    public sealed class AiRuntimePoolProcessCreationExecutorTests
    {
        /// <summary>
        /// Verifies that one request increases the exact selected host process count by
        /// one and reports the fresh runtime identity.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Create_One_Process_In_Exact_Host()
        {
            var manager =
                new FakeRuntimeProcessPoolManager(
                    poolId: "pool-step-7d",
                    hostId: "host-step-7d",
                    initialProcessCount: 1,
                    maximumProcessCount: 3);

            var executor =
                new AiRuntimePoolProcessCreationExecutor(manager);

            var result =
                await executor.ExecuteAsync(
                    CreateRequest("request-step-7d-a"),
                    CreateCandidate());

            Assert.Equal(
                AiRuntimePoolProcessCreationStatus.Created,
                result.Status);
            Assert.Equal(1, result.ProcessCountBefore);
            Assert.Equal(2, result.ProcessCountAfter);
            Assert.Equal(3, result.MaximumProcessCount);
            Assert.Equal(
                new[] { "runtime-step-7d-2" },
                result.CreatedRuntimeInstanceIds);
            Assert.Equal(1, manager.EnsureCapacityCallCount);
            Assert.Equal(2, manager.CurrentProcessCount);
        }

        /// <summary>
        /// Verifies that replaying the same provider request does not create another
        /// child process.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Deduplicate_Same_RequestId()
        {
            var manager =
                new FakeRuntimeProcessPoolManager(
                    poolId: "pool-step-7d",
                    hostId: "host-step-7d",
                    initialProcessCount: 1,
                    maximumProcessCount: 3);

            var executor =
                new AiRuntimePoolProcessCreationExecutor(manager);
            var request = CreateRequest("request-step-7d-duplicate");
            var candidate = CreateCandidate();

            var first =
                await executor.ExecuteAsync(request, candidate);
            var duplicate =
                await executor.ExecuteAsync(request, candidate);

            Assert.True(first.IsCreated);
            Assert.True(duplicate.IsDeduplicated);
            Assert.Empty(duplicate.CreatedRuntimeInstanceIds);
            Assert.Equal(1, manager.EnsureCapacityCallCount);
            Assert.Equal(2, manager.CurrentProcessCount);
        }

        /// <summary>
        /// Verifies that distinct concurrent requests may each create one process but
        /// can never exceed the exact manager maximum.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Bound_Distinct_Concurrent_Requests()
        {
            const int contenderCount = 32;
            const int expectedCreatedCount = 3;

            var manager =
                new FakeRuntimeProcessPoolManager(
                    poolId: "pool-step-7d",
                    hostId: "host-step-7d",
                    initialProcessCount: 1,
                    maximumProcessCount: 4);

            var executor =
                new AiRuntimePoolProcessCreationExecutor(manager);
            var candidate = CreateCandidate();

            var startGate =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var contenders =
                Enumerable
                    .Range(0, contenderCount)
                    .Select(
                        index =>
                            Task.Run(
                                async () =>
                                {
                                    await startGate.Task;
                                    return await executor.ExecuteAsync(
                                        CreateRequest(
                                            string.Concat(
                                                "request-step-7d-concurrent-",
                                                index)),
                                        candidate);
                                }))
                    .ToArray();

            startGate.SetResult(true);

            var results =
                await Task.WhenAll(contenders);

            Assert.Equal(
                expectedCreatedCount,
                results.Count(
                    result => result.IsCreated));

            Assert.Equal(
                contenderCount - expectedCreatedCount,
                results.Count(
                    result =>
                        result.Status ==
                        AiRuntimePoolProcessCreationStatus
                            .CapacityUnavailable));

            Assert.Equal(4, manager.CurrentProcessCount);
            Assert.Equal(
                expectedCreatedCount,
                manager.EnsureCapacityCallCount);

            Assert.Equal(
                expectedCreatedCount,
                results
                    .Where(result => result.IsCreated)
                    .SelectMany(
                        result =>
                            result.CreatedRuntimeInstanceIds)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }

        /// <summary>
        /// Verifies that a full host returns explicit capacity unavailability without
        /// invoking process creation.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Not_Create_When_Host_Is_Full()
        {
            var manager =
                new FakeRuntimeProcessPoolManager(
                    poolId: "pool-step-7d",
                    hostId: "host-step-7d",
                    initialProcessCount: 2,
                    maximumProcessCount: 2);

            var executor =
                new AiRuntimePoolProcessCreationExecutor(manager);

            var result =
                await executor.ExecuteAsync(
                    CreateRequest("request-step-7d-full"),
                    CreateCandidate());

            Assert.Equal(
                AiRuntimePoolProcessCreationStatus.CapacityUnavailable,
                result.Status);
            Assert.Equal(2, result.ProcessCountBefore);
            Assert.Equal(2, result.ProcessCountAfter);
            Assert.Equal(0, manager.EnsureCapacityCallCount);
        }

        /// <summary>
        /// Verifies that the selected host identity cannot be redirected to another
        /// local Runtime Pool Manager incarnation.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Reject_Different_Manager_Host()
        {
            var manager =
                new FakeRuntimeProcessPoolManager(
                    poolId: "pool-step-7d",
                    hostId: "other-host-step-7d",
                    initialProcessCount: 1,
                    maximumProcessCount: 3);

            var executor =
                new AiRuntimePoolProcessCreationExecutor(manager);

            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => executor.ExecuteAsync(
                        CreateRequest("request-step-7d-wrong-host"),
                        CreateCandidate()));

            Assert.Contains(
                "does not match",
                exception.Message.ToLowerInvariant());
            Assert.Equal(0, manager.EnsureCapacityCallCount);
        }

        /// <summary>
        /// Verifies that stale suppressed host evidence cannot reach the local process
        /// lifecycle authority.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Reject_Suppressed_Host_Candidate()
        {
            var manager =
                new FakeRuntimeProcessPoolManager(
                    poolId: "pool-step-7d",
                    hostId: "host-step-7d",
                    initialProcessCount: 1,
                    maximumProcessCount: 3);

            var executor =
                new AiRuntimePoolProcessCreationExecutor(manager);
            var candidate = CreateCandidate();
            candidate.IsSuppressed = true;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(
                    CreateRequest("request-step-7d-suppressed"),
                    candidate));

            Assert.Equal(0, manager.EnsureCapacityCallCount);
            Assert.Equal(1, manager.CurrentProcessCount);
        }

        /// <summary>
        /// Verifies that many concurrent executions of one logical request create
        /// exactly one child process and that every duplicate observes the applied
        /// result without consuming another process slot.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Deduplicate_High_Contention_Same_Request()
        {
            const int contenderCount = 32;

            var manager =
                new FakeRuntimeProcessPoolManager(
                    poolId: "pool-step-7d",
                    hostId: "host-step-7d",
                    initialProcessCount: 1,
                    maximumProcessCount: 4);

            var executor =
                new AiRuntimePoolProcessCreationExecutor(manager);
            var request =
                CreateRequest(
                    "request-step-7d-high-contention");
            var candidate = CreateCandidate();

            var startGate =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var contenders =
                Enumerable
                    .Range(0, contenderCount)
                    .Select(
                        _ =>
                            Task.Run(
                                async () =>
                                {
                                    await startGate.Task;
                                    return await executor.ExecuteAsync(
                                        request,
                                        candidate);
                                }))
                    .ToArray();

            startGate.SetResult(true);

            var results =
                await Task.WhenAll(contenders);

            Assert.Single(
                results,
                result => result.IsCreated);

            Assert.Equal(
                contenderCount - 1,
                results.Count(
                    result => result.IsDeduplicated));

            Assert.All(
                results.Where(result => result.IsDeduplicated),
                result =>
                    Assert.Empty(
                        result.CreatedRuntimeInstanceIds));

            Assert.Equal(1, manager.EnsureCapacityCallCount);
            Assert.Equal(2, manager.CurrentProcessCount);

            Assert.Equal(
                new[] { "runtime-step-7d-2" },
                results
                    .Where(result => result.IsCreated)
                    .SelectMany(
                        result =>
                            result.CreatedRuntimeInstanceIds)
                    .ToArray());
        }

        /// <summary>
        /// Creates one valid existing-host process-creation candidate.
        /// </summary>
        /// <returns>The candidate.</returns>
        private static AiRuntimeCapacitySelectionCandidate CreateCandidate()
        {
            return new AiRuntimeCapacitySelectionCandidate
            {
                Level =
                    AiRuntimeCapacitySelectionLevel
                        .ExistingPoolPodProcessCreation,
                PoolId = "pool-step-7d",
                HostId = "host-step-7d",
                ProviderName = "http",
                IsCompatible = true,
                IsAvailable = true,
                AvailableProcessSlots = 2
            };
        }

        /// <summary>
        /// Creates one existing provider-level request.
        /// </summary>
        /// <param name="requestId">The request identifier.</param>
        /// <returns>The provider request.</returns>
        private static AiRuntimeScaleOutProviderRequest CreateRequest(
            string requestId)
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = requestId,
                ControlPlaneId = "step-7d-control-plane",
                SharedRunId = Guid.NewGuid().ToString("N"),
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        contextKey:
                            string.Concat(
                                "step-7d:",
                                Guid.NewGuid().ToString("N")),
                        project: "step-7d",
                        userId: "unit-test",
                        tenantId: "tenant-step-7d",
                        tenantGroupId: "tenant-group-step-7d",
                        currentNamespace: "unit-test"),
                TenantId = "tenant-step-7d",
                TenantGroupId = "tenant-group-step-7d",
                ProviderHint = "http",
                RequestedTargetInstanceCount = 1
            };
        }

        /// <summary>
        /// Provides a deterministic in-memory implementation of the existing Runtime
        /// Pool Manager contract.
        /// </summary>
        private sealed class FakeRuntimeProcessPoolManager :
            IAiRuntimeProcessPoolManager
        {
            private readonly SemaphoreSlim lifecycleGate = new(1, 1);
            private readonly int maximumProcessCount;
            private int currentProcessCount;

            /// <summary>
            /// Initializes a new fake manager.
            /// </summary>
            public FakeRuntimeProcessPoolManager(
                string poolId,
                string hostId,
                int initialProcessCount,
                int maximumProcessCount)
            {
                this.Identity = new AiRuntimeProcessPoolIdentity
                {
                    PoolId = poolId,
                    HostId = hostId,
                    RuntimeInstanceIdPrefix = "runtime-step-7d"
                };
                this.currentProcessCount = initialProcessCount;
                this.maximumProcessCount = maximumProcessCount;
            }

            /// <inheritdoc />
            public AiRuntimeProcessPoolIdentity Identity { get; }

            /// <summary>
            /// Gets the current child-process count.
            /// </summary>
            public int CurrentProcessCount => this.currentProcessCount;

            /// <summary>
            /// Gets the number of exact capacity convergence calls.
            /// </summary>
            public int EnsureCapacityCallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolSnapshot>
                EnsureInitialCapacityAsync(
                    CancellationToken cancellationToken = default)
            {
                return this.EnsureCapacityAsync(
                    this.currentProcessCount,
                    cancellationToken);
            }

            /// <inheritdoc />
            public async Task<AiRuntimeProcessPoolSnapshot>
                EnsureCapacityAsync(
                    int requiredProcessCount,
                    CancellationToken cancellationToken = default)
            {
                await this.lifecycleGate
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    this.EnsureCapacityCallCount++;

                    if (requiredProcessCount > this.maximumProcessCount)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(requiredProcessCount));
                    }

                    this.currentProcessCount =
                        Math.Max(
                            this.currentProcessCount,
                            requiredProcessCount);

                    return this.CreateSnapshot();
                }
                finally
                {
                    this.lifecycleGate.Release();
                }
            }

            /// <inheritdoc />
            public async Task<AiRuntimeProcessPoolSnapshot> GetSnapshotAsync(
                CancellationToken cancellationToken = default)
            {
                await this.lifecycleGate
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    return this.CreateSnapshot();
                }
                finally
                {
                    this.lifecycleGate.Release();
                }
            }

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            /// <summary>
            /// Creates the current authoritative manager snapshot.
            /// </summary>
            /// <returns>The snapshot.</returns>
            private AiRuntimeProcessPoolSnapshot CreateSnapshot()
            {
                var children =
                    Enumerable
                        .Range(1, this.currentProcessCount)
                        .Select(ordinal =>
                            new AiRuntimeProcessPoolChildSnapshot
                            {
                                PoolId = this.Identity.PoolId,
                                HostId = this.Identity.HostId,
                                RuntimeInstanceId =
                                    string.Concat(
                                        this.Identity
                                            .RuntimeInstanceIdPrefix,
                                        "-",
                                        ordinal),
                                Ordinal = ordinal,
                                Status =
                                    AiRuntimeProcessPoolChildStatus.Running
                            })
                        .ToArray();

                return new AiRuntimeProcessPoolSnapshot
                {
                    PoolId = this.Identity.PoolId,
                    HostId = this.Identity.HostId,
                    Status = AiRuntimeProcessPoolManagerStatus.Running,
                    MinimumProcessCount = 1,
                    MaximumProcessCount = this.maximumProcessCount,
                    IsBelowMinimumCapacity = false,
                    Children = children
                };
            }
        }
    }
}
