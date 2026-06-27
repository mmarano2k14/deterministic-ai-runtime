using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
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
        private const string RecoveryForensicsIdMetadataKey = "recovery.forensicsId";
        private const string RecoveryFailedExecutionIdMetadataKey = "recovery.failedExecutionId";
        private const string RecoveryFailedLocalRunIdMetadataKey = "recovery.failedLocalRunId";
        private const string RecoveryFailedRuntimeInstanceIdMetadataKey = "recovery.failedRuntimeInstanceId";

        private readonly IAiRuntimeQueueControlPlane _runtimeQueue;
        private readonly IAiRuntimeRecoveryForensicsRecorder _forensicsRecorder;

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
                new NoopAiRuntimeRecoveryForensicsRecorder())
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
        {
            _runtimeQueue = runtimeQueue ?? throw new ArgumentNullException(nameof(runtimeQueue));
            _forensicsRecorder = forensicsRecorder ?? throw new ArgumentNullException(nameof(forensicsRecorder));
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
                    return new AiSharedRunDispatchResult
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
                }

                await RecordLocalRecoveryDispatchForensicsAsync(
                        request,
                        operationMetadata,
                        queueResult.RunHandle?.RunId,
                        queueResult.RunHandle?.ExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                return new AiSharedRunDispatchResult
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
            }
            catch (Exception exception)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;

                return new AiSharedRunDispatchResult
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
            }
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