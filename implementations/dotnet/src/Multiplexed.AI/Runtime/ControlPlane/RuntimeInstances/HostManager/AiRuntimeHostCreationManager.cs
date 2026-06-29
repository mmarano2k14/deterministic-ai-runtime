using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Selects the host creation strategy matching the requested host creation mode.
    /// </summary>
    public sealed class AiRuntimeHostCreationManager : IAiRuntimeHostManager
    {
        private const string RuntimeHostCreationOperation = "runtime-host-creation";

        /// <summary>
        /// The registered host creation strategies indexed by host creation mode.
        /// </summary>
        private readonly IReadOnlyDictionary<AiRuntimeHostCreationMode, IAiRuntimeHostCreationStrategy> strategies;

        /// <summary>
        /// The logger used to report host creation selection failures.
        /// </summary>
        private readonly ILogger<AiRuntimeHostCreationManager> logger;

        /// <summary>
        /// The control-plane observer.
        /// </summary>
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeHostCreationManager"/> class.
        /// </summary>
        /// <param name="strategies">The registered runtime host creation strategies.</param>
        /// <param name="logger">The logger used to report host creation selection failures.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="strategies"/> or <paramref name="logger"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when multiple strategies are registered for the same host creation mode.</exception>
        public AiRuntimeHostCreationManager(
            IEnumerable<IAiRuntimeHostCreationStrategy> strategies,
            ILogger<AiRuntimeHostCreationManager> logger)
            : this(
                strategies,
                logger,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeHostCreationManager"/> class.
        /// </summary>
        /// <param name="strategies">The registered runtime host creation strategies.</param>
        /// <param name="logger">The logger used to report host creation selection failures.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="strategies"/>, <paramref name="logger"/>, or <paramref name="observer"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when multiple strategies are registered for the same host creation mode.</exception>
        public AiRuntimeHostCreationManager(
            IEnumerable<IAiRuntimeHostCreationStrategy> strategies,
            ILogger<AiRuntimeHostCreationManager> logger,
            IAiControlPlaneObserver observer)
        {
            ArgumentNullException.ThrowIfNull(strategies);

            var strategyList = strategies.ToList();
            var duplicatedMode = strategyList
                .GroupBy(strategy => strategy.Mode)
                .FirstOrDefault(group => group.Count() > 1);

            if (duplicatedMode is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple runtime host creation strategies are registered for mode '{duplicatedMode.Key}'.");
            }

            this.strategies = strategyList.ToDictionary(strategy => strategy.Mode);
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartRuntimeAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc = DateTimeOffset.UtcNow;

            await this.RecordHostCreationEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["runtimeInstanceId"] = request.RuntimeInstanceId,
                        ["providerName"] = request.ProviderName,
                        ["transportName"] = request.TransportName,
                        ["transportEndpoint"] = request.TransportEndpoint,
                        ["hostCreationMode"] = request.HostCreationMode.ToString(),
                        ["strategyCount"] = this.strategies.Count,
                        ["registeredModes"] = string.Join(",", this.strategies.Keys.Select(mode => mode.ToString()))
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!this.strategies.TryGetValue(request.HostCreationMode, out var strategy))
            {
                this.logger.LogWarning(
                    "No runtime host creation strategy is registered for mode {HostCreationMode}. RuntimeInstanceId={RuntimeInstanceId}, ProviderName={ProviderName}.",
                    request.HostCreationMode,
                    request.RuntimeInstanceId,
                    request.ProviderName);

                var rejectedResult =
                    AiRuntimeHostStartResult.Rejected(
                        request.ExecutionContextSnapshot,
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        request.TransportEndpoint,
                        $"runtime-host-creation-mode-not-registered:{request.HostCreationMode}");

                await this.RecordHostCreationResultAsync(
                        request,
                        rejectedResult,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                return rejectedResult;
            }

            try
            {
                var result =
                    await strategy
                        .StartAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                await this.RecordHostCreationResultAsync(
                        request,
                        result,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await this.RecordHostCreationEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        null,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        durationMs,
                        new Dictionary<string, object?>
                        {
                            ["runtimeInstanceId"] = request.RuntimeInstanceId,
                            ["providerName"] = request.ProviderName,
                            ["transportName"] = request.TransportName,
                            ["transportEndpoint"] = request.TransportEndpoint,
                            ["hostCreationMode"] = request.HostCreationMode.ToString(),
                            ["durationMs"] = durationMs,
                            ["exception.type"] = exception.GetType().FullName,
                            ["exception.message"] = exception.Message
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <summary>
        /// Records a runtime host creation result.
        /// </summary>
        /// <param name="request">The host start request.</param>
        /// <param name="result">The host start result.</param>
        /// <param name="startedAtUtc">The operation start timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RecordHostCreationResultAsync(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);
            var outcome = ResolveOutcome(result);
            var eventType = result.Success
                ? AiControlPlaneEventType.OperationCompleted
                : AiControlPlaneEventType.OperationFailed;
            var failureReason = result.Success
                ? null
                : result.FailureReason;

            await this.RecordHostCreationEventAsync(
                    eventType,
                    request,
                    result,
                    outcome,
                    failureReason,
                    durationMs,
                    new Dictionary<string, object?>
                    {
                        ["runtimeInstanceId"] = result.RuntimeInstanceId ?? request.RuntimeInstanceId,
                        ["providerName"] = result.ProviderName ?? request.ProviderName,
                        ["transportName"] = result.TransportName ?? request.TransportName,
                        ["transportEndpoint"] = result.TransportEndpoint ?? request.TransportEndpoint,
                        ["hostCreationMode"] = request.HostCreationMode.ToString(),
                        ["success"] = result.Success,
                        ["failureReason"] = result.FailureReason,
                        ["durationMs"] = durationMs
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records a runtime host creation control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The host start request.</param>
        /// <param name="result">The optional host start result.</param>
        /// <param name="outcome">The optional operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <param name="properties">The event properties.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RecordHostCreationEventAsync(
            AiControlPlaneEventType eventType,
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult? result,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            long? durationMs,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.observer
                    .RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.Scaling,
                            Operation = RuntimeHostCreationOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.RuntimeInstanceId)
                                    ? Guid.NewGuid().ToString("N")
                                    : request.RuntimeInstanceId,
                                RuntimeInstanceId = result?.RuntimeInstanceId ?? request.RuntimeInstanceId,
                                PipelineKey = request.ExecutionContextSnapshot?.ContextKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                new Dictionary<string, object?>
                                {
                                    ["runtimeInstanceId"] = result?.RuntimeInstanceId ?? request.RuntimeInstanceId,
                                    ["providerName"] = result?.ProviderName ?? request.ProviderName,
                                    ["transportName"] = result?.TransportName ?? request.TransportName,
                                    ["transportEndpoint"] = result?.TransportEndpoint ?? request.TransportEndpoint,
                                    ["hostCreationMode"] = request.HostCreationMode.ToString(),
                                    ["tenantId"] = request.ExecutionContextSnapshot?.TenantId,
                                    ["tenantGroupId"] = request.ExecutionContextSnapshot?.TenantGroupId,
                                    ["pipelineKey"] = request.ExecutionContextSnapshot?.ContextKey,
                                    ["success"] = result?.Success,
                                    ["failureReason"] = result?.FailureReason
                                })
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break host creation.
            }
        }

        /// <summary>
        /// Resolves the operation outcome from a host start result.
        /// </summary>
        /// <param name="result">The host start result.</param>
        /// <returns>The operation outcome.</returns>
        private static AiControlPlaneOperationOutcome ResolveOutcome(
            AiRuntimeHostStartResult result)
        {
            return result.Success
                ? AiControlPlaneOperationOutcome.Succeeded
                : AiControlPlaneOperationOutcome.Denied;
        }

        /// <summary>
        /// Merges control-plane event properties.
        /// </summary>
        /// <param name="properties">The base event properties.</param>
        /// <param name="additionalProperties">The additional event properties.</param>
        /// <returns>The merged event properties.</returns>
        private static IReadOnlyDictionary<string, object?> MergeEventProperties(
            IReadOnlyDictionary<string, object?>? properties,
            IReadOnlyDictionary<string, object?> additionalProperties)
        {
            var merged = new Dictionary<string, object?>();

            if (properties is not null)
            {
                foreach (var item in properties)
                {
                    merged[item.Key] = item.Value;
                }
            }

            foreach (var item in additionalProperties)
            {
                merged[item.Key] = item.Value;
            }

            return merged;
        }

        /// <summary>
        /// Calculates duration in milliseconds.
        /// </summary>
        /// <param name="startedAtUtc">The start timestamp.</param>
        /// <param name="completedAtUtc">The completion timestamp.</param>
        /// <returns>The duration in milliseconds.</returns>
        private static long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }
    }
}