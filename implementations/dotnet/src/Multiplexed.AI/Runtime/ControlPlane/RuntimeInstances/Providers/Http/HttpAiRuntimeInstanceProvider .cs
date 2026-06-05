using System.Net.Http.Json;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// HTTP based runtime instance provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This provider communicates with a remote runtime instance through HTTP.
    /// </para>
    ///
    /// <para>
    /// It is selected when the runtime instance capacity descriptor contains:
    /// </para>
    ///
    /// <code>
    /// provider.name = http
    /// transport.endpoint = http://runtime-instance-1:8080
    /// </code>
    ///
    /// <para>
    /// This provider does not replace local runtime queues. The remote runtime instance
    /// receiving the HTTP command remains responsible for its own local queue,
    /// worker pool, and DAG execution engine.
    /// </para>
    /// </remarks>
    [AiRuntimeInstanceProvider("http")]
    public sealed class HttpAiRuntimeInstanceProvider :
        IAiRuntimeInstanceDispatchProvider,
        IAiRuntimeInstanceStatusProvider,
        IAiRuntimeInstanceControlProvider
    {
        /// <summary>
        /// The provider name used by this HTTP runtime instance provider.
        /// </summary>
        private const string ProviderName = "http";

        /// <summary>
        /// The default relative endpoint used to send runtime instance commands.
        /// </summary>
        private const string DefaultCommandEndpointPath = "/runtime-instance/commands";

        /// <summary>
        /// The HTTP client used to send runtime instance commands.
        /// </summary>
        private readonly HttpClient httpClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpAiRuntimeInstanceProvider"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        public HttpAiRuntimeInstanceProvider(
            HttpClient httpClient)
        {
            this.httpClient =
                httpClient
                ?? throw new ArgumentNullException(nameof(httpClient));
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

            var endpointResolution =
                ResolveCommandEndpoint(
                    descriptor);

            if (!endpointResolution.Success)
            {
                return CreateFailedDispatchResult(
                    request,
                    runtimeInstanceId,
                    endpointResolution.FailureReason ?? "http-endpoint-missing",
                    endpointResolution.Message ?? "HTTP runtime instance endpoint is missing.");
            }

            var commandResult =
                await SendCommandAsync(
                        endpointResolution.Endpoint!,
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
                                runtimeInstanceId,
                                endpointResolution.Endpoint!.ToString())
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
                commandResult.FailureReason ?? "http-dispatch-result-missing",
                commandResult.Message ?? "HTTP runtime instance provider did not receive a dispatch result.");
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
        /// Sends a runtime queue command through HTTP.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="commandOperation">The HTTP command operation.</param>
        /// <param name="queueOperation">The runtime queue operation.</param>
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

            var endpointResolution =
                ResolveCommandEndpoint(
                    descriptor);

            if (!endpointResolution.Success)
            {
                return CreateFailedQueueResult(
                    request,
                    queueOperation,
                    runtimeInstanceId,
                    endpointResolution.FailureReason ?? "http-endpoint-missing",
                    endpointResolution.Message ?? "HTTP runtime instance endpoint is missing.");
            }

            var commandResult =
                await SendCommandAsync(
                        endpointResolution.Endpoint!,
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
                                runtimeInstanceId,
                                endpointResolution.Endpoint!.ToString())
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
                commandResult.FailureReason ?? "http-queue-result-missing",
                commandResult.Message ?? "HTTP runtime instance provider did not receive a queue control-plane result.");
        }

        /// <summary>
        /// Sends a command request to the remote runtime instance HTTP endpoint.
        /// </summary>
        /// <param name="endpoint">The HTTP command endpoint.</param>
        /// <param name="request">The command request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The command result.</returns>
        private async Task<AiRuntimeInstanceCommandResult> SendCommandAsync(
            Uri endpoint,
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            try
            {
                using var response =
                    await httpClient
                        .PostAsJsonAsync(
                            endpoint,
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var completedAtUtc =
                        DateTimeOffset.UtcNow;

                    return new AiRuntimeInstanceCommandResult
                    {
                        Success = false,
                        Operation = request.Operation,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Message = $"HTTP command failed with status code {(int)response.StatusCode}.",
                        FailureReason = "http-command-failed",
                        StartedAtUtc = startedAtUtc,
                        CompletedAtUtc = completedAtUtc,
                        DurationMs = Math.Max(
                            0,
                            (long)(completedAtUtc - startedAtUtc).TotalMilliseconds),
                        Metadata = CreateHttpFailureMetadata(
                            response.StatusCode.ToString())
                    };
                }

                var result =
                    await response.Content
                        .ReadFromJsonAsync<AiRuntimeInstanceCommandResult>(
                            cancellationToken)
                        .ConfigureAwait(false);

                if (result is not null)
                {
                    return result;
                }

                var completedAtUtcForMissingBody =
                    DateTimeOffset.UtcNow;

                return new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = request.Operation,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = "HTTP command response body was empty.",
                    FailureReason = "http-command-empty-response",
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtcForMissingBody,
                    DurationMs = Math.Max(
                        0,
                        (long)(completedAtUtcForMissingBody - startedAtUtc).TotalMilliseconds)
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var completedAtUtc =
                    DateTimeOffset.UtcNow;

                return new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = request.Operation,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = exception.Message,
                    FailureReason = "http-command-exception",
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = Math.Max(
                        0,
                        (long)(completedAtUtc - startedAtUtc).TotalMilliseconds),
                    Metadata = new Dictionary<string, string>
                    {
                        ["exception.type"] =
                            exception.GetType().FullName ??
                            exception.GetType().Name
                    }
                };
            }
        }

        /// <summary>
        /// Resolves the HTTP command endpoint from the runtime instance descriptor.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <returns>The endpoint resolution.</returns>
        private static HttpCommandEndpointResolution ResolveCommandEndpoint(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            if (descriptor.Metadata is null ||
                !descriptor.Metadata.TryGetValue(
                    AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint,
                    out var endpoint) ||
                string.IsNullOrWhiteSpace(endpoint))
            {
                return HttpCommandEndpointResolution.Failed(
                    "http-endpoint-missing",
                    $"Runtime instance descriptor '{descriptor.RuntimeInstanceId}' does not define '{AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint}'.");
            }

            var endpointText =
                endpoint.Trim();

            if (!Uri.TryCreate(
                    endpointText,
                    UriKind.Absolute,
                    out var baseEndpoint))
            {
                return HttpCommandEndpointResolution.Failed(
                    "http-endpoint-invalid",
                    $"Runtime instance HTTP endpoint '{endpointText}' is not a valid absolute URI.");
            }

            var commandEndpoint =
                new Uri(
                    baseEndpoint.ToString().TrimEnd('/') + DefaultCommandEndpointPath);

            return HttpCommandEndpointResolution.Succeeded(
                commandEndpoint);
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
        /// <param name="endpoint">The HTTP endpoint.</param>
        /// <returns>The command metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateCommandMetadata(
            IReadOnlyDictionary<string, string>? metadata,
            string runtimeInstanceId,
            string endpoint)
        {
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName,
                [AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] =
                    AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                [AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = runtimeInstanceId,
                [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = endpoint
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
        /// Creates HTTP failure metadata.
        /// </summary>
        /// <param name="statusCode">The HTTP status code.</param>
        /// <returns>The HTTP failure metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateHttpFailureMetadata(
            string statusCode)
        {
            return new Dictionary<string, string>
            {
                ["http.status_code"] = statusCode
            };
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
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName
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