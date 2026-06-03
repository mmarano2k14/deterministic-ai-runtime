namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Defines the logical role of a runtime registration.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Distinguishes executable runtime instances from control-plane hosts.
    /// - Allows admission policies to exclude non-dispatchable registrations.
    /// - Supports future Kubernetes control-plane and runtime separation.
    ///
    /// EXAMPLES:
    ///
    /// Runtime:
    ///   RuntimeInstanceId = "mcp-runtime-1"
    ///   Role = Runtime
    ///
    /// Control plane:
    ///   RuntimeInstanceId = "mcp-control-plane"
    ///   Role = ControlPlane
    /// </remarks>
    public enum AiRuntimeInstanceRole
    {
        /// <summary>
        /// Executable runtime instance capable of accepting and executing runs.
        /// </summary>
        Runtime = 0,

        /// <summary>
        /// Control-plane host responsible for orchestration, admission,
        /// queue management, observability, and runtime coordination.
        /// </summary>
        ControlPlane = 1
    }
}