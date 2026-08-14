using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Extends hierarchical capacity reservation with bounded process creation inside
    /// an existing Runtime Pool host and deterministic Runtime Pool Pod creation.
    /// </summary>
    public sealed class AiRuntimeHierarchicalCapacityExecutionCoordinator :
        IAiRuntimeHierarchicalCapacityExecutionCoordinator
    {
        private readonly IAiRuntimeHierarchicalCapacityReservationCoordinator
            reservationCoordinator;
        private readonly IAiRuntimePoolProcessCreationExecutor
            processCreationExecutor;
        private readonly IAiRuntimePoolPodCreationExecutor
            podCreationExecutor;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimeHierarchicalCapacityExecutionCoordinator" /> class.
        /// </summary>
        /// <param name="reservationCoordinator">
        /// The Step 7C selection and atomic reservation coordinator.
        /// </param>
        /// <param name="processCreationExecutor">
        /// The existing-host process creation executor.
        /// </param>
        /// <param name="podCreationExecutor">
        /// The Runtime Pool Pod creation executor.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when a required dependency is <see langword="null" />.
        /// </exception>
        public AiRuntimeHierarchicalCapacityExecutionCoordinator(
            IAiRuntimeHierarchicalCapacityReservationCoordinator
                reservationCoordinator,
            IAiRuntimePoolProcessCreationExecutor processCreationExecutor,
            IAiRuntimePoolPodCreationExecutor podCreationExecutor)
        {
            this.reservationCoordinator =
                reservationCoordinator ??
                throw new ArgumentNullException(
                    nameof(reservationCoordinator));

            this.processCreationExecutor =
                processCreationExecutor ??
                throw new ArgumentNullException(
                    nameof(processCreationExecutor));

            this.podCreationExecutor =
                podCreationExecutor ??
                throw new ArgumentNullException(
                    nameof(podCreationExecutor));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeHierarchicalCapacityExecutionResult>
            SelectReserveAndExecuteAsync(
                AiRuntimeScaleOutProviderRequest request,
                int runCount = 1,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);

            var reservationResult =
                await this.reservationCoordinator
                    .SelectAndReserveAsync(
                        request,
                        runCount,
                        cancellationToken)
                    .ConfigureAwait(false);

            var candidate =
                reservationResult.Decision.Candidate;

            if (reservationResult.Decision.Level ==
                AiRuntimeCapacitySelectionLevel
                    .ExistingPoolPodProcessCreation)
            {
                if (candidate is null)
                {
                    throw new InvalidOperationException(
                        "An existing-host process creation decision must contain a candidate.");
                }

                var processCreation =
                    await this.processCreationExecutor
                        .ExecuteAsync(
                            request,
                            candidate,
                            cancellationToken)
                        .ConfigureAwait(false);

                return new AiRuntimeHierarchicalCapacityExecutionResult
                {
                    ReservationResult = reservationResult,
                    ProcessCreation = processCreation
                };
            }

            if (reservationResult.Decision.Level ==
                AiRuntimeCapacitySelectionLevel.RuntimePoolPodCreation)
            {
                if (candidate is null)
                {
                    throw new InvalidOperationException(
                        "A Runtime Pool Pod creation decision must contain a candidate.");
                }

                var podCreation =
                    await this.podCreationExecutor
                        .ExecuteAsync(
                            request,
                            candidate,
                            cancellationToken)
                        .ConfigureAwait(false);

                return new AiRuntimeHierarchicalCapacityExecutionResult
                {
                    ReservationResult = reservationResult,
                    PodCreation = podCreation
                };
            }

            return new AiRuntimeHierarchicalCapacityExecutionResult
            {
                ReservationResult = reservationResult
            };
        }
    }
}
