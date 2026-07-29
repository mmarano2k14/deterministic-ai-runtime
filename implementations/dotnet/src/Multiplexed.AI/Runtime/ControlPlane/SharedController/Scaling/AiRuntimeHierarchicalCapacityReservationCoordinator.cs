using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Coordinates hierarchical capacity selection with bounded atomic reservation of
    /// existing runtime slots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Selection remains deterministic and read-only. When selection chooses a warm or
    /// existing pooled runtime slot, this coordinator attempts one atomic bounded
    /// reservation against the existing admission reservation authority.
    /// </para>
    /// <para>
    /// If another concurrent selector acquires the selected slot first, the coordinator
    /// rebuilds the authoritative inventory and selects again. This allows the caller to
    /// converge on another runtime, a later hierarchy action, or explicit backpressure
    /// without over-reserving the original runtime.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeHierarchicalCapacityReservationCoordinator :
        IAiRuntimeHierarchicalCapacityReservationCoordinator
    {
        private readonly IAiRuntimeCapacitySelectionInventoryBuilder
            inventoryBuilder;
        private readonly IAiRuntimeHierarchicalCapacitySelector selector;
        private readonly IAiRuntimeAtomicAdmissionReservationStore
            reservationStore;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimeHierarchicalCapacityReservationCoordinator" /> class.
        /// </summary>
        /// <param name="inventoryBuilder">
        /// The authoritative capacity inventory builder.
        /// </param>
        /// <param name="selector">
        /// The deterministic hierarchical capacity selector.
        /// </param>
        /// <param name="reservationStore">
        /// The bounded atomic admission reservation authority.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when a required dependency is <see langword="null" />.
        /// </exception>
        public AiRuntimeHierarchicalCapacityReservationCoordinator(
            IAiRuntimeCapacitySelectionInventoryBuilder inventoryBuilder,
            IAiRuntimeHierarchicalCapacitySelector selector,
            IAiRuntimeAtomicAdmissionReservationStore reservationStore)
        {
            this.inventoryBuilder =
                inventoryBuilder ??
                throw new ArgumentNullException(nameof(inventoryBuilder));

            this.selector =
                selector ??
                throw new ArgumentNullException(nameof(selector));

            this.reservationStore =
                reservationStore ??
                throw new ArgumentNullException(nameof(reservationStore));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeHierarchicalCapacityReservationResult>
            SelectAndReserveAsync(
                AiRuntimeScaleOutProviderRequest request,
                int runCount = 1,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);

            cancellationToken.ThrowIfCancellationRequested();

            var selectionAttemptCount = 0;
            var maximumSelectionAttempts = 1;
            var lastEvaluatedCandidateCount = 0;

            while (selectionAttemptCount < maximumSelectionAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var inventory =
                    await this.inventoryBuilder
                        .BuildAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                var candidates =
                    FilterCandidatesForRunCount(
                        inventory,
                        runCount);

                lastEvaluatedCandidateCount = candidates.Count;

                if (selectionAttemptCount == 0)
                {
                    maximumSelectionAttempts =
                        Math.Max(
                            1,
                            CountRuntimeReservationCandidates(candidates) + 1);
                }

                selectionAttemptCount++;

                var decision =
                    await this.selector
                        .SelectAsync(
                            request,
                            candidates,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (!RequiresRuntimeReservation(decision.Level))
                {
                    return new AiRuntimeHierarchicalCapacityReservationResult
                    {
                        Decision = decision,
                        SelectionAttemptCount = selectionAttemptCount
                    };
                }

                var candidate =
                    decision.Candidate ??
                    throw new InvalidOperationException(
                        "A runtime reservation decision must contain a candidate.");

                if (string.IsNullOrWhiteSpace(candidate.RuntimeInstanceId) ||
                    candidate.PublishedAvailableRunSlots <= 0)
                {
                    throw new InvalidOperationException(
                        "A runtime reservation candidate must expose authoritative runtime identity and published slots.");
                }

                var reservation =
                    await this.reservationStore
                        .TryReserveAsync(
                            candidate.RuntimeInstanceId,
                            candidate.PublishedAvailableRunSlots,
                            runCount,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (reservation.IsAcquired)
                {
                    return new AiRuntimeHierarchicalCapacityReservationResult
                    {
                        Decision = decision,
                        Reservation = reservation,
                        SelectionAttemptCount = selectionAttemptCount
                    };
                }
            }

            return new AiRuntimeHierarchicalCapacityReservationResult
            {
                Decision =
                    new AiRuntimeCapacitySelectionDecision
                    {
                        Level =
                            AiRuntimeCapacitySelectionLevel.Backpressure,
                        EvaluatedCandidateCount =
                            lastEvaluatedCandidateCount,
                        Reason =
                            "atomic-runtime-reservation-contention-exhausted"
                    },
                SelectionAttemptCount = selectionAttemptCount
            };
        }

        /// <summary>
        /// Filters runtime candidates that cannot satisfy the requested reservation
        /// count while preserving later non-runtime hierarchy actions.
        /// </summary>
        /// <param name="candidates">The current capacity inventory.</param>
        /// <param name="runCount">The requested run-slot count.</param>
        /// <returns>The candidates eligible for this reservation count.</returns>
        private static IReadOnlyList<AiRuntimeCapacitySelectionCandidate>
            FilterCandidatesForRunCount(
                IReadOnlyList<AiRuntimeCapacitySelectionCandidate> candidates,
                int runCount)
        {
            return candidates
                .Where(candidate =>
                    !RequiresRuntimeReservation(candidate.Level) ||
                    candidate.AvailableRunSlots >= runCount)
                .ToArray();
        }

        /// <summary>
        /// Counts runtime candidates that may require one atomic reservation attempt.
        /// </summary>
        /// <param name="candidates">The filtered capacity candidates.</param>
        /// <returns>The runtime reservation candidate count.</returns>
        private static int CountRuntimeReservationCandidates(
            IReadOnlyList<AiRuntimeCapacitySelectionCandidate> candidates)
        {
            return candidates.Count(candidate =>
                RequiresRuntimeReservation(candidate.Level));
        }

        /// <summary>
        /// Determines whether one hierarchy level reserves existing runtime capacity.
        /// </summary>
        /// <param name="level">The hierarchy level.</param>
        /// <returns>
        /// <see langword="true" /> when the level requires a bounded runtime
        /// reservation; otherwise, <see langword="false" />.
        /// </returns>
        private static bool RequiresRuntimeReservation(
            AiRuntimeCapacitySelectionLevel level)
        {
            return level is
                AiRuntimeCapacitySelectionLevel.CompatibleWarmRuntime or
                AiRuntimeCapacitySelectionLevel.ExistingPoolRuntimeSlot;
        }
    }
}
