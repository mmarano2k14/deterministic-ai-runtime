using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.ControlPlane;
using Multiplexed.Abstractions.AI.Observability.Events;


namespace Multiplexed.AI.Runtime.ControlPlane.SharedController
{
    /// <summary>
    /// Local implementation of the shared run dispatcher.
    /// </summary>
    /// <remarks>
    /// This dispatcher sends a shared run to the local runtime queue through
    /// <see cref="IAiRuntimeQueueControlPlane"/>.
    ///
    /// V1 is intentionally local-only.
    /// It does not call remote pods.
    /// It does not use Kubernetes.
    /// It does not perform scaling.
    /// It does not execute DAG steps directly.
    /// </remarks>
    public sealed class LocalAiSharedRunDispatcher : IAiSharedRunDispatcher
    {
        private const string LocalSharedRunDispatchOperation = "local-shared-run-dispatch";

        private readonly IAiRuntimeQueueControlPlane _runtimeQueue;
        private readonly IAiControlPlaneObserver _observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalAiSharedRunDispatcher"/> class.
        /// </summary>
        /// <param name="runtimeQueue">The local runtime queue control-plane facade.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="runtimeQueue"/> is null.
        /// </exception>
        public LocalAiSharedRunDispatcher(
            IAiRuntimeQueueControlPlane runtimeQueue)
            : this(
                runtimeQueue,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalAiSharedRunDispatcher"/> class.
        /// </summary>
        /// <param name="runtimeQueue">The local runtime queue control-plane facade.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="runtimeQueue"/> or <paramref name="observer"/> is null.
        /// </exception>
        public LocalAiSharedRunDispatcher(
            IAiRuntimeQueueControlPlane runtimeQueue,
            IAiControlPlaneObserver observer)
            : this(
                runtimeQueue,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalAiSharedRunDispatcher"/> class.
        /// </summary>
        /// <param name="runtimeQueue">The local runtime queue control-plane facade.</param>
        /// <param name="forensicsRecorder">The runtime recovery forensics recorder.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="runtimeQueue"/> or <paramref name="forensicsRecorder"/> is null.
        /// </exception>
        public LocalAiSharedRunDispatcher(
            IAiRuntimeQueueControlPlane runtimeQueue,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder)
            : this(
                runtimeQueue,
                forensicsRecorder,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalAiSharedRunDispatcher"/> class.
        /// </summary>
        /// <param name="runtimeQueue">The local runtime queue control-plane facade.</param>
        /// <param name="forensicsRecorder">The runtime recovery forensics recorder.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="runtimeQueue"/>, <paramref name="forensicsRecorder"/>, or <paramref name="observer"/> is null.
        /// </exception>
        public LocalAiSharedRunDispatcher(
            IAiRuntimeQueueControlPlane runtimeQueue,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder,
            IAiControlPlaneObserver observer)
        {
            _runtimeQueue = runtimeQueue ?? throw new ArgumentNullException(nameof(runtimeQueue));
            ArgumentNullException.ThrowIfNull(forensicsRecorder);
            ArgumentNullException.ThrowIfNull(observer);
            _observer = AiRecoveryObservabilityCompatibility.Compose(observer, forensicsRecorder);
        }

        /// <inheritdoc />
        public async Task<AiSharedRunDispatchResult> DispatchAsync(
            AiSharedRunDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.SharedRun);
            ArgumentNullException.ThrowIfNull(request.SharedRun.RunRequest);

            if (string.IsNullOrWhiteSpace(request.SharedRun.SharedRunId))
            {
                throw new ArgumentException(
                    "Shared run id cannot be null or empty.",
                    nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                throw new ArgumentException(
                    "Runtime instance id cannot be null or empty.",
                    nameof(request));
            }

            var startedAtUtc = DateTimeOffset.UtcNow;

            await RecordLocalDispatchEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?>
                    {
                        [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRun.SharedRunId,
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request.RuntimeInstanceId,
                        [AiExecutionClaimMetadataKeys.CamelCaseClaimToken] = request.ClaimToken,
                        [AiControlPlaneRequestMetadataKeys.RequestedBy] = request.RequestedBy,
                        ["source"] = request.Source,
                        ["reason"] = request.Reason,
                        [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                        [AiControlPlaneMetadataKeys.ControlPlaneId] = request.SharedRun.ControlPlaneId
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var operationMetadata =
                    MergeMetadata(
                        request.SharedRun.Metadata,
                        request.Metadata);

                var queueResult = await _runtimeQueue
                    .EnqueueRunAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                            RunRequest = request.SharedRun.RunRequest,
                            CorrelationId = request.CorrelationId ?? request.SharedRun.CorrelationId,
                            RequestedBy = request.RequestedBy,
                            Source = request.Source,
                            Reason = request.Reason,
                            Metadata = operationMetadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                var completedAtUtc = DateTimeOffset.UtcNow;

                if (!queueResult.Success)
                {
                    var failedResult = new AiSharedRunDispatchResult
                    {
                        Success = false,
                        SharedRunId = request.SharedRun.SharedRunId,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        ClaimToken = request.ClaimToken,
                        Message = "Shared run dispatch failed.",
                        FailureReason = queueResult.FailureReason ?? queueResult.Message,
                        StartedAtUtc = startedAtUtc,
                        CompletedAtUtc = completedAtUtc,
                        DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                        Diagnostics = queueResult.Diagnostics
                    };

                    await RecordLocalDispatchResultEventAsync(
                            request,
                            failedResult,
                            AiControlPlaneOperationOutcome.CompletedWithIssues,
                            failedResult.FailureReason,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return failedResult;
                }

                await RecordLocalRecoveryDispatchForensicsAsync(
                        request,
                        operationMetadata,
                        queueResult.RunHandle?.RunId,
                        queueResult.RunHandle?.ExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                var succeededResult = new AiSharedRunDispatchResult
                {
                    Success = true,
                    SharedRunId = request.SharedRun.SharedRunId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    LocalRunId = queueResult.RunHandle?.RunId,
                    ExecutionId = queueResult.RunHandle?.ExecutionId,
                    ClaimToken = request.ClaimToken,
                    Message = "Shared run dispatched to local runtime queue.",
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                    Diagnostics = queueResult.Diagnostics
                };

                await RecordLocalDispatchResultEventAsync(
                        request,
                        succeededResult,
                        AiControlPlaneOperationOutcome.Succeeded,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);

                return succeededResult;
            }
            catch (Exception exception)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;

                var failedResult = new AiSharedRunDispatchResult
                {
                    Success = false,
                    SharedRunId = request.SharedRun.SharedRunId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    ClaimToken = request.ClaimToken,
                    Message = "Shared run dispatch failed.",
                    FailureReason = exception.Message,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                    Diagnostics = new[] { exception.Message }
                };

                await RecordLocalDispatchResultEventAsync(
                        request,
                        failedResult,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        cancellationToken)
                    .ConfigureAwait(false);

                return failedResult;
            }
        }

        /// <summary>
        /// Records a local shared run dispatch result control-plane event.
        /// </summary>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="result">The shared run dispatch result.</param>
        /// <param name="outcome">The control-plane operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the control-plane event has been recorded.</returns>
        private Task RecordLocalDispatchResultEventAsync(
            AiSharedRunDispatchRequest request,
            AiSharedRunDispatchResult result,
            AiControlPlaneOperationOutcome outcome,
            string? failureReason,
            CancellationToken cancellationToken)
        {
            return RecordLocalDispatchEventAsync(
                result.Success ? AiControlPlaneEventType.OperationCompleted : AiControlPlaneEventType.OperationFailed,
                request,
                result.LocalRunId,
                result.ExecutionId,
                outcome,
                failureReason,
                result.DurationMs,
                new Dictionary<string, object?>
                {
                    [AiRunMetadataKeys.CamelCaseSharedRunId] = result.SharedRunId,
                    [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = result.RuntimeInstanceId,
                    [AiRunMetadataKeys.CamelCaseLocalRunId] = result.LocalRunId,
                    [AiExecutionMetadataKeys.CamelCaseExecutionId] = result.ExecutionId,
                    [AiExecutionClaimMetadataKeys.CamelCaseClaimToken] = result.ClaimToken,
                    ["success"] = result.Success,
                    ["message"] = result.Message,
                    [AiObservabilityMetadataKeys.FailureReason] = result.FailureReason,
                    [AiObservabilityMetadataKeys.DurationMs] = result.DurationMs,
                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                    [AiControlPlaneMetadataKeys.ControlPlaneId] = request.SharedRun.ControlPlaneId
                },
                cancellationToken);
        }

        /// <summary>
        /// Records a local shared run dispatch control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="localRunId">The optional local run identifier.</param>
        /// <param name="executionId">The optional execution identifier.</param>
        /// <param name="outcome">The optional control-plane operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <param name="properties">The optional event properties.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the control-plane event has been recorded.</returns>
        private async Task RecordLocalDispatchEventAsync(
            AiControlPlaneEventType eventType,
            AiSharedRunDispatchRequest request,
            string? localRunId,
            string? executionId,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            long? durationMs,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            try
            {
                await _observer.RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.SharedController,
                            Operation = LocalSharedRunDispatchOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                                    ? request.SharedRun.CorrelationId ?? Guid.NewGuid().ToString("N")
                                    : request.CorrelationId,
                                RunId = request.SharedRun.SharedRunId,
                                ExecutionId = executionId,
                                RuntimeInstanceId = request.RuntimeInstanceId,
                                PipelineKey = request.SharedRun.PipelineKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                new Dictionary<string, object?>
                                {
                                    [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRun.SharedRunId,
                                    [AiRunMetadataKeys.CamelCaseLocalRunId] = localRunId,
                                    [AiExecutionMetadataKeys.CamelCaseExecutionId] = executionId,
                                    [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request.RuntimeInstanceId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                                    [AiExecutionClaimMetadataKeys.CamelCaseClaimToken] = request.ClaimToken,
                                    [AiControlPlaneMetadataKeys.ControlPlaneId] = request.SharedRun.ControlPlaneId
                                })
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break local shared run dispatch.
            }
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
        /// Records local runtime recovery dispatch forensics after a replacement local run has been created.
        /// </summary>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="metadata">The merged operation metadata.</param>
        /// <param name="localRunId">The replacement local run identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the forensics events have been recorded.</returns>
        private async Task RecordLocalRecoveryDispatchForensicsAsync(
            AiSharedRunDispatchRequest request,
            IReadOnlyDictionary<string, string> metadata,
            string? localRunId,
            string? executionId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(localRunId))
            {
                return;
            }

            if (!TryResolveRecoveryForensicsId(
                    request,
                    metadata,
                    out var forensicsId,
                    out var resolvedExecutionId,
                    out var failedLocalRunId))
            {
                return;
            }

            var durableExecutionId =
                !string.IsNullOrWhiteSpace(executionId)
                    ? executionId
                    : resolvedExecutionId;

            var recoveryMetadata = CreateRecoveryDispatchEventMetadata(
                request,
                metadata,
                localRunId,
                durableExecutionId,
                failedLocalRunId);
            var replacementRegisteredEventType =
                AiEngineEvents.Recovery.ReplacementLocalRunRegistered;

            await _observer
                .RecordAsync(
                    AiRecoveryEngineEventFactory.Create(
                        semanticEventType: replacementRegisteredEventType,
                        eventId: string.Join(":", forensicsId, replacementRegisteredEventType, request.RuntimeInstanceId, localRunId),
                        forensicsId: forensicsId,
                        timestampUtc: DateTimeOffset.UtcNow,
                        outcome: "registered",
                        reason: "replacement-local-run-registered-for-recovery-redispatch",
                        executionId: durableExecutionId,
                        sharedRunId: request.SharedRun.SharedRunId,
                        localRunId: localRunId,
                        runtimeInstanceId: request.RuntimeInstanceId,
                        metadata: recoveryMetadata),
                    cancellationToken)
                .ConfigureAwait(false);

            var resumeContextSeededEventType =
                AiEngineEvents.Recovery.ResumeContextSeeded;

            await _observer
                .RecordAsync(
                    AiRecoveryEngineEventFactory.Create(
                        semanticEventType: resumeContextSeededEventType,
                        eventId: string.Join(":", forensicsId, resumeContextSeededEventType, request.RuntimeInstanceId, localRunId),
                        forensicsId: forensicsId,
                        timestampUtc: DateTimeOffset.UtcNow,
                        outcome: "seeded",
                        reason: "resume-context-seeded-from-shared-run-snapshot",
                        executionId: durableExecutionId,
                        sharedRunId: request.SharedRun.SharedRunId,
                        localRunId: localRunId,
                        runtimeInstanceId: request.RuntimeInstanceId,
                        metadata: recoveryMetadata),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates metadata for local recovery dispatch forensics events.
        /// </summary>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="metadata">The merged operation metadata.</param>
        /// <param name="localRunId">The replacement local run identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="failedLocalRunId">The failed local run identifier.</param>
        /// <returns>The metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CreateRecoveryDispatchEventMetadata(
            AiSharedRunDispatchRequest request,
            IReadOnlyDictionary<string, string> metadata,
            string localRunId,
            string? executionId,
            string? failedLocalRunId)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId ?? string.Empty,
                [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId ?? string.Empty,
                [AiRuntimeRecoveryMetadataKeys.ReplacementRuntimeInstanceId] = request.RuntimeInstanceId,
                [AiRuntimeRecoveryMetadataKeys.ReplacementLocalRunId] = localRunId,
                [AiRuntimeRecoveryMetadataKeys.ReplacementExecutionId] = executionId ?? string.Empty,
                [AiRuntimeRecoveryMetadataKeys.TransitionFailedRuntimeInstanceId] = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId),
                [AiRuntimeRecoveryMetadataKeys.TransitionFailedLocalRunId] = failedLocalRunId ?? string.Empty,
                [AiExecutionClaimMetadataKeys.ClaimToken] = request.ClaimToken ?? string.Empty,
                [AiRuntimeRecoveryMetadataKeys.ResumeContextKey] = request.SharedRun.ExecutionContextSnapshot.ContextKey ?? string.Empty,
                [AiRuntimeRecoveryMetadataKeys.ResumeSource] = AiRuntimeRecoveryResumeSources.SharedRunExecutionContextSnapshot
            };
        }

        /// <summary>
        /// Tries to resolve recovery forensics identity from dispatch metadata.
        /// </summary>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="metadata">The merged operation metadata.</param>
        /// <param name="forensicsId">The resolved forensics identifier.</param>
        /// <param name="executionId">The resolved durable execution identifier.</param>
        /// <param name="failedLocalRunId">The failed local run identifier.</param>
        /// <returns><c>true</c> when the recovery forensics identity can be resolved; otherwise, <c>false</c>.</returns>
        private static bool TryResolveRecoveryForensicsId(
            AiSharedRunDispatchRequest request,
            IReadOnlyDictionary<string, string> metadata,
            out string forensicsId,
            out string? executionId,
            out string? failedLocalRunId)
        {
            if (TryGetMetadataValue(
                    metadata,
                    AiRuntimeRecoveryMetadataKeys.ForensicsId,
                    out var explicitForensicsId))
            {
                forensicsId = explicitForensicsId;
                executionId = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedExecutionId);
                failedLocalRunId = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedLocalRunId);

                return true;
            }

            executionId =
                ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedExecutionId);

            failedLocalRunId =
                ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedLocalRunId);

            if (string.IsNullOrWhiteSpace(executionId) ||
                string.IsNullOrWhiteSpace(failedLocalRunId))
            {
                forensicsId = string.Empty;
                return false;
            }

            forensicsId = string.Join(
                ":",
                "runtime-recovery",
                executionId,
                request.SharedRun.SharedRunId,
                failedLocalRunId);

            return !string.IsNullOrWhiteSpace(request.SharedRun.SharedRunId);
        }

        /// <summary>
        /// Resolves a metadata value or an empty string.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value when present; otherwise, an empty string.</returns>
        private static string ResolveMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key)
        {
            return TryGetMetadataValue(
                metadata,
                key,
                out var value)
                ? value
                : string.Empty;
        }

        /// <summary>
        /// Attempts to read a metadata value by key using ordinal ignore-case matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The resolved value.</param>
        /// <returns><c>true</c> when a non-empty value is found; otherwise, <c>false</c>.</returns>
        private static bool TryGetMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key,
            out string value)
        {
            if (metadata.TryGetValue(
                    key,
                    out var directValue) &&
                !string.IsNullOrWhiteSpace(directValue))
            {
                value = directValue;
                return true;
            }

            foreach (var pair in metadata)
            {
                if (string.Equals(
                        pair.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }

        /// <summary>
        /// Calculates dispatch duration in milliseconds.
        /// </summary>
        /// <param name="startedAtUtc">The dispatch start timestamp.</param>
        /// <param name="completedAtUtc">The dispatch completion timestamp.</param>
        /// <returns>The duration in milliseconds.</returns>
        private static long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }

        /// <summary>
        /// Merges metadata dictionaries.
        /// </summary>
        /// <param name="baseMetadata">The base metadata.</param>
        /// <param name="overrideMetadata">The override metadata.</param>
        /// <returns>The merged metadata.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string> baseMetadata,
            IReadOnlyDictionary<string, string> overrideMetadata)
        {
            var merged = new Dictionary<string, string>(
                baseMetadata,
                StringComparer.Ordinal);

            foreach (var pair in overrideMetadata)
            {
                merged[pair.Key] = pair.Value;
            }

            return merged;
        }
    }
}