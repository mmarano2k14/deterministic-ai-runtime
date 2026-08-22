using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Context;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.AI.Policies
{
    /// <summary>
    /// Provides shared behavior for step-scoped AI policy engines.
    /// </summary>
    /// <remarks>
    /// This base class is responsible for resolving step-scoped policy configuration,
    /// resolving registered policies, executing those policies, emitting canonical policy facts
    /// through the existing Event Manager, and recording implementation-level policy traces.
    ///
    /// It does not own domain-specific decisions such as retry, retention, eviction,
    /// concurrency admission, or recovery. Domain-specific runtime consequences remain
    /// owned by the caller.
    /// </remarks>
    public abstract class AiPolicyEngine : IAiPolicyEngine
    {
        private const string PolicyPipelineFallback = "policy-engine";
        private const string PolicyWorkerFallback = "policy-engine";
        private const string PolicyExecutionOperation = "policy.execute";

        private readonly IAiPolicyRegistry policyRegistry;
        private readonly IAiControlPlaneObserver observer;

        public readonly IAiRuntimeObservability _obs;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiPolicyEngine"/> class.
        /// </summary>
        /// <param name="policyRegistry">The registry used to resolve policies.</param>
        /// <param name="stepContext">The step execution context bound to this engine instance.</param>
        /// <param name="obs">The runtime observability facade.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="policyRegistry"/>, <paramref name="stepContext"/>,
        /// or <paramref name="obs"/> is <see langword="null"/>.
        /// </exception>
        protected AiPolicyEngine(
            IAiPolicyRegistry policyRegistry,
            AiStepExecutionContext stepContext,
            IAiRuntimeObservability obs)
        {
            this.policyRegistry = policyRegistry ?? throw new ArgumentNullException(nameof(policyRegistry));
            StepContext = stepContext ?? throw new ArgumentNullException(nameof(stepContext));
            _obs = obs ?? throw new ArgumentNullException(nameof(obs));
            this.observer = AiPolicyObservabilityCompatibility.Compose(StepContext.Services, _obs);
        }

        /// <inheritdoc />
        public abstract AiPolicyKind Kind { get; }

        /// <inheritdoc />
        public AiStepExecutionContext StepContext { get; }

        /// <summary>
        /// Resolves a typed policy definition from the current step configuration.
        /// </summary>
        /// <typeparam name="TDefinition">The policy definition type.</typeparam>
        /// <param name="configKey">The step configuration key.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The resolved policy definition, or <see langword="null"/> when missing.</returns>
        protected async Task<TDefinition?> ResolvePolicyDefinitionAsync<TDefinition>(
            string configKey,
            CancellationToken cancellationToken = default)
            where TDefinition : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configKey);

            return await StepContext
                .GetHelper()
                .GetConfigAsync<TDefinition>(configKey, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves policies from ordered policy keys and an expected policy kind.
        /// </summary>
        /// <param name="policyKeys">The ordered policy keys.</param>
        /// <param name="policyKind">The expected policy kind.</param>
        /// <returns>The resolved policies.</returns>
        protected IReadOnlyCollection<IAiPolicy> ResolvePolicies(
            IEnumerable<string> policyKeys,
            AiPolicyKind policyKind)
        {
            ArgumentNullException.ThrowIfNull(policyKeys);

            return policyRegistry.ResolveMany(policyKeys, policyKind);
        }

        /// <summary>
        /// Executes the specified policies against the provided policy context.
        /// </summary>
        /// <typeparam name="TPolicyContext">The policy context type.</typeparam>
        /// <param name="policyContext">The context evaluated by the policies.</param>
        /// <param name="policies">The policies to execute.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The ordered policy results.</returns>
        protected async Task<IReadOnlyCollection<AiPolicyResult>> ExecutePoliciesAsync<TPolicyContext>(
            TPolicyContext policyContext,
            IReadOnlyCollection<IAiPolicy> policies,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(policyContext);
            ArgumentNullException.ThrowIfNull(policies);

            if (policies.Count == 0)
            {
                return Array.Empty<AiPolicyResult>();
            }

            var results = new List<AiPolicyResult>(policies.Count);

            var executionId = StepContext.ExecutionId;
            var stepName = StepContext.StepName;
            var pipelineKey = ResolvePipelineKey();
            var workerId = ResolveWorkerId();

            foreach (var policy in policies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var policyName = policy.GetType().Name;

                var traceContext = new AiStepTraceContext
                {
                    ExecutionId = executionId,
                    StepId = stepName,
                    Operation = PolicyExecutionOperation
                };

                using var scope = _obs.Tracer.StartStep(traceContext);

                var start = DateTime.UtcNow;

                await RecordPolicyEventAsync(
                        executionId,
                        pipelineKey,
                        stepName,
                        workerId,
                        policyName,
                        AiEngineEvents.Policy.Evaluated,
                        "Policy evaluation started.",
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    var result = await policy
                        .ExecuteAsync(policyContext, cancellationToken)
                        .ConfigureAwait(false);

                    results.Add(result);

                    var duration = DateTime.UtcNow - start;

                    scope?.SetTag("policy", policyName);
                    scope?.SetTag("kind", Kind.ToString());
                    scope?.SetTag("success", result.IsSuccess);
                    scope?.SetTag(AiObservabilityMetadataKeys.DurationMs, duration.TotalMilliseconds);
                    scope?.SetTag(AiPolicyMetadataKeys.ResultKind, result.Kind.ToString());
                    scope?.SetTag("result.message", result.Message);

                    await RecordPolicyEventAsync(
                            executionId,
                            pipelineKey,
                            stepName,
                            workerId,
                            policyName,
                            ResolvePolicyDecisionEventType(result),
                            result.Message ?? ResolvePolicyDecisionReason(result),
                            new Dictionary<string, string>
                            {
                                [AiObservabilityMetadataKeys.DottedDurationMs] = duration.TotalMilliseconds.ToString("F2"),
                                [AiPolicyMetadataKeys.ResultKind] = result.Kind.ToString(),
                                [AiPolicyMetadataKeys.ResultSuccess] = result.IsSuccess.ToString()
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    scope?.SetTag("policy", policyName);
                    scope?.SetTag("kind", Kind.ToString());
                    scope?.SetTag("exception", true);
                    scope?.SetTag("error", ex.Message);

                    await RecordPolicyEventAsync(
                            executionId,
                            pipelineKey,
                            stepName,
                            workerId,
                            policyName,
                            AiEngineEvents.Policy.Failed,
                            ex.Message,
                            new Dictionary<string, string>
                            {
                                [AiExceptionMetadataKeys.ExceptionType] = ex.GetType().Name
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    throw;
                }
            }

            return results;
        }

        /// <summary>
        /// Emits one canonical policy event through the existing Event Manager.
        /// </summary>
        private async Task RecordPolicyEventAsync(
            string executionId,
            string pipelineKey,
            string stepName,
            string workerId,
            string policyName,
            string eventType,
            string? reason,
            IReadOnlyDictionary<string, string>? additionalMetadata,
            CancellationToken cancellationToken)
        {
            var properties = new Dictionary<string, object?>
            {
                [AiPolicyMetadataKeys.Name] = policyName,
                [AiPolicyMetadataKeys.Kind] = Kind.ToString(),
                [AiPipelineMetadataKeys.Key] = pipelineKey,
                [AiStepMetadataKeys.StepName] = stepName,
                [AiStepMetadataKeys.StepId] = stepName,
                [AiStepMetadataKeys.StepKey] = stepName,
                [AiWorkerMetadataKeys.WorkerId] = workerId
            };

            if (additionalMetadata is not null)
            {
                foreach (var pair in additionalMetadata)
                {
                    properties[pair.Key] = pair.Value;
                }
            }

            await this.observer
                .RecordAsync(
                    new AiControlPlaneEvent
                    {
                        SemanticEventType = eventType,
                        EventType = ResolveControlPlaneEventType(eventType),
                        Area = AiControlPlaneArea.Policy,
                        Operation = PolicyExecutionOperation,
                        Outcome = ResolveControlPlaneOutcome(eventType),
                        Message = reason,
                        FailureReason = string.Equals(eventType, AiEngineEvents.Policy.Failed, StringComparison.Ordinal)
                            ? reason
                            : null,
                        Correlation = new AiRuntimeExecutionCorrelationContext
                        {
                            CorrelationId = executionId,
                            ExecutionId = executionId,
                            PipelineName = pipelineKey,
                            PipelineKey = pipelineKey,
                            RuntimeInstanceId = workerId,
                            WorkerId = workerId
                        },
                        Properties = properties
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the generic control-plane phase envelope for a canonical policy event.
        /// </summary>
        private static AiControlPlaneEventType ResolveControlPlaneEventType(string eventType)
        {
            if (string.Equals(eventType, AiEngineEvents.Policy.Evaluated, StringComparison.Ordinal))
            {
                return AiControlPlaneEventType.OperationStarted;
            }

            if (string.Equals(eventType, AiEngineEvents.Policy.Denied, StringComparison.Ordinal))
            {
                return AiControlPlaneEventType.OperationDenied;
            }

            if (string.Equals(eventType, AiEngineEvents.Policy.Failed, StringComparison.Ordinal))
            {
                return AiControlPlaneEventType.OperationFailed;
            }

            return AiControlPlaneEventType.OperationCompleted;
        }

        /// <summary>
        /// Resolves the generic control-plane outcome for a canonical policy event.
        /// </summary>
        private static AiControlPlaneOperationOutcome? ResolveControlPlaneOutcome(string eventType)
        {
            if (string.Equals(eventType, AiEngineEvents.Policy.Evaluated, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(eventType, AiEngineEvents.Policy.Denied, StringComparison.Ordinal))
            {
                return AiControlPlaneOperationOutcome.Denied;
            }

            if (string.Equals(eventType, AiEngineEvents.Policy.Failed, StringComparison.Ordinal))
            {
                return AiControlPlaneOperationOutcome.Failed;
            }

            return AiControlPlaneOperationOutcome.Succeeded;
        }

        /// <summary>
        /// Resolves the policy decision ledger event type from a policy result.
        /// </summary>
        private static string ResolvePolicyDecisionEventType(
            AiPolicyResult result)
        {
            if (result.Kind == AiPolicyResultKind.Block)
            {
                return AiEngineEvents.Policy.Denied;
            }

            return result.IsSuccess
                ? AiEngineEvents.Policy.Allowed
                : AiEngineEvents.Policy.Failed;
        }

        /// <summary>
        /// Resolves a fallback policy decision reason.
        /// </summary>
        private static string ResolvePolicyDecisionReason(
            AiPolicyResult result)
        {
            if (result.Kind == AiPolicyResultKind.Block)
            {
                return "Policy denied execution.";
            }

            return result.IsSuccess
                ? "Policy allowed execution."
                : "Policy evaluation failed.";
        }

        /// <summary>
        /// Resolves the best available pipeline key for policy ledger correlation.
        /// </summary>
        private string ResolvePipelineKey()
        {
            var pipelineName = StepContext.Execution.Record.PipelineName;

            return string.IsNullOrWhiteSpace(pipelineName)
                ? PolicyPipelineFallback
                : pipelineName!;
        }

        /// <summary>
        /// Resolves the best available worker identifier for policy ledger correlation.
        /// </summary>
        private string ResolveWorkerId()
        {
            var workerId = StepContext.Execution.Record.CurrentStep;

            return string.IsNullOrWhiteSpace(workerId)
                ? PolicyWorkerFallback
                : workerId!;
        }
    }
}