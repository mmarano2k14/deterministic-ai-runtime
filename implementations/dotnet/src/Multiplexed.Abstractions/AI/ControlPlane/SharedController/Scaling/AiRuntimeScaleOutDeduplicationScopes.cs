namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines canonical deduplication-scope values used by runtime scale-out coordination.
    /// </summary>
    public static class AiRuntimeScaleOutDeduplicationScopes
    {
        /// <summary>
        /// Gets the deduplication scope used for recovery-driven replacement capacity.
        /// </summary>
        public const string RecoveryReplacement = "recovery-replacement";
    }
}
