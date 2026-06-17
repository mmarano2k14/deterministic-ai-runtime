using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.Abstractions.AI.Execution.Instance.Worker
{
    /// <summary>
    /// Represents one pipeline run request submitted to a runtime worker controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One request represents one pipeline run. Each request must create one new
    /// execution record and therefore one distinct execution identifier.
    /// </para>
    /// <para>
    /// The execution identifier must never be reused for another run, even when the
    /// same pipeline is executed multiple times.
    /// </para>
    /// <para>
    /// A pipeline definition can be supplied as raw JSON, as a JSON file path, or as
    /// an in-memory <see cref="AiPipelineDefinition"/> instance.
    /// </para>
    /// <para>
    /// Source priority is: raw JSON first, JSON file path second, in-memory pipeline
    /// definition third.
    /// </para>
    /// <para>
    /// The optional <see cref="ExecutionContextSnapshot"/> is the durable execution
    /// context captured by the control plane and propagated to runtime workers.
    /// It allows background runtime execution to restore the active RBAC execution
    /// context before creating the durable execution.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimePipelineRunRequest
    {
        /// <summary>
        /// Gets the pipeline name to execute.
        /// </summary>
        /// <remarks>
        /// This value is required and identifies which pipeline definition should be
        /// selected from the supplied JSON, JSON file, or in-memory definition.
        /// </remarks>
        public required string PipelineName { get; init; }

        /// <summary>
        /// Gets the optional durable execution context snapshot associated with this run.
        /// </summary>
        /// <remarks>
        /// This snapshot is captured when the shared run is submitted and is propagated
        /// through Redis, HTTP dispatch, and the local runtime queue.
        ///
        /// Runtime background workers use this value to restore the active RBAC
        /// execution context before creating the durable execution.
        ///
        /// The snapshot context key is volatile and must not be used as a durable
        /// execution identifier or tenant partition key. Persistent tenant isolation
        /// must use <see cref="ExecutionContextSnapshot.TenantId"/>.
        /// </remarks>
        public ExecutionContextSnapshot? ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets the optional raw JSON pipeline definition source.
        /// </summary>
        /// <remarks>
        /// When provided, this source has priority over <see cref="PipelineJsonFilePath"/>
        /// and <see cref="PipelineDefinition"/>.
        /// </remarks>
        public string? PipelineJson { get; init; }

        /// <summary>
        /// Gets the optional JSON pipeline definition file path.
        /// </summary>
        /// <remarks>
        /// This source is used when <see cref="PipelineJson"/> is empty.
        /// </remarks>
        public string? PipelineJsonFilePath { get; init; }

        /// <summary>
        /// Gets the optional in-memory pipeline definition.
        /// </summary>
        /// <remarks>
        /// This source is used when both <see cref="PipelineJson"/> and
        /// <see cref="PipelineJsonFilePath"/> are empty.
        /// </remarks>
        public AiPipelineDefinition? PipelineDefinition { get; init; }

        /// <summary>
        /// Gets the input payload used to seed the execution state.
        /// </summary>
        /// <remarks>
        /// Supported values are strings, dictionaries, anonymous objects, or <see langword="null"/>.
        /// The runtime controller is responsible for normalizing this value before creating
        /// the execution.
        /// </remarks>
        public object? Input { get; init; }
    }
}