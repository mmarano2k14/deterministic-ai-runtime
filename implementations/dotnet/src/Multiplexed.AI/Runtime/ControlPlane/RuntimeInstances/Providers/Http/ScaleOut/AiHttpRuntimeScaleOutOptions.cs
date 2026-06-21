using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

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
        /// Gets or sets the HTTP runtime scale-out mode.
        /// </summary>
        /// <remarks>
        /// <c>MetadataOnly</c> preserves the existing behavior and only materializes registry and capacity metadata.
        /// <c>HostManager</c> starts or attaches a runtime instance through the provider-agnostic runtime host manager,
        /// then waits for readiness before fulfilling the scale-out request.
        /// </remarks>
        public string Mode { get; set; } = AiHttpRuntimeScaleOutModes.MetadataOnly;

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

        /// <summary>
        /// Gets or sets a value indicating whether readiness must be validated in host-manager mode.
        /// </summary>
        public bool RequireReadiness { get; set; } = true;

        /// <summary>
        /// Gets or sets the readiness timeout in seconds.
        /// </summary>
        public int ReadinessTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets the readiness poll interval in milliseconds.
        /// </summary>
        public int ReadinessPollIntervalMilliseconds { get; set; } = 250;

        /// <summary>
        /// Gets or sets the physical host creation mode used when HTTP scale-out mode is HostManager.
        /// </summary>
        public AiRuntimeHostCreationMode HostCreationMode { get; set; } = AiRuntimeHostCreationMode.Fixture;
    }
}