using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Tracks in-memory circuit breaker state for one HTTP runtime endpoint.
    /// </summary>
    internal sealed class AiHttpRuntimeCircuitBreakerState
    {
        /// <summary>
        /// Gets or sets the number of consecutive failures recorded for the endpoint.
        /// </summary>
        public int ConsecutiveFailureCount { get; set; }

        /// <summary>
        /// Gets or sets the UTC instant until which the circuit remains open.
        /// </summary>
        public DateTimeOffset? OpenUntilUtc { get; set; }

        /// <summary>
        /// Gets a value indicating whether the circuit is currently open.
        /// </summary>
        public bool IsOpen
        {
            get
            {
                return OpenUntilUtc.HasValue &&
                    OpenUntilUtc.Value > DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// Records a successful HTTP command attempt and closes the circuit.
        /// </summary>
        public void RecordSuccess()
        {
            ConsecutiveFailureCount =
                0;

            OpenUntilUtc =
                null;
        }

        /// <summary>
        /// Records a failed HTTP command attempt and opens the circuit when the failure threshold is reached.
        /// </summary>
        /// <param name="failureThreshold">The number of consecutive failures required to open the circuit.</param>
        /// <param name="breakDuration">The duration for which the circuit remains open.</param>
        public void RecordFailure(
            int failureThreshold,
            TimeSpan breakDuration)
        {
            ConsecutiveFailureCount++;

            if (failureThreshold <= 0)
            {
                return;
            }

            if (ConsecutiveFailureCount < failureThreshold)
            {
                return;
            }

            if (breakDuration <= TimeSpan.Zero)
            {
                OpenUntilUtc =
                    DateTimeOffset.UtcNow;

                return;
            }

            OpenUntilUtc =
                DateTimeOffset.UtcNow.Add(
                    breakDuration);
        }
    }
}