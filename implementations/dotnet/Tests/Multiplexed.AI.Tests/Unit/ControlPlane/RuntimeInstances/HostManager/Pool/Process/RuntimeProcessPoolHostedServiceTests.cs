using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates hosted startup and shutdown of the opt-in process-host Runtime Pool Manager.
    /// </summary>
    public sealed class RuntimeProcessPoolHostedServiceTests
    {
        /// <summary>
        /// Verifies that host startup waits for the complete minimum process capacity.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Wait_For_Initial_Ready_Capacity()
        {
            var manager =
                new FakeManager(
                    CreateSnapshot(
                        AiRuntimeProcessPoolManagerStatus.Running,
                        childCount: 3,
                        minimumProcessCount: 3));

            var service =
                CreateService(manager);

            await service.StartAsync(CancellationToken.None);
            await service.StartAsync(CancellationToken.None);

            Assert.Equal(1, manager.EnsureInitialCapacityCount);
            Assert.Equal(0, manager.StopCount);
        }

        /// <summary>
        /// Verifies deterministic idempotent hosted shutdown.
        /// </summary>
        [Fact]
        public async Task StopAsync_Should_Stop_Manager_Exactly_Once()
        {
            var manager =
                new FakeManager(
                    CreateSnapshot(
                        AiRuntimeProcessPoolManagerStatus.Running,
                        childCount: 3,
                        minimumProcessCount: 3));

            var service =
                CreateService(manager);

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, manager.StopCount);
        }

        /// <summary>
        /// Verifies that degraded startup is rejected and partial capacity is cleaned up.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Reject_Degraded_Capacity_And_Stop_Manager()
        {
            var manager =
                new FakeManager(
                    CreateSnapshot(
                        AiRuntimeProcessPoolManagerStatus.Degraded,
                        childCount: 2,
                        minimumProcessCount: 3));

            var service =
                CreateService(manager);

            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => service.StartAsync(CancellationToken.None));

            Assert.Contains(
                "minimum ready capacity",
                exception.Message,
                StringComparison.Ordinal);

            Assert.Equal(1, manager.EnsureInitialCapacityCount);
            Assert.Equal(1, manager.StopCount);
        }

        /// <summary>
        /// Verifies that an initial-capacity exception remains authoritative while cleanup is still
        /// attempted.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Preserve_StartupFailure_And_Attempt_Cleanup()
        {
            var manager =
                new FakeManager(
                    CreateSnapshot(
                        AiRuntimeProcessPoolManagerStatus.Created,
                        childCount: 0,
                        minimumProcessCount: 3))
                {
                    StartException =
                        new InvalidOperationException(
                            "synthetic-start-failure")
                };

            var service =
                CreateService(manager);

            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => service.StartAsync(CancellationToken.None));

            Assert.Equal("synthetic-start-failure", exception.Message);
            Assert.Equal(1, manager.StopCount);
        }

        /// <summary>
        /// Creates the hosted service under test.
        /// </summary>
        /// <param name="manager">The fake process-pool manager.</param>
        /// <returns>The hosted service.</returns>
        private static AiRuntimeProcessPoolHostedService CreateService(
            FakeManager manager)
        {
            return new AiRuntimeProcessPoolHostedService(
                manager,
                NullLogger<AiRuntimeProcessPoolHostedService>.Instance);
        }

        /// <summary>
        /// Creates a deterministic process-pool snapshot.
        /// </summary>
        private static AiRuntimeProcessPoolSnapshot CreateSnapshot(
            AiRuntimeProcessPoolManagerStatus status,
            int childCount,
            int minimumProcessCount)
        {
            var children =
                Enumerable
                    .Range(1, childCount)
                    .Select(
                        ordinal =>
                            new AiRuntimeProcessPoolChildSnapshot
                            {
                                PoolId = "pool-01",
                                HostId = "host-01",
                                RuntimeInstanceId = $"runtime-{ordinal}",
                                Ordinal = ordinal,
                                Status =
                                    AiRuntimeProcessPoolChildStatus.Running
                            })
                    .ToArray();

            return new AiRuntimeProcessPoolSnapshot
            {
                PoolId = "pool-01",
                HostId = "host-01",
                Status = status,
                MinimumProcessCount = minimumProcessCount,
                MaximumProcessCount = minimumProcessCount,
                IsBelowMinimumCapacity =
                    children.Length < minimumProcessCount,
                Children = children
            };
        }

        /// <summary>
        /// Provides a deterministic process-pool manager for hosted lifecycle tests.
        /// </summary>
        private sealed class FakeManager : IAiRuntimeProcessPoolManager
        {
            private readonly AiRuntimeProcessPoolSnapshot snapshot;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeManager"/> class.
            /// </summary>
            public FakeManager(
                AiRuntimeProcessPoolSnapshot snapshot)
            {
                this.snapshot = snapshot;
                this.Identity = new AiRuntimeProcessPoolIdentity
                {
                    PoolId = snapshot.PoolId,
                    HostId = snapshot.HostId,
                    RuntimeInstanceIdPrefix = "runtime"
                };
            }

            /// <summary>
            /// Gets or sets the exception thrown by initial-capacity creation.
            /// </summary>
            public Exception? StartException { get; set; }

            /// <summary>
            /// Gets the number of initial-capacity requests.
            /// </summary>
            public int EnsureInitialCapacityCount { get; private set; }

            /// <summary>
            /// Gets the number of stop requests.
            /// </summary>
            public int StopCount { get; private set; }

            /// <inheritdoc />
            public AiRuntimeProcessPoolIdentity Identity { get; }

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolSnapshot> EnsureInitialCapacityAsync(
                CancellationToken cancellationToken = default)
            {
                this.EnsureInitialCapacityCount++;

                if (this.StartException is not null)
                {
                    throw this.StartException;
                }

                return Task.FromResult(this.snapshot);
            }

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolSnapshot> EnsureCapacityAsync(
                int requiredProcessCount,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.snapshot);
            }

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolSnapshot> GetSnapshotAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.snapshot);
            }

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                this.StopCount++;
                return Task.CompletedTask;
            }
        }
    }
}
