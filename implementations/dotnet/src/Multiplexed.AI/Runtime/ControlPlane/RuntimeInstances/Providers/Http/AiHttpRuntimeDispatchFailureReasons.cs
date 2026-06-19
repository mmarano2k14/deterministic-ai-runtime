namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Defines stable HTTP runtime dispatch failure reason codes.
    /// </summary>
    public static class AiHttpRuntimeDispatchFailureReasons
    {
        /// <summary>
        /// The dispatch command completed successfully.
        /// </summary>
        public const string None = "none";

        /// <summary>
        /// The target HTTP runtime endpoint is missing from the runtime descriptor metadata.
        /// </summary>
        public const string EndpointMissing = "http-endpoint-missing";

        /// <summary>
        /// The target HTTP runtime endpoint is invalid.
        /// </summary>
        public const string EndpointInvalid = "http-endpoint-invalid";

        /// <summary>
        /// The HTTP runtime provider could not reach the target runtime host.
        /// </summary>
        public const string ProviderUnavailable = "http-provider-unavailable";

        /// <summary>
        /// The HTTP dispatch command exceeded the configured timeout.
        /// </summary>
        public const string Timeout = "http-dispatch-timeout";

        /// <summary>
        /// The HTTP runtime host returned a non-success HTTP status code.
        /// </summary>
        public const string HttpError = "http-command-failed";

        /// <summary>
        /// The HTTP runtime host returned a client-side non-retryable HTTP status code.
        /// </summary>
        public const string NonRetryableHttpError = "http-command-non-retryable";

        /// <summary>
        /// The HTTP runtime host returned an empty or invalid response body.
        /// </summary>
        public const string InvalidResponse = "http-command-invalid-response";

        /// <summary>
        /// The HTTP dispatch was rejected because the circuit breaker is open.
        /// </summary>
        public const string CircuitOpen = "http-circuit-open";

        /// <summary>
        /// The HTTP dispatch was cancelled by the caller.
        /// </summary>
        public const string Cancelled = "http-command-cancelled";

        /// <summary>
        /// The HTTP dispatch failed with an unexpected exception.
        /// </summary>
        public const string Exception = "http-command-exception";
    }
}