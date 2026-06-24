using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health
{
    /// <summary>
    /// Represents a runtime instance health reconciliation decision.
    /// </summary>
    public sealed class AiRuntimeInstanceHealthDecision
    {
        /// <summary>
        /// Gets or sets the runtime instance identifier.
        /// </summary>
        public string RuntimeInstanceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the previous runtime instance status.
        /// </summary>
        public AiRuntimeInstanceStatus PreviousStatus { get; set; }

        /// <summary>
        /// Gets or sets the new runtime instance status.
        /// </summary>
        public AiRuntimeInstanceStatus NewStatus { get; set; }

        /// <summary>
        /// Gets or sets the decision reason.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the last heartbeat timestamp.
        /// </summary>
        public DateTimeOffset LastHeartbeatAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the decision timestamp.
        /// </summary>
        public DateTimeOffset DecisionAtUtc { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the decision changed registry state.
        /// </summary>
        public bool Changed { get; set; }
    }
}