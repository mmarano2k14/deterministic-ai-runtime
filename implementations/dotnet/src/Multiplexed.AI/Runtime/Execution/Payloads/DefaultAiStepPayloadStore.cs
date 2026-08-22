using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.Execution.Payloads
{
    /// <summary>
    /// Default step payload store.
    ///
    /// PURPOSE:
    /// - Stores complete <see cref="AiStepState"/> objects externally.
    /// - Provides the persistence layer required by Evict retention.
    /// - Reuses the configured <see cref="IAiPayloadStore"/> through
    ///   <see cref="IAiPayloadStoreResolver"/>.
    ///
    /// DESIGN:
    /// - <see cref="IAiPayloadStore"/> remains generic payload storage.
    /// - <see cref="IAiStepPayloadStore"/> adds step-specific semantics.
    /// - Retention can safely evict a step only after <see cref="SaveStepAsync"/>
    ///   succeeds.
    ///
    /// IMPORTANT:
    /// - This service does not mutate <see cref="AiExecutionState"/>.
    /// - This service does not decide retention policy.
    /// - Redis/Mongo/InMemory selection stays inside <see cref="IAiPayloadStoreResolver"/>.
    /// </summary>
    public sealed class DefaultAiStepPayloadStore : IAiStepPayloadStore
    {
        private const string ContentType = "application/vnd.multiplexed.ai.step-state+json";

        private readonly IAiPayloadStoreResolver _storeResolver;
        private readonly IAiRuntimeObservability _observability;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiStepPayloadStore"/> class.
        /// </summary>
        /// <param name="storeResolver">
        /// The payload store resolver used to select the configured payload store.
        /// </param>
        /// <param name="observability">
        /// The runtime observability facade used to record payload rehydration ledger events.
        /// </param>
        public DefaultAiStepPayloadStore(
            IAiPayloadStoreResolver storeResolver,
            IAiRuntimeObservability observability)
        {
            _storeResolver = storeResolver ?? throw new ArgumentNullException(nameof(storeResolver));
            _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        }

        /// <inheritdoc />
        public async Task<AiStoredPayload> SaveStepAsync(
            string executionId,
            string stepName,
            AiStepState step,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentNullException.ThrowIfNull(step);

            var json = JsonSerializer.Serialize(step, JsonOptions());

            var metadata = new AiPayloadMetadata
            {
                Kind = "step-state",
                ExecutionId = executionId,
                StepName = stepName,
                ContentType = ContentType,
                Reason = "retention"
            };

            var store = _storeResolver.Resolve();

            var key = await store.SaveAsync(
                    json,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            return new AiStoredPayload
            {
                IsInline = false,
                ArtifactId = key,
                ContentType = ContentType,
                SizeBytes = json.Length
            };
        }

        /// <inheritdoc />
        public async Task<AiStepState?> LoadStepAsync(
            string executionId,
            string stepName,
            AiStoredPayload payload,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentNullException.ThrowIfNull(payload);

            if (payload.IsInline)
            {
                var inlineStep = payload.InlineValue as AiStepState;

                if (inlineStep is null)
                {
                    await RecordPayloadResolutionFailedAsync(
                            executionId,
                            stepName,
                            payload,
                            "Inline step payload could not be converted to AiStepState.",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RecordPayloadRehydratedAsync(
                            executionId,
                            stepName,
                            payload,
                            source: "inline",
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return inlineStep;
            }

            if (string.IsNullOrWhiteSpace(payload.ArtifactId))
            {
                await RecordPayloadResolutionFailedAsync(
                        executionId,
                        stepName,
                        payload,
                        $"Evicted step '{stepName}' for execution '{executionId}' has no payload artifact id.",
                        cancellationToken)
                    .ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Evicted step '{stepName}' for execution '{executionId}' has no payload artifact id.");
            }

            var store = _storeResolver.Resolve();

            var content = await store.LoadAsync(
                    payload.ArtifactId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (content is null)
            {
                await RecordPayloadResolutionFailedAsync(
                        executionId,
                        stepName,
                        payload,
                        "Archived step payload content was not found.",
                        cancellationToken)
                    .ConfigureAwait(false);

                return null;
            }

            var step = JsonSerializer.Deserialize<AiStepState>(
                content,
                JsonOptions());

            if (step is null)
            {
                await RecordPayloadResolutionFailedAsync(
                        executionId,
                        stepName,
                        payload,
                        "Archived step payload content could not be deserialized.",
                        cancellationToken)
                    .ConfigureAwait(false);

                return null;
            }

            await RecordPayloadRehydratedAsync(
                    executionId,
                    stepName,
                    payload,
                    source: "external",
                    cancellationToken)
                .ConfigureAwait(false);

            return step;
        }

        /// <summary>
        /// Records a payload rehydration ledger event.
        /// </summary>
        /// <param name="executionId">
        /// The execution identifier.
        /// </param>
        /// <param name="stepName">
        /// The step name.
        /// </param>
        /// <param name="payload">
        /// The stored payload reference.
        /// </param>
        /// <param name="source">
        /// The source used to rehydrate the payload.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous ledger operation.
        /// </returns>
        private async Task RecordPayloadRehydratedAsync(
            string executionId,
            string stepName,
            AiStoredPayload payload,
            string source,
            CancellationToken cancellationToken)
        {
            await _observability.Ledger.RecordAsync(
                    CreateCorrelationContext(
                        executionId,
                        stepName,
                        payload),
                    AiDecisionLedgerCategory.Payload,
                    AiEngineEvents.Payload.Rehydrated,
                    AiDecisionLedgerOutcome.Applied,
                    "Step payload was rehydrated.",
                    new Dictionary<string, string>
                    {
                        [AiStepMetadataKeys.StepName] = stepName,
                        ["payload.source"] = source,
                        ["payload.inline"] = payload.IsInline.ToString(),
                        ["payload.artifact.id"] = payload.ArtifactId ?? string.Empty,
                        ["payload.content.type"] = payload.ContentType ?? string.Empty,
                        ["payload.size.bytes"] = payload.SizeBytes.ToString() ?? string.Empty
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records a payload resolution failure ledger event.
        /// </summary>
        /// <param name="executionId">
        /// The execution identifier.
        /// </param>
        /// <param name="stepName">
        /// The step name.
        /// </param>
        /// <param name="payload">
        /// The stored payload reference.
        /// </param>
        /// <param name="reason">
        /// The failure reason.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous ledger operation.
        /// </returns>
        private async Task RecordPayloadResolutionFailedAsync(
            string executionId,
            string stepName,
            AiStoredPayload payload,
            string reason,
            CancellationToken cancellationToken)
        {
            await _observability.Ledger.RecordAsync(
                    CreateCorrelationContext(
                        executionId,
                        stepName,
                        payload),
                    AiDecisionLedgerCategory.Payload,
                    AiEngineEvents.Payload.ResolutionFailed,
                    AiDecisionLedgerOutcome.Failed,
                    reason,
                    new Dictionary<string, string>
                    {
                        [AiStepMetadataKeys.StepName] = stepName,
                        ["payload.inline"] = payload.IsInline.ToString(),
                        ["payload.artifact.id"] = payload.ArtifactId ?? string.Empty,
                        ["payload.content.type"] = payload.ContentType ?? string.Empty,
                        ["payload.size.bytes"] = payload.SizeBytes.ToString() ?? string.Empty
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates the runtime correlation context used for payload ledger events.
        /// </summary>
        /// <param name="executionId">
        /// The execution identifier.
        /// </param>
        /// <param name="stepName">
        /// The step name.
        /// </param>
        /// <param name="payload">
        /// The stored payload reference.
        /// </param>
        /// <returns>
        /// The runtime correlation context.
        /// </returns>
        private static AiRuntimeLedgerEventCorrelationContext CreateCorrelationContext(
            string executionId,
            string stepName,
            AiStoredPayload payload)
        {
            return new AiRuntimeLedgerEventCorrelationContext
            {
                ExecutionId = executionId,
                StepId = stepName,
                StepKey = stepName,
                Operation = "payload.rehydrate",
                InputPayloadRef = payload.ArtifactId
            };
        }

        /// <summary>
        /// Creates the JSON serializer options used by the step payload store.
        /// </summary>
        /// <returns>
        /// The JSON serializer options.
        /// </returns>
        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }
    }
}