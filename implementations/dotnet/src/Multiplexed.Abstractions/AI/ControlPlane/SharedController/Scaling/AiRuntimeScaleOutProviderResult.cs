namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents the result of a runtime scale-out provider operation.
    /// </summary>
    public sealed class AiRuntimeScaleOutProviderResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether the scale-out request was fulfilled.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the scale-out request was rejected by the provider.
        /// </summary>
        public bool Rejected { get; set; }

        /// <summary>
        /// Gets or sets the runtime instance identifier created or exposed by the provider.
        /// </summary>
        public string? RuntimeInstanceId { get; set; }

        /// <summary>
        /// Gets or sets an optional provider operation identifier.
        /// </summary>
        public string? ProviderOperationId { get; set; }

        /// <summary>
        /// Gets or sets a human-readable provider result message.
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// Gets or sets the failure reason when the provider operation failed.
        /// </summary>
        public string? FailureReason { get; set; }

        /// <summary>
        /// Gets or sets provider result metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>();
    }
}