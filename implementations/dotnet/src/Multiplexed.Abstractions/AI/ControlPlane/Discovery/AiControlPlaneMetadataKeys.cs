namespace Multiplexed.Abstractions.AI.ControlPlane.Discovery
{
    /// <summary>
    /// Defines canonical metadata keys and compatibility aliases for logical control-plane identity.
    /// </summary>
    public static class AiControlPlaneMetadataKeys
    {
        /// <summary>Gets the canonical control-plane identifier metadata key.</summary>
        public const string ControlPlaneId = "controlPlaneId";

        /// <summary>Gets the legacy dotted control-plane identifier metadata key.</summary>
        public const string LegacyDottedControlPlaneId = "control.plane.id";

        /// <summary>Gets the runtime-scoped control-plane identifier metadata key.</summary>
        public const string RuntimeControlPlaneId = "runtime.controlPlaneId";

        /// <summary>Gets the dashed compatibility control-plane identifier metadata key.</summary>
        public const string DashedControlPlaneId = "control-plane.id";

        /// <summary>Gets the compact compatibility control-plane identifier metadata key.</summary>
        public const string CompactControlPlaneId = "controlplane.id";

        /// <summary>Gets the MCP camel-case control-plane identifier metadata key.</summary>
        public const string McpControlPlaneId = "mcp.controlPlaneId";

        /// <summary>Gets the MCP dashed control-plane identifier metadata key.</summary>
        public const string McpDashedControlPlaneId = "mcp.control-plane.id";
    }
}
