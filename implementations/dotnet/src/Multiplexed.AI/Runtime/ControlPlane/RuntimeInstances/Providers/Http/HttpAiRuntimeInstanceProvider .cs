using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
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
        IAiRuntimeInstanceControlProvider,
        IAiRuntimeInstanceControlPlaneContext
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
        /// Logger used for HTTP provider diagnostics.
        /// </summary>
        private readonly ILogger<HttpAiRuntimeInstanceProvider> logger;

        /// <summary>
        /// HTTP provider hardening options.
        /// </summary>
        private readonly AiHttpRuntimeInstanceProviderOptions options;

        /// <summary>
        /// In-memory circuit breaker states indexed by HTTP runtime endpoint key.
        /// </summary>
        private readonly ConcurrentDictionary<string, AiHttpRuntimeCircuitBreakerState> circuitBreakerStates =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the control-plane host identity associated with this provider instance.
        /// </summary>
        public IAiControlPlaneHostIdentity? Identity { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpAiRuntimeInstanceProvider"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="options">The HTTP provider hardening options.</param>
        public HttpAiRuntimeInstanceProvider(
            HttpClient httpClient,
            ILogger<HttpAiRuntimeInstanceProvider> logger,
            IOptions<AiHttpRuntimeInstanceProviderOptions> options)
        {
            this.httpClient =
                httpClient
                ?? throw new ArgumentNullException(nameof(httpClient));

            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));

            ArgumentNullException.ThrowIfNull(options);

            this.options =
                options.Value
                ?? throw new ArgumentException(
                    "HTTP runtime instance provider options must be provided.",
                    nameof(options));
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
                var canHandle =
                    string.Equals(
                        providerName.Trim(),
                        ProviderName,
                        StringComparison.OrdinalIgnoreCase);

                logger.LogInformation(
                    "HTTP PROVIDER CAN HANDLE CHECK RuntimeInstanceId={RuntimeInstanceId} ProviderName={ProviderName} CanHandle={CanHandle}",
                    descriptor.RuntimeInstanceId,
                    providerName,
                    canHandle);

                return canHandle;
            }

            logger.LogInformation(
                "HTTP PROVIDER CAN HANDLE CHECK RuntimeInstanceId={RuntimeInstanceId} ProviderName=(missing) CanHandle=False",
                descriptor.RuntimeInstanceId);

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

            logger.LogInformation(
                "HTTP DISPATCH START RuntimeInstanceId={RuntimeInstanceId} DescriptorRuntimeInstanceId={DescriptorRuntimeInstanceId} RequestRuntimeInstanceId={RequestRuntimeInstanceId} SharedRunId={SharedRunId} ClaimToken={ClaimToken} CorrelationId={CorrelationId}",
                runtimeInstanceId,
                descriptor.RuntimeInstanceId,
                request.RuntimeInstanceId,
                request.SharedRun.SharedRunId,
                request.ClaimToken,
                request.CorrelationId);

            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                logger.LogWarning(
                    "HTTP DISPATCH FAILED RuntimeInstanceId=(missing) SharedRunId={SharedRunId} Reason=runtime-instance-id-missing",
                    request.SharedRun.SharedRunId);

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
                logger.LogWarning(
                    "HTTP DISPATCH ENDPOINT RESOLUTION FAILED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Reason={FailureReason} Message={Message}",
                    runtimeInstanceId,
                    request.SharedRun.SharedRunId,
                    endpointResolution.FailureReason,
                    endpointResolution.Message);

                return CreateFailedDispatchResult(
                    request,
                    runtimeInstanceId,
                    endpointResolution.FailureReason ?? AiHttpRuntimeDispatchFailureReasons.EndpointMissing,
                    endpointResolution.Message ?? "HTTP runtime instance endpoint is missing.");
            }

            logger.LogInformation(
                "HTTP DISPATCH ENDPOINT RESOLVED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Endpoint={Endpoint}",
                runtimeInstanceId,
                request.SharedRun.SharedRunId,
                endpointResolution.Endpoint);

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
                logger.LogInformation(
                    "HTTP DISPATCH RESULT RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Success={Success} LocalRunId={LocalRunId} ExecutionId={ExecutionId} ClaimToken={ClaimToken} FailureReason={FailureReason}",
                    runtimeInstanceId,
                    request.SharedRun.SharedRunId,
                    commandResult.DispatchResult.Success,
                    commandResult.DispatchResult.LocalRunId,
                    commandResult.DispatchResult.ExecutionId,
                    commandResult.DispatchResult.ClaimToken,
                    commandResult.DispatchResult.FailureReason);

                return commandResult.DispatchResult;
            }

            logger.LogWarning(
                "HTTP DISPATCH RESULT MISSING RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} CommandSuccess={CommandSuccess} CommandFailureReason={FailureReason} CommandMessage={Message}",
                runtimeInstanceId,
                request.SharedRun.SharedRunId,
                commandResult.Success,
                commandResult.FailureReason,
                commandResult.Message);

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

            logger.LogInformation(
                "HTTP QUEUE COMMAND START RuntimeInstanceId={RuntimeInstanceId} DescriptorRuntimeInstanceId={DescriptorRuntimeInstanceId} RequestRuntimeInstanceId={RequestRuntimeInstanceId} CommandOperation={CommandOperation} QueueOperation={QueueOperation} RunId={RunId} CorrelationId={CorrelationId}",
                runtimeInstanceId,
                descriptor.RuntimeInstanceId,
                request.RuntimeInstanceId,
                commandOperation,
                queueOperation,
                request.RunId,
                request.CorrelationId);

            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                logger.LogWarning(
                    "HTTP QUEUE COMMAND FAILED RuntimeInstanceId=(missing) CommandOperation={CommandOperation} QueueOperation={QueueOperation} RunId={RunId} Reason=runtime-instance-id-missing",
                    commandOperation,
                    queueOperation,
                    request.RunId);

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
                logger.LogWarning(
                    "HTTP QUEUE COMMAND ENDPOINT RESOLUTION FAILED RuntimeInstanceId={RuntimeInstanceId} CommandOperation={CommandOperation} QueueOperation={QueueOperation} RunId={RunId} Reason={FailureReason} Message={Message}",
                    runtimeInstanceId,
                    commandOperation,
                    queueOperation,
                    request.RunId,
                    endpointResolution.FailureReason,
                    endpointResolution.Message);

                return CreateFailedQueueResult(
                    request,
                    queueOperation,
                    runtimeInstanceId,
                    endpointResolution.FailureReason ?? AiHttpRuntimeDispatchFailureReasons.EndpointMissing,
                    endpointResolution.Message ?? "HTTP runtime instance endpoint is missing.");
            }

            logger.LogInformation(
                "HTTP QUEUE COMMAND ENDPOINT RESOLVED RuntimeInstanceId={RuntimeInstanceId} CommandOperation={CommandOperation} QueueOperation={QueueOperation} RunId={RunId} Endpoint={Endpoint}",
                runtimeInstanceId,
                commandOperation,
                queueOperation,
                request.RunId,
                endpointResolution.Endpoint);

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
                logger.LogInformation(
                    "HTTP QUEUE COMMAND RESULT RuntimeInstanceId={RuntimeInstanceId} CommandOperation={CommandOperation} QueueOperation={QueueOperation} RunId={RunId} Success={Success} ExecutionId={ExecutionId} FailureReason={FailureReason}",
                    runtimeInstanceId,
                    commandOperation,
                    queueOperation,
                    request.RunId,
                    commandResult.QueueResult.Success,
                    commandResult.QueueResult.ExecutionId,
                    commandResult.QueueResult.FailureReason);

                return commandResult.QueueResult;
            }

            logger.LogWarning(
                "HTTP QUEUE COMMAND RESULT MISSING RuntimeInstanceId={RuntimeInstanceId} CommandOperation={CommandOperation} QueueOperation={QueueOperation} RunId={RunId} CommandSuccess={CommandSuccess} CommandFailureReason={FailureReason} CommandMessage={Message}",
                runtimeInstanceId,
                commandOperation,
                queueOperation,
                request.RunId,
                commandResult.Success,
                commandResult.FailureReason,
                commandResult.Message);

            return CreateFailedQueueResult(
                request,
                queueOperation,
                runtimeInstanceId,
                commandResult.FailureReason ?? "http-queue-result-missing",
                commandResult.Message ?? "HTTP runtime instance provider did not receive a queue control-plane result.");
        }

        /// <summary>
        /// Sends a command request to the remote runtime instance HTTP endpoint with conservative retry handling.
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

            var circuitBreakerKey =
                ResolveCircuitBreakerKey(
                    request.RuntimeInstanceId,
                    endpoint);

            if (TryCreateCircuitOpenResult(
                    circuitBreakerKey,
                    request,
                    endpoint,
                    out var circuitOpenResult))
            {
                return circuitOpenResult;
            }

            var maxRetryAttempts =
                this.options.EnableRetry
                    ? Math.Max(
                        0,
                        this.options.MaxRetryAttempts)
                    : 0;

            var totalAttempts =
                maxRetryAttempts + 1;

            AiRuntimeInstanceCommandResult? lastResult = null;

            for (var attempt = 1; attempt <= totalAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                logger.LogInformation(
                    "HTTP RUNTIME COMMAND ATTEMPT RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} SharedRunId={SharedRunId} RunId={RunId} Attempt={Attempt} TotalAttempts={TotalAttempts}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    request.DispatchRequest?.SharedRun.SharedRunId,
                    request.QueueRequest?.RunId,
                    attempt,
                    totalAttempts);

                lastResult =
                    await SendCommandOnceAsync(
                            endpoint,
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (lastResult.Success)
                {
                    RecordCircuitBreakerSuccess(
                        circuitBreakerKey,
                        request,
                        endpoint);

                    return lastResult;
                }

                if (attempt >= totalAttempts ||
                    !IsRetryableCommandFailure(
                        lastResult.FailureReason))
                {
                    RecordCircuitBreakerFailure(
                        circuitBreakerKey,
                        request,
                        endpoint,
                        lastResult.FailureReason);

                    return lastResult;
                }

                var retryDelay =
                    CalculateRetryDelay(
                        attempt);

                logger.LogWarning(
                    "HTTP RUNTIME COMMAND RETRY RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} SharedRunId={SharedRunId} RunId={RunId} Attempt={Attempt} NextAttempt={NextAttempt} TotalAttempts={TotalAttempts} FailureReason={FailureReason} RetryDelayMs={RetryDelayMs}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    request.DispatchRequest?.SharedRun.SharedRunId,
                    request.QueueRequest?.RunId,
                    attempt,
                    attempt + 1,
                    totalAttempts,
                    lastResult.FailureReason,
                    Math.Max(
                        0,
                        (long)retryDelay.TotalMilliseconds));

                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(
                            retryDelay,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var now =
                DateTimeOffset.UtcNow;

            var fallbackResult =
                lastResult ??
                new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = request.Operation,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = "HTTP command failed before a command result was produced.",
                    FailureReason = AiHttpRuntimeDispatchFailureReasons.Exception,
                    StartedAtUtc = now,
                    CompletedAtUtc = now,
                    DurationMs = 0
                };

            RecordCircuitBreakerFailure(
                circuitBreakerKey,
                request,
                endpoint,
                fallbackResult.FailureReason);

            return fallbackResult;
        }

        /// <summary>
        /// Sends one command attempt to the remote runtime instance HTTP endpoint.
        /// </summary>
        /// <param name="endpoint">The HTTP command endpoint.</param>
        /// <param name="request">The command request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The command result.</returns>
        private async Task<AiRuntimeInstanceCommandResult> SendCommandOnceAsync(
            Uri endpoint,
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var dispatchTimeout =
                this.options.DispatchTimeout;

            logger.LogInformation(
                "HTTP RUNTIME COMMAND SEND RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} SharedRunId={SharedRunId} RunId={RunId} CorrelationId={CorrelationId} RequestedBy={RequestedBy} Source={Source} DispatchTimeoutMs={DispatchTimeoutMs}",
                request.RuntimeInstanceId,
                request.Operation,
                endpoint,
                request.DispatchRequest?.SharedRun.SharedRunId,
                request.QueueRequest?.RunId,
                request.CorrelationId,
                request.RequestedBy,
                request.Source,
                Math.Max(
                    0,
                    (long)dispatchTimeout.TotalMilliseconds));

            using var timeoutCancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            if (dispatchTimeout > TimeSpan.Zero)
            {
                timeoutCancellationTokenSource.CancelAfter(
                    dispatchTimeout);
            }

            try
            {
                using var response =
                    await httpClient
                        .PostAsJsonAsync(
                            endpoint,
                            request,
                            timeoutCancellationTokenSource.Token)
                        .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var completedAtUtc =
                        DateTimeOffset.UtcNow;

                    var failureReason =
                        IsNonRetryableHttpStatusCode(
                            response.StatusCode)
                            ? AiHttpRuntimeDispatchFailureReasons.NonRetryableHttpError
                            : AiHttpRuntimeDispatchFailureReasons.HttpError;

                    logger.LogWarning(
                        "HTTP RUNTIME COMMAND RESPONSE FAILED RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} StatusCode={StatusCode} FailureReason={FailureReason} DurationMs={DurationMs}",
                        request.RuntimeInstanceId,
                        request.Operation,
                        endpoint,
                        response.StatusCode,
                        failureReason,
                        Math.Max(
                            0,
                            (long)(completedAtUtc - startedAtUtc).TotalMilliseconds));

                    return new AiRuntimeInstanceCommandResult
                    {
                        Success = false,
                        Operation = request.Operation,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Message = $"HTTP command failed with status code {(int)response.StatusCode}.",
                        FailureReason = failureReason,
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
                            timeoutCancellationTokenSource.Token)
                        .ConfigureAwait(false);

                if (result is not null)
                {
                    logger.LogInformation(
                        "HTTP RUNTIME COMMAND RESPONSE RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} Success={Success} DispatchSuccess={DispatchSuccess} QueueSuccess={QueueSuccess} LocalRunId={LocalRunId} ExecutionId={ExecutionId} FailureReason={FailureReason} DurationMs={DurationMs}",
                        request.RuntimeInstanceId,
                        request.Operation,
                        endpoint,
                        result.Success,
                        result.DispatchResult?.Success,
                        result.QueueResult?.Success,
                        result.DispatchResult?.LocalRunId,
                        result.DispatchResult?.ExecutionId ?? result.QueueResult?.ExecutionId,
                        result.FailureReason,
                        result.DurationMs);

                    return result;
                }

                var completedAtUtcForMissingBody =
                    DateTimeOffset.UtcNow;

                logger.LogWarning(
                    "HTTP RUNTIME COMMAND RESPONSE EMPTY RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} DurationMs={DurationMs}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    Math.Max(
                        0,
                        (long)(completedAtUtcForMissingBody - startedAtUtc).TotalMilliseconds));

                return new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = request.Operation,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = "HTTP command response body was empty.",
                    FailureReason = AiHttpRuntimeDispatchFailureReasons.InvalidResponse,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtcForMissingBody,
                    DurationMs = Math.Max(
                        0,
                        (long)(completedAtUtcForMissingBody - startedAtUtc).TotalMilliseconds)
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var completedAtUtc =
                    DateTimeOffset.UtcNow;

                logger.LogWarning(
                    "HTTP RUNTIME COMMAND TIMEOUT RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} SharedRunId={SharedRunId} RunId={RunId} DispatchTimeoutMs={DispatchTimeoutMs} DurationMs={DurationMs}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    request.DispatchRequest?.SharedRun.SharedRunId,
                    request.QueueRequest?.RunId,
                    Math.Max(
                        0,
                        (long)dispatchTimeout.TotalMilliseconds),
                    Math.Max(
                        0,
                        (long)(completedAtUtc - startedAtUtc).TotalMilliseconds));

                return new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = request.Operation,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = $"HTTP command timed out after {dispatchTimeout.TotalMilliseconds:0} ms.",
                    FailureReason = AiHttpRuntimeDispatchFailureReasons.Timeout,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = Math.Max(
                        0,
                        (long)(completedAtUtc - startedAtUtc).TotalMilliseconds),
                    Metadata = new Dictionary<string, string>
                    {
                        ["timeout.ms"] =
                            Math.Max(
                                    0,
                                    (long)dispatchTimeout.TotalMilliseconds)
                                .ToString()
                    }
                };
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "HTTP RUNTIME COMMAND CANCELLED RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} SharedRunId={SharedRunId} RunId={RunId}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    request.DispatchRequest?.SharedRun.SharedRunId,
                    request.QueueRequest?.RunId);

                throw;
            }
            catch (HttpRequestException exception)
            {
                var completedAtUtc =
                    DateTimeOffset.UtcNow;

                logger.LogWarning(
                    exception,
                    "HTTP RUNTIME COMMAND PROVIDER UNAVAILABLE RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} SharedRunId={SharedRunId} RunId={RunId} DurationMs={DurationMs}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    request.DispatchRequest?.SharedRun.SharedRunId,
                    request.QueueRequest?.RunId,
                    Math.Max(
                        0,
                        (long)(completedAtUtc - startedAtUtc).TotalMilliseconds));

                return new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = request.Operation,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = exception.Message,
                    FailureReason = AiHttpRuntimeDispatchFailureReasons.ProviderUnavailable,
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
            catch (Exception exception)
            {
                var completedAtUtc =
                    DateTimeOffset.UtcNow;

                logger.LogError(
                    exception,
                    "HTTP RUNTIME COMMAND EXCEPTION RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} SharedRunId={SharedRunId} RunId={RunId} DurationMs={DurationMs}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    request.DispatchRequest?.SharedRun.SharedRunId,
                    request.QueueRequest?.RunId,
                    Math.Max(
                        0,
                        (long)(completedAtUtc - startedAtUtc).TotalMilliseconds));

                return new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = request.Operation,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = exception.Message,
                    FailureReason = AiHttpRuntimeDispatchFailureReasons.Exception,
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
        /// Determines whether an HTTP status code represents a non-retryable command failure.
        /// </summary>
        /// <param name="statusCode">The HTTP status code.</param>
        /// <returns>
        /// <c>true</c> when the status code is a client-side failure that should not be retried;
        /// otherwise, <c>false</c>.
        /// </returns>
        private static bool IsNonRetryableHttpStatusCode(
            System.Net.HttpStatusCode statusCode)
        {
            var numericStatusCode =
                (int)statusCode;

            return numericStatusCode >= 400 &&
                numericStatusCode < 500;
        }

        /// <summary>
        /// Determines whether a command failure can be retried safely by the HTTP provider.
        /// </summary>
        /// <param name="failureReason">The command failure reason.</param>
        /// <returns>
        /// <c>true</c> when the failure can be retried safely; otherwise, <c>false</c>.
        /// </returns>
        private bool IsRetryableCommandFailure(
            string? failureReason)
        {
            if (!this.options.EnableRetry)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(failureReason))
            {
                return false;
            }

            if (string.Equals(
                    failureReason,
                    AiHttpRuntimeDispatchFailureReasons.HttpError,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(
                    failureReason,
                    AiHttpRuntimeDispatchFailureReasons.ProviderUnavailable,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(
                    failureReason,
                    AiHttpRuntimeDispatchFailureReasons.Timeout,
                    StringComparison.OrdinalIgnoreCase))
            {
                return this.options.RetryTimeouts;
            }

            return false;
        }

        /// <summary>
        /// Calculates the retry delay for the specified retry attempt.
        /// </summary>
        /// <param name="retryAttempt">The retry attempt number starting at one.</param>
        /// <returns>The retry delay.</returns>
        private TimeSpan CalculateRetryDelay(
            int retryAttempt)
        {
            if (retryAttempt <= 0)
            {
                return TimeSpan.Zero;
            }

            var baseDelay =
                this.options.RetryBaseDelay > TimeSpan.Zero
                    ? this.options.RetryBaseDelay
                    : TimeSpan.Zero;

            var maxDelay =
                this.options.RetryMaxDelay > TimeSpan.Zero
                    ? this.options.RetryMaxDelay
                    : baseDelay;

            var multiplier =
                Math.Pow(
                    2,
                    retryAttempt - 1);

            var calculatedDelay =
                TimeSpan.FromMilliseconds(
                    baseDelay.TotalMilliseconds * multiplier);

            return calculatedDelay <= maxDelay
                ? calculatedDelay
                : maxDelay;
        }

        /// <summary>
        /// Attempts to create a failed command result when the in-memory circuit breaker is open.
        /// </summary>
        /// <param name="circuitBreakerKey">The circuit breaker key.</param>
        /// <param name="request">The HTTP runtime command request.</param>
        /// <param name="endpoint">The HTTP command endpoint.</param>
        /// <param name="result">The circuit-open command result when the circuit is open.</param>
        /// <returns>
        /// <c>true</c> when the circuit is open and a result was created; otherwise, <c>false</c>.
        /// </returns>
        private bool TryCreateCircuitOpenResult(
            string circuitBreakerKey,
            AiRuntimeInstanceCommandRequest request,
            Uri endpoint,
            out AiRuntimeInstanceCommandResult result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(circuitBreakerKey);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(endpoint);

            result =
                null!;

            if (!this.options.EnableCircuitBreaker)
            {
                return false;
            }

            if (!this.circuitBreakerStates.TryGetValue(
                    circuitBreakerKey,
                    out var state))
            {
                return false;
            }

            if (!state.IsOpen)
            {
                return false;
            }

            var now =
                DateTimeOffset.UtcNow;

            logger.LogWarning(
                "HTTP RUNTIME CIRCUIT BREAKER OPEN RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} CircuitBreakerKey={CircuitBreakerKey} ConsecutiveFailureCount={ConsecutiveFailureCount} OpenUntilUtc={OpenUntilUtc}",
                request.RuntimeInstanceId,
                request.Operation,
                endpoint,
                circuitBreakerKey,
                state.ConsecutiveFailureCount,
                state.OpenUntilUtc);

            result =
                new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = request.Operation,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = "HTTP runtime circuit breaker is open.",
                    FailureReason = AiHttpRuntimeDispatchFailureReasons.CircuitOpen,
                    StartedAtUtc = now,
                    CompletedAtUtc = now,
                    DurationMs = 0,
                    Metadata = new Dictionary<string, string>
                    {
                        ["circuit_breaker.key"] = circuitBreakerKey,
                        ["circuit_breaker.open_until_utc"] =
                            state.OpenUntilUtc?.ToString("O") ??
                            string.Empty,
                        ["circuit_breaker.consecutive_failure_count"] =
                            state.ConsecutiveFailureCount.ToString()
                    }
                };

            return true;
        }

        /// <summary>
        /// Records a successful HTTP command in the in-memory circuit breaker state.
        /// </summary>
        /// <param name="circuitBreakerKey">The circuit breaker key.</param>
        /// <param name="request">The HTTP runtime command request.</param>
        /// <param name="endpoint">The HTTP command endpoint.</param>
        private void RecordCircuitBreakerSuccess(
            string circuitBreakerKey,
            AiRuntimeInstanceCommandRequest request,
            Uri endpoint)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(circuitBreakerKey);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(endpoint);

            if (!this.options.EnableCircuitBreaker)
            {
                return;
            }

            var state =
                this.circuitBreakerStates.GetOrAdd(
                    circuitBreakerKey,
                    _ => new AiHttpRuntimeCircuitBreakerState());

            state.RecordSuccess();

            logger.LogInformation(
                "HTTP RUNTIME CIRCUIT BREAKER SUCCESS RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} CircuitBreakerKey={CircuitBreakerKey} ConsecutiveFailureCount={ConsecutiveFailureCount} IsOpen={IsOpen}",
                request.RuntimeInstanceId,
                request.Operation,
                endpoint,
                circuitBreakerKey,
                state.ConsecutiveFailureCount,
                state.IsOpen);
        }

        /// <summary>
        /// Records a failed HTTP command in the in-memory circuit breaker state.
        /// </summary>
        /// <param name="circuitBreakerKey">The circuit breaker key.</param>
        /// <param name="request">The HTTP runtime command request.</param>
        /// <param name="endpoint">The HTTP command endpoint.</param>
        /// <param name="failureReason">The HTTP command failure reason.</param>
        private void RecordCircuitBreakerFailure(
            string circuitBreakerKey,
            AiRuntimeInstanceCommandRequest request,
            Uri endpoint,
            string? failureReason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(circuitBreakerKey);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(endpoint);

            if (!this.options.EnableCircuitBreaker)
            {
                return;
            }

            var state =
                this.circuitBreakerStates.GetOrAdd(
                    circuitBreakerKey,
                    _ => new AiHttpRuntimeCircuitBreakerState());

            state.RecordFailure(
                Math.Max(
                    0,
                    this.options.CircuitBreakerFailureThreshold),
                this.options.CircuitBreakerBreakDuration);

            logger.LogWarning(
                "HTTP RUNTIME CIRCUIT BREAKER FAILURE RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} CircuitBreakerKey={CircuitBreakerKey} FailureReason={FailureReason} ConsecutiveFailureCount={ConsecutiveFailureCount} IsOpen={IsOpen} OpenUntilUtc={OpenUntilUtc}",
                request.RuntimeInstanceId,
                request.Operation,
                endpoint,
                circuitBreakerKey,
                failureReason,
                state.ConsecutiveFailureCount,
                state.IsOpen,
                state.OpenUntilUtc);
        }

        /// <summary>
        /// Resolves the in-memory circuit breaker key for an HTTP runtime command endpoint.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="endpoint">The HTTP command endpoint.</param>
        /// <returns>The circuit breaker key.</returns>
        private static string ResolveCircuitBreakerKey(
            string runtimeInstanceId,
            Uri endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);

            var normalizedRuntimeInstanceId =
                string.IsNullOrWhiteSpace(runtimeInstanceId)
                    ? "unknown-runtime-instance"
                    : runtimeInstanceId.Trim();

            return $"{normalizedRuntimeInstanceId}|{endpoint.AbsoluteUri}";
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
                    AiHttpRuntimeDispatchFailureReasons.EndpointMissing,
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
                    AiHttpRuntimeDispatchFailureReasons.EndpointInvalid,
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
            var result =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            if (metadata is not null)
            {
                foreach (var item in metadata)
                {
                    if (!string.IsNullOrWhiteSpace(item.Key))
                    {
                        result[item.Key] =
                            item.Value ?? string.Empty;
                    }
                }
            }

            result[AiRuntimeInstanceProviderMetadataKeys.ProviderName] =
                ProviderName;

            result["provider"] =
                ProviderName;

            result[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] =
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName;

            result[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] =
                runtimeInstanceId;

            result[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                endpoint;

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

        /// <summary>
        /// Sets the control-plane host identity associated with this provider instance.
        /// </summary>
        /// <param name="identity">The control-plane host identity.</param>
        public void SetControlPlaneIdentity(
            IAiControlPlaneHostIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

            if (string.IsNullOrWhiteSpace(identity.ControlPlaneHostId))
            {
                throw new InvalidOperationException(
                    "ControlPlaneHostId must be provided.");
            }

            Identity = identity;
        }
    }
}