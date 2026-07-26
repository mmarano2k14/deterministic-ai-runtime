using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates deterministic replacement after runtime child-process failure.
    /// </summary>
    public sealed class RuntimeProcessPoolReplacementTests
    {
        /// <summary>
        /// Verifies that one failed child is replaced while healthy siblings keep their identities.
        /// </summary>
        [Fact]
        public async Task ChildExit_Should_Replace_Only_Failed_Runtime()
        {
            var factory =
                new RuntimeProcessPoolLifecycleTests.FakeChildFactory();

            var manager =
                RuntimeProcessPoolLifecycleTests.CreateManager(factory);

            var initial =
                await manager.EnsureInitialCapacityAsync();

            var failedRuntime =
                initial.Children.Single(child => child.Ordinal == 1);

            var healthyRuntimeIds =
                initial.Children
                    .Where(child => child.Ordinal != 1)
                    .Select(child => child.RuntimeInstanceId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            factory.GetChild(1).ExitUnexpectedly();

            var recovered =
                await WaitForSnapshotAsync(
                    manager,
                    snapshot =>
                        snapshot.Status == AiRuntimeProcessPoolManagerStatus.Running &&
                        snapshot.Children.Count == 3 &&
                        snapshot.Children.All(
                            child =>
                                !StringComparer.Ordinal.Equals(
                                    child.RuntimeInstanceId,
                                    failedRuntime.RuntimeInstanceId)));

            var preservedRuntimeIds =
                recovered.Children
                    .Where(child => child.Ordinal is 2 or 3)
                    .Select(child => child.RuntimeInstanceId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            var replacement =
                Assert.Single(
                    recovered.Children.Where(child => child.Ordinal == 4));

            Assert.Equal(healthyRuntimeIds, preservedRuntimeIds);
            Assert.Equal(manager.Identity.PoolId, replacement.PoolId);
            Assert.Equal(manager.Identity.HostId, replacement.HostId);
            Assert.NotEqual(failedRuntime.RuntimeInstanceId, replacement.RuntimeInstanceId);
            Assert.Equal(4, factory.StartAttemptCount);
            Assert.False(recovered.IsBelowMinimumCapacity);
        }

        /// <summary>
        /// Verifies that concurrent sibling exits restore exactly the missing capacity.
        /// </summary>
        [Fact]
        public async Task ConcurrentChildExits_Should_Not_OverCreate_ReplacementCapacity()
        {
            var factory =
                new RuntimeProcessPoolLifecycleTests.FakeChildFactory();

            var manager =
                RuntimeProcessPoolLifecycleTests.CreateManager(factory);

            await manager.EnsureInitialCapacityAsync();

            factory.GetChild(1).ExitUnexpectedly();
            factory.GetChild(2).ExitUnexpectedly();

            var recovered =
                await WaitForSnapshotAsync(
                    manager,
                    snapshot =>
                        snapshot.Status == AiRuntimeProcessPoolManagerStatus.Running &&
                        snapshot.Children.Count == 3 &&
                        snapshot.Children.Any(child => child.Ordinal == 4) &&
                        snapshot.Children.Any(child => child.Ordinal == 5));

            Assert.Equal(5, factory.StartAttemptCount);
            Assert.Equal(
                new[] { 3, 4, 5 },
                recovered.Children.Select(child => child.Ordinal).ToArray());
            Assert.Equal(
                3,
                recovered.Children
                    .Select(child => child.RuntimeInstanceId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }

        /// <summary>
        /// Verifies that a failed replacement leaves an explicit degraded pool and that a later
        /// reconciliation retries only missing capacity.
        /// </summary>
        [Fact]
        public async Task ReplacementFailure_Should_Leave_DegradedPool_And_Allow_Retry()
        {
            var factory =
                new RuntimeProcessPoolLifecycleTests.FakeChildFactory();

            var manager =
                RuntimeProcessPoolLifecycleTests.CreateManager(factory);

            await manager.EnsureInitialCapacityAsync();

            factory.FailOnStartAttempt = 4;
            factory.GetChild(1).ExitUnexpectedly();

            var degraded =
                await WaitForSnapshotAsync(
                    manager,
                    snapshot =>
                        snapshot.Status == AiRuntimeProcessPoolManagerStatus.Degraded &&
                        snapshot.Children.Count == 2);

            Assert.True(degraded.IsBelowMinimumCapacity);
            Assert.Equal(4, factory.StartAttemptCount);

            factory.FailOnStartAttempt = null;

            var recovered =
                await manager.EnsureInitialCapacityAsync();

            Assert.Equal(AiRuntimeProcessPoolManagerStatus.Running, recovered.Status);
            Assert.Equal(3, recovered.Children.Count);
            Assert.False(recovered.IsBelowMinimumCapacity);
            Assert.Equal(5, factory.StartAttemptCount);
        }

        /// <summary>
        /// Verifies that requested manager shutdown never recreates stopped capacity.
        /// </summary>
        [Fact]
        public async Task StopAsync_Should_Not_Replace_Requested_ChildExits()
        {
            var factory =
                new RuntimeProcessPoolLifecycleTests.FakeChildFactory();

            var manager =
                RuntimeProcessPoolLifecycleTests.CreateManager(factory);

            await manager.EnsureInitialCapacityAsync();
            await manager.StopAsync();

            await Task.Delay(50);

            var stopped =
                await manager.GetSnapshotAsync();

            Assert.Equal(AiRuntimeProcessPoolManagerStatus.Stopped, stopped.Status);
            Assert.Empty(stopped.Children);
            Assert.Equal(3, factory.StartAttemptCount);
        }

        /// <summary>
        /// Waits for a deterministic pool snapshot condition without using a long integration-test
        /// timeout.
        /// </summary>
        /// <param name="manager">The process pool manager.</param>
        /// <param name="predicate">The expected snapshot condition.</param>
        /// <returns>The first snapshot matching the condition.</returns>
        /// <exception cref="TimeoutException">
        /// Thrown when the condition is not reached within the focused unit-test boundary.
        /// </exception>
        private static async Task<AiRuntimeProcessPoolSnapshot> WaitForSnapshotAsync(
            IAiRuntimeProcessPoolManager manager,
            Func<AiRuntimeProcessPoolSnapshot, bool> predicate)
        {
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(3));

            try
            {
                while (true)
                {
                    var snapshot =
                        await manager.GetSnapshotAsync(timeout.Token);

                    if (predicate(snapshot))
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
                    "The expected runtime process pool snapshot was not observed.");
            }
        }
    }
}
