using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Retention;
using Multiplexed.AI.Runtime.Execution.Retention.Models;
using Multiplexed.Abstractions.AI.Policies;

namespace Multiplexed.AI.Runtime.Execution.Engine.Retention
{
    /// <summary>
    /// Coordinates policy-driven retention for DAG execution state.
    ///
    /// PURPOSE:
    /// - Apply retention policies after step lifecycle mutations.
    /// - Persist the updated distributed execution state.
    /// - Warm the archive-aware resolver for evicted steps.
    /// - Record execution-correlated retention ledger events.
    ///
    /// IMPORTANT:
    /// - This coordinator does not execute steps.
    /// - This coordinator does not evaluate convergence.
    /// - This coordinator does not finalize executions.
    /// </summary>
    public sealed class AiDagRetentionCoordinator
    {
        private readonly IAiDagExecutionEngineServices _engineServices;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagRetentionCoordinator"/> class.
        /// </summary>
        /// <param name="engineServices">
        /// The DAG execution engine services.
        /// </param>
        public AiDagRetentionCoordinator(
            IAiDagExecutionEngineServices engineServices)
        {
            _engineServices = engineServices;
        }

        /// <summary>
        /// Applies retention, persists state, and warms the resolver cache.
        /// </summary>
        /// <param name="executionId">
        /// The execution identifier.
        /// </param>
        /// <param name="state">
        /// The current execution state.
        /// </param>
        /// <param name="stepContext">
        /// The current step context used to resolve retention policies.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        public async Task ApplyRetentionPersistAndWarmAsync(
            string executionId,
            AiExecutionState state,
            AiStepExecutionContext stepContext,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(stepContext);

            await _engineServices.ObservabilityService.Tracer.TraceRetentionAsync(
                new AiRetentionTraceContext
                {
                    ExecutionId = executionId,
                    PolicyName = "policy-driven-retention",
                    InspectedSteps = state.Steps.Count
                },
                async trace =>
                {
                    var stepsBefore = state.Steps.Count;
                    var policyName = "policy-driven-retention";
                    var runtimeInstanceId = _engineServices.RuntimeInstanceIdentity.RuntimeInstanceId;
                    var pipelineKey = string.IsNullOrWhiteSpace(state.PipelineName)
                        ? "unknown"
                        : state.PipelineName;

                    await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                            _engineServices,
                            executionId,
                            pipelineKey,
                            stepContext.StepName,
                            stepContext.StepKey,
                            runtimeInstanceId,
                            claimToken: null,
                            concurrencyContext: null,
                            AiDecisionLedgerCategory.Retention,
                            AiDecisionLedgerEvents.Retention.Evaluated,
                            AiDecisionLedgerOutcome.Started,
                            "Retention evaluation started.",
                            new Dictionary<string, string>
                            {
                                [AiPolicyMetadataKeys.Name] = policyName,
                                ["steps.count"] = state.Steps.Count.ToString()
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    _engineServices.ObservabilityService.Metrics.Retention.Trigger.RecordTriggered(
                        executionId,
                        "retention-invoked");

                    var result = await _engineServices.PolicyEngineFactory
                        .Create<IAiRetentionEngine>(AiPolicyKind.Retention, stepContext)
                        .ApplyAsync(
                            new AiRetentionContext
                            {
                                ExecutionId = executionId,
                                ExecutionState = state,
                                UtcNow = DateTime.UtcNow
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    var evictedSteps = result.EvictedSteps ?? Array.Empty<string>();

                    if (result.IsEmpty)
                    {
                        var reason = result.Decision?.Reason ?? "no-policy-or-no-op";

                        _engineServices.ObservabilityService.Metrics.Retention.Trigger.RecordSkipped(
                            executionId,
                            reason);

                        trace.SetTag("skipped", true);
                        trace.SetTag("reason", reason);

                        await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                _engineServices,
                                executionId,
                                pipelineKey,
                                "_retention",
                                "_retention",
                                runtimeInstanceId,
                                claimToken: null,
                                concurrencyContext: null,
                                AiDecisionLedgerCategory.Retention,
                                AiDecisionLedgerEvents.Retention.Skipped,
                                AiDecisionLedgerOutcome.Skipped,
                                reason,
                                new Dictionary<string, string>
                                {
                                    [AiPolicyMetadataKeys.Name] = policyName,
                                    ["steps.count"] = state.Steps.Count.ToString()
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var compactedSteps = result.CompactedSteps ?? Array.Empty<string>();
                        var compactedCount = compactedSteps.Count;
                        var evictedCount = evictedSteps.Count;

                        trace.SetTag("skipped", false);
                        trace.SetTag("compactedCount", compactedCount);
                        trace.SetTag("evictedCount", evictedCount);
                        trace.SetTag("totalSteps", state.Steps.Count);

                        await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                _engineServices,
                                executionId,
                                pipelineKey,
                                stepContext.StepName,
                                stepContext.StepKey,
                                runtimeInstanceId,
                                claimToken: null,
                                concurrencyContext: null,
                                AiDecisionLedgerCategory.Retention,
                                AiDecisionLedgerEvents.Retention.Triggered,
                                AiDecisionLedgerOutcome.Triggered,
                                result.Decision?.Reason ?? "Retention policy triggered.",
                                new Dictionary<string, string>
                                {
                                    [AiPolicyMetadataKeys.Name] = policyName,
                                    ["steps.count"] = state.Steps.Count.ToString(),
                                    ["compacted.count"] = compactedCount.ToString(),
                                    ["evicted.count"] = evictedCount.ToString()
                                },
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (compactedCount > 0)
                        {
                            _engineServices.ObservabilityService.Metrics.Retention.Decision.RecordCompactionRequired(
                                executionId,
                                state.Steps.Count,
                                compactedCount);

                            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                    _engineServices,
                                    executionId,
                                    pipelineKey,
                                    stepContext.StepName,
                                    stepContext.StepKey,
                                    runtimeInstanceId,
                                    claimToken: null,
                                    concurrencyContext: null,
                                    AiDecisionLedgerCategory.Retention,
                                    AiDecisionLedgerEvents.Retention.Compacted,
                                    AiDecisionLedgerOutcome.Applied,
                                    "Retention compacted step payloads.",
                                    new Dictionary<string, string>
                                    {
                                        [AiPolicyMetadataKeys.Name] = policyName,
                                        ["compacted.count"] = compactedCount.ToString(),
                                        ["compacted.steps"] = string.Join(",", compactedSteps)
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);

                            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                    _engineServices,
                                    executionId,
                                    pipelineKey,
                                    stepContext.StepName,
                                    stepContext.StepKey,
                                    runtimeInstanceId,
                                    claimToken: null,
                                    concurrencyContext: null,
                                    AiDecisionLedgerCategory.Payload,
                                    AiDecisionLedgerEvents.Payload.Externalized,
                                    AiDecisionLedgerOutcome.Persisted,
                                    "Step payloads externalized during retention compaction.",
                                    new Dictionary<string, string>
                                    {
                                        [AiPolicyMetadataKeys.Name] = policyName,
                                        ["payload.externalized.count"] = compactedCount.ToString(),
                                        ["compacted.steps"] = string.Join(",", compactedSteps)
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        if (evictedCount > 0)
                        {
                            _engineServices.ObservabilityService.Metrics.Retention.Decision.RecordEvictionRequired(
                                executionId,
                                state.Steps.Count,
                                evictedCount);

                            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                    _engineServices,
                                    executionId,
                                    pipelineKey,
                                    stepContext.StepName,
                                    stepContext.StepKey,
                                    runtimeInstanceId,
                                    claimToken: null,
                                    concurrencyContext: null,
                                    AiDecisionLedgerCategory.Retention,
                                    AiDecisionLedgerEvents.Retention.Evicted,
                                    AiDecisionLedgerOutcome.Applied,
                                    "Retention evicted archived steps from hot state.",
                                    new Dictionary<string, string>
                                    {
                                        [AiPolicyMetadataKeys.Name] = policyName,
                                        ["evicted.count"] = evictedCount.ToString(),
                                        ["evicted.steps"] = string.Join(",", evictedSteps)
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        if (compactedCount == 0 && evictedCount == 0)
                        {
                            _engineServices.ObservabilityService.Metrics.Retention.Decision.RecordNoActionRequired(
                                executionId,
                                state.Steps.Count);
                        }

                        _engineServices.ObservabilityService.Metrics.Retention.Plan.RecordPlanCreated(
                            executionId,
                            compactedCount,
                            evictedCount,
                            state.Steps.Count);

                        foreach (var stepId in evictedSteps)
                        {
                            _engineServices.ObservabilityService.Metrics.Retention.Execution.RecordStepEvicted(
                                executionId,
                                stepId);

                            _engineServices.ObservabilityService.Metrics.Retention.Execution.RecordStepMarkedArchived(
                                executionId,
                                stepId);
                        }

                        if (result.CompactedSteps is not null)
                        {
                            foreach (var stepId in result.CompactedSteps)
                            {
                                _engineServices.ObservabilityService.Metrics.Retention.Execution.RecordPayloadCompacted(
                                    executionId,
                                    stepId,
                                    beforeBytes: 0,
                                    afterBytes: 0);
                            }
                        }

                        _engineServices.ObservabilityService.Metrics.Retention.Execution.RecordRetentionCompleted(
                            executionId);
                    }

                    if (_engineServices.DagStore is not null)
                    {
                        await _engineServices.DagStore.SaveStateAsync(
                            executionId,
                            state,
                            cancellationToken).ConfigureAwait(false);

                        trace.SetTag("statePersisted", true);
                    }
                    else
                    {
                        trace.SetTag("statePersisted", false);
                    }

                    if (evictedSteps.Count > 0)
                    {
                        await _engineServices.StepResolver.WarmStepsAsync(
                            executionId,
                            state,
                            evictedSteps,
                            cancellationToken).ConfigureAwait(false);

                        trace.SetTag("resolverWarmed", true);
                        trace.SetTag("resolverWarmStepCount", evictedSteps.Count);
                    }
                    else
                    {
                        trace.SetTag("resolverWarmed", false);
                        trace.SetTag("resolverWarmStepCount", 0);
                    }

                    var stepsAfter = state.Steps.Count;

                    trace.SetTag("stepsBefore", stepsBefore);
                    trace.SetTag("stepsAfter", stepsAfter);
                    trace.SetTag("removedSteps", stepsBefore - stepsAfter);
                    trace.SetTag(AiWorkerMetadataKeys.CamelCaseWorkerId, runtimeInstanceId);

                    return true;
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Applies retention once for a completed batch of DAG step transitions, persists state,
        /// and warms the resolver for all steps that may be needed after batch retention.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="state">The current execution state after all batch step transitions have been persisted.</param>
        /// <param name="stepContext">A representative step context used to resolve retention policies.</param>
        /// <param name="candidateStepIds">
        /// Step identifiers that should remain resolvable after retention. This should include
        /// transitioned batch steps and any required completed steps captured before retention.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task ApplyBatchRetentionPersistAndWarmAsync(
            string executionId,
            AiExecutionState state,
            AiStepExecutionContext stepContext,
            IReadOnlyCollection<string> candidateStepIds,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(stepContext);
            ArgumentNullException.ThrowIfNull(candidateStepIds);

            await _engineServices.ObservabilityService.Tracer.TraceRetentionAsync(
                new AiRetentionTraceContext
                {
                    ExecutionId = executionId,
                    PolicyName = "policy-driven-batch-retention",
                    InspectedSteps = state.Steps.Count
                },
                async trace =>
                {
                    var stepsBefore = state.Steps.Count;
                    var policyName = "policy-driven-batch-retention";
                    var runtimeInstanceId = _engineServices.RuntimeInstanceIdentity.RuntimeInstanceId;
                    var pipelineKey = string.IsNullOrWhiteSpace(state.PipelineName)
                        ? "unknown"
                        : state.PipelineName;

                    await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                            _engineServices,
                            executionId,
                            pipelineKey,
                            stepContext.StepName,
                            stepContext.StepKey,
                            runtimeInstanceId,
                            claimToken: null,
                            concurrencyContext: null,
                            AiDecisionLedgerCategory.Retention,
                            AiDecisionLedgerEvents.Retention.Evaluated,
                            AiDecisionLedgerOutcome.Started,
                            "Batch retention evaluation started.",
                            new Dictionary<string, string>
                            {
                                [AiPolicyMetadataKeys.Name] = policyName,
                                ["steps.count"] = state.Steps.Count.ToString(),
                                ["candidate.steps.count"] = candidateStepIds.Count.ToString()
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    _engineServices.ObservabilityService.Metrics.Retention.Trigger.RecordTriggered(
                        executionId,
                        "batch-retention-invoked");

                    var result = await _engineServices.PolicyEngineFactory
                        .Create<IAiRetentionEngine>(AiPolicyKind.Retention, stepContext)
                        .ApplyAtomicAsync(
                            new AiRetentionContext
                            {
                                ExecutionId = executionId,
                                ExecutionState = state,
                                UtcNow = DateTime.UtcNow
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    var evictedSteps = result.EvictedSteps ?? Array.Empty<string>();
                    var compactedSteps = result.CompactedSteps ?? Array.Empty<string>();

                    if (result.IsEmpty)
                    {
                        var reason = result.Decision?.Reason ?? "no-policy-or-no-op";

                        _engineServices.ObservabilityService.Metrics.Retention.Trigger.RecordSkipped(
                            executionId,
                            reason);

                        trace.SetTag("skipped", true);
                        trace.SetTag("reason", reason);

                        await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                _engineServices,
                                executionId,
                                pipelineKey,
                                stepContext.StepName,
                                stepContext.StepKey,
                                runtimeInstanceId,
                                claimToken: null,
                                concurrencyContext: null,
                                AiDecisionLedgerCategory.Retention,
                                AiDecisionLedgerEvents.Retention.Skipped,
                                AiDecisionLedgerOutcome.Skipped,
                                reason,
                                new Dictionary<string, string>
                                {
                                    [AiPolicyMetadataKeys.Name] = policyName,
                                    ["steps.count"] = state.Steps.Count.ToString(),
                                    ["candidate.steps.count"] = candidateStepIds.Count.ToString()
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var compactedCount = compactedSteps.Count;
                        var evictedCount = evictedSteps.Count;

                        trace.SetTag("skipped", false);
                        trace.SetTag("compactedCount", compactedCount);
                        trace.SetTag("evictedCount", evictedCount);
                        trace.SetTag("totalSteps", state.Steps.Count);

                        await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                _engineServices,
                                executionId,
                                pipelineKey,
                                stepContext.StepName,
                                stepContext.StepKey,
                                runtimeInstanceId,
                                claimToken: null,
                                concurrencyContext: null,
                                AiDecisionLedgerCategory.Retention,
                                AiDecisionLedgerEvents.Retention.Triggered,
                                AiDecisionLedgerOutcome.Triggered,
                                result.Decision?.Reason ?? "Batch retention policy triggered.",
                                new Dictionary<string, string>
                                {
                                    [AiPolicyMetadataKeys.Name] = policyName,
                                    ["steps.count"] = state.Steps.Count.ToString(),
                                    ["candidate.steps.count"] = candidateStepIds.Count.ToString(),
                                    ["compacted.count"] = compactedCount.ToString(),
                                    ["evicted.count"] = evictedCount.ToString()
                                },
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (compactedCount > 0)
                        {
                            _engineServices.ObservabilityService.Metrics.Retention.Decision.RecordCompactionRequired(
                                executionId,
                                state.Steps.Count,
                                compactedCount);

                            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                    _engineServices,
                                    executionId,
                                    pipelineKey,
                                    stepContext.StepName,
                                    stepContext.StepKey,
                                    runtimeInstanceId,
                                    claimToken: null,
                                    concurrencyContext: null,
                                    AiDecisionLedgerCategory.Retention,
                                    AiDecisionLedgerEvents.Retention.Compacted,
                                    AiDecisionLedgerOutcome.Applied,
                                    "Batch retention compacted step payloads.",
                                    new Dictionary<string, string>
                                    {
                                        [AiPolicyMetadataKeys.Name] = policyName,
                                        ["compacted.count"] = compactedCount.ToString(),
                                        ["compacted.steps"] = string.Join(",", compactedSteps)
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);

                            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                    _engineServices,
                                    executionId,
                                    pipelineKey,
                                    stepContext.StepName,
                                    stepContext.StepKey,
                                    runtimeInstanceId,
                                    claimToken: null,
                                    concurrencyContext: null,
                                    AiDecisionLedgerCategory.Payload,
                                    AiDecisionLedgerEvents.Payload.Externalized,
                                    AiDecisionLedgerOutcome.Persisted,
                                    "Step payloads externalized during batch retention compaction.",
                                    new Dictionary<string, string>
                                    {
                                        [AiPolicyMetadataKeys.Name] = policyName,
                                        ["payload.externalized.count"] = compactedCount.ToString(),
                                        ["compacted.steps"] = string.Join(",", compactedSteps)
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        if (evictedCount > 0)
                        {
                            _engineServices.ObservabilityService.Metrics.Retention.Decision.RecordEvictionRequired(
                                executionId,
                                state.Steps.Count,
                                evictedCount);

                            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                    _engineServices,
                                    executionId,
                                    pipelineKey,
                                    stepContext.StepName,
                                    stepContext.StepKey,
                                    runtimeInstanceId,
                                    claimToken: null,
                                    concurrencyContext: null,
                                    AiDecisionLedgerCategory.Retention,
                                    AiDecisionLedgerEvents.Retention.Evicted,
                                    AiDecisionLedgerOutcome.Applied,
                                    "Batch retention evicted archived steps from hot state.",
                                    new Dictionary<string, string>
                                    {
                                        [AiPolicyMetadataKeys.Name] = policyName,
                                        ["evicted.count"] = evictedCount.ToString(),
                                        ["evicted.steps"] = string.Join(",", evictedSteps)
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        if (compactedCount == 0 && evictedCount == 0)
                        {
                            _engineServices.ObservabilityService.Metrics.Retention.Decision.RecordNoActionRequired(
                                executionId,
                                state.Steps.Count);

                            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                                    _engineServices,
                                    executionId,
                                    pipelineKey,
                                    stepContext.StepName,
                                    stepContext.StepKey,
                                    runtimeInstanceId,
                                    claimToken: null,
                                    concurrencyContext: null,
                                    AiDecisionLedgerCategory.Retention,
                                    AiDecisionLedgerEvents.Retention.Skipped,
                                    AiDecisionLedgerOutcome.Skipped,
                                    result.Decision?.Reason ?? "Batch retention did not apply any atomic hot-state changes.",
                                    new Dictionary<string, string>
                                    {
                                        [AiPolicyMetadataKeys.Name] = policyName,
                                        ["steps.count"] = state.Steps.Count.ToString(),
                                        ["candidate.steps.count"] = candidateStepIds.Count.ToString(),
                                        ["decision.kind"] = result.Decision?.Kind.ToString() ?? "Unknown",
                                        ["compacted.count"] = compactedCount.ToString(),
                                        ["evicted.count"] = evictedCount.ToString()
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        _engineServices.ObservabilityService.Metrics.Retention.Plan.RecordPlanCreated(
                            executionId,
                            compactedCount,
                            evictedCount,
                            state.Steps.Count);

                        foreach (var stepId in evictedSteps)
                        {
                            _engineServices.ObservabilityService.Metrics.Retention.Execution.RecordStepEvicted(
                                executionId,
                                stepId);

                            _engineServices.ObservabilityService.Metrics.Retention.Execution.RecordStepMarkedArchived(
                                executionId,
                                stepId);
                        }

                        foreach (var stepId in compactedSteps)
                        {
                            _engineServices.ObservabilityService.Metrics.Retention.Execution.RecordPayloadCompacted(
                                executionId,
                                stepId,
                                beforeBytes: 0,
                                afterBytes: 0);
                        }

                        _engineServices.ObservabilityService.Metrics.Retention.Execution.RecordRetentionCompleted(
                            executionId);
                    }

                    trace.SetTag("statePersisted", false);
                    trace.SetTag("statePersistenceMode", "atomic-retention-patch");

                    var warmStepIds = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var stepId in candidateStepIds)
                    {
                        if (!string.IsNullOrWhiteSpace(stepId))
                        {
                            warmStepIds.Add(stepId);
                        }
                    }

                    foreach (var stepId in evictedSteps)
                    {
                        if (!string.IsNullOrWhiteSpace(stepId))
                        {
                            warmStepIds.Add(stepId);
                        }
                    }

                    foreach (var stepId in compactedSteps)
                    {
                        if (!string.IsNullOrWhiteSpace(stepId))
                        {
                            warmStepIds.Add(stepId);
                        }
                    }

                    if (warmStepIds.Count > 0)
                    {
                        await _engineServices.StepResolver.WarmStepsAsync(
                            executionId,
                            state,
                            warmStepIds.ToArray(),
                            cancellationToken).ConfigureAwait(false);

                        trace.SetTag("resolverWarmed", true);
                        trace.SetTag("resolverWarmStepCount", warmStepIds.Count);
                    }
                    else
                    {
                        trace.SetTag("resolverWarmed", false);
                        trace.SetTag("resolverWarmStepCount", 0);
                    }

                    var stepsAfter = state.Steps.Count;

                    trace.SetTag("stepsBefore", stepsBefore);
                    trace.SetTag("stepsAfter", stepsAfter);
                    trace.SetTag("removedSteps", stepsBefore - stepsAfter);
                    trace.SetTag(AiWorkerMetadataKeys.CamelCaseWorkerId, runtimeInstanceId);

                    return true;
                }).ConfigureAwait(false);
        }
    }
}