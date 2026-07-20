namespace Multiplexed.Abstractions.AI.ControlPlane.Discovery
{
    /// <summary>
    /// Describes the inputs used to resolve a logical control-plane identifier.
    /// </summary>
    public sealed class AiControlPlaneIdResolutionRequest
    {
        /// <summary>
        /// Gets or sets the explicit requested control-plane identifier.
        /// </summary>
        public string? RequestedControlPlaneId { get; set; }

        /// <summary>
        /// Gets or sets the metadata that may contain a logical control-plane identifier.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; set; }

        /// <summary>
        /// Gets or sets the diagnostic source of the resolution request.
        /// </summary>
        public string? Source { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether generated host identifiers can be used as a fallback.
        /// </summary>
        public bool AllowGeneratedFallback { get; set; } = true;
    }
}