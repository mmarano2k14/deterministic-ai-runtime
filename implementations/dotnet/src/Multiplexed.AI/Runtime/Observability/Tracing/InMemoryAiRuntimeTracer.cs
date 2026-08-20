using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.AI.Runtime.Observability.Tracing.Stores;
using System;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;


namespace Multiplexed.AI.Runtime.Observability.Tracing
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiRuntimeTracer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This tracer creates trace scopes for AI runtime operations and stores completed
    /// trace records through <see cref="IAiTraceRecorder"/>.
    /// </para>
    ///
    /// <para>
    /// The tracer captures a detached runtime correlation snapshot when a trace scope
    /// starts. This aligns tracing with metrics, ledger entries, runtime workers,
    /// controller runs, and future replay diagnostics.
    /// </para>
    ///
    /// <para>
    /// This implementation is intended for tests, local diagnostics, and in-memory
    /// observability. It is not an OpenTelemetry exporter.
    /// </para>
    /// </remarks>
    public sealed class InMemoryAiRuntimeTracer : IAiRuntimeTracer
    {
        private readonly IAiTraceRecorder _recorder;
        private readonly IAiRuntimeCorrelationAccessor? _correlationAccessor;

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryAiRuntimeTracer"/> class.
        /// </summary>
        /// <param name="recorder">The trace recorder.</param>
        /// <remarks>
        /// This constructor is kept for compatibility with tests and lightweight
        /// usages that instantiate the tracer without dependency injection.
        /// </remarks>
        public InMemoryAiRuntimeTracer(
            IAiTraceRecorder recorder)
            : this(
                recorder,
                correlationAccessor: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryAiRuntimeTracer"/> class.
        /// </summary>
        /// <param name="recorder">The trace recorder.</param>
        /// <param name="correlationAccessor">The ambient runtime correlation accessor.</param>
        public InMemoryAiRuntimeTracer(
            IAiTraceRecorder recorder,
            IAiRuntimeCorrelationAccessor? correlationAccessor)
        {
            _recorder = recorder
                ?? throw new ArgumentNullException(nameof(recorder));

            _correlationAccessor = correlationAccessor;
        }

        /// <inheritdoc />
        public IAiTraceScope StartExecution(
            AiExecutionTraceContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return Start(
                operation: "execution",
                executionId: context.ExecutionId,
                stepId: null,
                stepKey: null,
                workerId: context.WorkerId,
                claimToken: null,
                provider: null,
                model: null,
                logicalOperation: "execution",
                traceSource: "execution",
                configure: record =>
                {
                    record.Tags["pipelineId"] = context.PipelineId;
                    record.Tags["executionMode"] = context.ExecutionMode;
                    record.Tags["status"] = context.Status;
                    record.Tags[AiWorkerMetadataKeys.CamelCaseWorkerId] = context.WorkerId;
                });
        }

        /// <inheritdoc />
        public IAiTraceScope StartStep(
            AiStepTraceContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var stepKey = !string.IsNullOrWhiteSpace(context.StepKey)
                ? context.StepKey
                : context.StepType;

            return Start(
                operation: "step",
                executionId: _correlationAccessor?.Current?.ExecutionId ?? context.ExecutionId,
                stepId: context.StepId,
                stepKey: stepKey,
                workerId: context.WorkerId,
                claimToken: context.ClaimToken,
                provider: null,
                model: null,
                logicalOperation: "step.execute",
                traceSource: "step",
                configure: record =>
                {
                    record.Tags["stepType"] = context.StepType;
                    record.Tags[AiStepMetadataKeys.CamelCaseStepKey] = context.StepKey;
                    record.Tags["status"] = context.Status;
                    record.Tags["retryCount"] = context.RetryCount;
                    record.Tags["recoveryCount"] = context.RecoveryCount;
                    record.Tags[AiWorkerMetadataKeys.CamelCaseWorkerId] = context.WorkerId;
                    record.Tags[AiExecutionClaimMetadataKeys.CamelCaseClaimToken] = context.ClaimToken;
                });
        }

        /// <inheritdoc />
        public IAiTraceScope StartRetention(
            AiRetentionTraceContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return Start(
                operation: "retention",
                executionId: context.ExecutionId,
                stepId: null,
                stepKey: null,
                workerId: null,
                claimToken: null,
                provider: null,
                model: null,
                logicalOperation: context.Action,
                traceSource: "retention",
                configure: record =>
                {
                    record.Tags["action"] = context.Action;
                    record.Tags["policyName"] = context.PolicyName;
                    record.Tags["inspectedSteps"] = context.InspectedSteps;
                    record.Tags["affectedSteps"] = context.AffectedSteps;
                });
        }

        /// <inheritdoc />
        public IAiTraceScope StartStorage(
            AiStorageTraceContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return Start(
                operation: "storage",
                executionId: context.ExecutionId,
                stepId: context.StepId,
                stepKey: null,
                workerId: null,
                claimToken: null,
                provider: null,
                model: null,
                logicalOperation: context.Operation,
                traceSource: context.Backend,
                configure: record =>
                {
                    record.Tags["backend"] = context.Backend;
                    record.Tags["operation"] = context.Operation;
                    record.Tags["hit"] = context.Hit;
                });
        }

        /// <inheritdoc />
        public IAiTraceScope StartResolver(
            AiResolverTraceContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return Start(
                operation: "resolver",
                executionId: context.ExecutionId,
                stepId: context.StepId,
                stepKey: null,
                workerId: null,
                claimToken: null,
                provider: null,
                model: null,
                logicalOperation: "resolve",
                traceSource: "resolver",
                configure: record =>
                {
                    record.Tags["path"] = context.Path;
                    record.Tags["source"] = context.Source;
                    record.Tags["found"] = context.Found;
                });
        }

        /// <summary>
        /// Starts a trace scope and captures a detached runtime correlation snapshot.
        /// </summary>
        /// <param name="operation">The trace operation name.</param>
        /// <param name="executionId">The explicit execution identifier.</param>
        /// <param name="stepId">The explicit step identifier.</param>
        /// <param name="stepKey">The explicit step key.</param>
        /// <param name="workerId">The explicit worker identifier.</param>
        /// <param name="claimToken">The explicit claim token.</param>
        /// <param name="provider">The provider name.</param>
        /// <param name="model">The model name.</param>
        /// <param name="logicalOperation">The logical operation name.</param>
        /// <param name="traceSource">The trace source.</param>
        /// <param name="configure">The trace record configuration callback.</param>
        /// <returns>The started trace scope.</returns>
        private IAiTraceScope Start(
            string operation,
            string? executionId,
            string? stepId,
            string? stepKey,
            string? workerId,
            string? claimToken,
            string? provider,
            string? model,
            string? logicalOperation,
            string? traceSource,
            Action<AiTraceRecord> configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            ArgumentNullException.ThrowIfNull(configure);

            var record = new AiTraceRecord
            {
                Operation = operation,
                ExecutionId = executionId,
                StepId = stepId,
                StartedAtUtc = DateTime.UtcNow
            };

            record.Correlation = AiRuntimeTraceCorrelationSnapshotFactory.Create(
                _correlationAccessor?.Current,
                executionId: executionId,
                stepId: stepId,
                stepKey: stepKey,
                workerId: workerId,
                claimToken: claimToken,
                provider: provider,
                model: model,
                operation: logicalOperation,
                traceId: record.Id,
                traceScopeId: record.Id,
                parentTraceScopeId: null,
                source: traceSource);

            configure(record);

            return new AiTraceScope(
                _recorder,
                record);
        }
    }
}