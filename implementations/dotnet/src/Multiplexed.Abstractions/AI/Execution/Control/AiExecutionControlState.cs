using System;
using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.Execution.Control
{
    /// <summary>
    /// Represents durable control state for a single AI execution.
    /// </summary>
    /// <remarks>
    /// This state is intentionally separated from the DAG execution state. The DAG state
    /// describes execution progress, step status, retry state, payload references, and
    /// convergence data. The control state describes operator, system, or user-level
    /// control over that execution, such as pause, resume, cancellation, or waiting for
    /// human input.
    /// </remarks>
    public sealed class AiExecutionControlState
    {
        /// <summary>
        /// Gets or sets the durable execution identifier controlled by this state.
        /// </summary>
        public string ExecutionId { get; set; } = default!;

        /// <summary>
        /// Gets or sets the effective durable control status of the execution.
        /// </summary>
        public AiExecutionControlStatus Status { get; set; } = AiExecutionControlStatus.None;

        /// <summary>
        /// Gets or sets the latest requested control action.
        /// </summary>
        public AiExecutionControlAction PendingAction { get; set; } = AiExecutionControlAction.None;

        /// <summary>
        /// Gets or sets the optional reason associated with the latest control request.
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>
        /// Gets or sets the identity of the operator, user, service, or system that requested the control action.
        /// </summary>
        public string? RequestedBy { get; set; }

        /// <summary>
        /// Gets or sets the stable waiting key used to correlate external or human input.
        /// </summary>
        public string? WaitingKey { get; set; }

        /// <summary>
        /// Gets or sets the step name that caused the execution to wait for input, when applicable.
        /// </summary>
        public string? WaitingStepName { get; set; }

        /// <summary>
        /// Gets or sets how submitted input reactivates this waiting boundary.
        /// </summary>
        public AiExecutionInputWaitMode InputWaitMode { get; set; } = AiExecutionInputWaitMode.ExecutionGate;

        /// <summary>
        /// Gets or sets durable input submitted for a waiting execution.
        /// </summary>
        public Dictionary<string, object?> Input { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the optimistic concurrency version of the control state.
        /// </summary>
        /// <remarks>
        /// This value is intended for distributed-safe updates, especially when multiple
        /// runtime instances or operators may request control transitions concurrently.
        /// </remarks>
        public long Version { get; set; }

        /// <summary>
        /// Gets or sets the timestamp of the latest update to this control state.
        /// </summary>
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the timestamp when pause was requested.
        /// </summary>
        public DateTime? PauseRequestedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the execution effectively became paused.
        /// </summary>
        public DateTime? PausedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when resume was requested.
        /// </summary>
        public DateTime? ResumeRequestedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when cancellation was requested.
        /// </summary>
        public DateTime? CancellationRequestedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the execution effectively became cancelled.
        /// </summary>
        public DateTime? CancelledAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when the execution started waiting for external or human input.
        /// </summary>
        public DateTime? WaitingStartedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the timestamp when input was received for the waiting execution.
        /// </summary>
        public DateTime? InputReceivedAtUtc { get; set; }
    }
}