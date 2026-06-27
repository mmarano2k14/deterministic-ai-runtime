using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Provides read-only query access to runtime recovery forensics records.
    /// </summary>
    /// <remarks>
    /// This service is a read model only. It must never be used as the source of truth
    /// for runtime recovery decisions. Recovery ownership remains with the shared queue,
    /// shared run store, runtime run execution index, DAG state, snapshots, registry,
    /// and capacity stores.
    /// </remarks>
    public interface IAiRuntimeRecoveryForensicsQueryService
    {
        /// <summary>
        /// Gets one recovery forensics record by its stable forensics identifier.
        /// </summary>
        /// <param name="forensicsId">The recovery forensics identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The matching read model, or <c>null</c>.</returns>
        Task<AiRuntimeRecoveryForensicsReadModel?> GetByForensicsIdAsync(
            string forensicsId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Searches runtime recovery forensics records using read-only query criteria.
        /// </summary>
        /// <param name="query">The query criteria.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The query result.</returns>
        Task<AiRuntimeRecoveryForensicsQueryResult> SearchAsync(
            AiRuntimeRecoveryForensicsQuery query,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets an ordered recovery timeline by forensics identifier.
        /// </summary>
        /// <param name="forensicsId">The recovery forensics identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The ordered recovery timeline.</returns>
        Task<IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem>> GetTimelineAsync(
            string forensicsId,
            CancellationToken cancellationToken = default);
    }
}
