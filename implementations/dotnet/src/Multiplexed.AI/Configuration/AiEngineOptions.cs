using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.AI.Configuration
{
    /// <summary>
    /// Defines configuration options for the AI runtime engine.
    ///
    /// Responsibilities:
    /// - Select the default pipeline definition source
    /// - Provide source-specific settings
    /// - Group runtime-related option sections
    /// - Prepare the runtime for future extensibility
    ///
    /// Notes:
    /// - The execution engine does not consume every option directly.
    /// - Some sections are primarily used by pipeline preparation components,
    ///   persistence wiring, cleanup behavior, and future runtime services.
    /// </summary>
    public sealed class AiEngineOptions
    {
        /// <summary>
        /// Gets or sets the default pipeline definition source.
        ///
        /// Expected values:
        /// - InMemory
        /// - Json
        ///
        /// Additional values may be introduced later.
        /// </summary>
        public string DefaultPipelineDefinitionSource { get; set; } = "Json";

        /// <summary>
        /// Gets or sets the optional JSON file path used by the JSON pipeline definition provider.
        /// </summary>
        public string? JsonPipelineDefinitionFilePath { get; set; }

        /// <summary>
        /// Gets or sets the optional default pipeline name used when no explicit pipeline name is provided.
        /// </summary>
        public string? DefaultPipelineName { get; set; }

        /// <summary>
        /// Gets or sets cleanup-related runtime options.
        /// </summary>
        public AiExecutionCleanupOptions Cleanup { get; set; } = new();

        /// <summary>
        /// Gets or sets durable execution snapshot options.
        /// </summary>
        public AiExecutionSnapshotOptions Snapshots { get; set; } = new();

        /// <summary>
        /// Gets or sets payload-store-related runtime options.
        /// </summary>
        public AiPayloadStoreOptions PayloadStore { get; set; } = new();

        /// <summary>
        /// Gets or sets observability-related runtime options.
        /// </summary>
        /// <remarks>
        /// PURPOSE:
        /// - Controls tracing, metrics, and recording behavior across the runtime.
        /// - Allows switching observability implementations without impacting the engine.
        ///
        /// IMPORTANT:
        /// - Disabling observability must never affect execution correctness.
        /// - Observability remains fully optional and non-blocking.
        /// </remarks>
        public AiObservabilityOptions Observability { get; set; } = new();


        /// <summary>
        /// Gets or sets runtime pipeline background controller options.
        /// </summary>
        public AiRuntimePipelineBackgroundControllerOptions PipelineBackgroundController { get; set; } = new();

        /// <summary>
        /// Gets or sets runtime instance worker options.
        /// </summary>
        /// 
        public AiRuntimeInstanceWorkerOptions RuntimeInstanceWorker { get; set; } = new();

        /// <summary>
        /// Gets or sets the control-plane identity and discovery options used to isolate shared runtime state.
        /// </summary>
        public AiControlPlaneOptions ControlPlane { get; set; } = new();


    }
}