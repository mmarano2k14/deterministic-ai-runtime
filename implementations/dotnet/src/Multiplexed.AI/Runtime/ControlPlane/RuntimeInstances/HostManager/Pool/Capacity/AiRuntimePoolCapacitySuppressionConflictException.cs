using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Represents conflicting reuse of one immutable runtime-instance capacity identity.
    /// </summary>
    public sealed class AiRuntimePoolCapacitySuppressionConflictException :
        InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolCapacitySuppressionConflictException"/> class.
        /// </summary>
        /// <param name="runtimeInstanceId">The conflicting runtime instance identifier.</param>
        public AiRuntimePoolCapacitySuppressionConflictException(
            string runtimeInstanceId)
            : base(
                $"RuntimeInstanceId '{runtimeInstanceId}' is already bound to another capacity suppression.")
        {
            this.RuntimeInstanceId = runtimeInstanceId;
        }

        /// <summary>
        /// Gets the conflicting runtime instance identifier.
        /// </summary>
        public string RuntimeInstanceId { get; }
    }
}
