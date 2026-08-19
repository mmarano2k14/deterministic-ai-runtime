using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;
using Multiplexed.AI.Runtime.Execution.Payloads.Serialization;

namespace Multiplexed.AI.Runtime.Execution.Engine.Helpers
{
    /// <summary>
    /// Resolves the declarative pipeline definition bound to one durable DAG execution.
    /// </summary>
    /// <remarks>
    /// Historical executions without an immutable definition snapshot preserve the existing name-based provider
    /// lookup. Executions with a snapshot always reload and verify that exact payload and never consult the mutable
    /// latest-definition provider, which keeps multi-step execution and crash recovery pinned to one definition.
    /// </remarks>
    internal static class AiExecutionBoundPipelineResolver
    {
        /// <summary>
        /// Resolves the pipeline that must drive the supplied durable execution record.
        /// </summary>
        /// <param name="services">The DAG engine services.</param>
        /// <param name="record">The authoritative durable execution record.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved pipeline bound to the execution.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a pinned definition is invalid, has no explicit version, targets another pipeline name,
        /// or is not a DAG definition.
        /// </exception>
        public static async Task<ResolvedAiPipeline> PrepareAsync(
            IAiDagExecutionEngineServices services,
            AiExecutionRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(record);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.PipelineName);

            if (record.PipelineDefinitionSnapshot is null)
            {
                return await services.PipelineExecutor
                    .PrepareAsync(record.PipelineName, cancellationToken)
                    .ConfigureAwait(false);
            }

            var reader = new AiImmutableJsonPayloadReader(services.PayloadStoreResolver);
            var canonicalJson = await reader
                .LoadAndVerifyAsync(record.PipelineDefinitionSnapshot, cancellationToken)
                .ConfigureAwait(false);

            var definition = AiCanonicalJson.Deserialize<AiPipelineDefinition>(canonicalJson);

            if (!string.Equals(definition.Name, record.PipelineName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Execution '{record.ExecutionId}' is pinned to pipeline definition '{definition.Name}', not '{record.PipelineName}'.");
            }

            if (string.IsNullOrWhiteSpace(definition.Version))
            {
                throw new InvalidOperationException(
                    $"Execution '{record.ExecutionId}' is pinned to pipeline '{record.PipelineName}' without an explicit definition version.");
            }

            if (definition.ExecutionMode != AiExecutionMode.Dag)
            {
                throw new InvalidOperationException(
                    $"Execution '{record.ExecutionId}' is pinned to non-DAG pipeline mode '{definition.ExecutionMode}'.");
            }

            return await services.PipelineExecutor
                .PrepareAsync(definition, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
