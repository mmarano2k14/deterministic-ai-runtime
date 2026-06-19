using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Defines hardening options for the HTTP runtime instance provider.
    /// </summary>
    public sealed class AiHttpRuntimeInstanceProviderOptions
    {
        /// <summary>
        /// Gets or sets the maximum duration allowed for a single HTTP dispatch command.
        /// </summary>
        public TimeSpan DispatchTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets whether retry is enabled for safe transient HTTP dispatch failures.
        /// </summary>
        public bool EnableRetry { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of retry attempts after the initial dispatch attempt.
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 1;

        /// <summary>
        /// Gets or sets the base delay used before retrying a transient dispatch failure.
        /// </summary>
        public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Gets or sets the maximum delay allowed between retry attempts.
        /// </summary>
        public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Gets or sets whether HTTP dispatch timeouts may be retried.
        /// </summary>
        /// <remarks>
        /// This defaults to <c>false</c> because a timeout is ambiguous: the remote runtime
        /// may already have accepted and enqueued the run before the response was lost.
        /// </remarks>
        public bool RetryTimeouts { get; set; } = false;

        /// <summary>
        /// Gets or sets whether the in-memory HTTP dispatch circuit breaker is enabled.
        /// </summary>
        public bool EnableCircuitBreaker { get; set; } = true;

        /// <summary>
        /// Gets or sets the number of consecutive dispatch failures required before opening the circuit.
        /// </summary>
        public int CircuitBreakerFailureThreshold { get; set; } = 5;

        /// <summary>
        /// Gets or sets how long an opened circuit remains open before a half-open dispatch attempt is allowed.
        /// </summary>
        public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(30);
    }
}