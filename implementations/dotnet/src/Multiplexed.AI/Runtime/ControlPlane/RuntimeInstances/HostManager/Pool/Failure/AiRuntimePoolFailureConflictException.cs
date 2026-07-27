using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Represents conflicting reuse of one immutable failure observation identifier.
    /// </summary>
    public sealed class AiRuntimePoolFailureConflictException :
        InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolFailureConflictException"/> class.
        /// </summary>
        /// <param name="failureId">The conflicting failure identifier.</param>
        public AiRuntimePoolFailureConflictException(
            string failureId)
            : base(
                $"FailureId '{failureId}' is already bound to another runtime-pool failure observation.")
        {
            this.FailureId = failureId;
        }

        /// <summary>
        /// Gets the conflicting failure identifier.
        /// </summary>
        public string FailureId { get; }
    }
}
