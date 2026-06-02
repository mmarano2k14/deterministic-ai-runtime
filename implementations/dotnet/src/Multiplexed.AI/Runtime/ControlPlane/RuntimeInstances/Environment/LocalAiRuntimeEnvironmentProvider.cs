using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;

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
        /// <inheritdoc />
        public Task<AiRuntimeEnvironmentSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var processId = System.Environment.ProcessId;

            var hostName = System.Environment.MachineName;

            return Task.FromResult(
                new AiRuntimeEnvironmentSnapshot
                {
                    ProviderName = "local",
                    RuntimeInstanceId = $"local:{hostName}:{processId}",
                    HostName = hostName,
                    ProcessId = processId,
                    ProviderMetadata = new Dictionary<string, string>
                    {
                        ["machineName"] = hostName,
                        ["processId"] = processId.ToString()
                    }
                });
        }
    }
}