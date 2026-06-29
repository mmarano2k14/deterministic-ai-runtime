using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;

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
        private const string RecoveryForensicsIdMetadataKey = "recovery.forensicsId";
        private const string RecoveryFailedExecutionIdMetadataKey = "recovery.failedExecutionId";
        private const string RecoveryFailedLocalRunIdMetadataKey = "recovery.failedLocalRunId";
        private const string RecoveryFailedRuntimeInstanceIdMetadataKey = "recovery.failedRuntimeInstanceId";

        private readonly IAiRuntimeQueueControlPlane _runtimeQueue;
        private readonly IAiRuntimeRecoveryForensicsRecorder _forensicsRecorder;
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
            _forensicsRecorder = forensicsRecorder ?? throw new ArgumentNullException(nameof(forensicsRecorder));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
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
                        ["sharedRunId"] = request.SharedRun.SharedRunId,
                        ["runtimeInstanceId"] = request.RuntimeInstanceId,
                        ["claimToken"] = request.ClaimToken,
                        ["requestedBy"] = request.RequestedBy,
                        ["source"] = request.Source,
                        ["reason"] = request.Reason,
                        ["tenantId"] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                        ["tenantGroupId"] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                        ["controlPlaneId"] = request.SharedRun.ControlPlaneId
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
                    ["sharedRunId"] = result.SharedRunId,
                    ["runtimeInstanceId"] = result.RuntimeInstanceId,
                    ["localRunId"] = result.LocalRunId,
                    ["executionId"] = result.ExecutionId,
                    ["claimToken"] = result.ClaimToken,
                    ["success"] = result.Success,
                    ["message"] = result.Message,
                    ["failureReason"] = result.FailureReason,
                    ["durationMs"] = result.DurationMs,
                    ["tenantId"] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                    ["tenantGroupId"] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                    ["controlPlaneId"] = request.SharedRun.ControlPlaneId
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
                                    ["sharedRunId"] = request.SharedRun.SharedRunId,
                                    ["localRunId"] = localRunId,
                                    ["executionId"] = executionId,
                                    ["runtimeInstanceId"] = request.RuntimeInstanceId,
                                    ["tenantId"] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                                    ["tenantGroupId"] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                                    ["claimToken"] = request.ClaimToken,
                                    ["controlPlaneId"] = request.SharedRun.ControlPlaneId
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

            await _forensicsRecorder
                .RecordEventAsync(
                    new AiRuntimeRecoveryForensicsEvent
                    {
                        EventId = string.Join(
                            ":",
                            forensicsId,
                            AiRuntimeRecoveryForensicsEventType.ReplacementLocalRunRegistered,
                            request.RuntimeInstanceId,
                            localRunId),
                        ForensicsId = forensicsId,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        EventType = AiRuntimeRecoveryForensicsEventType.ReplacementLocalRunRegistered,
                        Outcome = "registered",
                        Reason = "replacement-local-run-registered-for-recovery-redispatch",
                        ExecutionId = durableExecutionId,
                        SharedRunId = request.SharedRun.SharedRunId,
                        LocalRunId = localRunId,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Metadata = CreateRecoveryDispatchEventMetadata(
                            request,
                            metadata,
                            localRunId,
                            durableExecutionId,
                            failedLocalRunId)
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await _forensicsRecorder
                .RecordEventAsync(
                    new AiRuntimeRecoveryForensicsEvent
                    {
                        EventId = string.Join(
                            ":",
                            forensicsId,
                            AiRuntimeRecoveryForensicsEventType.ResumeContextSeeded,
                            request.RuntimeInstanceId,
                            localRunId),
                        ForensicsId = forensicsId,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        EventType = AiRuntimeRecoveryForensicsEventType.ResumeContextSeeded,
                        Outcome = "seeded",
                        Reason = "resume-context-seeded-from-shared-run-snapshot",
                        ExecutionId = durableExecutionId,
                        SharedRunId = request.SharedRun.SharedRunId,
                        LocalRunId = localRunId,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Metadata = CreateRecoveryDispatchEventMetadata(
                            request,
                            metadata,
                            localRunId,
                            durableExecutionId,
                            failedLocalRunId)
                    },
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
                ["tenant.id"] = request.SharedRun.ExecutionContextSnapshot.TenantId ?? string.Empty,
                ["tenant.group.id"] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId ?? string.Empty,
                ["replacement.runtimeInstanceId"] = request.RuntimeInstanceId,
                ["replacement.localRunId"] = localRunId,
                ["replacement.executionId"] = executionId ?? string.Empty,
                ["failed.runtimeInstanceId"] = ResolveMetadataValue(metadata, RecoveryFailedRuntimeInstanceIdMetadataKey),
                ["failed.localRunId"] = failedLocalRunId ?? string.Empty,
                ["claim.token"] = request.ClaimToken ?? string.Empty,
                ["resume.contextKey"] = request.SharedRun.ExecutionContextSnapshot.ContextKey ?? string.Empty,
                ["resume.source"] = "shared-run.execution-context-snapshot"
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
                    RecoveryForensicsIdMetadataKey,
                    out var explicitForensicsId))
            {
                forensicsId = explicitForensicsId;
                executionId = ResolveMetadataValue(metadata, RecoveryFailedExecutionIdMetadataKey);
                failedLocalRunId = ResolveMetadataValue(metadata, RecoveryFailedLocalRunIdMetadataKey);

                return true;
            }

            executionId =
                ResolveMetadataValue(metadata, RecoveryFailedExecutionIdMetadataKey);

            failedLocalRunId =
                ResolveMetadataValue(metadata, RecoveryFailedLocalRunIdMetadataKey);

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