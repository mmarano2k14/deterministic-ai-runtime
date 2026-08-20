using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Default HTTP runtime instance command handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This handler runs on the target runtime instance host side.
    /// </para>
    ///
    /// <para>
    /// It receives HTTP command requests and routes them into the local runtime
    /// instance queue/control-plane.
    /// </para>
    ///
    /// <para>
    /// In simple runtime-instance mode, the HTTP host usually owns a single runtime
    /// instance and commands can be handled by the fallback local shared runtime instance
    /// and queue control-plane.
    /// </para>
    ///
    /// <para>
    /// In pooled runtime-instance mode, the HTTP host owns several in-process runtime
    /// instances. In that case, commands must be routed to the child runtime instance
    /// identified by <see cref="AiRuntimeInstanceCommandRequest.RuntimeInstanceId"/>
    /// or by the nested dispatch request target.
    /// </para>
    ///
    /// <para>
    /// Dispatch commands are routed through <see cref="IAiSharedRuntimeInstance"/>.
    /// Queue status and control commands are routed through <see cref="IAiRuntimeQueueControlPlane"/>.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeInstanceHttpCommandHandler : IAiRuntimeInstanceHttpCommandHandler
    {
        private readonly IAiSharedRuntimeInstance sharedRuntimeInstance;
        private readonly IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry;
        private readonly IAiRuntimeQueueControlPlane runtimeQueueControlPlane;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceHttpCommandHandler"/> class.
        /// </summary>
        /// <param name="sharedRuntimeInstance">The fallback local shared runtime instance.</param>
        /// <param name="sharedRuntimeInstanceRegistry">The local shared runtime instance registry.</param>
        /// <param name="runtimeQueueControlPlane">The fallback local runtime queue control-plane.</param>
        public AiRuntimeInstanceHttpCommandHandler(
            IAiSharedRuntimeInstance sharedRuntimeInstance,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            IAiRuntimeQueueControlPlane runtimeQueueControlPlane)
        {
            this.sharedRuntimeInstance =
                sharedRuntimeInstance
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstance));

            this.sharedRuntimeInstanceRegistry =
                sharedRuntimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstanceRegistry));

            this.runtimeQueueControlPlane =
                runtimeQueueControlPlane
                ?? throw new ArgumentNullException(nameof(runtimeQueueControlPlane));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceCommandResult> HandleAsync(
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            try
            {
                return request.Operation switch
                {
                    AiRuntimeInstanceCommandOperation.DispatchRun =>
                        await HandleDispatchRunAsync(
                                request,
                                startedAtUtc,
                                cancellationToken)
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.GetRunStatus =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                cancellationToken,
                                static (queue, queueRequest, token) =>
                                    queue.GetRunStatusAsync(
                                        queueRequest,
                                        token))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.GetQueueStatus =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                cancellationToken,
                                static (queue, queueRequest, token) =>
                                    queue.GetQueueStatusAsync(
                                        queueRequest,
                                        token))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.PauseQueue =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                cancellationToken,
                                static (queue, queueRequest, token) =>
                                    queue.PauseQueueAsync(
                                        queueRequest,
                                        token))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.ResumeQueue =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                cancellationToken,
                                static (queue, queueRequest, token) =>
                                    queue.ResumeQueueAsync(
                                        queueRequest,
                                        token))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.CancelRun =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                cancellationToken,
                                static (queue, queueRequest, token) =>
                                    queue.CancelRunAsync(
                                        queueRequest,
                                        token))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.CancelQueuedRun =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                cancellationToken,
                                static (queue, queueRequest, token) =>
                                    queue.CancelQueuedRunAsync(
                                        queueRequest,
                                        token))
                            .ConfigureAwait(false),

                    _ => CreateFailedResult(
                        request,
                        startedAtUtc,
                        AiRuntimeInstanceCommandFailureReasons.UnsupportedCommandOperation,
                        $"Runtime instance command operation '{request.Operation}' is not supported.")
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    "http-command-handler-exception",
                    exception.Message,
                    exception);
            }
        }

        /// <summary>
        /// Handles a dispatch run command.
        /// </summary>
        /// <param name="request">The command request.</param>
        /// <param name="startedAtUtc">The command start time.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The command result.</returns>
        private async Task<AiRuntimeInstanceCommandResult> HandleDispatchRunAsync(
            AiRuntimeInstanceCommandRequest request,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (request.DispatchRequest is null)
            {
                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    "dispatch-request-missing",
                    "Dispatch command request is missing DispatchRequest.");
            }

            var targetRuntimeInstanceId =
                ResolveTargetRuntimeInstanceId(
                    request);

            var targetSharedRuntimeInstance =
                await ResolveSharedRuntimeInstanceAsync(
                        targetRuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (targetSharedRuntimeInstance is null)
            {
                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    AiRuntimeInstanceFailureReasons.RuntimeInstanceNotFound,
                    $"Runtime instance '{targetRuntimeInstanceId}' was not found in the local shared runtime instance registry.");
            }

            var dispatchResult =
                await targetSharedRuntimeInstance
                    .DispatchAsync(
                        request.DispatchRequest,
                        cancellationToken)
                    .ConfigureAwait(false);

            var completedAtUtc =
                DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceCommandResult
            {
                Success = dispatchResult.Success,
                Operation = request.Operation,
                RuntimeInstanceId = targetRuntimeInstanceId,
                DispatchResult = dispatchResult,
                Message = dispatchResult.Message,
                FailureReason = dispatchResult.FailureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs = GetDurationMs(startedAtUtc, completedAtUtc),
                Metadata = MergeMetadata(
                    request.Metadata,
                    dispatchResult.Metadata,
                    targetRuntimeInstanceId)
            };
        }

        /// <summary>
        /// Handles a runtime queue command.
        /// </summary>
        /// <param name="request">The command request.</param>
        /// <param name="startedAtUtc">The command start time.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="handler">The runtime queue operation handler.</param>
        /// <returns>The command result.</returns>
        private async Task<AiRuntimeInstanceCommandResult> HandleQueueCommandAsync(
            AiRuntimeInstanceCommandRequest request,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken,
            Func<
                IAiRuntimeQueueControlPlane,
                AiRuntimeQueueControlPlaneRequest,
                CancellationToken,
                Task<AiRuntimeQueueControlPlaneResult>> handler)
        {
            if (request.QueueRequest is null)
            {
                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    AiRuntimeInstanceCommandFailureReasons.QueueRequestMissing,
                    "Queue command request is missing QueueRequest.");
            }

            var targetRuntimeInstanceId =
                ResolveTargetRuntimeInstanceId(
                    request);

            var queueControlPlane =
                await ResolveRuntimeQueueControlPlaneAsync(
                        targetRuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (queueControlPlane is null)
            {
                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    AiRuntimeInstanceCommandFailureReasons.RuntimeQueueNotFound,
                    $"Runtime queue control-plane for runtime instance '{targetRuntimeInstanceId}' was not found.");
            }

            var queueResult =
                await handler(
                        queueControlPlane,
                        request.QueueRequest,
                        cancellationToken)
                    .ConfigureAwait(false);

            var completedAtUtc =
                DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceCommandResult
            {
                Success = queueResult.Success,
                Operation = request.Operation,
                RuntimeInstanceId = targetRuntimeInstanceId,
                QueueResult = queueResult,
                Message = queueResult.Message,
                FailureReason = queueResult.FailureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs = GetDurationMs(startedAtUtc, completedAtUtc),
                Metadata = CopyMetadata(
                    request.Metadata,
                    targetRuntimeInstanceId)
            };
        }

        /// <summary>
        /// Resolves the target runtime instance id for the command.
        /// </summary>
        /// <param name="request">The command request.</param>
        /// <returns>The target runtime instance id.</returns>
        private static string ResolveTargetRuntimeInstanceId(
            AiRuntimeInstanceCommandRequest request)
        {
            var targetRuntimeInstanceId =
                request.DispatchRequest?.RuntimeInstanceId ??
                request.RuntimeInstanceId;

            ArgumentException.ThrowIfNullOrWhiteSpace(
                targetRuntimeInstanceId);

            return targetRuntimeInstanceId;
        }

        /// <summary>
        /// Resolves the local shared runtime instance that owns the target runtime instance id.
        /// </summary>
        /// <remarks>
        /// The registry path is used first so a pooled HTTP runtime host can route commands
        /// to child runtime instances such as <c>runtime-http-1</c>, <c>runtime-http-2</c>,
        /// or <c>runtime-http-3</c>.
        ///
        /// The fallback shared runtime instance preserves single runtime-instance behavior.
        /// </remarks>
        /// <param name="runtimeInstanceId">The target runtime instance id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The local shared runtime instance, or <see langword="null"/> when it cannot be found.</returns>
        private async Task<IAiSharedRuntimeInstance?> ResolveSharedRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var registeredRuntimeInstance =
                await sharedRuntimeInstanceRegistry
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (registeredRuntimeInstance is not null)
            {
                return registeredRuntimeInstance;
            }

            if (string.Equals(
                    sharedRuntimeInstance.RuntimeInstanceId,
                    runtimeInstanceId,
                    StringComparison.Ordinal))
            {
                return sharedRuntimeInstance;
            }

            return null;
        }

        /// <summary>
        /// Resolves the runtime queue control-plane for the target runtime instance id.
        /// </summary>
        /// <remarks>
        /// When the target runtime instance is a child of a local runtime instance pool,
        /// the queue control-plane must come from that child instance.
        ///
        /// If no child instance is found and the target matches the fallback runtime instance,
        /// the fallback queue control-plane is returned to preserve single runtime-instance behavior.
        /// </remarks>
        /// <param name="runtimeInstanceId">The target runtime instance id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime queue control-plane, or <see langword="null"/> when it cannot be resolved.</returns>
        private async Task<IAiRuntimeQueueControlPlane?> ResolveRuntimeQueueControlPlaneAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var targetSharedRuntimeInstance =
                await ResolveSharedRuntimeInstanceAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (targetSharedRuntimeInstance is LocalAiSharedRuntimeInstance localRuntimeInstance)
            {
                return localRuntimeInstance.QueueControlPlane;
            }

            if (string.Equals(
                    sharedRuntimeInstance.RuntimeInstanceId,
                    runtimeInstanceId,
                    StringComparison.Ordinal))
            {
                return runtimeQueueControlPlane;
            }

            return null;
        }

        /// <summary>
        /// Copies command metadata into a new dictionary.
        /// </summary>
        /// <param name="metadata">The metadata to copy.</param>
        /// <param name="targetRuntimeInstanceId">The resolved target runtime instance id.</param>
        /// <returns>The copied metadata.</returns>
        private static IReadOnlyDictionary<string, string> CopyMetadata(
            IReadOnlyDictionary<string, string>? metadata,
            string targetRuntimeInstanceId)
        {
            var result =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            if (metadata is not null)
            {
                foreach (var item in metadata)
                {
                    result[item.Key] = item.Value;
                }
            }

            result[AiRuntimeInstanceCommandTransportMetadataKeys.TargetRuntimeInstanceId] =
                targetRuntimeInstanceId;

            return result;
        }

        /// <summary>
        /// Creates a failed command result.
        /// </summary>
        /// <param name="request">The command request.</param>
        /// <param name="startedAtUtc">The command start time.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="message">The failure message.</param>
        /// <param name="exception">The optional exception.</param>
        /// <returns>The failed command result.</returns>
        private static AiRuntimeInstanceCommandResult CreateFailedResult(
            AiRuntimeInstanceCommandRequest request,
            DateTimeOffset startedAtUtc,
            string failureReason,
            string message,
            Exception? exception = null)
        {
            var completedAtUtc =
                DateTimeOffset.UtcNow;

            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["runtime.http.command.handler.failure"] = "true"
                };

            if (request.Metadata is not null)
            {
                foreach (var item in request.Metadata)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            if (exception is not null)
            {
                metadata[AiExceptionMetadataKeys.ExceptionType] =
                    exception.GetType().FullName ??
                    exception.GetType().Name;
            }

            return new AiRuntimeInstanceCommandResult
            {
                Success = false,
                Operation = request.Operation,
                RuntimeInstanceId = request.RuntimeInstanceId,
                Message = message,
                FailureReason = failureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs = GetDurationMs(startedAtUtc, completedAtUtc),
                Metadata = metadata
            };
        }

        /// <summary>
        /// Merges command metadata with operation result metadata.
        /// </summary>
        /// <param name="commandMetadata">The command metadata.</param>
        /// <param name="resultMetadata">The operation result metadata.</param>
        /// <param name="targetRuntimeInstanceId">The resolved target runtime instance id.</param>
        /// <returns>The merged metadata.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? commandMetadata,
            IReadOnlyDictionary<string, string>? resultMetadata,
            string targetRuntimeInstanceId)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            if (commandMetadata is not null)
            {
                foreach (var item in commandMetadata)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            if (resultMetadata is not null)
            {
                foreach (var item in resultMetadata)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TargetRuntimeInstanceId] =
                targetRuntimeInstanceId;

            return metadata;
        }

        /// <summary>
        /// Gets command duration in milliseconds.
        /// </summary>
        /// <param name="startedAtUtc">The start time.</param>
        /// <param name="completedAtUtc">The completion time.</param>
        /// <returns>The duration in milliseconds.</returns>
        private static long GetDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return Math.Max(
                0,
                (long)(completedAtUtc - startedAtUtc).TotalMilliseconds);
        }
    }
}