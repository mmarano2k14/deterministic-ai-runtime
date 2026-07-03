namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Tracks in-memory gRPC runtime command circuit breaker state.
    /// </summary>
    public sealed class AiGrpcRuntimeCircuitBreakerState
    {
        /// <summary>
        /// Gets the consecutive failure count.
        /// </summary>
        public int ConsecutiveFailureCount { get; private set; }

        /// <summary>
        /// Gets the UTC instant until which the circuit is open.
        /// </summary>
        public DateTimeOffset? OpenUntilUtc { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the circuit is currently open.
        /// </summary>
        public bool IsOpen =>
            OpenUntilUtc is not null &&
            OpenUntilUtc.Value > DateTimeOffset.UtcNow;

        /// <summary>
        /// Records a successful command.
        /// </summary>
        public void RecordSuccess()
        {
            ConsecutiveFailureCount = 0;
            OpenUntilUtc = null;
        }

        /// <summary>
        /// Records a failed command.
        /// </summary>
        /// <param name="failureThreshold">The failure threshold.</param>
        /// <param name="breakDuration">The break duration.</param>
        public void RecordFailure(
            int failureThreshold,
            TimeSpan breakDuration)
        {
            ConsecutiveFailureCount++;

            if (failureThreshold > 0 &&
                ConsecutiveFailureCount >= failureThreshold &&
                breakDuration > TimeSpan.Zero)
            {
                OpenUntilUtc =
                    DateTimeOffset.UtcNow.Add(
                        breakDuration);
            }
        }
    }
}