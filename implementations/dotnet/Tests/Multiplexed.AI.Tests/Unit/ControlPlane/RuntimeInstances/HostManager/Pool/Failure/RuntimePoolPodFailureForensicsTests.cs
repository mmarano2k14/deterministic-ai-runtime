using System;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Validates first-class Pod failure forensics without synthetic child or route identities.
    /// </summary>
    public sealed class RuntimePoolPodFailureForensicsTests
    {
        /// <summary>
        /// Verifies a Pod failure is journaled at host scope and does not project one fake route suppression.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Journal_One_Host_Failure_Without_Synthetic_Route()
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
                CreatePodFailure();

            var stored =
                await observer.RecordAsync(observation);

            Assert.Equal(observation, stored);
            Assert.Null(stored.RuntimeInstanceId);
            Assert.Null(stored.RouteId);

            var hostFailures =
                await journal.ListByHostIdAsync("pod-a");

            var suppressions =
                await safety.ListByHostIdAsync("pod-a");

            var hostFailure = Assert.Single(hostFailures);
            Assert.Equal(AiRuntimePoolFailureScope.Host, hostFailure.Scope);
            Assert.Equal(
                AiRuntimePoolFailureKind.UnexpectedPodDeletion,
                hostFailure.Kind);
            Assert.Empty(suppressions);
        }

        /// <summary>
        /// Verifies runtime scope still requires the existing exact child and route identities.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Reject_Runtime_Scope_Without_RouteId()
        {
            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            await Assert.ThrowsAsync<ArgumentException>(
                () => journal.RecordAsync(
                    CreatePodFailure() with
                    {
                        Scope = AiRuntimePoolFailureScope.RuntimeInstance,
                        RuntimeInstanceId = "runtime-a-1"
                    }));
        }

        private static AiRuntimePoolFailureObservation CreatePodFailure()
        {
            return new AiRuntimePoolFailureObservation
            {
                FailureId = "failure-pod-a",
                Scope = AiRuntimePoolFailureScope.Host,
                PoolId = "pool-a",
                HostId = "pod-a",
                RuntimeInstanceId = null,
                RouteId = null,
                Kind = AiRuntimePoolFailureKind.UnexpectedPodDeletion,
                ExitCode = null,
                ObservedAtUtc =
                    new DateTimeOffset(
                        2026,
                        7,
                        28,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                FailureMessage = "Pod deleted by the integration proof."
            };
        }
    }
}
