using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.AI.Runtime.Execution.Retention.Models;

namespace Multiplexed.AI.Runtime.Execution.Retention.Services
{
    /// <summary>
    /// Default distributed-safe implementation of <see cref="IAiAtomicRetentionCompactionService"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This service prepares compaction candidates only. It archives step payloads and
    /// marks the archived payload index, but it does not mutate hot state directly.
    /// </para>
    ///
    /// <para>
    /// The final hot-state patch is applied atomically by the distributed DAG store.
    /// </para>
    ///
    /// <para>
    /// This service is used during active distributed execution, where multiple workers may
    /// still be claiming, running, completing, failing, retrying, compacting, or evicting steps
    /// concurrently.
    /// </para>
    /// </remarks>
    public sealed class DefaultAiAtomicRetentionCompactionService : IAiAtomicRetentionCompactionService
    {
        private readonly IAiStepPayloadStore _stepPayloadStore;
        private readonly IAiStepPayloadIndexStore _stepPayloadIndexStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiAtomicRetentionCompactionService"/> class.
        /// </summary>
        /// <param name="stepPayloadStore">
        /// The durable step payload store.
        /// </param>
        /// <param name="stepPayloadIndexStore">
        /// The archived step payload index store.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when a required dependency is <see langword="null"/>.
        /// </exception>
        public DefaultAiAtomicRetentionCompactionService(
            IAiStepPayloadStore stepPayloadStore,
            IAiStepPayloadIndexStore stepPayloadIndexStore)
        {
            _stepPayloadStore = stepPayloadStore ?? throw new ArgumentNullException(nameof(stepPayloadStore));
            _stepPayloadIndexStore = stepPayloadIndexStore ?? throw new ArgumentNullException(nameof(stepPayloadIndexStore));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyCollection<AiRetentionPatchCandidate>> BuildCandidatesAsync(
            AiExecutionState state,
            IReadOnlyCollection<string> stepNames,
            string reason = "retention",
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(stepNames);

            var candidates = new List<AiRetentionPatchCandidate>();

            foreach (var stepName in stepNames)
            {
                if (string.IsNullOrWhiteSpace(stepName))
                {
                    continue;
                }

                if (!state.Steps.TryGetValue(stepName, out var step))
                {
                    continue;
                }

                if (!IsSafeCandidateSnapshot(step))
                {
                    continue;
                }

                var effectiveReason = string.IsNullOrWhiteSpace(reason)
                    ? "retention-compaction"
                    : reason;

                var payload = await _stepPayloadStore.SaveStepAsync(
                        state.ExecutionId,
                        stepName,
                        step,
                        cancellationToken)
                    .ConfigureAwait(false);

                await _stepPayloadIndexStore.MarkArchivedAsync(
                        new AiArchivedStepPayloadIndex
                        {
                            ExecutionId = state.ExecutionId,
                            StepName = stepName,
                            Status = step.Status,
                            Payload = payload,
                            ArchivedAtUtc = DateTime.UtcNow,
                            Reason = effectiveReason
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                candidates.Add(
                    new AiRetentionPatchCandidate
                    {
                        StepName = stepName,
                        Action = AiRetentionPatchAction.Compact,
                        ExpectedStatus = step.Status,
                        ExpectedClaimToken = null,
                        ExpectedExecutionId = state.ExecutionId,
                        ArchivePayloadId = payload.ArtifactId,
                        Reason = effectiveReason
                    });
            }

            return candidates;
        }

        /// <summary>
        /// Determines whether the local snapshot is safe enough to build an atomic compaction candidate.
        /// </summary>
        /// <param name="step">
        /// The step state from the local execution state snapshot.
        /// </param>
        /// <returns>
        /// <c>true</c> when the snapshot represents a terminal non-evicted and non-compacted step; otherwise, <c>false</c>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method is only a pre-filter. The distributed DAG store must still re-check the
        /// current stored state atomically before applying the patch.
        /// </para>
        ///
        /// <para>
        /// A completed or failed step may still contain an old claim token as audit metadata.
        /// Therefore this method intentionally does not reject terminal steps based only on
        /// <c>ClaimToken</c>.
        /// </para>
        ///
        /// <para>
        /// Already compacted steps are ignored because their heavy result payload has already
        /// been archived and removed from hot state by a previous retention patch.
        /// </para>
        /// </remarks>
        private static bool IsSafeCandidateSnapshot(
            AiStepState step)
        {
            return step.Status is AiStepExecutionStatus.Completed or AiStepExecutionStatus.Failed &&
                   !step.IsEvictedFromHotState &&
                   !step.IsCompacted;
        }
    }
}