namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>Defines stable outcome codes emitted by runtime recovery diagnostics and forensics.</summary>
    public static class AiRuntimeRecoveryOutcomeCodes
    {
        /// <summary>The candidate is recoverable.</summary>
        public const string Recoverable = "recoverable";

        /// <summary>The candidate is not recoverable.</summary>
        public const string NotRecoverable = "not-recoverable";
    }
}
