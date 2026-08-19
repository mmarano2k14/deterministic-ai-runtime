using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;

namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Represents the persisted orchestration record of an AI execution.
    ///
    /// This object is the authoritative source for the global execution lifecycle.
    /// It stores orchestration metadata such as:
    ///
    /// - Execution identity
    /// - Pipeline binding
    /// - Execution mode
    /// - Current step position
    /// - Global lifecycle status
    /// - Concurrency/versioning information
    ///
    /// It is intentionally separated from <see cref="AiExecutionState"/>
    /// which stores the mutable working payload exchanged between steps.
    /// </summary>
    public sealed class AiExecutionRecord
    {
        /// <summary>
        /// Gets or sets the unique execution identifier.
        /// </summary>
        public string ExecutionId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Gets or sets the optional pipeline name associated with this execution.
        /// This value identifies which declarative workflow definition should be used
        /// when continuing or replaying the execution.
        /// </summary>
        public string? PipelineName { get; set; }

        /// <summary>
        /// Gets or sets the optional immutable declarative pipeline definition bound to this execution.
        /// </summary>
        /// <remarks>
        /// When present, DAG continuation must resolve the pipeline from this verified payload instead of consulting
        /// the mutable runtime definition provider by name. Normal historical executions leave this value null and
        /// retain the existing provider-based resolution behavior.
        /// </remarks>
        public AiStoredPayload? PipelineDefinitionSnapshot { get; set; }

        /// <summary>
        /// Gets or sets the execution mode used for this pipeline execution.
        ///
        /// This determines how the pipeline is scheduled and executed:
        /// - Sequential: index-based orchestration using CurrentStepIndex
        /// - Dag: dependency-driven orchestration using per-step runtime state
        /// </summary>
        public AiExecutionMode ExecutionMode { get; set; } = AiExecutionMode.Sequential;

        /// <summary>
        /// Gets or sets the current RBAC context key used to resolve the live execution context.
        /// </summary>
        public string ContextKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the zero-based index of the current step.
        ///
        /// IMPORTANT:
        /// This field belongs to the sequential execution model.
        /// It remains available for compatibility with sequential engines,
        /// but it is not the source of truth for DAG-based execution.
        /// </summary>
        public int CurrentStepIndex { get; set; }

        /// <summary>
        /// Gets or sets the ordered list of configured step names.
        /// </summary>
        public List<string> Steps { get; set; } = new();

        /// <summary>
        /// Gets or sets the ordered list of completed step names.
        ///
        /// In DAG mode, this list is useful for diagnostics and execution history,
        /// but the authoritative completion state must come from the corresponding
        /// per-step runtime status stored in <see cref="AiExecutionState"/>.
        /// </summary>
        public List<string> CompletedSteps { get; set; } = new();

        /// <summary>
        /// Gets or sets the RBAC execution context snapshot captured at creation time.
        /// </summary>
        public ExecutionContextSnapshot? ExecutionContextSnapshot { get; set; }

        /// <summary>
        /// Gets or sets the current global execution lifecycle status.
        /// This is the authoritative orchestration status for the execution as a whole.
        /// </summary>
        public AiExecutionStatus Status { get; set; } = AiExecutionStatus.Pending;

        /// <summary>
        /// Gets or sets the optimistic concurrency version.
        /// </summary>
        public int Version { get; set; }

        /// <summary>
        /// Gets or sets the current step name.
        ///
        /// IMPORTANT:
        /// This field belongs to the sequential execution model.
        /// It may still be populated for diagnostics or compatibility,
        /// but it must not be treated as the authoritative active step
        /// in DAG-based execution.
        /// </summary>
        public string? CurrentStep { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the execution-level step transition key.
        /// This key can be renewed after each successful step transition
        /// to support optimistic concurrency and duplicate transition protection.
        /// </summary>
        public string? ExecutionStepKey { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Gets or sets the UTC creation timestamp.
        /// </summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the UTC last update timestamp.
        /// </summary>
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        /// <summary>
        /// Gets the UTC timestamp at which the execution reached its terminal state.
        /// </summary>
        public DateTime CompletedAtUtc { get; set; }

        /// <summary>
        /// Returns true when the execution is in a terminal state.
        /// Terminal states cannot transition to any other status.
        /// </summary>
        public bool IsTerminal =>
            Status is AiExecutionStatus.Completed
                or AiExecutionStatus.Failed
                or AiExecutionStatus.Cancelled;

        /// <summary>
        /// Returns true when the execution is currently running.
        /// </summary>
        public bool IsRunning => Status == AiExecutionStatus.Running;

        /// <summary>
        /// Returns true when the execution is currently waiting.
        /// </summary>
        public bool IsWaiting => Status == AiExecutionStatus.Waiting;

        /// <summary>
        /// Marks the execution as running.
        /// </summary>
        public void MarkRunning()
        {
            Status = AiExecutionStatus.Running;
            UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Marks the execution as waiting.
        /// Use this when the execution as a whole is suspended
        /// pending an external event or resumable condition.
        /// </summary>
        public void MarkWaiting()
        {
            Status = AiExecutionStatus.Waiting;
            UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Marks the execution as completed.
        /// </summary>
        public void MarkCompleted()
        {
            Status = AiExecutionStatus.Completed;
            UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Marks the execution as failed.
        /// </summary>
        public void MarkFailed()
        {
            Status = AiExecutionStatus.Failed;
            UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Marks the execution as cancelled.
        /// </summary>
        public void MarkCancelled()
        {
            Status = AiExecutionStatus.Cancelled;
            UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Increments the orchestration version.
        /// This should be called whenever the persisted orchestration record
        /// changes in a way that matters for optimistic concurrency.
        /// </summary>
        public void TouchVersion()
        {
            Version++;
            UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Replaces the execution step key.
        /// This should typically be renewed after a successful step transition
        /// so that stale concurrent writers cannot replay the same transition.
        /// </summary>
        public void RenewExecutionStepKey()
        {
            ExecutionStepKey = Guid.NewGuid().ToString("N");
            UpdatedAtUtc = DateTime.UtcNow;
        }
    }
}