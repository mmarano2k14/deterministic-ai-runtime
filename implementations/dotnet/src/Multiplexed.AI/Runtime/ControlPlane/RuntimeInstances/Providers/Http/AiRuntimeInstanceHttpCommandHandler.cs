using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Default HTTP runtime instance command handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This handler runs on the target runtime instance side.
    /// </para>
    ///
    /// <para>
    /// It receives HTTP command requests and routes them into the local runtime instance
    /// queue/control-plane.
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
        private readonly IAiRuntimeQueueControlPlane runtimeQueueControlPlane;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceHttpCommandHandler"/> class.
        /// </summary>
        /// <param name="sharedRuntimeInstance">The local shared runtime instance.</param>
        /// <param name="runtimeQueueControlPlane">The local runtime queue control-plane.</param>
        public AiRuntimeInstanceHttpCommandHandler(
            IAiSharedRuntimeInstance sharedRuntimeInstance,
            IAiRuntimeQueueControlPlane runtimeQueueControlPlane)
        {
            this.sharedRuntimeInstance =
                sharedRuntimeInstance
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstance));

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
                                operation => runtimeQueueControlPlane.GetRunStatusAsync(
                                    operation,
                                    cancellationToken))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.GetQueueStatus =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                operation => runtimeQueueControlPlane.GetQueueStatusAsync(
                                    operation,
                                    cancellationToken))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.PauseQueue =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                operation => runtimeQueueControlPlane.PauseQueueAsync(
                                    operation,
                                    cancellationToken))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.ResumeQueue =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                operation => runtimeQueueControlPlane.ResumeQueueAsync(
                                    operation,
                                    cancellationToken))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.CancelRun =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                operation => runtimeQueueControlPlane.CancelRunAsync(
                                    operation,
                                    cancellationToken))
                            .ConfigureAwait(false),

                    AiRuntimeInstanceCommandOperation.CancelQueuedRun =>
                        await HandleQueueCommandAsync(
                                request,
                                startedAtUtc,
                                operation => runtimeQueueControlPlane.CancelQueuedRunAsync(
                                    operation,
                                    cancellationToken))
                            .ConfigureAwait(false),

                    _ => CreateFailedResult(
                        request,
                        startedAtUtc,
                        "unsupported-command-operation",
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

            var dispatchResult =
                await sharedRuntimeInstance
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
                RuntimeInstanceId = request.RuntimeInstanceId,
                DispatchResult = dispatchResult,
                Message = dispatchResult.Message,
                FailureReason = dispatchResult.FailureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs = GetDurationMs(startedAtUtc, completedAtUtc),
                Metadata = MergeMetadata(
                    request.Metadata,
                    dispatchResult.Metadata)
            };
        }


        /// <summary>
        /// Handles a runtime queue command.
        /// </summary>
        /// <param name="request">The command request.</param>
        /// <param name="startedAtUtc">The command start time.</param>
        /// <param name="handler">The runtime queue operation handler.</param>
        /// <returns>The command result.</returns>
        private async Task<AiRuntimeInstanceCommandResult> HandleQueueCommandAsync(
            AiRuntimeInstanceCommandRequest request,
            DateTimeOffset startedAtUtc,
            Func<AiRuntimeQueueControlPlaneRequest, Task<AiRuntimeQueueControlPlaneResult>> handler)
        {
            if (request.QueueRequest is null)
            {
                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    "queue-request-missing",
                    "Queue command request is missing QueueRequest.");
            }

            var queueResult =
                await handler(request.QueueRequest)
                    .ConfigureAwait(false);

            var completedAtUtc =
                DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceCommandResult
            {
                Success = queueResult.Success,
                Operation = request.Operation,
                RuntimeInstanceId = request.RuntimeInstanceId,
                QueueResult = queueResult,
                Message = queueResult.Message,
                FailureReason = queueResult.FailureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs = GetDurationMs(startedAtUtc, completedAtUtc),
                Metadata = CopyMetadata(request.Metadata)
            };
        }

        /// <summary>
        /// Copies command metadata into a new dictionary.
        /// </summary>
        /// <param name="metadata">The metadata to copy.</param>
        /// <returns>The copied metadata.</returns>
        private static IReadOnlyDictionary<string, string> CopyMetadata(
            IReadOnlyDictionary<string, string>? metadata)
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
                    request.Metadata,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["runtime.http.command.handler.failure"] = "true"
                };

            if (exception is not null)
            {
                metadata["exception.type"] =
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
        /// <returns>The merged metadata.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? commandMetadata,
            IReadOnlyDictionary<string, string>? resultMetadata)
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