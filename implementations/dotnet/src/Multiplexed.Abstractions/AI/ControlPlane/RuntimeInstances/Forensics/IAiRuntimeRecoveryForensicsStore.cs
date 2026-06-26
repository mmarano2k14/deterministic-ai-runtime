using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Stores runtime recovery forensics records.
    /// </summary>
    public interface IAiRuntimeRecoveryForensicsStore
    {
        /// <summary>
        /// Upserts a recovery forensics record.
        /// </summary>
        /// <param name="record">The recovery forensics record to upsert.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the record has been upserted.</returns>
        Task UpsertAsync(AiRuntimeRecoveryForensicsRecord record, CancellationToken cancellationToken = default);

        /// <summary>
        /// Appends an event to an existing recovery forensics record.
        /// </summary>
        /// <param name="forensicsId">The forensics identifier.</param>
        /// <param name="evt">The event to append.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the event has been appended.</returns>
        Task AppendEventAsync(string forensicsId, AiRuntimeRecoveryForensicsEvent evt, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a recovery forensics record by forensics identifier.
        /// </summary>
        /// <param name="forensicsId">The forensics identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The matching recovery forensics record when found; otherwise, null.</returns>
        Task<AiRuntimeRecoveryForensicsRecord?> GetByForensicsIdAsync(string forensicsId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists recovery forensics records for a durable execution identifier.
        /// </summary>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The matching recovery forensics records.</returns>
        Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByExecutionIdAsync(string executionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists recovery forensics records for a shared run identifier.
        /// </summary>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The matching recovery forensics records.</returns>
        Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListBySharedRunIdAsync(string sharedRunId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists recovery forensics records associated with a runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The matching recovery forensics records.</returns>
        Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeInstanceIdAsync(string runtimeInstanceId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists recovery forensics records associated with a runtime failure incident.
        /// </summary>
        /// <param name="runtimeFailureIncidentId">The runtime failure incident identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The matching recovery forensics records.</returns>
        Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeFailureIncidentIdAsync(string runtimeFailureIncidentId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists recent recovery forensics records.
        /// </summary>
        /// <param name="limit">The maximum number of records to return.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The recent recovery forensics records.</returns>
        Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);
    }
}