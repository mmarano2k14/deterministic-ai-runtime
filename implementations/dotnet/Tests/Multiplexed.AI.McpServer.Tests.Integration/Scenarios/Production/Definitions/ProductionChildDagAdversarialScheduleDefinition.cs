using System;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Describes one deterministic failure schedule coordinate for the Recursive Child DAG
    /// adversarial validation matrix.
    /// </summary>
    /// <remarks>
    /// The baseline definition reproduces the already-proven production schedule exactly.
    /// New adversarial rows must opt in explicitly and must not mutate production runtime semantics.
    /// </remarks>
    public sealed record ProductionChildDagAdversarialScheduleDefinition
    {
        /// <summary>
        /// Gets the frozen baseline schedule used when no adversarial row is explicitly selected.
        /// </summary>
        public static ProductionChildDagAdversarialScheduleDefinition Baseline { get; } =
            new()
            {
                MatrixScenarioId = "baseline",
                FailureSeed = "baseline",
                FailureScheduleMode = "DeterministicBaseline",
                FailurePosition = "mid-parent",
                MatrixStatus = "NOT_YET_VALIDATED",
                KillAfterCompletedStepCount = 25
            };

        /// <summary>
        /// Gets the first deterministic adversarial schedule. The targeted runtime is killed at the earliest
        /// already-supported durable checkpoint: after one completed ordinary parent step and while step two
        /// is checkpoint-blocked.
        /// </summary>
        /// <remarks>
        /// Step two is not an invented coordinate: existing focused Child DAG runtime-failure scenarios already
        /// use <c>CrashCheckpointStepIndex = 2</c>, and the production scenario runner rejects checkpoint indices
        /// lower than two.
        /// </remarks>
        public static ProductionChildDagAdversarialScheduleDefinition CrashEarly { get; } =
            new()
            {
                MatrixScenarioId = "crash-early",
                FailureSeed = "crash-early",
                FailureScheduleMode = "DeterministicAdversarial",
                FailurePosition = "early-parent",
                MatrixStatus = "IN_PROGRESS",
                KillAfterCompletedStepCount = 1
            };

        /// <summary>
        /// Gets the stable matrix row identifier written into proof output.
        /// </summary>
        public required string MatrixScenarioId { get; init; }

        /// <summary>
        /// Gets the deterministic schedule seed written into proof output.
        /// </summary>
        public required string FailureSeed { get; init; }

        /// <summary>
        /// Gets the schedule mode written into the human-readable proof contract.
        /// </summary>
        public required string FailureScheduleMode { get; init; }

        /// <summary>
        /// Gets the descriptive logical failure position used by matrix orchestration.
        /// </summary>
        public required string FailurePosition { get; init; }

        /// <summary>
        /// Gets the aggregate matrix status written into the frozen machine-readable proof row.
        /// </summary>
        public required string MatrixStatus { get; init; }

        /// <summary>
        /// Gets the number of durable parent steps that must complete before the exact runtime process is killed.
        /// </summary>
        public required int KillAfterCompletedStepCount { get; init; }

        /// <summary>
        /// Resolves the one-based crash checkpoint step that holds the targeted execution immediately after the
        /// required durable progress count.
        /// </summary>
        /// <param name="pipelineStepCount">The number of ordinary parent pipeline steps before Child DAG composition.</param>
        /// <returns>The one-based crash checkpoint step index.</returns>
        public int ResolveCrashCheckpointStepIndex(
            int pipelineStepCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                pipelineStepCount,
                2);

            if (this.KillAfterCompletedStepCount < 1 ||
                this.KillAfterCompletedStepCount >= pipelineStepCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(KillAfterCompletedStepCount),
                    this.KillAfterCompletedStepCount,
                    $"The adversarial runtime failure must occur after at least one completed step and before the final parent pipeline step '{pipelineStepCount}'.");
            }

            return checked(this.KillAfterCompletedStepCount + 1);
        }
    }
}
