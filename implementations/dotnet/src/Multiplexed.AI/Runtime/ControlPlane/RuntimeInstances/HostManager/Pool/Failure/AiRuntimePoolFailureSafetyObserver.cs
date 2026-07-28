using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Records exact runtime failures and projects them into capacity suppressions.
    /// </summary>
    public sealed class AiRuntimePoolFailureSafetyObserver :
        IAiRuntimePoolFailureObserver
    {
        private readonly IAiRuntimePoolFailureObserver journalObserver;
        private readonly IAiRuntimePoolCapacitySafetyWriter safetyWriter;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolFailureSafetyObserver"/> class.
        /// </summary>
        /// <param name="journalObserver">The authoritative failure journal writer.</param>
        /// <param name="safetyWriter">The exact capacity suppression writer.</param>
        public AiRuntimePoolFailureSafetyObserver(
            IAiRuntimePoolFailureObserver journalObserver,
            IAiRuntimePoolCapacitySafetyWriter safetyWriter)
        {
            this.journalObserver =
                journalObserver
                ?? throw new ArgumentNullException(
                    nameof(journalObserver));

            this.safetyWriter =
                safetyWriter
                ?? throw new ArgumentNullException(
                    nameof(safetyWriter));
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolFailureObservation> RecordAsync(
            AiRuntimePoolFailureObservation observation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(observation);

            var storedObservation =
                await this.journalObserver
                    .RecordAsync(
                        observation,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (storedObservation.Scope ==
                AiRuntimePoolFailureScope.RuntimeInstance)
            {
                await this.safetyWriter
                    .SuppressAsync(
                        new AiRuntimePoolCapacitySuppression
                        {
                            FailureId =
                                storedObservation.FailureId,
                            PoolId =
                                storedObservation.PoolId,
                            HostId =
                                storedObservation.HostId,
                            Scope =
                                AiRuntimePoolCapacitySuppressionScope
                                    .RuntimeInstanceRoute,
                            RuntimeInstanceId =
                                storedObservation.RuntimeInstanceId!,
                            RouteId =
                                storedObservation.RouteId,
                            SuppressedAtUtc =
                                storedObservation.ObservedAtUtc
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return storedObservation;
        }
    }
}
