namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Provides hardening options for the gRPC runtime instance provider.
    /// </summary>
    public sealed class AiGrpcRuntimeInstanceProviderOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the gRPC runtime instance provider is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the dispatch timeout.
        /// </summary>
        public TimeSpan DispatchTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets a value indicating whether command retry is enabled.
        /// </summary>
        public bool EnableRetry { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum retry attempts after the first command attempt.
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 1;

        /// <summary>
        /// Gets or sets a value indicating whether timeout failures can be retried.
        /// </summary>
        public bool RetryTimeouts { get; set; }

        /// <summary>
        /// Gets or sets the retry base delay.
        /// </summary>
        public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Gets or sets the retry maximum delay.
        /// </summary>
        public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets or sets a value indicating whether the in-memory circuit breaker is enabled.
        /// </summary>
        public bool EnableCircuitBreaker { get; set; } = true;

        /// <summary>
        /// Gets or sets the circuit breaker failure threshold.
        /// </summary>
        public int CircuitBreakerFailureThreshold { get; set; } = 3;

        /// <summary>
        /// Gets or sets the circuit breaker break duration.
        /// </summary>
        public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
    }
}