using Multiplexed.Abstractions.AI.Execution;

namespace Multiplexed.Abstractions.AI.Execution.Instance.Worker
{
    /// <summary>
    /// Represents a handle returned when a pipeline run is submitted to a runtime background controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The run identifier belongs to the controller lifecycle.
    /// The execution identifier belongs to the persisted runtime execution lifecycle
    /// and becomes available after the controller creates the execution record.
    /// </para>
    /// <para>
    /// This separation allows future pause, resume, cancel, and replay behavior to
    /// target either the submitted controller run or the created runtime execution.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeWorkerRunHandle
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeWorkerRunHandle"/> class.
        /// </summary>
        /// <param name="runId">The controller-level run identifier.</param>
        /// <param name="completion">The completion task for the submitted run.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="runId"/> is null, empty, or whitespace.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="completion"/> is null.
        /// </exception>
        public AiRuntimeWorkerRunHandle(
            string runId,
            Task<AiExecutionRecord> completion)
        {
            if (string.IsNullOrWhiteSpace(runId))
            {
                throw new ArgumentException(
                    "Run id cannot be null or empty.",
                    nameof(runId));
            }

            RunId = runId;
            Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeWorkerRunHandle"/> class
        /// with an already known durable execution identifier.
        /// </summary>
        /// <param name="runId">The controller-level run identifier.</param>
        /// <param name="completion">The completion task for the submitted run.</param>
        /// <param name="executionId">The durable runtime execution identifier.</param>
        public AiRuntimeWorkerRunHandle(
            string runId,
            Task<AiExecutionRecord> completion,
            string executionId)
            : this(
                runId,
                completion)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new ArgumentException(
                    "Execution id cannot be null or empty.",
                    nameof(executionId));
            }

            ExecutionId = executionId;
        }

        /// <summary>
        /// Gets the controller-level run identifier.
        /// </summary>
        public string RunId { get; }

        /// <summary>
        /// Gets the runtime execution identifier once the execution has been created.
        /// </summary>
        public string? ExecutionId { get; private set; }

        /// <summary>
        /// Gets the current controller-level run status.
        /// </summary>
        public AiRuntimeWorkerRunStatus Status { get; private set; } =
            AiRuntimeWorkerRunStatus.Queued;

        /// <summary>
        /// Marks the run as creating a runtime execution.
        /// </summary>
        public void MarkCreatingExecution()
        {
            Status = AiRuntimeWorkerRunStatus.CreatingExecution;
        }

        /// <summary>
        /// Marks the run as running and stores the created execution identifier.
        /// </summary>
        /// <param name="executionId">The created runtime execution identifier.</param>
        public void MarkRunning(string executionId)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new ArgumentException(
                    "Execution id cannot be null or empty.",
                    nameof(executionId));
            }

            ExecutionId = executionId;
            Status = AiRuntimeWorkerRunStatus.Running;
        }

        /// <summary>
        /// Marks the run as completed.
        /// </summary>
        public void MarkCompleted()
        {
            Status = AiRuntimeWorkerRunStatus.Completed;
        }

        /// <summary>
        /// Marks the run as failed.
        /// </summary>
        public void MarkFailed()
        {
            Status = AiRuntimeWorkerRunStatus.Failed;
        }

        /// <summary>
        /// Marks the run as cancelled.
        /// </summary>
        public void MarkCancelled()
        {
            Status = AiRuntimeWorkerRunStatus.Cancelled;
        }

        /// <summary>
        /// Gets the task completed when the submitted pipeline run reaches a terminal state.
        /// </summary>
        public Task<AiExecutionRecord> Completion { get; }

        public static AiRuntimeWorkerRunHandle CreateTestHandle()
        {
            var completionSource = new TaskCompletionSource<AiExecutionRecord>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            return new AiRuntimeWorkerRunHandle(
                Guid.NewGuid().ToString("N"),
                completionSource.Task);
        }

    }
}