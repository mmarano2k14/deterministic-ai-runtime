using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;

namespace Multiplexed.AI.Observability.Ledger
{
    /// <summary>
    /// Records important runtime decisions into the configured decision ledger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This recorder converts a runtime ledger event correlation context into an
    /// append-only decision ledger entry.
    /// </para>
    ///
    /// <para>
    /// The configured write mode controls whether ledger writes are disabled,
    /// best-effort, or strict.
    /// </para>
    ///
    /// <para>
    /// The recorder can enrich explicit ledger correlation values from the current
    /// ambient runtime correlation context. Explicit event values remain the source
    /// of truth. Ambient values are only used as safe fallbacks.
    /// </para>
    ///
    /// <para>
    /// IMPORTANT:
    /// - The recorder must not load execution records.
    /// - The recorder must not query the DAG store.
    /// - The recorder must not create runtime state.
    /// - The accessor is passive and only provides the current async-flow context.
    /// </para>
    /// </remarks>
    public sealed class DefaultAiDecisionLedgerRecorder : IAiDecisionLedgerRecorder
    {
        private readonly IAiDecisionLedger _ledger;
        private readonly IAiRuntimeCorrelationAccessor _correlationAccessor;
        private readonly AiDecisionLedgerRecorderOptions _options;
        private readonly ILogger<DefaultAiDecisionLedgerRecorder> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiDecisionLedgerRecorder"/> class.
        /// </summary>
        /// <param name="ledger">The decision ledger.</param>
        /// <param name="correlationAccessor">The ambient runtime correlation accessor.</param>
        /// <param name="options">The recorder options.</param>
        /// <param name="logger">The logger.</param>
        public DefaultAiDecisionLedgerRecorder(
            IAiDecisionLedger ledger,
            IAiRuntimeCorrelationAccessor correlationAccessor,
            IOptions<AiDecisionLedgerRecorderOptions> options,
            ILogger<DefaultAiDecisionLedgerRecorder> logger)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _correlationAccessor = correlationAccessor ?? throw new ArgumentNullException(nameof(correlationAccessor));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task RecordAsync(
            AiRuntimeLedgerEventCorrelationContext context,
            AiDecisionLedgerCategory category,
            string eventType,
            AiDecisionLedgerOutcome outcome,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.ExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

            if (_options.WriteMode == AiDecisionLedgerWriteMode.Disabled)
            {
                return;
            }

            var enrichedContext = EnrichCorrelationContext(
                context,
                category);

            var entry = new AiDecisionLedgerEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                CorrelationContext = enrichedContext,
                Sequence = 0,
                Category = category,
                EventType = eventType,
                Outcome = outcome,
                TimestampUtc = DateTimeOffset.UtcNow,
                Reason = reason,
                Metadata = metadata
            };

            try
            {
                await _ledger.AppendAsync(
                        entry,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "AI decision ledger recording skipped because the operation was cancelled. ExecutionId='{ExecutionId}', Category='{Category}', EventType='{EventType}', Outcome='{Outcome}'.",
                    enrichedContext.ExecutionId,
                    category,
                    eventType,
                    outcome);
            }
            catch (Exception exception) when (_options.WriteMode == AiDecisionLedgerWriteMode.BestEffort)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to record AI decision ledger entry in best-effort mode. ExecutionId='{ExecutionId}', Category='{Category}', EventType='{EventType}', Outcome='{Outcome}'.",
                    enrichedContext.ExecutionId,
                    category,
                    eventType,
                    outcome);
            }
        }

        /// <summary>
        /// Enriches the supplied ledger event correlation context with ambient runtime
        /// correlation values when explicit values are missing.
        /// </summary>
        /// <param name="context">The explicit ledger event correlation context.</param>
        /// <param name="category">The decision ledger category.</param>
        /// <returns>The enriched ledger event correlation context.</returns>
        private AiRuntimeLedgerEventCorrelationContext EnrichCorrelationContext(
            AiRuntimeLedgerEventCorrelationContext context,
            AiDecisionLedgerCategory category)
        {
            ArgumentNullException.ThrowIfNull(context);

            var current = _correlationAccessor.Current;

            return new AiRuntimeLedgerEventCorrelationContext
            {
                ExecutionId = context.ExecutionId,

                RunId = ResolveRunId(
                    context,
                    current,
                    category),

                PipelineName = context.PipelineName
                    ?? current?.PipelineName,

                PipelineVersion = context.PipelineVersion
                    ?? current?.PipelineVersion,

                StepId = context.StepId,
                StepKey = context.StepKey,

                RuntimeInstanceId = context.RuntimeInstanceId
                    ?? current?.RuntimeInstanceId,

                WorkerId = ResolveWorkerId(
                    context.WorkerId,
                    current),

                ClaimToken = context.ClaimToken,
                PolicyKey = context.PolicyKey,
                Provider = context.Provider,
                Model = context.Model,
                Operation = context.Operation,
                InputPayloadRef = context.InputPayloadRef,
                OutputPayloadRef = context.OutputPayloadRef,
                HumanInputRef = context.HumanInputRef,
                PromptRef = context.PromptRef,
                TraceId = context.TraceId,

                CorrelationId = ResolveCorrelationId(
                    context,
                    current,
                    category)
            };
        }

        /// <summary>
        /// Resolves the run identifier for a ledger event.
        /// </summary>
        /// <param name="context">The explicit ledger event correlation context.</param>
        /// <param name="current">The current ambient runtime execution correlation context.</param>
        /// <param name="category">The decision ledger category.</param>
        /// <returns>The resolved run identifier.</returns>
        private static string? ResolveRunId(
            AiRuntimeLedgerEventCorrelationContext context,
            AiRuntimeExecutionCorrelationContext? current,
            AiDecisionLedgerCategory category)
        {
            if (!string.IsNullOrWhiteSpace(context.RunId))
            {
                return context.RunId;
            }

            if (!string.IsNullOrWhiteSpace(current?.RunId))
            {
                return current.RunId;
            }

            if (category == AiDecisionLedgerCategory.Run &&
                !string.IsNullOrWhiteSpace(context.ExecutionId))
            {
                return context.ExecutionId;
            }

            return null;
        }

        /// <summary>
        /// Resolves the correlation identifier while avoiding runtime-host fallback
        /// correlation from replacing an explicit ledger execution or run identifier.
        /// </summary>
        /// <param name="context">The explicit ledger event correlation context.</param>
        /// <param name="current">The current ambient runtime execution correlation context.</param>
        /// <param name="category">The decision ledger category.</param>
        /// <returns>The resolved correlation identifier.</returns>
        private static string? ResolveCorrelationId(
            AiRuntimeLedgerEventCorrelationContext context,
            AiRuntimeExecutionCorrelationContext? current,
            AiDecisionLedgerCategory category)
        {
            var contextCorrelationIsRuntimeHostFallback =
                !string.IsNullOrWhiteSpace(context.CorrelationId) &&
                current is not null &&
                IsRuntimeHostFallbackCorrelation(current) &&
                string.Equals(
                    context.CorrelationId,
                    current.RuntimeInstanceId,
                    StringComparison.Ordinal);

            if (!string.IsNullOrWhiteSpace(context.CorrelationId) &&
                !contextCorrelationIsRuntimeHostFallback)
            {
                return context.CorrelationId;
            }

            if (!string.IsNullOrWhiteSpace(context.RunId))
            {
                return context.RunId;
            }

            if (!string.IsNullOrWhiteSpace(current?.RunId))
            {
                return current.RunId;
            }

            if (!string.IsNullOrWhiteSpace(current?.CorrelationId) &&
                !IsRuntimeHostFallbackCorrelation(current))
            {
                return current.CorrelationId;
            }

            if (category == AiDecisionLedgerCategory.Run &&
                !string.IsNullOrWhiteSpace(context.ExecutionId))
            {
                return context.ExecutionId;
            }

            return context.ExecutionId;
        }

        /// <summary>
        /// Resolves the worker identifier while preserving explicit worker values and upgrading
        /// old runtime-instance identifiers to the ambient logical worker identifier when safe.
        /// </summary>
        /// <param name="explicitWorkerId">The worker identifier supplied by the caller.</param>
        /// <param name="current">The current ambient runtime execution correlation context.</param>
        /// <returns>The resolved worker identifier.</returns>
        private static string? ResolveWorkerId(
            string? explicitWorkerId,
            AiRuntimeExecutionCorrelationContext? current)
        {
            if (string.IsNullOrWhiteSpace(explicitWorkerId))
            {
                return current?.WorkerId
                    ?? current?.RuntimeInstanceId;
            }

            if (current is null)
            {
                return explicitWorkerId;
            }

            if (string.IsNullOrWhiteSpace(current.WorkerId))
            {
                return explicitWorkerId;
            }

            if (string.IsNullOrWhiteSpace(current.RuntimeInstanceId))
            {
                return explicitWorkerId;
            }

            var explicitWorkerIsOldRuntimeInstanceId = string.Equals(
                explicitWorkerId,
                current.RuntimeInstanceId,
                StringComparison.Ordinal);

            var currentWorkerIsMoreSpecific = !string.Equals(
                current.WorkerId,
                current.RuntimeInstanceId,
                StringComparison.Ordinal);

            if (explicitWorkerIsOldRuntimeInstanceId && currentWorkerIsMoreSpecific)
            {
                return current.WorkerId;
            }

            return explicitWorkerId;
        }

        /// <summary>
        /// Determines whether the ambient correlation context is only the runtime-host
        /// fallback context created by the accessor when no execution or run context
        /// has been pushed.
        /// </summary>
        /// <param name="current">The current ambient runtime execution correlation context.</param>
        /// <returns><c>true</c> when the context is only the runtime-host fallback.</returns>
        private static bool IsRuntimeHostFallbackCorrelation(
            AiRuntimeExecutionCorrelationContext current)
        {
            return
                string.IsNullOrWhiteSpace(current.RunId) &&
                string.IsNullOrWhiteSpace(current.ExecutionId) &&
                !string.IsNullOrWhiteSpace(current.RuntimeInstanceId) &&
                string.Equals(
                    current.CorrelationId,
                    current.RuntimeInstanceId,
                    StringComparison.Ordinal);
        }
    }
}