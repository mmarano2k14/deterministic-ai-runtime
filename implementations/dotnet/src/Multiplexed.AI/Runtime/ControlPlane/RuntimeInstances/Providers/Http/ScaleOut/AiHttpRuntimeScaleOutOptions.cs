namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut
{
    /// <summary>
    /// Defines HTTP runtime scale-out technical options.
    /// </summary>
    public sealed class AiHttpRuntimeScaleOutOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether HTTP runtime scale-out is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the default runtime instance id prefix used only when the tenant-aware request does not provide one.
        /// </summary>
        public string DefaultRuntimeInstanceIdPrefix { get; set; } = "http-runtime";

        /// <summary>
        /// Gets or sets the HTTP endpoint template used for newly materialized HTTP runtime instances.
        /// </summary>
        /// <remarks>
        /// Supported tokens:
        /// <c>{runtimeInstanceId}</c>, <c>{runtimeInstanceIdPrefix}</c>, <c>{tenantId}</c>, <c>{tenantGroupId}</c>, <c>{controlPlaneId}</c>.
        /// </remarks>
        public string EndpointTemplate { get; set; } = "http://localhost";
    }
}