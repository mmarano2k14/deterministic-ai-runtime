using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Validates exact failure-to-capacity safety projection.
    /// </summary>
    public sealed class RuntimePoolFailureSafetyObserverTests
    {
        /// <summary>
        /// Verifies that A1 is journaled and suppressed without suppressing A2.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Journal_And_Suppress_Only_A1()
        {
            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            var safety =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            var observer =
                new AiRuntimePoolFailureSafetyObserver(
                    journal,
                    safety);

            var observation =
                CreateObservation(
                    runtimeInstanceId: "runtime-a1",
                    routeId: "route-a1");

            var stored =
                await observer.RecordAsync(
                    observation);

            var journaled =
                await journal.ListByHostIdAsync(
                    "host-01");

            var runtimeA1 =
                await safety.GetSuppressionAsync(
                    "pool-01",
                    "host-01",
                    "runtime-a1");

            var runtimeA2 =
                await safety.GetSuppressionAsync(
                    "pool-01",
                    "host-01",
                    "runtime-a2");

            Assert.Equal(
                observation,
                stored);

            Assert.Single(journaled);
            Assert.Null(runtimeA2);

            var suppression =
                Assert.IsType<
                    AiRuntimePoolCapacitySuppression>(
                    runtimeA1);

            Assert.Equal(
                observation.FailureId,
                suppression.FailureId);

            Assert.Equal(
                observation.RouteId,
                suppression.RouteId);
        }

        /// <summary>
        /// Verifies that the journal is authoritative before capacity suppression begins.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Journal_Before_Suppression()
        {
            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            var blockingSafety =
                new BlockingSafetyWriter();

            var observer =
                new AiRuntimePoolFailureSafetyObserver(
                    journal,
                    blockingSafety);

            var observation =
                CreateObservation(
                    runtimeInstanceId: "runtime-a1",
                    routeId: "route-a1");

            var recording =
                observer.RecordAsync(
                    observation);

            await blockingSafety.Entered;

            Assert.False(recording.IsCompleted);

            var journaled =
                await journal.ListByRuntimeInstanceIdAsync(
                    "runtime-a1");

            Assert.Single(journaled);

            blockingSafety.Release();

            await recording;
        }

        /// <summary>
        /// Creates one exact runtime-instance failure observation.
        /// </summary>
        private static AiRuntimePoolFailureObservation
            CreateObservation(
                string runtimeInstanceId,
                string routeId)
        {
            return new AiRuntimePoolFailureObservation
            {
                FailureId =
                    string.Concat(
                        "failure-",
                        runtimeInstanceId),
                Scope =
                    AiRuntimePoolFailureScope.RuntimeInstance,
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId =
                    runtimeInstanceId,
                RouteId = routeId,
                Kind =
                    AiRuntimePoolFailureKind
                        .UnexpectedProcessExit,
                ExitCode = 137,
                ObservedAtUtc =
                    new DateTimeOffset(
                        2026,
                        7,
                        26,
                        0,
                        0,
                        0,
                        TimeSpan.Zero)
            };
        }

        /// <summary>
        /// Blocks capacity suppression to prove journal-first ordering.
        /// </summary>
        private sealed class BlockingSafetyWriter :
            IAiRuntimePoolCapacitySafetyWriter
        {
            private readonly TaskCompletionSource<bool> entered =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> release =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

            /// <summary>
            /// Gets a task completed when suppression begins.
            /// </summary>
            public Task Entered => this.entered.Task;

            /// <summary>
            /// Releases suppression.
            /// </summary>
            public void Release()
            {
                this.release.TrySetResult(true);
            }

            /// <inheritdoc />
            public async Task<AiRuntimePoolCapacitySuppression>
                SuppressAsync(
                    AiRuntimePoolCapacitySuppression suppression,
                    CancellationToken cancellationToken = default)
            {
                this.entered.TrySetResult(true);

                await this.release.Task
                    .WaitAsync(cancellationToken);

                return suppression;
            }
        }
    }
}
