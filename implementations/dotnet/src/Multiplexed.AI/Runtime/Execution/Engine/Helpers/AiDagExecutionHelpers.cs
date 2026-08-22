using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Observability.Helpers;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.Execution.Engine.Helpers
{
    /// <summary>
    /// Provides small shared helper methods for DAG execution.
    /// </summary>
    public static class AiDagExecutionHelpers
    {
        /// <summary>
        /// Gets the required optimistic execution step key.
        /// </summary>
        /// <param name="record">The execution record.</param>
        /// <returns>The execution step key.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the execution step key is missing.
        /// </exception>
        public static string GetRequiredExecutionStepKey(
            AiExecutionRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (string.IsNullOrWhiteSpace(record.ExecutionStepKey))
            {
                throw new InvalidOperationException(
                    "ExecutionStepKey must be set before persisting execution state.");
            }

            return record.ExecutionStepKey;
        }

        /// <summary>
        /// Ensures that a distributed DAG store is configured.
        /// </summary>
        /// <param name="dagStore">The DAG store instance.</param>
        /// <typeparam name="TDagStore">The DAG store type.</typeparam>
        /// <returns>The configured DAG store.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the DAG store is not configured.
        /// </exception>
        public static TDagStore RequireDagStore<TDagStore>(
            TDagStore? dagStore)
            where TDagStore : class
        {
            return dagStore
                ?? throw new InvalidOperationException(
                    "Distributed DAG store is not configured.");
        }

        /// <summary>
        /// Determines whether an execution can be finalized from the current hot state.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="targetStatus">The target terminal status.</param>
        /// <returns>
        /// <c>true</c> when the execution can be finalized; otherwise, <c>false</c>.
        /// </returns>
        [Obsolete(
            "CanFinalize is obsolete. Finalization is now driven by convergence evaluation (AiDagExecutionConvergenceEvaluator). " +
            "This method is not archive-aware and should not be used in retention-enabled execution paths.",
            false)]
        public static bool CanFinalize(
            AiExecutionState state,
            AiExecutionStatus targetStatus)
        {
            ArgumentNullException.ThrowIfNull(state);

            var steps = state.Steps.Values.ToList();

            if (steps.Count == 0)
            {
                return false;
            }

            if (steps.Any(x =>
                x.Status == AiStepExecutionStatus.Running ||
                x.Status == AiStepExecutionStatus.WaitingForRetry ||
                x.Status == AiStepExecutionStatus.Ready ||
                x.Status == AiStepExecutionStatus.None))
            {
                return false;
            }

            if (targetStatus == AiExecutionStatus.Completed)
            {
                return steps.All(x => x.IsCompleted);
            }

            if (targetStatus == AiExecutionStatus.Failed)
            {
                return steps.Any(x => x.Status == AiStepExecutionStatus.Failed)
                    && steps.All(x =>
                        x.Status == AiStepExecutionStatus.Failed ||
                        x.Status == AiStepExecutionStatus.Completed);
            }

            if (targetStatus == AiExecutionStatus.Cancelled)
            {
                return steps.All(x =>
                    x.Status == AiStepExecutionStatus.Completed ||
                    x.Status == AiStepExecutionStatus.Failed);
            }

            return false;
        }

        /// <summary>
        /// Creates the concurrency context matching the lease acquired by the claim service.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="pipelineKey">The stable pipeline key.</param>
        /// <param name="stepName">The claimed logical DAG step name.</param>
        /// <param name="runtimeInstanceId">The stable runtime instance identifier participating in distributed execution.</param>
        /// <param name="stepState">The step state containing optional provider, model, and operation metadata.</param>
        /// <returns>The concurrency context used to release the lease.</returns>
        public static AiConcurrencyContext CreateConcurrencyContext(
            string executionId,
            string pipelineKey,
            string stepName,
            string runtimeInstanceId,
            AiStepState stepState)
        {
            return CreateConcurrencyContext(
                executionId,
                pipelineKey,
                stepName,
                stepKey: stepName,
                runtimeInstanceId,
                stepState);
        }

        /// <summary>
        /// Creates the concurrency context matching the lease acquired by the claim service.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="pipelineKey">The stable pipeline key.</param>
        /// <param name="stepName">The claimed logical DAG step name.</param>
        /// <param name="stepKey">The technical step implementation key.</param>
        /// <param name="runtimeInstanceId">The stable runtime instance identifier participating in distributed execution.</param>
        /// <param name="stepState">The step state containing optional provider, model, and operation metadata.</param>
        /// <returns>The concurrency context used to release the lease.</returns>
        public static AiConcurrencyContext CreateConcurrencyContext(
            string executionId,
            string pipelineKey,
            string stepName,
            string stepKey,
            string runtimeInstanceId,
            AiStepState stepState)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(stepState);

            return new AiConcurrencyContext
            {
                ExecutionId = executionId,
                PipelineKey = pipelineKey,
                StepId = stepName,
                StepKey = stepKey,
                RuntimeInstanceId = runtimeInstanceId,
                LeaseId = $"{executionId}:{stepName}:{runtimeInstanceId}",
                Provider = TryReadString(stepState.Config, "provider"),
                Model = TryReadString(stepState.Config, "model"),
                Operation =
                    TryReadString(stepState.Config, "operation")
                    ?? TryReadString(stepState.Config, "type")
            };
        }

        /// <summary>
        /// Attempts to read a string value from a step configuration dictionary.
        /// </summary>
        /// <param name="config">The step configuration dictionary.</param>
        /// <param name="key">The configuration key to read.</param>
        /// <returns>The string value when present and non-empty; otherwise, <c>null</c>.</returns>
        public static string? TryReadString(
            IReadOnlyDictionary<string, object?>? config,
            string key)
        {
            if (config is null || !config.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            return value switch
            {
                string text when !string.IsNullOrWhiteSpace(text) => text,
                JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                JsonElement element when element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
                _ => value.ToString()
            };
        }

        /// <summary>
        /// Creates the effective concurrency admission data for a DAG step.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="pipelineKey">The stable pipeline key.</param>
        /// <param name="stepName">The concrete logical step name.</param>
        /// <param name="runtimeInstanceId">The stable runtime instance identifier participating in distributed execution.</param>
        /// <param name="stepState">The step state containing concurrency, provider, model, and operation config.</param>
        /// <param name="pipelineConfig">The optional pipeline-level configuration used to resolve effective concurrency rules.</param>
        /// <param name="stepDefinition">The pipeline step definition used to resolve step-level concurrency rules.</param>
        /// <param name="definitionResolver">The concurrency definition resolver.</param>
        /// <returns>The concurrency context and effective concurrency definition.</returns>
        public static AiDagConcurrencyAdmission CreateConcurrencyAdmission(
            string executionId,
            string pipelineKey,
            string stepName,
            string runtimeInstanceId,
            AiStepState stepState,
            IReadOnlyDictionary<string, object?>? pipelineConfig,
            AiPipelineStepDefinition stepDefinition,
            IAiConcurrencyDefinitionResolver definitionResolver)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(stepState);
            ArgumentNullException.ThrowIfNull(stepDefinition);
            ArgumentNullException.ThrowIfNull(definitionResolver);

            var effectiveStepKey = string.IsNullOrWhiteSpace(stepDefinition.StepKey)
                ? stepName
                : stepDefinition.StepKey;

            var pipelineDefinition = new AiPipelineDefinition
            {
                Name = pipelineKey,
                Version = "runtime",
                ExecutionMode = AiExecutionMode.Dag,
                Config = pipelineConfig ?? new Dictionary<string, object?>(),
                Steps = new[]
                {
                    stepDefinition
                }
            };

            var definition = definitionResolver.Resolve(
                pipelineDefinition,
                stepDefinition);

            var context = CreateConcurrencyContext(
                executionId,
                pipelineKey,
                stepName,
                effectiveStepKey,
                runtimeInstanceId,
                stepState);

            var effectiveDefinition = AiConcurrencyThrottleRuleApplicator.Apply(
                definition,
                context);

            return new AiDagConcurrencyAdmission
            {
                Context = context,
                Definition = effectiveDefinition
            };
        }

        /// <summary>
        /// Gets the declared step names from the resolved pipeline.
        /// </summary>
        /// <param name="pipeline">The resolved pipeline.</param>
        /// <returns>The declared pipeline step names.</returns>
        public static IReadOnlyCollection<string> GetDeclaredStepNames(
            ResolvedAiPipeline pipeline)
        {
            ArgumentNullException.ThrowIfNull(pipeline);

            return pipeline.Steps
                .Select(step => step.Name)
                .Where(stepName => !string.IsNullOrWhiteSpace(stepName))
                .Select(stepName => stepName!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Records a decision ledger event for the current DAG runtime flow.
        /// </summary>
        /// <param name="services">The DAG execution engine services.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="pipelineKey">The pipeline key.</param>
        /// <param name="stepName">The DAG step name, or a stable synthetic scope such as "_execution" for execution-level events.</param>
        /// <param name="workerId">The worker identifier.</param>
        /// <param name="claimToken">The optional claim token.</param>
        /// <param name="concurrencyContext">The optional concurrency context.</param>
        /// <param name="category">The ledger category.</param>
        /// <param name="eventType">The ledger event type.</param>
        /// <param name="outcome">The ledger event outcome.</param>
        /// <param name="reason">The optional decision reason.</param>
        /// <param name="metadata">The optional non-sensitive metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="stepKey">
        /// The optional technical step implementation key. When omitted, the value is resolved
        /// from the concurrency context or falls back to <paramref name="stepName"/>.
        /// </param>
        /// <returns>A task representing the asynchronous record operation.</returns>
        public static async Task RecordDagLedgerEventAsync(
            IAiDagExecutionEngineServices services,
            string executionId,
            string pipelineKey,
            string stepName,
            string stepKey,
            string workerId,
            string? claimToken,
            AiConcurrencyContext? concurrencyContext,
            AiDecisionLedgerCategory category,
            string eventType,
            AiDecisionLedgerOutcome outcome,
            string? reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken
            )
        {
            ArgumentNullException.ThrowIfNull(services);

            if (services.ObservabilityService?.Ledger is null)
            {
                return;
            }

            var resolvedStepKey = !string.IsNullOrWhiteSpace(stepKey)
                ? stepKey
                : concurrencyContext?.StepKey ?? stepName;

            var correlationContext = AiRuntimeCorrelationContextHelper.Create(
                executionId,
                pipelineKey,
                stepName,
                resolvedStepKey,
                workerId,
                claimToken,
                concurrencyContext);

            await services.ObservabilityService.Ledger
                .RecordAsync(
                    correlationContext,
                    category,
                    eventType,
                    outcome,
                    reason,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records retry ledger events after a failed step transition has been persisted.
        /// </summary>
        /// <param name="services">The DAG execution engine services.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="pipelineKey">The stable pipeline key.</param>
        /// <param name="stepName">The failed logical DAG step name.</param>
        /// <param name="stepKey">The failed technical step implementation key.</param>
        /// <param name="workerId">The worker identifier.</param>
        /// <param name="claimToken">The claim token associated with the failed step.</param>
        /// <param name="stepState">The reloaded step state after failure persistence.</param>
        /// <param name="error">The failure error.</param>
        /// <param name="failureSource">The failure source.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous ledger operation.</returns>
        public static async Task RecordRetryLedgerEventsAsync(
            IAiDagExecutionEngineServices services,
            string executionId,
            string pipelineKey,
            string stepName,
            string stepKey,
            string workerId,
            string? claimToken,
            AiStepState? stepState,
            string? error,
            string failureSource,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failureSource);

            var resolvedStepKey = string.IsNullOrWhiteSpace(stepKey)
                ? stepName
                : stepKey;

            await RecordDagLedgerEventAsync(
                    services,
                    executionId,
                    pipelineKey,
                    stepName,
                    resolvedStepKey,
                    workerId,
                    claimToken,
                    concurrencyContext: null,
                    AiDecisionLedgerCategory.Retry,
                    AiEngineEvents.Retry.Evaluated,
                    AiDecisionLedgerOutcome.Started,
                    "Retry decision evaluated after step failure.",
                    new Dictionary<string, string>
                    {
                        [AiStepMetadataKeys.StepName] = stepName,
                        [AiStepMetadataKeys.StepKey] = resolvedStepKey,
                        ["failure.source"] = failureSource,
                        ["error"] = error ?? string.Empty,
                        ["step.status"] = stepState?.Status.ToString() ?? "unknown",
                        ["retry.count"] = (stepState?.RetryState?.RetryCount ?? 0).ToString(),
                        ["retry.max"] = (stepState?.Retry?.MaxRetries ?? 0).ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (stepState is null)
            {
                await RecordDagLedgerEventAsync(
                        services,
                        executionId,
                        pipelineKey,
                        stepName,
                        resolvedStepKey,
                        workerId,
                        claimToken,
                        concurrencyContext: null,
                        AiDecisionLedgerCategory.Retry,
                        AiEngineEvents.Retry.Denied,
                        AiDecisionLedgerOutcome.Denied,
                        "Retry decision could not be resolved because the step state was not found after failure persistence.",
                        new Dictionary<string, string>
                        {
                            [AiStepMetadataKeys.StepName] = stepName,
                            [AiStepMetadataKeys.StepKey] = resolvedStepKey,
                            ["failure.source"] = failureSource,
                            ["error"] = error ?? string.Empty
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            if (stepState.Status == AiStepExecutionStatus.WaitingForRetry)
            {
                await RecordDagLedgerEventAsync(
                        services,
                        executionId,
                        pipelineKey,
                        stepName,
                        resolvedStepKey,
                        workerId,
                        claimToken,
                        concurrencyContext: null,
                        AiDecisionLedgerCategory.Retry,
                        AiEngineEvents.Retry.Scheduled,
                        AiDecisionLedgerOutcome.Applied,
                        stepState.RetryState?.RetryReason ?? "Step retry scheduled.",
                        new Dictionary<string, string>
                        {
                            [AiStepMetadataKeys.StepName] = stepName,
                            [AiStepMetadataKeys.StepKey] = resolvedStepKey,
                            ["failure.source"] = failureSource,
                            ["error"] = error ?? string.Empty,
                            ["retry.count"] = (stepState.RetryState?.RetryCount ?? 0).ToString(),
                            ["retry.max"] = (stepState.Retry?.MaxRetries ?? 0).ToString(),
                            ["next.retry.at.utc"] = stepState.RetryState?.NextRetryAtUtc?.ToString("O") ?? string.Empty
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            if (stepState.Status == AiStepExecutionStatus.Completed ||
                stepState.Status == AiStepExecutionStatus.Running ||
                stepState.Status == AiStepExecutionStatus.Ready)
            {
                return;
            }

            var retryCount = stepState.RetryState?.RetryCount ?? 0;
            var maxRetries = stepState.Retry?.MaxRetries;
            var budgetExhausted = maxRetries.HasValue && retryCount >= maxRetries.Value;

            await RecordDagLedgerEventAsync(
                    services,
                    executionId,
                    pipelineKey,
                    stepName,
                    resolvedStepKey,
                    workerId,
                    claimToken,
                    concurrencyContext: null,
                    AiDecisionLedgerCategory.Retry,
                    budgetExhausted
                        ? AiEngineEvents.Retry.BudgetExhausted
                        : AiEngineEvents.Retry.Denied,
                    AiDecisionLedgerOutcome.Denied,
                    budgetExhausted
                        ? "Retry budget exhausted."
                        : "Retry was denied or the failure was not retryable.",
                    new Dictionary<string, string>
                    {
                        [AiStepMetadataKeys.StepName] = stepName,
                        [AiStepMetadataKeys.StepKey] = resolvedStepKey,
                        ["failure.source"] = failureSource,
                        ["error"] = error ?? string.Empty,
                        ["step.status"] = stepState.Status.ToString(),
                        ["retry.count"] = retryCount.ToString(),
                        ["retry.max"] = maxRetries?.ToString() ?? string.Empty
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves recovered step names by comparing recovery counters before and after recovery.
        /// </summary>
        /// <param name="beforeRecoveryCounts">The recovery count per step before the recovery operation.</param>
        /// <param name="afterState">The execution state after recovery.</param>
        /// <returns>The step names whose recovery count increased.</returns>
        public static IReadOnlyList<string> ResolveRecoveredStepNames(
            IReadOnlyDictionary<string, int> beforeRecoveryCounts,
            AiExecutionState? afterState)
        {
            ArgumentNullException.ThrowIfNull(beforeRecoveryCounts);

            if (afterState is null || afterState.Steps.Count == 0)
            {
                return Array.Empty<string>();
            }

            var recoveredStepNames = new List<string>();

            foreach (var pair in afterState.Steps)
            {
                var stepName = pair.Key;
                var stepState = pair.Value;

                beforeRecoveryCounts.TryGetValue(
                    stepName,
                    out var previousRecoveryCount);

                if (stepState.RecoveryCount > previousRecoveryCount)
                {
                    recoveredStepNames.Add(stepName);
                }
            }

            return recoveredStepNames
                .OrderBy(stepName => stepName, StringComparer.Ordinal)
                .ToArray();
        }

        public static AiPipelineStepDefinition FindPipelineStep(
            ResolvedAiPipeline pipeline,
            string stepName)
        {
            var step = pipeline.Steps.FirstOrDefault(x =>
                string.Equals(x.Name, stepName, StringComparison.OrdinalIgnoreCase));

            if (step is not null)
            {
                return new AiPipelineStepDefinition
                {
                    Name = step.Name,
                    StepKey = step.StepKey,
                    Config = step.Config ?? new Dictionary<string, object?>(),
                    DependsOn = step.DependsOn ?? Array.Empty<string>()
                };
            }

            return new AiPipelineStepDefinition
            {
                Name = stepName,
                StepKey = stepName,
                Config = new Dictionary<string, object?>(),
                DependsOn = Array.Empty<string>()
            };
        }
    }
}