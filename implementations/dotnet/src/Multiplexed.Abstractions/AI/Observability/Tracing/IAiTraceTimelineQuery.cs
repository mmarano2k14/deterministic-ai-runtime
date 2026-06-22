using Multiplexed.Abstractions.AI.Observability.Tracing;

namespace Multiplexed.Abstractions.AI.Observability.Tracing
{
    /// <summary>
    /// Queries trace timeline events across process-local and durable trace stores.
    /// </summary>
    public interface IAiTraceTimelineQuery
    {
        /// <summary>
        /// Gets ordered trace events for an execution.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The ordered trace events.</returns>
        Task<IReadOnlyList<AiTraceEvent>> GetByExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default);
    }
}