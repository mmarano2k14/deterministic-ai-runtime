using System;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Identifies the durable authority used to target one adversarial physical failure.
    /// </summary>
    public enum ProductionChildDagAdversarialFailureTarget
    {
        /// <summary>
        /// The selected parent is held at one deterministic ordinary-step checkpoint.
        /// </summary>
        ParentStepCheckpoint = 0,

        /// <summary>
        /// The selected parent continuation is killed only after durable continuation acceptance
        /// and before the child call-site becomes terminal.
        /// </summary>
        ContinuationConsume = 1,

        /// <summary>
        /// The selected nested child execution is held at a deterministic child checkpoint and its exact
        /// physical runtime process is destroyed while the relation remains durably waiting.
        /// </summary>
        RecursiveChildRuntime = 2
    }

    /// <summary>
    /// Defines deterministic test-only submission ordering for adversarial interleaving rows.
    /// </summary>
    public enum ProductionChildDagSubmissionOrdering
    {
        /// <summary>
        /// Preserves the historical ascending logical run order.
        /// </summary>
        Natural = 0,

        /// <summary>
        /// Starts logical runs in descending run-number order while proof results remain normalized
        /// to the historical ascending identity order.
        /// </summary>
        Reverse = 1
    }

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
                FailureTarget = ProductionChildDagAdversarialFailureTarget.ParentStepCheckpoint,
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
                FailureTarget = ProductionChildDagAdversarialFailureTarget.ParentStepCheckpoint,
                KillAfterCompletedStepCount = 1
            };

        /// <summary>
        /// Gets the deterministic schedule that fails the selected parent at the final ordinary root checkpoint,
        /// immediately before the ExecuteChildDag call-site can become runnable.
        /// </summary>
        /// <remarks>
        /// The production matrix pipeline has fifty ordinary parent steps. ExecuteChildDag is appended after those
        /// steps and depends on all of them. Holding step fifty after forty-nine durable completions therefore
        /// creates the closest existing deterministic pre-invocation failure boundary without changing runtime
        /// execution semantics or introducing a test-only Child DAG state machine.
        /// </remarks>
        public static ProductionChildDagAdversarialScheduleDefinition ChildInvocationBoundary { get; } =
            new()
            {
                MatrixScenarioId = "child-invocation-boundary",
                FailureSeed = "child-invocation-boundary",
                FailureScheduleMode = "DeterministicAdversarial",
                FailurePosition = "pre-child-invocation",
                MatrixStatus = "IN_PROGRESS",
                FailureTarget = ProductionChildDagAdversarialFailureTarget.ParentStepCheckpoint,
                KillAfterCompletedStepCount = 49
            };

        /// <summary>
        /// Gets the deterministic physical continuation-consume schedule.
        /// </summary>
        /// <remarks>
        /// The physical runtime is not killed from an ordinary parent checkpoint. The targeting harness first
        /// proves a Completed/Scheduled child relation, monotonic post-schedule parent call-site progress, a
        /// non-terminal call-site, and exact physical continuation ownership. Fifty ordinary parent steps are
        /// therefore already durable when the continuation runtime is terminated.
        /// </remarks>
        public static ProductionChildDagAdversarialScheduleDefinition ContinuationConsume { get; } =
            new()
            {
                MatrixScenarioId = "continuation-consume",
                FailureSeed = "continuation-consume",
                FailureScheduleMode = "DeterministicAdversarial",
                FailurePosition = "continuation-consume",
                MatrixStatus = "IN_PROGRESS",
                FailureTarget = ProductionChildDagAdversarialFailureTarget.ContinuationConsume,
                KillAfterCompletedStepCount = 50
            };

        /// <summary>
        /// Gets the deterministic intermediate-recursion failure schedule that destroys the runtime owning the
        /// Depth 2 child while that exact child is held at the earliest already-supported durable child checkpoint.
        /// </summary>
        /// <remarks>
        /// The checkpoint is embedded only in the targeted recursive child definition. The root and every other
        /// child execution keep the normal production pipeline shape. Holding child step two after one durable
        /// child step creates a deterministic physical failure window without changing runtime semantics.
        /// </remarks>
        public static ProductionChildDagAdversarialScheduleDefinition Depth2RuntimeFailure { get; } =
            new()
            {
                MatrixScenarioId = "depth2-runtime-failure",
                FailureSeed = "depth2-runtime-failure",
                FailureScheduleMode = "DeterministicAdversarial",
                FailurePosition = "depth2-child-runtime",
                MatrixStatus = "IN_PROGRESS",
                FailureTarget = ProductionChildDagAdversarialFailureTarget.RecursiveChildRuntime,
                TargetRecursiveDepth = 2,
                KillAfterCompletedStepCount = 1
            };

        /// <summary>
        /// Gets the deterministic deepest-recursion failure schedule that destroys the runtime owning the
        /// Depth 3 child while that exact child is held at the earliest already-supported durable child checkpoint.
        /// </summary>
        /// <remarks>
        /// The checkpoint is embedded only in the deepest recursive child definition. Depth 1 and Depth 2 keep
        /// their normal production pipeline shape, so recovery must converge upward through both continuations.
        /// </remarks>
        public static ProductionChildDagAdversarialScheduleDefinition Depth3RuntimeFailure { get; } =
            new()
            {
                MatrixScenarioId = "depth3-runtime-failure",
                FailureSeed = "depth3-runtime-failure",
                FailureScheduleMode = "DeterministicAdversarial",
                FailurePosition = "depth3-child-runtime",
                MatrixStatus = "IN_PROGRESS",
                FailureTarget = ProductionChildDagAdversarialFailureTarget.RecursiveChildRuntime,
                TargetRecursiveDepth = 3,
                KillAfterCompletedStepCount = 1
            };

        /// <summary>
        /// Gets the first deterministic interleaving seed. The workload, runtime failure boundary, and
        /// durable proof contract remain identical to the frozen baseline while every logical run in a
        /// submission segment is started in reverse run-number order.
        /// </summary>
        /// <remarks>
        /// This varies only test orchestration. Pipeline semantics, queue semantics, runtime selection,
        /// recovery behavior, and durable identities are not modified.
        /// </remarks>
        public static ProductionChildDagAdversarialScheduleDefinition SeedA { get; } =
            new()
            {
                MatrixScenarioId = "seed-a",
                FailureSeed = "seed-a",
                FailureScheduleMode = "DeterministicAdversarial",
                FailurePosition = "submission-order-reverse",
                MatrixStatus = "IN_PROGRESS",
                FailureTarget = ProductionChildDagAdversarialFailureTarget.ParentStepCheckpoint,
                KillAfterCompletedStepCount = 25,
                SubmissionOrdering = ProductionChildDagSubmissionOrdering.Reverse
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
        /// Gets the durable authority used to select the physical failure boundary.
        /// </summary>
        public required ProductionChildDagAdversarialFailureTarget FailureTarget { get; init; }

        /// <summary>
        /// Gets the deterministic test-only ordering used when logical runs are submitted within one segment.
        /// </summary>
        public ProductionChildDagSubmissionOrdering SubmissionOrdering { get; init; } =
            ProductionChildDagSubmissionOrdering.Natural;

        /// <summary>
        /// Gets whether this row deliberately varies submission ordering from the historical contract.
        /// </summary>
        public bool UsesDeterministicSubmissionOrdering =>
            this.SubmissionOrdering != ProductionChildDagSubmissionOrdering.Natural;

        /// <summary>
        /// Gets the one-based recursive child depth targeted by a recursive child runtime failure schedule.
        /// </summary>
        public int? TargetRecursiveDepth { get; init; }

        /// <summary>
        /// Gets whether this schedule uses one ordinary parent crash checkpoint as the physical failure boundary.
        /// </summary>
        public bool UsesParentCrashCheckpoint =>
            this.FailureTarget == ProductionChildDagAdversarialFailureTarget.ParentStepCheckpoint;

        /// <summary>
        /// Gets whether this schedule embeds one durable crash checkpoint in an exact recursive child definition.
        /// </summary>
        public bool UsesRecursiveChildCrashCheckpoint =>
            this.FailureTarget == ProductionChildDagAdversarialFailureTarget.RecursiveChildRuntime;

        /// <summary>
        /// Gets the number of durable ordinary steps that must complete in the targeted execution before the exact
        /// runtime process is killed. Parent-targeted schedules count parent steps; recursive-child schedules count
        /// steps in the targeted child execution.
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
            if (!this.UsesParentCrashCheckpoint)
            {
                throw new InvalidOperationException(
                    $"Adversarial failure target '{this.FailureTarget}' does not use an ordinary parent crash checkpoint.");
            }

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

        /// <summary>
        /// Resolves the one-based crash checkpoint step embedded only in the targeted recursive child definition.
        /// </summary>
        /// <param name="configuredChildDepth">The total configured recursive child depth.</param>
        /// <param name="pipelineStepCount">The number of ordinary steps in each recursive child pipeline.</param>
        /// <returns>The one-based child crash checkpoint step index.</returns>
        public int ResolveRecursiveChildCrashCheckpointStepIndex(
            int configuredChildDepth,
            int pipelineStepCount)
        {
            if (!this.UsesRecursiveChildCrashCheckpoint)
            {
                throw new InvalidOperationException(
                    $"Adversarial failure target '{this.FailureTarget}' does not use a recursive child crash checkpoint.");
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredChildDepth);
            ArgumentOutOfRangeException.ThrowIfLessThan(pipelineStepCount, 2);

            if (!this.TargetRecursiveDepth.HasValue ||
                this.TargetRecursiveDepth.Value <= 0 ||
                this.TargetRecursiveDepth.Value > configuredChildDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(TargetRecursiveDepth),
                    this.TargetRecursiveDepth,
                    $"The adversarial recursive target depth must be between 1 and '{configuredChildDepth}'.");
            }

            if (this.KillAfterCompletedStepCount < 1 ||
                this.KillAfterCompletedStepCount >= pipelineStepCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(KillAfterCompletedStepCount),
                    this.KillAfterCompletedStepCount,
                    $"The adversarial recursive runtime failure must occur after at least one completed child step and before the final child pipeline step '{pipelineStepCount}'.");
            }

            return checked(this.KillAfterCompletedStepCount + 1);
        }
    }
}
