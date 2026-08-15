using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Pipeline;

namespace Multiplexed.AI.Runtime.Execution.Convergence
{
    /// <summary>
    /// Evaluates the global convergence status of a DAG execution.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Project a global <see cref="AiExecutionStatus"/> from the resolved DAG topology.
    /// - Preserve deterministic convergence across distributed workers.
    /// - Support both hot-state-only evaluation and archive-aware evaluation after retention.
    ///
    /// IMPORTANT:
    /// - The evaluator must not mutate hot execution state in archive-aware mode.
    /// - The step state remains the source of truth.
    /// - The execution record is only a projection of the evaluated step states.
    /// - Evicted / archived steps must be resolved through <see cref="IAiExecutionStepResolver"/>.
    /// </remarks>
    public static class AiDagExecutionConvergenceEvaluator
    {
        /// <summary>
        /// Evaluates convergence using the current hot execution state only.
        /// </summary>
        /// <remarks>
        /// This method may initialize missing hot step states through
        /// <see cref="IAiExecutionStateWriter"/> and is kept for compatibility with
        /// non-eviction or local execution paths.
        /// </remarks>
        /// <param name="pipeline">The resolved pipeline topology.</param>
        /// <param name="state">The current execution state.</param>
        /// <param name="stateWriter">The state writer used to initialize missing hot steps.</param>
        /// <param name="utcNow">The UTC timestamp used for lease and retry evaluation.</param>
        /// <returns>The evaluated convergence result.</returns>
        public static AiDagExecutionConvergenceResult Evaluate(
            ResolvedAiPipeline pipeline,
            AiExecutionState state,
            IAiExecutionStateWriter stateWriter,
            DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(stateWriter);

            var resolvedSteps = pipeline.Steps
                .OrderBy(x => x.Order)
                .ToList();

            var stepStates = resolvedSteps
                .Select(x => stateWriter.GetOrCreateStep(state, x.Name))
                .ToList();

            return EvaluateResolvedStepStates(
                resolvedSteps,
                stepStates,
                utcNow);
        }

        /// <summary>
        /// Evaluates convergence using hot state plus archived / evicted step state.
        /// </summary>
        /// <remarks>
        /// PURPOSE:
        /// - Preserve convergence correctness after retention eviction.
        /// - Resolve evicted steps from the external archived-step store.
        /// - Avoid reintroducing evicted steps into the hot state.
        ///
        /// IMPORTANT:
        /// - This method is read-only with respect to <paramref name="state"/>.
        /// - Missing unresolved steps are represented as transient <see cref="AiStepExecutionStatus.None"/>
        ///   states for evaluation only.
        /// - It must not call methods that create or write hot step entries.
        /// </remarks>
        /// <param name="pipeline">The resolved pipeline topology.</param>
        /// <param name="state">The current hot execution state.</param>
        /// <param name="stateWriter">The state writer, retained for API compatibility.</param>
        /// <param name="stepResolver">The resolver used to load hot or archived step state.</param>
        /// <param name="utcNow">The UTC timestamp used for lease and retry evaluation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The evaluated convergence result.</returns>
        public static async Task<AiDagExecutionConvergenceResult> EvaluateAsync(
            ResolvedAiPipeline pipeline,
            AiExecutionState state,
            IAiExecutionStateWriter stateWriter,
            IAiExecutionStepResolver stepResolver,
            DateTime utcNow,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(stateWriter);
            ArgumentNullException.ThrowIfNull(stepResolver);

            var resolvedSteps = pipeline.Steps
                .OrderBy(x => x.Order)
                .ToList();

            var stepStates = new List<AiStepState>(resolvedSteps.Count);

            foreach (var resolvedStep in resolvedSteps)
            {
                var step = await stepResolver.GetStepAsync(
                        state.ExecutionId,
                        resolvedStep.Name,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (step is null)
                {
                    step = new AiStepState
                    {
                        StepName = resolvedStep.Name,
                        Status = AiStepExecutionStatus.None,
                        DependsOn = resolvedStep.DependsOn?.ToList() ?? new List<string>()
                    };
                }

                stepStates.Add(step);
            }

            return EvaluateResolvedStepStates(
                resolvedSteps,
                stepStates,
                utcNow);
        }

        /// <summary>
        /// Evaluates convergence from a fully resolved step-state view.
        /// </summary>
        /// <remarks>
        /// This method is intentionally read-only. It must evaluate the supplied step states
        /// without querying storage, mutating hot state, or creating missing step entries.
        /// </remarks>
        /// <param name="resolvedSteps">The resolved DAG steps in deterministic order.</param>
        /// <param name="stepStates">The resolved step states corresponding to the DAG topology.</param>
        /// <param name="utcNow">The UTC timestamp used for lease and retry evaluation.</param>
        /// <returns>The evaluated convergence result.</returns>
        private static AiDagExecutionConvergenceResult EvaluateResolvedStepStates(
            IReadOnlyList<ResolvedAiPipelineStep> resolvedSteps,
            IReadOnlyList<AiStepState> stepStates,
            DateTime utcNow)
        {
            if (stepStates.Count == 0)
            {
                return AiDagExecutionConvergenceResult.Waiting();
            }

            var stepStateByName = stepStates.ToDictionary(
                x => x.StepName,
                StringComparer.Ordinal);

            var allCompleted = stepStates.All(x => x.IsCompleted);

            var hasReadyStep = resolvedSteps.Any(resolvedStep =>
            {
                if (!stepStateByName.TryGetValue(resolvedStep.Name, out var step))
                {
                    return false;
                }

                if (step.Status != AiStepExecutionStatus.Ready &&
                    step.Status != AiStepExecutionStatus.None)
                {
                    return false;
                }

                foreach (var dependencyName in resolvedStep.DependsOn)
                {
                    if (!stepStateByName.TryGetValue(dependencyName, out var dependencyStep))
                    {
                        return false;
                    }

                    if (!dependencyStep.IsCompleted)
                    {
                        return false;
                    }
                }

                return true;
            });

            var hasRetryReadyStep = stepStates.Any(step =>
                step.Status == AiStepExecutionStatus.WaitingForRetry &&
                step.RetryState != null &&
                step.RetryState.NextRetryAtUtc.HasValue &&
                step.RetryState.NextRetryAtUtc.Value <= utcNow);

            var hasRunnableNow = hasReadyStep || hasRetryReadyStep;

            var hasValidRunningLease = stepStates.Any(step =>
                step.Status == AiStepExecutionStatus.Running &&
                HasValidLease(step, utcNow));

            var hasFutureRetry = stepStates.Any(step =>
                step.Status == AiStepExecutionStatus.WaitingForRetry &&
                step.RetryState != null &&
                step.RetryState.NextRetryAtUtc.HasValue &&
                step.RetryState.NextRetryAtUtc.Value > utcNow);

            var hasExternalWait = stepStates.Any(step =>
                step.Status == AiStepExecutionStatus.WaitingForExternal);

            var hasRecoverableExpiredRunning = stepStates.Any(step =>
                step.Status == AiStepExecutionStatus.Running &&
                IsExpiredLease(step, utcNow));

            var hasRecoverablePendingWork = resolvedSteps.Any(resolvedStep =>
            {
                if (!stepStateByName.TryGetValue(resolvedStep.Name, out var step))
                {
                    return false;
                }

                if (step.Status != AiStepExecutionStatus.None)
                {
                    return false;
                }

                return CanStillBecomeRunnableLater(
                    resolvedStep,
                    stepStateByName,
                    utcNow);
            });

            var hasFailedSteps = stepStates.Any(x => x.IsFailed);

            if (allCompleted)
            {
                return AiDagExecutionConvergenceResult.Completed();
            }

            if (hasRunnableNow || hasValidRunningLease)
            {
                return AiDagExecutionConvergenceResult.Running();
            }

            var canStillProgress =
                hasExternalWait ||
                hasFutureRetry ||
                hasRecoverableExpiredRunning ||
                hasRecoverablePendingWork;

            if (canStillProgress)
            {
                return AiDagExecutionConvergenceResult.Waiting();
            }

            if (hasFailedSteps)
            {
                return AiDagExecutionConvergenceResult.Failed();
            }

            return AiDagExecutionConvergenceResult.Waiting();
        }

        /// <summary>
        /// Determines whether a running step still has a valid distributed lease.
        /// </summary>
        private static bool HasValidLease(AiStepState step, DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(step);

            return step.Status == AiStepExecutionStatus.Running
                && step.LeaseExpiresAtUtc.HasValue
                && step.LeaseExpiresAtUtc.Value > utcNow;
        }

        /// <summary>
        /// Determines whether a running step has an expired distributed lease.
        /// </summary>
        private static bool IsExpiredLease(AiStepState step, DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(step);

            return step.Status == AiStepExecutionStatus.Running
                && step.LeaseExpiresAtUtc.HasValue
                && step.LeaseExpiresAtUtc.Value <= utcNow;
        }

        /// <summary>
        /// Determines whether an uninitialized step may still become runnable later.
        /// </summary>
        /// <remarks>
        /// This is used to keep convergence conservative when a step is still in
        /// <see cref="AiStepExecutionStatus.None"/> and its dependencies may still progress.
        /// </remarks>
        private static bool CanStillBecomeRunnableLater(
            ResolvedAiPipelineStep resolvedStep,
            IReadOnlyDictionary<string, AiStepState> stepStateByName,
            DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(resolvedStep);
            ArgumentNullException.ThrowIfNull(stepStateByName);

            foreach (var dependencyName in resolvedStep.DependsOn)
            {
                if (!stepStateByName.TryGetValue(dependencyName, out var dependencyStep))
                {
                    return false;
                }

                if (dependencyStep.IsCompleted)
                {
                    continue;
                }

                if (dependencyStep.IsFailed)
                {
                    return false;
                }

                if (dependencyStep.Status == AiStepExecutionStatus.WaitingForRetry)
                {
                    continue;
                }

                if (dependencyStep.Status == AiStepExecutionStatus.WaitingForExternal)
                {
                    continue;
                }

                if (dependencyStep.Status == AiStepExecutionStatus.Ready)
                {
                    continue;
                }

                if (dependencyStep.Status == AiStepExecutionStatus.None)
                {
                    continue;
                }

                if (dependencyStep.Status == AiStepExecutionStatus.Running)
                {
                    if (HasValidLease(dependencyStep, utcNow))
                    {
                        continue;
                    }

                    if (IsExpiredLease(dependencyStep, utcNow))
                    {
                        continue;
                    }

                    return false;
                }

                return false;
            }

            return true;
        }
    }
}