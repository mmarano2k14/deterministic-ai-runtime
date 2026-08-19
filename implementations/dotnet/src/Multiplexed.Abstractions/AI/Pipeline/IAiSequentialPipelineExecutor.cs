using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;

namespace Multiplexed.Abstractions.AI.Pipeline
{
    /// <summary>
    /// Executes a resolved AI pipeline using sequential orchestration.
    /// </summary>
    public interface IAiSequentialPipelineExecutor
    {
        /// <summary>
        /// Resolves the specified pipeline into an executable runtime pipeline.
        /// </summary>
        Task<ResolvedAiPipeline> PrepareAsync(
            string pipelineName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves the supplied declarative pipeline definition into an executable runtime pipeline.
        /// </summary>
        /// <remarks>
        /// This overload is used when a caller already owns an immutable declarative definition and must not
        /// re-resolve a newer provider definition by pipeline name.
        /// </remarks>
        /// <param name="definition">The exact declarative pipeline definition to resolve.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved runtime pipeline.</returns>
        Task<ResolvedAiPipeline> PrepareAsync(
            AiPipelineDefinition definition,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes the next sequential step of the supplied resolved pipeline.
        /// </summary>
        Task<PipelineExecutionResult> ExecuteNextAsync(
            ResolvedAiPipeline pipeline,
            AiExecutionContext context,
            CancellationToken cancellationToken = default);
    }
}