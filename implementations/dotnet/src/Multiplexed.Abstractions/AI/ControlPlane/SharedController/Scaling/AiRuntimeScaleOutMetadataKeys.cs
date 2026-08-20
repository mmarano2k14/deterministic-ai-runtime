namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines canonical metadata keys used by runtime scale-out coordination.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing physical metadata names used across
    /// scale-out publication, persistence, provider selection, provisioning,
    /// fulfillment, and replacement-capacity flows.
    /// </remarks>
    public static class AiRuntimeScaleOutMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the scale-out request identifier.
        /// </summary>
        public const string RequestId = "scaleout.requestId";

        /// <summary>
        /// Gets the legacy dotted scale-out request identifier metadata key.
        /// </summary>
        public const string LegacyRequestId = "scaleout.request.id";

        /// <summary>
        /// Gets the metadata key carrying the scale-out intent.
        /// </summary>
        public const string Intent = "scaleout.intent";

        /// <summary>
        /// Gets the metadata key carrying the runtime instance identifier that replacement scale-out must not reuse.
        /// </summary>
        public const string ExcludedRuntimeInstanceId =
            "scaleout.excludedRuntimeInstanceId";

        /// <summary>
        /// Gets the metadata key carrying the runtime instance identifier being replaced.
        /// </summary>
        public const string ReplacementForRuntimeInstanceId =
            "scaleout.replacementForRuntimeInstanceId";

        /// <summary>
        /// Gets the metadata key carrying the scale-out deduplication scope.
        /// </summary>
        public const string DeduplicationScope = "scaleout.dedup.scope";

        /// <summary>
        /// Gets the metadata key carrying the scale-out provider name.
        /// </summary>
        public const string Provider = "scaleout.provider";

        /// <summary>
        /// Gets the metadata key carrying the shared run identifier associated with scale-out.
        /// </summary>
        public const string SharedRunId = "scaleout.sharedRunId";

        /// <summary>
        /// Gets the legacy dotted shared-run identifier metadata key used by scale-out compatibility payloads.
        /// </summary>
        public const string LegacySharedRunId = "scaleout.shared.run.id";

        /// <summary>
        /// Gets the metadata key carrying the runtime instance identifier fulfilled by scale-out.
        /// </summary>
        public const string RuntimeInstanceId = "scaleout.runtimeInstanceId";

        /// <summary>
        /// Gets the camel-case metadata key carrying the scale-out provider hint.
        /// </summary>
        public const string ProviderHint = "providerHint";

        /// <summary>
        /// Gets the camel-case scale-out request identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseScaleOutRequestId = "scaleOutRequestId";

        /// <summary>
        /// Gets the compact camel-case request identifier metadata key used by scale-out persistence and diagnostics.
        /// </summary>
        public const string CamelCaseRequestId = "requestId";
    }
}
