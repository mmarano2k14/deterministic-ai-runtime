using System.Collections.Concurrent;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using SystemProcess = global::System.Diagnostics.Process;

namespace Multiplexed.AI.Tests.Integration.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Tracks concrete operating-system children while delegating production startup to the system
    /// launcher.
    /// </summary>
    internal sealed class TrackingSystemRuntimeProcessPoolLauncher :
        IAiRuntimeProcessPoolChildProcessLauncher
    {
        private readonly SystemAiRuntimeProcessPoolChildProcessLauncher inner =
            new();
        private readonly ConcurrentDictionary<
            string,
            SystemAiRuntimeProcessPoolChild> children =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Gets all real child processes created during the proof.
        /// </summary>
        public IReadOnlyCollection<SystemAiRuntimeProcessPoolChild> Children =>
            this.children.Values.ToArray();

        /// <inheritdoc />
        public async Task<IAiRuntimeProcessPoolChild> StartAsync(
            AiRuntimeProcessPoolChildStartRequest request,
            AiRuntimeProcessPoolChildProcessOptions options,
            CancellationToken cancellationToken = default)
        {
            var child =
                await this.inner
                    .StartAsync(
                        request,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);

            var systemChild =
                child as SystemAiRuntimeProcessPoolChild
                ?? throw new InvalidOperationException(
                    "The production system launcher did not return a SystemAiRuntimeProcessPoolChild.");

            if (!this.children.TryAdd(
                    systemChild.RuntimeInstanceId,
                    systemChild))
            {
                await systemChild
                    .StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"A child with RuntimeInstanceId '{systemChild.RuntimeInstanceId}' was already tracked.");
            }

            return systemChild;
        }

        /// <summary>
        /// Kills one exact child process without using its graceful stop contract.
        /// </summary>
        public void KillUnexpectedly(
            string runtimeInstanceId)
        {
            if (!this.children.TryGetValue(
                    runtimeInstanceId,
                    out var child))
            {
                throw new InvalidOperationException(
                    $"No tracked child exists for RuntimeInstanceId '{runtimeInstanceId}'.");
            }

            using var process =
                SystemProcess.GetProcessById(
                    child.ProcessId);

            process.Kill(
                entireProcessTree: true);
        }
    }
}
