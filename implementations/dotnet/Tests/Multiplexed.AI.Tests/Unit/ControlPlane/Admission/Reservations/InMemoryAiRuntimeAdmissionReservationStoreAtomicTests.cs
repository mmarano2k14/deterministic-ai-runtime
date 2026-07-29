using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Admission.Reservations
{
    /// <summary>
    /// Validates bounded atomic runtime admission reservations in the focused
    /// process-local implementation.
    /// </summary>
    public sealed class InMemoryAiRuntimeAdmissionReservationStoreAtomicTests
    {
        /// <summary>
        /// Verifies that two concurrent attempts cannot reserve the same single slot.
        /// </summary>
        [Fact]
        public async Task TryReserveAsync_Should_Acquire_Only_One_Concurrent_Slot()
        {
            var store =
                new InMemoryAiRuntimeAdmissionReservationStore();

            var results =
                await Task.WhenAll(
                    store.TryReserveAsync(
                        "runtime-step-7c",
                        maximumReservedRunCount: 1),
                    store.TryReserveAsync(
                        "runtime-step-7c",
                        maximumReservedRunCount: 1));

            var acquired =
                Assert.Single(
                    results,
                    result => result.IsAcquired);

            var rejected =
                Assert.Single(
                    results,
                    result =>
                        result.Status ==
                        AiRuntimeAdmissionReservationAttemptStatus
                            .CapacityUnavailable);

            Assert.Equal(1, acquired.ReservedRunCount);
            Assert.Equal(1, rejected.ReservedRunCount);
            Assert.Equal(
                1,
                await store.GetReservedRunCountAsync(
                    "runtime-step-7c"));
        }

        /// <summary>
        /// Verifies that released capacity can be acquired by a later bounded attempt.
        /// </summary>
        [Fact]
        public async Task TryReserveAsync_Should_Reacquire_After_Release()
        {
            var store =
                new InMemoryAiRuntimeAdmissionReservationStore();

            var first =
                await store.TryReserveAsync(
                    "runtime-step-7c",
                    maximumReservedRunCount: 1);

            Assert.True(first.IsAcquired);

            await store.ReleaseAsync(
                "runtime-step-7c");

            var second =
                await store.TryReserveAsync(
                    "runtime-step-7c",
                    maximumReservedRunCount: 1);

            Assert.True(second.IsAcquired);
            Assert.Equal(1, second.ReservedRunCount);
        }

        /// <summary>
        /// Verifies that a request larger than the supplied boundary is rejected
        /// without mutating reservation state.
        /// </summary>
        [Fact]
        public async Task TryReserveAsync_Should_Reject_Request_Above_Boundary()
        {
            var store =
                new InMemoryAiRuntimeAdmissionReservationStore();

            var result =
                await store.TryReserveAsync(
                    "runtime-step-7c",
                    maximumReservedRunCount: 1,
                    runCount: 2);

            Assert.False(result.IsAcquired);
            Assert.Equal(
                AiRuntimeAdmissionReservationAttemptStatus
                    .CapacityUnavailable,
                result.Status);
            Assert.Equal(0, result.ReservedRunCount);
            Assert.Equal(
                0,
                await store.GetReservedRunCountAsync(
                    "runtime-step-7c"));
        }
    }
}
