using Multiplexed.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.Abstractions.AI.Execution.Instance.Worker
{
    /// <summary>
    /// Configures runtime pipeline background controller behavior.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These options control how the background controller accepts queued pipeline
    /// run requests and limits how many created executions may be processed in
    /// parallel by this controller instance.
    /// </para>
    /// <para>
    /// These options are separate from <see cref="AiRuntimeInstanceWorkerOptions"/>.
    /// Worker options control how one worker advances one existing execution.
    /// Controller options control queue capacity and maximum active pipeline runs.
    /// </para>
    /// <para>
    /// This is not a replacement for distributed concurrency throttling. The runtime
    /// concurrency engine and Redis-backed gate remain responsible for provider,
    /// model, operation, step, pipeline, execution, and instance-level admission.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimePipelineBackgroundControllerOptions
    {
        /// <summary>
        /// Gets or sets the maximum number of pipeline runs this controller may process
        /// concurrently.
        /// </summary>
        /// <remarks>
        /// Each active run creates its own execution identifier and is advanced by the
        /// runtime instance worker. This value controls controller-level parallelism,
        /// not distributed step-level concurrency.
        /// </remarks>
        public int MaxConcurrentRuns { get; set; } = 4;

        /// <summary>
        /// Gets or sets the maximum number of queued pipeline run requests.
        /// </summary>
        /// <remarks>
        /// When the queue is full, enqueue behavior depends on the configured channel
        /// mode used by the controller implementation.
        /// </remarks>
        public int QueueCapacity { get; set; } = 1000;

        /// <summary>
        /// Gets or sets a value indicating whether enqueue calls should be rejected
        /// when the controller has not been started.
        /// </summary>
        public bool RejectEnqueueWhenStopped { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the controller should stop accepting
        /// new work after the first run failure.
        /// </summary>
        /// <remarks>
        /// The default is <see langword="false"/> so one failed run does not prevent
        /// unrelated queued runs from executing.
        /// </remarks>
        public bool StopOnFirstFailure { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of local workers from this runtime instance
        /// allowed to work on the same execution concurrently.
        /// </summary>
        /// <remarks>
        /// This limits the worker group size created for one execution by this controller.
        /// It does not limit total runtime instance workers across different executions.
        /// </remarks>
        public int MaxLocalWorkersPerExecution { get; set; } = 5;

        /// <summary>
        /// Gets or sets the distributed multi-runtime-instance execution options.
        /// </summary>
        public AiRuntimeDistributedExecutionOptions Distributed { get; set; } = new();
    }
}