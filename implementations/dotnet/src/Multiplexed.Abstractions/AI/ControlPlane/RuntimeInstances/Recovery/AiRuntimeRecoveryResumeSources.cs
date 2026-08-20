namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Defines canonical source values used when recovery seeds a resume context.
    /// </summary>
    public static class AiRuntimeRecoveryResumeSources
    {
        /// <summary>
        /// Gets the source identifying a durable shared-run execution-context snapshot.
        /// </summary>
        public const string SharedRunExecutionContextSnapshot =
            "shared-run.execution-context-snapshot";
    }
}
