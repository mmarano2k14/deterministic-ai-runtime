using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Represents rejected authority for claimed runtime-pool recovery transitions.
    /// </summary>
    public sealed class AiRuntimePoolRecoveryExecutionAuthorityException :
        InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolRecoveryExecutionAuthorityException"/> class.
        /// </summary>
        public AiRuntimePoolRecoveryExecutionAuthorityException(
            string failureId,
            string? localRunId,
            AiRuntimePoolRecoveryExecutionAuthorityFailure reason,
            string message)
            : base(message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);

            this.FailureId = failureId.Trim();
            this.LocalRunId = localRunId;
            this.Reason = reason;
        }

        /// <summary>
        /// Gets the exact failure observation identifier.
        /// </summary>
        public string FailureId { get; }

        /// <summary>
        /// Gets the candidate local run identifier, when applicable.
        /// </summary>
        public string? LocalRunId { get; }

        /// <summary>
        /// Gets the typed authority rejection reason.
        /// </summary>
        public AiRuntimePoolRecoveryExecutionAuthorityFailure Reason
        {
            get;
        }
    }
}
