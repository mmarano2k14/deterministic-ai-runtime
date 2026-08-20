using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;


namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Environment
{
    /// <summary>
    /// Provides runtime environment information for a local process.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Supplies provider-neutral environment metadata when the runtime is running
    ///   as a local process, test host, console app, Windows service, or simple container.
    /// - Avoids coupling runtime instance registration to Kubernetes-specific concepts.
    /// - Gives priority to explicit runtime registration configuration when a runtime instance
    ///   is started by the control plane through a host manager.
    /// </remarks>
    public sealed class LocalAiRuntimeEnvironmentProvider : IAiRuntimeEnvironmentProvider
    {
        /// <summary>
        /// The runtime host identity.
        /// </summary>
        private readonly IAiRuntimeHostIdentity runtimeHostIdentity;

        /// <summary>
        /// The runtime instance registration options.
        /// </summary>
        private readonly AiRuntimeInstanceRegistrationOptions registrationOptions;

        /// <summary>
        /// The application configuration.
        /// </summary>
        private readonly IConfiguration configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalAiRuntimeEnvironmentProvider"/> class.
        /// </summary>
        /// <param name="runtimeHostIdentity">The runtime host identity.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="configuration">The application configuration.</param>
        public LocalAiRuntimeEnvironmentProvider(
            IAiRuntimeHostIdentity runtimeHostIdentity,
            IOptions<AiRuntimeInstanceRegistrationOptions> registrationOptions,
            IConfiguration configuration)
        {
            this.runtimeHostIdentity = runtimeHostIdentity
                ?? throw new ArgumentNullException(nameof(runtimeHostIdentity));

            this.registrationOptions = registrationOptions?.Value
                ?? throw new ArgumentNullException(nameof(registrationOptions));

            this.configuration = configuration
                ?? throw new ArgumentNullException(nameof(configuration));
        }

        /// <inheritdoc />
        public Task<AiRuntimeEnvironmentSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var processId = System.Environment.ProcessId;
            var hostName = System.Environment.MachineName;
            var hostId = this.runtimeHostIdentity.HostId;

            var configuredRuntimeInstanceId = this.registrationOptions.RuntimeInstanceId;
            var configuredControlPlaneId = this.configuration["AiRuntimeInstanceRegistration:ControlPlaneId"];
            var configuredProviderName = this.configuration["AiRuntimeInstanceRegistration:ProviderName"];

            var runtimeId = !string.IsNullOrWhiteSpace(configuredRuntimeInstanceId)
                ? configuredRuntimeInstanceId
                : "local-runtime-to-assign";

            var runtimeInstanceId = !string.IsNullOrWhiteSpace(configuredRuntimeInstanceId)
                ? configuredRuntimeInstanceId
                : $"{hostId}:{runtimeId}";

            var controlPlaneHostId = !string.IsNullOrWhiteSpace(configuredControlPlaneId)
                ? configuredControlPlaneId
                : "control-plane-host-to-assign";

            var providerName = configuredProviderName ?? string.Empty;

            return Task.FromResult(
                new AiRuntimeEnvironmentSnapshot
                {
                    ProviderName = providerName,
                    RuntimeInstanceId = runtimeInstanceId,
                    HostId = hostId,
                    RuntimeId = runtimeId,
                    ControlPlaneHostId = controlPlaneHostId,
                    HostName = hostName,
                    ProcessId = processId,
                    ProviderMetadata = new Dictionary<string, string>
                    {
                        ["machineName"] = hostName,
                        ["processId"] = processId.ToString(),
                        [AiRuntimeHostMetadataKeys.CamelCaseHostId] = hostId,
                        ["runtimeId"] = runtimeId,
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = runtimeInstanceId,
                        ["controlPlaneHostId"] = controlPlaneHostId,
                        [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = providerName
                    }
                });
        }
    }
}