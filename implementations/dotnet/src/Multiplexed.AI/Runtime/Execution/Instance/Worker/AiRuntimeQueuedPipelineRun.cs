using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability.Context;

namespace Multiplexed.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Represents one queued pipeline run inside the runtime background controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A queued pipeline run owns the submitted pipeline request, the public run handle,
    /// the completion source, and the runtime execution correlation context associated
    /// with this specific run.
    /// </para>
    ///
    /// <para>
    /// The correlation context is created when the run is enqueued. It starts with
    /// controller-level information such as the run identifier and correlation identifier,
    /// then it can be enriched later when the durable execution identifier becomes known.
    /// </para>
    ///
    /// <para>
    /// The same correlation context instance must not be reused by another queued run.
    /// </para>
    /// </remarks>
    internal sealed class AiRuntimeQueuedPipelineRun
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeQueuedPipelineRun"/> class.
        /// </summary>
        /// <param name="request">The submitted pipeline run request.</param>
        /// <param name="handle">The public run handle.</param>
        /// <param name="completionSource">The completion source for the submitted run.</param>
        /// <param name="correlation">The runtime execution correlation context owned by this queued run.</param>
        /// <param name="resumeExecutionId">The optional existing execution identifier to resume.</param>
        public AiRuntimeQueuedPipelineRun(
            AiRuntimePipelineRunRequest request,
            AiRuntimeWorkerRunHandle handle,
            TaskCompletionSource<AiExecutionRecord> completionSource,
            AiRuntimeExecutionCorrelationContext correlation,
            string? resumeExecutionId = null)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            CompletionSource = completionSource ?? throw new ArgumentNullException(nameof(completionSource));
            Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
            ResumeExecutionId = string.IsNullOrWhiteSpace(resumeExecutionId)
                ? null
                : resumeExecutionId;
        }

        /// <summary>
        /// Gets the existing durable execution identifier to resume, if this queued run
        /// was created by controlled runtime execution recovery.
        /// </summary>
        public string? ResumeExecutionId { get; }

        /// <summary>
        /// Gets a value indicating whether this queued run should resume an existing execution.
        /// </summary>
        public bool IsResume =>
            !string.IsNullOrWhiteSpace(ResumeExecutionId);

        /// <summary>
        /// Gets a value indicating whether this run reactivates a normal externally waiting step.
        /// </summary>
        public bool IsExternalWaitContinuation =>
            Request.ExternalWaitContinuation is not null;

        /// <summary>
        /// Gets the submitted pipeline run request.
        /// </summary>
        public AiRuntimePipelineRunRequest Request { get; }

        /// <summary>
        /// Gets the public run handle.
        /// </summary>
        public AiRuntimeWorkerRunHandle Handle { get; }

        /// <summary>
        /// Gets the completion source for the submitted run.
        /// </summary>
        public TaskCompletionSource<AiExecutionRecord> CompletionSource { get; }

        /// <summary>
        /// Gets the runtime execution correlation context owned by this queued run.
        /// </summary>
        public AiRuntimeExecutionCorrelationContext Correlation { get; }
    }
}