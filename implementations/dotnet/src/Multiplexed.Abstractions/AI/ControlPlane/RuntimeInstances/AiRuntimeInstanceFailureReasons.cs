namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// Defines transport-neutral runtime instance failure reason codes shared across control-plane components.
    /// </summary>
    public static class AiRuntimeInstanceFailureReasons
    {
        /// <summary>The requested runtime instance could not be found.</summary>
        public const string RuntimeInstanceNotFound = "runtime-instance-not-found";

        /// <summary>The runtime instance identifier required by an operation is missing.</summary>
        public const string RuntimeInstanceIdMissing = "runtime-instance-id-missing";

        /// <summary>The runtime status is outside the statuses handled by the current reconciliation operation.</summary>
        public const string RuntimeStatusNotIncluded = "runtime-status-not-included";

        /// <summary>The selected runtime instance cannot be routed by the active provider path.</summary>
        public const string RuntimeInstanceNotRoutable = "runtime-instance-not-routable";
    }
}
