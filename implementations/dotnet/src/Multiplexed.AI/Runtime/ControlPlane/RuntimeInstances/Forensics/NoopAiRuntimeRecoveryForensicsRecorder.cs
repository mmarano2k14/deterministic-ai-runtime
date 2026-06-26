using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Provides a no-op runtime recovery forensics recorder.
    /// </summary>
    public sealed class NoopAiRuntimeRecoveryForensicsRecorder : IAiRuntimeRecoveryForensicsRecorder
    {
        /// <inheritdoc />
        public Task RecordAsync(AiRuntimeRecoveryForensicsRecord record, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RecordEventAsync(AiRuntimeRecoveryForensicsEvent evt, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}