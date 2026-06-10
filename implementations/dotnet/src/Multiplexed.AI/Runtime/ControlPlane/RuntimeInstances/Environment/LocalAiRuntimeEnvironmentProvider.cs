using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;

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
    /// </remarks>
    public sealed class LocalAiRuntimeEnvironmentProvider : IAiRuntimeEnvironmentProvider
    {
        private readonly IAiRuntimeHostIdentity runtimeHostIdentity;

        public LocalAiRuntimeEnvironmentProvider(
            IAiRuntimeHostIdentity runtimeHostIdentity)
        {
            this.runtimeHostIdentity = runtimeHostIdentity
                ?? throw new ArgumentNullException(nameof(runtimeHostIdentity));
        }

        /// <inheritdoc />
        public Task<AiRuntimeEnvironmentSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var processId = System.Environment.ProcessId;
            var hostName = System.Environment.MachineName;

            var hostId =
                runtimeHostIdentity.HostId;

            const string runtimeId =
                "local-runtime-to-assign";

            var runtimeInstanceId =
                $"{hostId}:{runtimeId}";

            var controlPlaneHostId = "control-plane-host-to-assign";   

            return Task.FromResult(
                new AiRuntimeEnvironmentSnapshot
                {
                    ProviderName = "",
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
                        ["hostId"] = hostId,
                        ["runtimeId"] = runtimeId,
                        ["controlPlaneHostId"] = controlPlaneHostId
                    }
                });
        }
    }
}