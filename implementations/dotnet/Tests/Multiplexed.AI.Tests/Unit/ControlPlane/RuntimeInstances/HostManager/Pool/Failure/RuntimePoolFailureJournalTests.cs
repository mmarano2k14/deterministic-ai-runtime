using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Validates first-class exact runtime-pool failure storage.
    /// </summary>
    public sealed class RuntimePoolFailureJournalTests
    {
        /// <summary>
        /// Verifies exact lookup without sibling contamination.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Store_Only_Exact_Runtime_Failure()
        {
            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            var observation =
                CreateObservation(
                    failureId: "failure-a1",
                    runtimeInstanceId: "runtime-a1");

            await journal.RecordAsync(observation);

            var hostFailures =
                await journal.ListByHostIdAsync(
                    "host-01");

            var runtimeA1Failures =
                await journal.ListByRuntimeInstanceIdAsync(
                    "runtime-a1");

            var runtimeA2Failures =
                await journal.ListByRuntimeInstanceIdAsync(
                    "runtime-a2");

            Assert.Single(hostFailures);
            Assert.Single(runtimeA1Failures);
            Assert.Empty(runtimeA2Failures);

            Assert.Equal(
                "runtime-a1",
                hostFailures[0].RuntimeInstanceId);

            Assert.Equal(
                AiRuntimePoolFailureScope.RuntimeInstance,
                hostFailures[0].Scope);
        }

        /// <summary>
        /// Verifies idempotent concurrent recording of one immutable failure fact.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Be_Idempotent_Under_Concurrency()
        {
            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            var observation =
                CreateObservation(
                    failureId: "failure-a1",
                    runtimeInstanceId: "runtime-a1");

            var results =
                await Task.WhenAll(
                    Enumerable
                        .Range(0, 20)
                        .Select(
                            _ =>
                                journal.RecordAsync(
                                    observation)));

            Assert.All(
                results,
                result =>
                    Assert.Equal(
                        observation,
                        result));

            var hostFailures =
                await journal.ListByHostIdAsync(
                    "host-01");

            Assert.Single(hostFailures);
        }

        /// <summary>
        /// Verifies that one FailureId cannot be rebound to another runtime.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Reject_Conflicting_FailureId()
        {
            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            await journal.RecordAsync(
                CreateObservation(
                    failureId: "failure-shared",
                    runtimeInstanceId: "runtime-a1"));

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolFailureConflictException>(
                    () =>
                        journal.RecordAsync(
                            CreateObservation(
                                failureId: "failure-shared",
                                runtimeInstanceId:
                                    "runtime-a2")));

            Assert.Equal(
                "failure-shared",
                exception.FailureId);
        }

        /// <summary>
        /// Creates one deterministic runtime-instance failure observation.
        /// </summary>
        internal static AiRuntimePoolFailureObservation
            CreateObservation(
                string failureId,
                string runtimeInstanceId)
        {
            return new AiRuntimePoolFailureObservation
            {
                FailureId = failureId,
                Scope =
                    AiRuntimePoolFailureScope.RuntimeInstance,
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId =
                    runtimeInstanceId,
                RouteId =
                    string.Concat(
                        "route-",
                        runtimeInstanceId),
                Kind =
                    AiRuntimePoolFailureKind
                        .UnexpectedProcessExit,
                ExitCode = 1,
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
    }
}
