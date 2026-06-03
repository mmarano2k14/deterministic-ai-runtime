using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance
{
    /// <summary>
    /// Local in-process shared runtime instance adapter.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Represents one dispatchable runtime instance inside the current process.
    /// - Dispatches a shared run into this instance's local runtime queue.
    ///
    /// This is the bridge needed for multi-instance tests:
    ///
    /// Shared queue claim
    /// -> resolve RuntimeInstanceId
    /// -> LocalAiSharedRuntimeInstance.DispatchAsync
    /// -> IAiRuntimeQueueControlPlane.EnqueueRunAsync
    /// -> LocalRunId returned.
    ///
    /// IMPORTANT:
    /// - This implementation is in-memory / in-process.
    /// - It is useful for tests and local multi-instance simulation.
    /// - Future Kubernetes implementations can use HTTP, gRPC, Redis streams,
    ///   or command queues behind the same shared runtime instance abstraction.
    /// </remarks>
    public sealed class LocalAiSharedRuntimeInstance : IAiSharedRuntimeInstance
    {
        private readonly IAiRuntimeQueueControlPlane _runtimeQueue;
        public IAiRuntimeQueueControlPlane QueueControlPlane { get; }

        public LocalAiSharedRuntimeInstance(
            string runtimeInstanceId,
            IAiRuntimeQueueControlPlane runtimeQueue)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(runtimeQueue);

            RuntimeInstanceId = runtimeInstanceId;
            _runtimeQueue = runtimeQueue;
            QueueControlPlane = runtimeQueue;
        }

        /// <inheritdoc />
        public string RuntimeInstanceId { get; }

        /// <inheritdoc />
        public async Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
            AiSharedRuntimeInstanceDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(request.SharedRun);
            ArgumentNullException.ThrowIfNull(request.RunRequest);

            

            var startedAtUtc = DateTimeOffset.UtcNow;

            if (!string.Equals(
                    RuntimeInstanceId,
                    request.RuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                var completedAtUtc = DateTimeOffset.UtcNow;

                return new AiSharedRuntimeInstanceDispatchResult
                {
                    Success = false,
                    RuntimeInstanceId = RuntimeInstanceId,
                    SharedRunId = request.SharedRun.SharedRunId,
                    ClaimToken = request.ClaimToken,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds,
                    FailureReason =
                        $"Runtime instance mismatch. Target='{request.RuntimeInstanceId}', Local='{RuntimeInstanceId}'."
                };
            }

            try
            {
                var result = await _runtimeQueue
                    .EnqueueRunAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                            RunRequest = request.RunRequest,
                            CorrelationId = request.CorrelationId ?? request.SharedRun.CorrelationId,
                            RequestedBy = request.RequestedBy,
                            Source = request.Source,
                            Reason = request.Reason,
                            Metadata = MergeMetadata(
                                request.Metadata,
                                request.SharedRun.Metadata,
                                request.SharedRun.SharedRunId,
                                RuntimeInstanceId,
                                request.ClaimToken)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;

                if (!result.Success)
                {
                    return new AiSharedRuntimeInstanceDispatchResult
                    {
                        Success = false,
                        RuntimeInstanceId = RuntimeInstanceId,
                        SharedRunId = request.SharedRun.SharedRunId,
                        ClaimToken = request.ClaimToken,
                        StartedAtUtc = startedAtUtc,
                        CompletedAtUtc = completedAtUtc,
                        DurationMs = durationMs,
                        FailureReason =
                            result.FailureReason ??
                            result.Message ??
                            "Local runtime queue dispatch failed.",
                        Metadata = CreateDebugMetadata(
                            request,
                            result,
                            RuntimeInstanceId,
                            localRunId: null,
                            executionId: null)
                    };
                }

                var localRunId =
                    result.RunHandle?.RunId ??
                    result.RunState?.RunId ??
                    result.RunId;

                var executionId =
                    result.RunState?.ExecutionId ??
                    result.ExecutionId;

                if (string.IsNullOrWhiteSpace(localRunId))
                {
                    return new AiSharedRuntimeInstanceDispatchResult
                    {
                        Success = false,
                        RuntimeInstanceId = RuntimeInstanceId,
                        SharedRunId = request.SharedRun.SharedRunId,
                        ClaimToken = request.ClaimToken,
                        StartedAtUtc = startedAtUtc,
                        CompletedAtUtc = completedAtUtc,
                        DurationMs = durationMs,
                        FailureReason =
                            "Local runtime queue dispatch succeeded but did not return a usable local run id.",
                        Metadata = CreateDebugMetadata(
                            request,
                            result,
                            RuntimeInstanceId,
                            localRunId: null,
                            executionId: executionId)
                    };
                }

                var visibilityCheck = await _runtimeQueue
                    .GetRunStatusAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                            RunId = localRunId,
                            CorrelationId = request.CorrelationId ?? request.SharedRun.CorrelationId,
                            RequestedBy = request.RequestedBy,
                            Source = "local-shared-runtime-instance-visibility-check",
                            Reason = "Verify that the local run id returned by enqueue is visible from the same runtime queue control-plane.",
                            Metadata = new Dictionary<string, string>
                            {
                                ["runtime.instance.id"] = RuntimeInstanceId,
                                ["shared.run.id"] = request.SharedRun.SharedRunId,
                                ["local.run.id"] = localRunId
                            }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (visibilityCheck.RunState is null)
                {
                    var visibilityFailedAtUtc = DateTimeOffset.UtcNow;

                    return new AiSharedRuntimeInstanceDispatchResult
                    {
                        Success = false,
                        RuntimeInstanceId = RuntimeInstanceId,
                        SharedRunId = request.SharedRun.SharedRunId,
                        LocalRunId = localRunId,
                        ExecutionId = executionId,
                        ClaimToken = request.ClaimToken,
                        StartedAtUtc = startedAtUtc,
                        CompletedAtUtc = visibilityFailedAtUtc,
                        DurationMs = (long)(visibilityFailedAtUtc - startedAtUtc).TotalMilliseconds,
                        FailureReason =
                            $"Local runtime queue dispatch returned LocalRunId='{localRunId}', but GetRunStatusAsync could not find it immediately on runtime instance '{RuntimeInstanceId}'.",
                        Metadata = CreateDebugMetadata(
                            request,
                            result,
                            RuntimeInstanceId,
                            localRunId,
                            executionId,
                            visibilityCheck)
                    };
                }

                var finalCompletedAtUtc = DateTimeOffset.UtcNow;

                return new AiSharedRuntimeInstanceDispatchResult
                {
                    Success = true,
                    RuntimeInstanceId = RuntimeInstanceId,
                    SharedRunId = request.SharedRun.SharedRunId,
                    LocalRunId = localRunId,
                    ExecutionId = executionId,
                    ClaimToken = request.ClaimToken,
                    Message = result.Message ?? "Shared run dispatched to local runtime instance.",
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = finalCompletedAtUtc,
                    DurationMs = (long)(finalCompletedAtUtc - startedAtUtc).TotalMilliseconds,
                    Metadata = CreateDebugMetadata(
                        request,
                        result,
                        RuntimeInstanceId,
                        localRunId,
                        executionId,
                        visibilityCheck)
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;

                return new AiSharedRuntimeInstanceDispatchResult
                {
                    Success = false,
                    RuntimeInstanceId = RuntimeInstanceId,
                    SharedRunId = request.SharedRun.SharedRunId,
                    ClaimToken = request.ClaimToken,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds,
                    FailureReason = exception.Message,
                    Metadata = new Dictionary<string, string>
                    {
                        ["runtime.instance.id"] = RuntimeInstanceId,
                        ["shared.run.id"] = request.SharedRun.SharedRunId,
                        ["exception.type"] = exception.GetType().FullName ?? exception.GetType().Name
                    }
                };
            }
        }

        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? requestMetadata,
            IReadOnlyDictionary<string, string>? sharedRunMetadata,
            string sharedRunId,
            string runtimeInstanceId,
            string? claimToken)
        {
            var metadata = new Dictionary<string, string>(
                StringComparer.Ordinal);

            if (sharedRunMetadata is not null)
            {
                foreach (var item in sharedRunMetadata)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            if (requestMetadata is not null)
            {
                foreach (var item in requestMetadata)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            metadata["shared.run.id"] = sharedRunId;
            metadata["runtime.instance.id"] = runtimeInstanceId;

            if (!string.IsNullOrWhiteSpace(claimToken))
            {
                metadata["claim.token"] = claimToken;
            }

            return metadata;
        }

        private static IReadOnlyDictionary<string, string> CreateDebugMetadata(
            AiSharedRuntimeInstanceDispatchRequest request,
            AiRuntimeQueueControlPlaneResult result,
            string runtimeInstanceId,
            string? localRunId,
            string? executionId,
            AiRuntimeQueueControlPlaneResult? visibilityCheck = null)
        {
            var metadata = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["runtime.instance.id"] = runtimeInstanceId,
                ["shared.run.id"] = request.SharedRun.SharedRunId,
                ["local.run.id"] = localRunId ?? string.Empty,
                ["execution.id"] = executionId ?? string.Empty,
                ["result.run.id"] = result.RunId ?? string.Empty,
                ["result.handle.run.id"] = result.RunHandle?.RunId ?? string.Empty,
                ["result.state.run.id"] = result.RunState?.RunId ?? string.Empty,
                ["result.execution.id"] = result.ExecutionId ?? string.Empty,
                ["result.state.execution.id"] = result.RunState?.ExecutionId ?? string.Empty,
                ["result.success"] = result.Success.ToString(),
                ["result.message"] = result.Message ?? string.Empty,
                ["result.failure"] = result.FailureReason ?? string.Empty
            };

            if (visibilityCheck is not null)
            {
                metadata["visibility.success"] = visibilityCheck.Success.ToString();
                metadata["visibility.message"] = visibilityCheck.Message ?? string.Empty;
                metadata["visibility.failure"] = visibilityCheck.FailureReason ?? string.Empty;
                metadata["visibility.run.id"] = visibilityCheck.RunId ?? string.Empty;
                metadata["visibility.state.run.id"] = visibilityCheck.RunState?.RunId ?? string.Empty;
                metadata["visibility.execution.id"] = visibilityCheck.ExecutionId ?? string.Empty;
                metadata["visibility.state.execution.id"] = visibilityCheck.RunState?.ExecutionId ?? string.Empty;
            }

            return metadata;
        }
    }
}