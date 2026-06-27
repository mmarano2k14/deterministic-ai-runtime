using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Records runtime recovery forensics evidence.
    /// </summary>
    public interface IAiRuntimeRecoveryForensicsRecorder
    {
        /// <summary>
        /// Starts or updates a recovery forensics record.
        /// </summary>
        /// <param name="record">The recovery forensics record to start or update.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the record has been recorded.</returns>
        Task RecordAsync(AiRuntimeRecoveryForensicsRecord record, CancellationToken cancellationToken = default);

        /// <summary>
        /// Appends a recovery forensics event.
        /// </summary>
        /// <param name="evt">The recovery forensics event to append.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the event has been recorded.</returns>
        Task RecordEventAsync(AiRuntimeRecoveryForensicsEvent evt, CancellationToken cancellationToken = default);
    }
}