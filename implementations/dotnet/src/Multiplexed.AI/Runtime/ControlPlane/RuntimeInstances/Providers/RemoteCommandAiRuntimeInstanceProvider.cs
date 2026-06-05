using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Remote command based runtime instance provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This provider exposes dispatch, status, and control capabilities by delegating
    /// runtime instance commands to an <see cref="IAiRuntimeInstanceCommandTransport"/>.
    /// </para>
    ///
    /// <para>
    /// This provider does not know whether the underlying transport is Redis, HTTP,
    /// gRPC, Kubernetes, or another future command channel.
    /// </para>
    ///
    /// <para>
    /// This provider does not replace local runtime queues. It sends commands to the
    /// runtime instance that owns the local queue.
    /// </para>
    /// </remarks>
    [AiRuntimeInstanceProvider("remote-command")]
    public sealed class RemoteCommandAiRuntimeInstanceProvider :
        IAiRuntimeInstanceDispatchProvider,
        IAiRuntimeInstanceStatusProvider,
        IAiRuntimeInstanceControlProvider
    {
        /// <summary>
        /// The provider name used by this remote command provider.
        /// </summary>
        private const string ProviderName = "remote-command";

        private readonly IAiRuntimeInstanceCommandTransport transport;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteCommandAiRuntimeInstanceProvider"/> class.
        /// </summary>
        /// <param name="transport">The runtime instance command transport.</param>
        public RemoteCommandAiRuntimeInstanceProvider(
            IAiRuntimeInstanceCommandTransport transport)
        {
            this.transport =
                transport
                ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <inheritdoc />
        public bool CanHandle(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            if (descriptor.Metadata is not null &&
                descriptor.Metadata.TryGetValue(
                    AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                    out var providerName) &&
                !string.IsNullOrWhiteSpace(providerName))
            {
                return string.Equals(
                    providerName.Trim(),
                    ProviderName,
                    StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <inheritdoc />
        public async Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiSharedRuntimeInstanceDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(request);

            var runtimeInstanceId =
                ResolveRuntimeInstanceId(
                    descriptor,
                    request.RuntimeInstanceId);

            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return CreateFailedDispatchResult(
                    request,
                    string.Empty,
                    "runtime-instance-id-missing",
                    "Runtime instance id is missing.");
            }

            var commandResult =
                await transport
                    .SendAsync(
                        new AiRuntimeInstanceCommandRequest
                        {
                            Operation = AiRuntimeInstanceCommandOperation.DispatchRun,
                            RuntimeInstanceId = runtimeInstanceId,
                            Descriptor = descriptor,
                            DispatchRequest = request,
                            CorrelationId = request.CorrelationId,
                            RequestedBy = request.RequestedBy,
                            Source = request.Source,
                            Reason = request.Reason,
                            Metadata = CreateCommandMetadata(
                                request.Metadata,
                                runtimeInstanceId)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (commandResult.DispatchResult is not null)
            {
                return commandResult.DispatchResult;
            }

            return CreateFailedDispatchResult(
                request,
                runtimeInstanceId,
                commandResult.FailureReason ?? "remote-command-dispatch-result-missing",
                commandResult.Message ?? "Remote command transport did not return a dispatch result.");
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return SendQueueCommandAsync(
                descriptor,
                request,
                AiRuntimeInstanceCommandOperation.GetRunStatus,
                AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return SendQueueCommandAsync(
                descriptor,
                request,
                AiRuntimeInstanceCommandOperation.GetQueueStatus,
                AiRuntimeQueueControlPlaneOperation.GetQueueStatus,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return SendQueueCommandAsync(
                descriptor,
                request,
                AiRuntimeInstanceCommandOperation.PauseQueue,
                AiRuntimeQueueControlPlaneOperation.PauseQueue,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return SendQueueCommandAsync(
                descriptor,
                request,
                AiRuntimeInstanceCommandOperation.ResumeQueue,
                AiRuntimeQueueControlPlaneOperation.ResumeQueue,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return SendQueueCommandAsync(
                descriptor,
                request,
                AiRuntimeInstanceCommandOperation.CancelRun,
                AiRuntimeQueueControlPlaneOperation.CancelRun,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return SendQueueCommandAsync(
                descriptor,
                request,
                AiRuntimeInstanceCommandOperation.CancelQueuedRun,
                AiRuntimeQueueControlPlaneOperation.CancelQueuedRun,
                cancellationToken);
        }

        /// <summary>
        /// Sends a runtime queue command through the command transport.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="commandOperation">The command transport operation.</param>
        /// <param name="queueOperation">The runtime queue control-plane operation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        private async Task<AiRuntimeQueueControlPlaneResult> SendQueueCommandAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            AiRuntimeInstanceCommandOperation commandOperation,
            AiRuntimeQueueControlPlaneOperation queueOperation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(request);

            var runtimeInstanceId =
                ResolveRuntimeInstanceId(
                    descriptor,
                    request.RuntimeInstanceId);

            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return CreateFailedQueueResult(
                    request,
                    queueOperation,
                    string.Empty,
                    "runtime-instance-id-missing",
                    "Runtime instance id is missing.");
            }

            var commandResult =
                await transport
                    .SendAsync(
                        new AiRuntimeInstanceCommandRequest
                        {
                            Operation = commandOperation,
                            RuntimeInstanceId = runtimeInstanceId,
                            Descriptor = descriptor,
                            QueueRequest = request,
                            CorrelationId = request.CorrelationId,
                            RequestedBy = request.RequestedBy,
                            Source = request.Source,
                            Reason = request.Reason,
                            Metadata = CreateCommandMetadata(
                                request.Metadata,
                                runtimeInstanceId)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (commandResult.QueueResult is not null)
            {
                return commandResult.QueueResult;
            }

            return CreateFailedQueueResult(
                request,
                queueOperation,
                runtimeInstanceId,
                commandResult.FailureReason ?? "remote-command-queue-result-missing",
                commandResult.Message ?? "Remote command transport did not return a queue control-plane result.");
        }

        /// <summary>
        /// Resolves the runtime instance identifier.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <param name="fallbackRuntimeInstanceId">The fallback runtime instance identifier.</param>
        /// <returns>The resolved runtime instance identifier.</returns>
        private static string ResolveRuntimeInstanceId(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            string? fallbackRuntimeInstanceId)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            return string.IsNullOrWhiteSpace(descriptor.RuntimeInstanceId)
                ? fallbackRuntimeInstanceId ?? string.Empty
                : descriptor.RuntimeInstanceId;
        }

        /// <summary>
        /// Creates command metadata.
        /// </summary>
        /// <param name="metadata">The source metadata.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The command metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateCommandMetadata(
            IReadOnlyDictionary<string, string>? metadata,
            string runtimeInstanceId)
        {
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["provider.name"] = ProviderName,
                ["runtime.instance.id"] = runtimeInstanceId,
                ["runtime.command.remote"] = "true"
            };

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
        /// Creates a failed dispatch result.
        /// </summary>
        /// <param name="request">The dispatch request.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="message">The message.</param>
        /// <returns>The failed dispatch result.</returns>
        private static AiSharedRuntimeInstanceDispatchResult CreateFailedDispatchResult(
            AiSharedRuntimeInstanceDispatchRequest request,
            string runtimeInstanceId,
            string failureReason,
            string message)
        {
            var now =
                DateTimeOffset.UtcNow;

            return new AiSharedRuntimeInstanceDispatchResult
            {
                Success = false,
                RuntimeInstanceId = runtimeInstanceId,
                SharedRunId = request.SharedRun.SharedRunId,
                ClaimToken = request.ClaimToken,
                Message = message,
                FailureReason = failureReason,
                StartedAtUtc = now,
                CompletedAtUtc = now,
                DurationMs = 0,
                Metadata = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["provider.name"] = ProviderName
                }
            };
        }

        /// <summary>
        /// Creates a failed queue result.
        /// </summary>
        /// <param name="request">The queue control-plane request.</param>
        /// <param name="operation">The queue operation.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="message">The message.</param>
        /// <returns>The failed queue result.</returns>
        private static AiRuntimeQueueControlPlaneResult CreateFailedQueueResult(
            AiRuntimeQueueControlPlaneRequest request,
            AiRuntimeQueueControlPlaneOperation operation,
            string runtimeInstanceId,
            string failureReason,
            string message)
        {
            var now =
                DateTimeOffset.UtcNow;

            return new AiRuntimeQueueControlPlaneResult
            {
                Operation = operation,
                Success = false,
                Message = message,
                RunId = request.RunId,
                CorrelationId = request.CorrelationId,
                RuntimeInstanceId = runtimeInstanceId,
                RequestedBy = request.RequestedBy,
                StartedAtUtc = now,
                CompletedAtUtc = now,
                DurationMs = 0,
                FailureReason = failureReason,
                Diagnostics = request.IncludeDiagnostics
                    ? new[]
                    {
                        message
                    }
                    : Array.Empty<string>()
            };
        }
    }
}