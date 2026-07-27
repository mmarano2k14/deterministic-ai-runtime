using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents an attempt to bind one runtime instance identifier to conflicting route
    /// authority.
    /// </summary>
    public sealed class AiRuntimePoolRouteConflictException :
        InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolRouteConflictException"/> class.
        /// </summary>
        /// <param name="runtimeInstanceId">The conflicting runtime instance identifier.</param>
        public AiRuntimePoolRouteConflictException(
            string runtimeInstanceId)
            : base(
                $"RuntimeInstanceId '{runtimeInstanceId}' is already bound to a different route incarnation or endpoint.")
        {
            this.RuntimeInstanceId = runtimeInstanceId;
        }

        /// <summary>
        /// Gets the conflicting runtime instance identifier.
        /// </summary>
        public string RuntimeInstanceId { get; }
    }
}
