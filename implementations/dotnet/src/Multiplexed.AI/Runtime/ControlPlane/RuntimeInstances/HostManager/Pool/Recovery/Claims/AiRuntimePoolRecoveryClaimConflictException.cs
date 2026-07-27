using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Represents conflicting recovery authority for one immutable failure identifier.
    /// </summary>
    public sealed class AiRuntimePoolRecoveryClaimConflictException :
        InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolRecoveryClaimConflictException"/> class.
        /// </summary>
        /// <param name="failureId">The conflicting failure identifier.</param>
        public AiRuntimePoolRecoveryClaimConflictException(
            string failureId)
            : base(
                $"FailureId '{failureId}' is already bound to another recovery claim authority or inventory.")
        {
            this.FailureId = failureId;
        }

        /// <summary>
        /// Gets the conflicting failure identifier.
        /// </summary>
        public string FailureId { get; }
    }
}
