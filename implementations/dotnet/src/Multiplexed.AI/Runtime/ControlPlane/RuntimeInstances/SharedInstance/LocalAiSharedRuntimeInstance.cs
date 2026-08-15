using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
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
    ///
    /// Tenant context propagation:
    /// - The shared run record is the durable source of truth for the execution
    ///   context snapshot.
    /// - The snapshot is copied into the local runtime run request before the run
    ///   is enqueued locally so the background runtime controller can restore the
    ///   active RBAC execution context before creating the execution.
    /// </remarks>
    public sealed class LocalAiSharedRuntimeInstance : IAiSharedRuntimeInstance
    {
        private readonly IAiRuntimeQueueControlPlane _runtimeQueue;

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

        /// <summary>
        /// Gets the runtime queue control-plane used by this local runtime instance.
        /// </summary>
        public IAiRuntimeQueueControlPlane QueueControlPlane { get; }

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
                var runRequest = AttachExecutionContextSnapshot(
                    request.RunRequest,
                    request.SharedRun.ExecutionContextSnapshot);

                Console.WriteLine(
                    $"[LOCAL SHARED DISPATCH] BEFORE ENQUEUE RuntimeInstanceId='{RuntimeInstanceId}', SharedRunId='{request.SharedRun.SharedRunId}', ClaimToken='{request.ClaimToken}'.");

                var result = await _runtimeQueue
                    .EnqueueRunAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                            RunRequest = runRequest,
                            CorrelationId = request.CorrelationId ?? request.SharedRun.CorrelationId,
                            RequestedBy = request.RequestedBy,
                            Source = request.Source,
                            Reason = request.Reason,
                            Metadata = MergeMetadata(
                                request.Metadata,
                                request.SharedRun.Metadata,
                                request.SharedRun.SharedRunId,
                                RuntimeInstanceId,
                                request.ClaimToken,
                                runRequest.ExecutionContextSnapshot)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine(
                     $"[LOCAL SHARED DISPATCH] AFTER ENQUEUE RuntimeInstanceId='{RuntimeInstanceId}', SharedRunId='{request.SharedRun.SharedRunId}', Success='{result.Success}', RunId='{result.RunId}', HandleRunId='{result.RunHandle?.RunId}', StateRunId='{result.RunState?.RunId}', ExecutionId='{result.ExecutionId}', StateExecutionId='{result.RunState?.ExecutionId}', Failure='{result.FailureReason}', Message='{result.Message}'.");

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

                var acceptedRuntimeInstanceId =
                    string.IsNullOrWhiteSpace(result.RuntimeInstanceId)
                        ? RuntimeInstanceId
                        : result.RuntimeInstanceId;

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

                AiRuntimeQueueControlPlaneResult? visibilityCheck =
                    null;

                string? visibilityWarning =
                    null;

                try
                {
                    visibilityCheck =
                        await _runtimeQueue
                            .GetRunStatusAsync(
                                new AiRuntimeQueueControlPlaneRequest
                                {
                                    Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                                    RunId = localRunId,
                                    CorrelationId = request.CorrelationId ?? request.SharedRun.CorrelationId,
                                    RequestedBy = request.RequestedBy,
                                    Source = "local-shared-runtime-instance-visibility-check",
                                    Reason = "Diagnose immediate local run visibility after an accepted enqueue.",
                                    Metadata = new Dictionary<string, string>
                                    {
                                        ["runtime.instance.id"] = RuntimeInstanceId,
                                        ["shared.run.id"] = request.SharedRun.SharedRunId,
                                        ["local.run.id"] = localRunId,
                                        [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                                        [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                                        ["context.key"] = request.SharedRun.ExecutionContextSnapshot.ContextKey
                                    }
                                },
                                CancellationToken.None)
                            .ConfigureAwait(false);

                    if (visibilityCheck.RunState is null)
                    {
                        visibilityWarning =
                            $"Accepted LocalRunId='{localRunId}' was not immediately visible on runtime instance '{RuntimeInstanceId}'.";
                    }
                }
                catch (Exception exception)
                {
                    visibilityWarning =
                        $"Immediate visibility check failed after accepted enqueue. ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.";
                }

                if (!string.IsNullOrWhiteSpace(visibilityWarning))
                {
                    Console.WriteLine(
                        $"[LOCAL SHARED DISPATCH] ACCEPTED VISIBILITY WARNING RuntimeInstanceId='{RuntimeInstanceId}', SharedRunId='{request.SharedRun.SharedRunId}', LocalRunId='{localRunId}', ExecutionId='{executionId}', Warning='{visibilityWarning}'.");
                }

                var finalCompletedAtUtc = DateTimeOffset.UtcNow;

                return new AiSharedRuntimeInstanceDispatchResult
                {
                    Success = true,
                    RuntimeInstanceId = acceptedRuntimeInstanceId,
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
                        acceptedRuntimeInstanceId,
                        localRunId,
                        executionId,
                        visibilityCheck,
                        visibilityWarning)
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
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                        ["context.key"] = request.SharedRun.ExecutionContextSnapshot.ContextKey,
                        ["exception.type"] = exception.GetType().FullName ?? exception.GetType().Name,
                        ["exception.message"] = exception.Message,
                        ["exception.stack"] = exception.StackTrace ?? string.Empty
                    }
                };
            }
        }

        private static AiRuntimePipelineRunRequest AttachExecutionContextSnapshot(
            AiRuntimePipelineRunRequest request,
            ExecutionContextSnapshot executionContextSnapshot)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(executionContextSnapshot);

            return new AiRuntimePipelineRunRequest
            {
                PipelineName = request.PipelineName,
                RequestedExecutionId = request.RequestedExecutionId,
                ExternalWaitContinuation = request.ExternalWaitContinuation,
                PipelineDefinitionSnapshot = request.PipelineDefinitionSnapshot,
                ExecutionContextSnapshot = executionContextSnapshot,
                PipelineJson = request.PipelineJson,
                PipelineJsonFilePath = request.PipelineJsonFilePath,
                PipelineDefinition = request.PipelineDefinition,
                Input = request.Input
            };
        }

        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? requestMetadata,
            IReadOnlyDictionary<string, string>? sharedRunMetadata,
            string sharedRunId,
            string runtimeInstanceId,
            string? claimToken,
            ExecutionContextSnapshot? executionContextSnapshot)
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

            if (executionContextSnapshot is not null)
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = executionContextSnapshot.TenantId;
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = executionContextSnapshot.TenantGroupId;
                metadata["project"] = executionContextSnapshot.Project;
                metadata["user.id"] = executionContextSnapshot.UserId;
                metadata["context.key"] = executionContextSnapshot.ContextKey;
            }

            return metadata;
        }

        private static IReadOnlyDictionary<string, string> CreateDebugMetadata(
            AiSharedRuntimeInstanceDispatchRequest request,
            AiRuntimeQueueControlPlaneResult result,
            string runtimeInstanceId,
            string? localRunId,
            string? executionId,
            AiRuntimeQueueControlPlaneResult? visibilityCheck = null,
            string? visibilityWarning = null)
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
                ["result.failure"] = result.FailureReason ?? string.Empty,
                [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                ["project"] = request.SharedRun.ExecutionContextSnapshot.Project,
                ["user.id"] = request.SharedRun.ExecutionContextSnapshot.UserId,
                ["context.key"] = request.SharedRun.ExecutionContextSnapshot.ContextKey,
                ["run.request.has.snapshot"] = (request.RunRequest.ExecutionContextSnapshot is not null).ToString()
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

            metadata["visibility.warning"] =
                (!string.IsNullOrWhiteSpace(visibilityWarning)).ToString();

            metadata["visibility.warning.reason"] =
                visibilityWarning ??
                string.Empty;

            return metadata;
        }
    }
}
