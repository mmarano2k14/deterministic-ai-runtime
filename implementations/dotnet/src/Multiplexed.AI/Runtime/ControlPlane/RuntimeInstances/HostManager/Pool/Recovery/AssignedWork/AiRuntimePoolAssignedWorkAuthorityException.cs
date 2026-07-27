using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Represents rejected authority for exact assigned-work enumeration.
    /// </summary>
    public sealed class AiRuntimePoolAssignedWorkAuthorityException :
        InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolAssignedWorkAuthorityException"/> class.
        /// </summary>
        /// <param name="failureId">The requested failure identifier.</param>
        /// <param name="reason">The typed rejection reason.</param>
        /// <param name="message">The diagnostic message.</param>
        public AiRuntimePoolAssignedWorkAuthorityException(
            string failureId,
            AiRuntimePoolAssignedWorkAuthorityFailure reason,
            string message)
            : base(message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);

            this.FailureId = failureId.Trim();
            this.Reason = reason;
        }

        /// <summary>
        /// Gets the requested failure observation identifier.
        /// </summary>
        public string FailureId { get; }

        /// <summary>
        /// Gets the typed authority rejection reason.
        /// </summary>
        public AiRuntimePoolAssignedWorkAuthorityFailure Reason { get; }
    }
}
