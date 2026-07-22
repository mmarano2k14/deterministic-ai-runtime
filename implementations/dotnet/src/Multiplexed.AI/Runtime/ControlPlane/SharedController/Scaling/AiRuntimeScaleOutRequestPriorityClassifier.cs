namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Classifies persisted scale-out requests for process-wide scheduling priority.
    /// </summary>
    /// <remarks>
    /// Generic control-plane identity aliases such as
    /// <c>recovery.control-plane.id</c> are deliberately not recovery markers.
    /// They can be present on ordinary initial scale-out requests. Recovery priority
    /// is granted only when the request identity, source, reason, or work-specific
    /// metadata proves that the request belongs to crash recovery.
    /// </remarks>
    public static class AiRuntimeScaleOutRequestPriorityClassifier
    {
        /// <summary>
        /// Returns whether a scale-out request belongs to crash recovery.
        /// </summary>
        /// <param name="requestId">The persisted request identifier.</param>
        /// <param name="metadataKeys">The persisted metadata keys.</param>
        /// <param name="source">The request source.</param>
        /// <param name="reason">The request reason.</param>
        /// <returns><c>true</c> for recovery work; otherwise, <c>false</c>.</returns>
        public static bool IsRecoveryRequest(
            string requestId,
            IEnumerable<string>? metadataKeys,
            string? source,
            string? reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

            if (requestId.StartsWith(
                    "scale-out-redispatch-",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (metadataKeys is not null &&
                metadataKeys.Any(IsRecoveryWorkMetadataKey))
            {
                return true;
            }

            return ContainsRecoveryMarker(source) ||
                   ContainsRecoveryMarker(reason);
        }

        /// <summary>
        /// Returns whether a metadata key represents recovery work rather than
        /// a generic control-plane identity alias.
        /// </summary>
        private static bool IsRecoveryWorkMetadataKey(
            string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return key.StartsWith("failed.", StringComparison.OrdinalIgnoreCase) ||
                   key.StartsWith("recovery.failed", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("recovery.mode", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("recovery.reason", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("recovery.forensicsId", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("recovery.runtimeFailureIncidentId", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns whether a value contains an explicit recovery marker.
        /// </summary>
        private static bool ContainsRecoveryMarker(
            string? value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Contains("recovery", StringComparison.OrdinalIgnoreCase);
        }
    }
}
