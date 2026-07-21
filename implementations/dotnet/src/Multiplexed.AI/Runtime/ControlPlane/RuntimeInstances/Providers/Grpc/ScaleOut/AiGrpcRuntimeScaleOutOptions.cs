using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut
{
    /// <summary>
    /// Defines gRPC runtime scale-out technical options.
    /// </summary>
    public sealed class AiGrpcRuntimeScaleOutOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether gRPC runtime scale-out is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the gRPC runtime scale-out mode.
        /// </summary>
        /// <remarks>
        /// <para>
        /// MetadataOnly only materializes registry and capacity metadata.
        /// </para>
        ///
        /// <para>
        /// HostManager starts or attaches a runtime instance through the provider-agnostic runtime host manager,
        /// then waits for readiness before fulfilling the scale-out request.
        /// </para>
        /// </remarks>
        public string Mode { get; set; } = AiGrpcRuntimeScaleOutModes.MetadataOnly;

        /// <summary>
        /// Gets or sets the default runtime instance id prefix used only when the tenant-aware request does not provide one.
        /// </summary>
        public string DefaultRuntimeInstanceIdPrefix { get; set; } = AiGrpcRuntimeProviderConstants.DefaultRuntimeInstanceIdPrefix;

        /// <summary>
        /// Gets or sets the gRPC endpoint template used for newly materialized gRPC runtime instances.
        /// </summary>
        /// <remarks>
        /// Supported tokens: {runtimeInstanceId}, {runtimeInstanceIdPrefix}, {tenantId}, {tenantGroupId}, {controlPlaneId}.
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
        /// Gets or sets the maximum number of process hosts that may be concurrently started and held in readiness.
        /// </summary>
        /// <remarks>
        /// A value less than or equal to zero disables process-host startup gating.
        /// The gate is applied only when <see cref="HostCreationMode"/> is <see cref="AiRuntimeHostCreationMode.Process"/>.
        /// </remarks>
        public int MaxConcurrentProcessHostStartups { get; set; }

        /// <summary>
        /// Gets or sets the process-wide concurrency key used to coordinate process-host startup across provisioner instances.
        /// </summary>
        public string ProcessHostStartupConcurrencyKey { get; set; } = "grpc-process-host-startup";

        /// <summary>
        /// Gets or sets the physical host creation mode used when gRPC scale-out mode is HostManager.
        /// </summary>
        public AiRuntimeHostCreationMode HostCreationMode { get; set; } = AiRuntimeHostCreationMode.Fixture;
    }
}