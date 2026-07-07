using System.Collections.Concurrent;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// gRPC based runtime instance provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This provider communicates with a remote runtime instance through gRPC.
    /// </para>
    ///
    /// <para>
    /// It is selected when the runtime instance capacity descriptor contains
    /// <c>provider.name = grpc</c> and a valid <c>transport.endpoint</c>.
    /// </para>
    ///
    /// <para>
    /// The provider does not own recovery. It only transports runtime commands to
    /// the selected runtime instance. Health reconciliation, execution recovery,
    /// replay, ledger, trace and forensics remain owned by the control plane.
    /// </para>
    /// </remarks>
    [AiRuntimeInstanceProvider(AiGrpcRuntimeProviderConstants.ProviderName)]
    public sealed class AiGrpcRuntimeInstanceProvider :
        IAiRuntimeInstanceProvider,
        IAiRuntimeInstanceDispatchProvider,
        IAiRuntimeInstanceStatusProvider,
        IAiRuntimeInstanceControlProvider,
        IAiRuntimeScaleOutProvider,
        IAiRuntimeInstanceControlPlaneContext
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly ILogger<AiGrpcRuntimeInstanceProvider> logger;
        private readonly AiGrpcRuntimeInstanceProviderOptions options;
        private readonly IAiGrpcRuntimeScaleOutProvisioner scaleOutProvisioner;
        private readonly ConcurrentDictionary<string, AiGrpcRuntimeCircuitBreakerState> circuitBreakerStates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="AiGrpcRuntimeInstanceProvider"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="options">The gRPC provider hardening options.</param>
        /// <param name="scaleOutProvisioner">The gRPC runtime scale-out provisioner.</param>
        public AiGrpcRuntimeInstanceProvider(
            ILogger<AiGrpcRuntimeInstanceProvider> logger,
            IOptions<AiGrpcRuntimeInstanceProviderOptions> options,
            IAiGrpcRuntimeScaleOutProvisioner scaleOutProvisioner)
        {
            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));

            ArgumentNullException.ThrowIfNull(options);

            this.options =
                options.Value
                ?? throw new ArgumentException(
                    "gRPC runtime instance provider options must be provided.",
                    nameof(options));

            this.scaleOutProvisioner = scaleOutProvisioner ?? throw new ArgumentNullException(nameof(scaleOutProvisioner));
        }

        /// <summary>
        /// Gets the control-plane host identity associated with this provider instance.
        /// </summary>
        public IAiControlPlaneHostIdentity? Identity { get; private set; }

        /// <inheritdoc />
        public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            return scaleOutProvisioner.ProvisionAsync(request, cancellationToken);
        }

        /// <inheritdoc />
        public bool CanHandle(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            if (!options.Enabled)
            {
                logger.LogInformation(
                    "GRPC PROVIDER CAN HANDLE CHECK RuntimeInstanceId={RuntimeInstanceId} Enabled=False CanHandle=False",
                    descriptor.RuntimeInstanceId);

                return false;
            }

            if (descriptor.Metadata is not null &&
                descriptor.Metadata.TryGetValue(
                    AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                    out var providerName) &&
                !string.IsNullOrWhiteSpace(providerName))
            {
                var canHandle =
                    string.Equals(
                        providerName.Trim(),
                        AiGrpcRuntimeProviderConstants.ProviderName,
                        StringComparison.OrdinalIgnoreCase);

                logger.LogInformation(
                    "GRPC PROVIDER CAN HANDLE CHECK RuntimeInstanceId={RuntimeInstanceId} ProviderName={ProviderName} CanHandle={CanHandle}",
                    descriptor.RuntimeInstanceId,
                    providerName,
                    canHandle);

                return canHandle;
            }

            logger.LogInformation(
                "GRPC PROVIDER CAN HANDLE CHECK RuntimeInstanceId={RuntimeInstanceId} ProviderName=(missing) CanHandle=False",
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
                "GRPC DISPATCH START RuntimeInstanceId={RuntimeInstanceId} DescriptorRuntimeInstanceId={DescriptorRuntimeInstanceId} RequestRuntimeInstanceId={RequestRuntimeInstanceId} SharedRunId={SharedRunId} ClaimToken={ClaimToken} CorrelationId={CorrelationId}",
                runtimeInstanceId,
                descriptor.RuntimeInstanceId,
                request.RuntimeInstanceId,
                request.SharedRun.SharedRunId,
                request.ClaimToken,
                request.CorrelationId);

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
                logger.LogWarning(
                    "GRPC DISPATCH ENDPOINT RESOLUTION FAILED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} FailureReason={FailureReason} Message={Message} DescriptorMetadata={DescriptorMetadata}",
                    runtimeInstanceId,
                    request.SharedRun.SharedRunId,
                    endpointResolution.FailureReason,
                    endpointResolution.Message,
                    FormatMetadata(descriptor.Metadata));

                Console.WriteLine(
                    $"[GRPC PROVIDER ENDPOINT RESOLUTION FAILED] RuntimeInstanceId='{runtimeInstanceId}', SharedRunId='{request.SharedRun.SharedRunId}', FailureReason='{endpointResolution.FailureReason}', Message='{endpointResolution.Message}', DescriptorMetadata='{FormatMetadata(descriptor.Metadata)}'.");

                return CreateFailedDispatchResult(
                    request,
                    runtimeInstanceId,
                    endpointResolution.FailureReason ?? AiGrpcRuntimeDispatchFailureReasons.EndpointMissing,
                    endpointResolution.Message ?? "gRPC runtime instance endpoint is missing.");
            }

            logger.LogInformation(
                "GRPC DISPATCH ENDPOINT RESOLVED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Endpoint={Endpoint} DescriptorMetadata={DescriptorMetadata}",
                runtimeInstanceId,
                request.SharedRun.SharedRunId,
                endpointResolution.Endpoint,
                FormatMetadata(descriptor.Metadata));

            Console.WriteLine(
                $"[GRPC PROVIDER ENDPOINT RESOLVED] RuntimeInstanceId='{runtimeInstanceId}', SharedRunId='{request.SharedRun.SharedRunId}', Endpoint='{endpointResolution.Endpoint}', DescriptorMetadata='{FormatMetadata(descriptor.Metadata)}'.");

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
                commandResult.FailureReason ?? "grpc-dispatch-result-missing",
                commandResult.Message ?? "gRPC runtime instance provider did not receive a dispatch result.");
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

        /// <inheritdoc />
        public void SetControlPlaneIdentity(
            IAiControlPlaneHostIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

            if (string.IsNullOrWhiteSpace(identity.ControlPlaneHostId))
            {
                throw new InvalidOperationException(
                    "ControlPlaneHostId must be provided.");
            }

            Identity =
                identity;
        }

        /// <summary>
        /// Sends a runtime queue command through gRPC.
        /// </summary>
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
                logger.LogWarning(
                    "GRPC QUEUE COMMAND ENDPOINT RESOLUTION FAILED RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} FailureReason={FailureReason} Message={Message} DescriptorMetadata={DescriptorMetadata}",
                    runtimeInstanceId,
                    queueOperation,
                    endpointResolution.FailureReason,
                    endpointResolution.Message,
                    FormatMetadata(descriptor.Metadata));

                Console.WriteLine(
                    $"[GRPC QUEUE COMMAND ENDPOINT RESOLUTION FAILED] RuntimeInstanceId='{runtimeInstanceId}', Operation='{queueOperation}', FailureReason='{endpointResolution.FailureReason}', Message='{endpointResolution.Message}', DescriptorMetadata='{FormatMetadata(descriptor.Metadata)}'.");

                return CreateFailedQueueResult(
                    request,
                    queueOperation,
                    runtimeInstanceId,
                    endpointResolution.FailureReason ?? AiGrpcRuntimeDispatchFailureReasons.EndpointMissing,
                    endpointResolution.Message ?? "gRPC runtime instance endpoint is missing.");
            }

            logger.LogInformation(
                "GRPC QUEUE COMMAND ENDPOINT RESOLVED RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} DescriptorMetadata={DescriptorMetadata}",
                runtimeInstanceId,
                queueOperation,
                endpointResolution.Endpoint,
                FormatMetadata(descriptor.Metadata));

            Console.WriteLine(
                $"[GRPC QUEUE COMMAND ENDPOINT RESOLVED] RuntimeInstanceId='{runtimeInstanceId}', Operation='{queueOperation}', Endpoint='{endpointResolution.Endpoint}', DescriptorMetadata='{FormatMetadata(descriptor.Metadata)}'.");

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
                commandResult.FailureReason ?? "grpc-queue-result-missing",
                commandResult.Message ?? "gRPC runtime instance provider did not receive a queue control-plane result.");
        }

        /// <summary>
        /// Sends a command request to the remote runtime instance gRPC endpoint.
        /// </summary>
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
                options.EnableRetry
                    ? Math.Max(0, options.MaxRetryAttempts)
                    : 0;

            var totalAttempts =
                maxRetryAttempts + 1;

            AiRuntimeInstanceCommandResult? lastResult = null;

            for (var attempt = 1; attempt <= totalAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

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
                    !IsRetryableCommandFailure(lastResult.FailureReason))
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
                    Message = "gRPC command failed before a command result was produced.",
                    FailureReason = AiGrpcRuntimeDispatchFailureReasons.RuntimeUnavailable,
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
        /// Sends one command attempt to the remote runtime instance gRPC endpoint.
        /// </summary>
        private async Task<AiRuntimeInstanceCommandResult> SendCommandOnceAsync(
            Uri endpoint,
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken)
        {
            var startedAtUtc =
                DateTimeOffset.UtcNow;

            using var timeoutCancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            if (options.DispatchTimeout > TimeSpan.Zero)
            {
                timeoutCancellationTokenSource.CancelAfter(
                    options.DispatchTimeout);
            }

            try
            {
                logger.LogInformation(
                    "GRPC COMMAND SEND BEGIN RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} TimeoutMs={TimeoutMs}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    options.DispatchTimeout.TotalMilliseconds);

                Console.WriteLine(
                    $"[GRPC COMMAND SEND BEGIN] RuntimeInstanceId='{request.RuntimeInstanceId}', Operation='{request.Operation}', Endpoint='{endpoint}', TimeoutMs='{options.DispatchTimeout.TotalMilliseconds:0}'.");

                using var channel =
                    GrpcChannel.ForAddress(
                        endpoint);

                var client =
                    new AiRuntimeInstanceCommandGrpc.AiRuntimeInstanceCommandGrpcClient(
                        channel);

                var grpcRequest =
                    new AiRuntimeInstanceGrpcCommandRequest
                    {
                        RequestJson = JsonSerializer.Serialize(
                            request,
                            JsonOptions)
                    };

                var grpcResponse =
                    await client.ExecuteCommandAsync(
                            grpcRequest,
                            cancellationToken: timeoutCancellationTokenSource.Token)
                        .ResponseAsync
                        .ConfigureAwait(false);

                if (grpcResponse is null ||
                    string.IsNullOrWhiteSpace(grpcResponse.ResponseJson))
                {
                    return CreateFailedCommandResult(
                        request,
                        startedAtUtc,
                        AiGrpcRuntimeDispatchFailureReasons.EmptyResponse,
                        "gRPC command response was empty.");
                }

                var commandResult =
                    JsonSerializer.Deserialize<AiRuntimeInstanceCommandResult>(
                        grpcResponse.ResponseJson,
                        JsonOptions);

                if (commandResult is null)
                {
                    return CreateFailedCommandResult(
                        request,
                        startedAtUtc,
                        AiGrpcRuntimeDispatchFailureReasons.InvalidResponse,
                        "gRPC command response payload deserialized to null.");
                }

                return commandResult;
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.DeadlineExceeded)
            {
                logger.LogWarning(
                    exception,
                    "GRPC COMMAND DEADLINE EXCEEDED RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} StatusCode={StatusCode} Detail={Detail}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    exception.StatusCode,
                    exception.Status.Detail);

                Console.WriteLine(
                    $"[GRPC COMMAND DEADLINE EXCEEDED] RuntimeInstanceId='{request.RuntimeInstanceId}', Operation='{request.Operation}', Endpoint='{endpoint}', StatusCode='{exception.StatusCode}', Detail='{exception.Status.Detail}', Message='{exception.Message}'.");

                return CreateFailedCommandResult(
                    request,
                    startedAtUtc,
                    AiGrpcRuntimeDispatchFailureReasons.CommandTimeout,
                    exception.Message,
                    exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "GRPC COMMAND TIMEOUT RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} TimeoutMs={TimeoutMs}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    options.DispatchTimeout.TotalMilliseconds);

                Console.WriteLine(
                    $"[GRPC COMMAND TIMEOUT] RuntimeInstanceId='{request.RuntimeInstanceId}', Operation='{request.Operation}', Endpoint='{endpoint}', TimeoutMs='{options.DispatchTimeout.TotalMilliseconds:0}'.");

                return CreateFailedCommandResult(
                    request,
                    startedAtUtc,
                    AiGrpcRuntimeDispatchFailureReasons.CommandTimeout,
                    $"gRPC command timed out after {options.DispatchTimeout.TotalMilliseconds:0} ms.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RpcException exception)
            {
                logger.LogWarning(
                    exception,
                    "GRPC COMMAND RPC FAILED RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} StatusCode={StatusCode} Detail={Detail}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    exception.StatusCode,
                    exception.Status.Detail);

                Console.WriteLine(
                    $"[GRPC COMMAND RPC FAILED] RuntimeInstanceId='{request.RuntimeInstanceId}', Operation='{request.Operation}', Endpoint='{endpoint}', StatusCode='{exception.StatusCode}', Detail='{exception.Status.Detail}', Message='{exception.Message}'.");

                return CreateFailedCommandResult(
                    request,
                    startedAtUtc,
                    AiGrpcRuntimeDispatchFailureReasons.RuntimeUnavailable,
                    exception.Message,
                    exception);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(
                    exception,
                    "GRPC COMMAND INVALID JSON RESPONSE RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} Message={Message}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    exception.Message);

                Console.WriteLine(
                    $"[GRPC COMMAND INVALID JSON RESPONSE] RuntimeInstanceId='{request.RuntimeInstanceId}', Operation='{request.Operation}', Endpoint='{endpoint}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");

                return CreateFailedCommandResult(
                    request,
                    startedAtUtc,
                    AiGrpcRuntimeDispatchFailureReasons.InvalidResponse,
                    exception.Message,
                    exception);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "GRPC COMMAND FAILED RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} ExceptionType={ExceptionType} Message={Message}",
                    request.RuntimeInstanceId,
                    request.Operation,
                    endpoint,
                    exception.GetType().FullName,
                    exception.Message);

                Console.WriteLine(
                    $"[GRPC COMMAND FAILED] RuntimeInstanceId='{request.RuntimeInstanceId}', Operation='{request.Operation}', Endpoint='{endpoint}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");

                return CreateFailedCommandResult(
                    request,
                    startedAtUtc,
                    AiGrpcRuntimeDispatchFailureReasons.RuntimeUnavailable,
                    exception.Message,
                    exception);
            }
        }

        /// <summary>
        /// Determines whether a command failure can be retried safely by the gRPC provider.
        /// </summary>
        private bool IsRetryableCommandFailure(
            string? failureReason)
        {
            if (!options.EnableRetry ||
                string.IsNullOrWhiteSpace(failureReason))
            {
                return false;
            }

            if (string.Equals(
                    failureReason,
                    AiGrpcRuntimeDispatchFailureReasons.RuntimeUnavailable,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(
                    failureReason,
                    AiGrpcRuntimeDispatchFailureReasons.CommandTimeout,
                    StringComparison.OrdinalIgnoreCase))
            {
                return options.RetryTimeouts;
            }

            return false;
        }

        /// <summary>
        /// Calculates the retry delay for the specified retry attempt.
        /// </summary>
        private TimeSpan CalculateRetryDelay(
            int retryAttempt)
        {
            if (retryAttempt <= 0)
            {
                return TimeSpan.Zero;
            }

            var baseDelay =
                options.RetryBaseDelay > TimeSpan.Zero
                    ? options.RetryBaseDelay
                    : TimeSpan.Zero;

            var maxDelay =
                options.RetryMaxDelay > TimeSpan.Zero
                    ? options.RetryMaxDelay
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
        /// Attempts to create a failed command result when the circuit breaker is open.
        /// </summary>
        private bool TryCreateCircuitOpenResult(
            string circuitBreakerKey,
            AiRuntimeInstanceCommandRequest request,
            Uri endpoint,
            out AiRuntimeInstanceCommandResult result)
        {
            result = null!;

            if (!options.EnableCircuitBreaker ||
                !circuitBreakerStates.TryGetValue(circuitBreakerKey, out var state) ||
                !state.IsOpen)
            {
                return false;
            }

            var now =
                DateTimeOffset.UtcNow;

            result =
                new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = request.Operation,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = "gRPC runtime circuit breaker is open.",
                    FailureReason = AiGrpcRuntimeDispatchFailureReasons.CircuitOpen,
                    StartedAtUtc = now,
                    CompletedAtUtc = now,
                    DurationMs = 0,
                    Metadata = new Dictionary<string, string>
                    {
                        ["circuit_breaker.key"] = circuitBreakerKey,
                        ["circuit_breaker.open_until_utc"] = state.OpenUntilUtc?.ToString("O") ?? string.Empty,
                        ["circuit_breaker.consecutive_failure_count"] = state.ConsecutiveFailureCount.ToString()
                    }
                };

            return true;
        }

        /// <summary>
        /// Records a successful gRPC command in the circuit breaker state.
        /// </summary>
        private void RecordCircuitBreakerSuccess(
            string circuitBreakerKey,
            AiRuntimeInstanceCommandRequest request,
            Uri endpoint)
        {
            if (!options.EnableCircuitBreaker)
            {
                return;
            }

            var state =
                circuitBreakerStates.GetOrAdd(
                    circuitBreakerKey,
                    _ => new AiGrpcRuntimeCircuitBreakerState());

            state.RecordSuccess();
        }

        /// <summary>
        /// Records a failed gRPC command in the circuit breaker state.
        /// </summary>
        private void RecordCircuitBreakerFailure(
            string circuitBreakerKey,
            AiRuntimeInstanceCommandRequest request,
            Uri endpoint,
            string? failureReason)
        {
            if (!options.EnableCircuitBreaker)
            {
                return;
            }

            var state =
                circuitBreakerStates.GetOrAdd(
                    circuitBreakerKey,
                    _ => new AiGrpcRuntimeCircuitBreakerState());

            state.RecordFailure(
                Math.Max(0, options.CircuitBreakerFailureThreshold),
                options.CircuitBreakerBreakDuration);

            logger.LogWarning(
                "GRPC RUNTIME CIRCUIT BREAKER FAILURE RuntimeInstanceId={RuntimeInstanceId} Operation={Operation} Endpoint={Endpoint} CircuitBreakerKey={CircuitBreakerKey} FailureReason={FailureReason} ConsecutiveFailureCount={ConsecutiveFailureCount} IsOpen={IsOpen} OpenUntilUtc={OpenUntilUtc}",
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
        /// Resolves the gRPC command endpoint from the runtime instance descriptor.
        /// </summary>
        private static GrpcCommandEndpointResolution ResolveCommandEndpoint(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            var endpoint =
                GetMetadataValue(
                    descriptor.Metadata,
                    AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint) ??
                GetMetadataValue(
                    descriptor.Metadata,
                    AiGrpcRuntimeProviderConstants.TransportEndpointMetadataKey);

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return GrpcCommandEndpointResolution.Failed(
                    AiGrpcRuntimeDispatchFailureReasons.EndpointMissing,
                    $"Runtime instance descriptor '{descriptor.RuntimeInstanceId}' does not define '{AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint}' or 'transport.endpoint'.");
            }

            var endpointText =
                endpoint.Trim();

            if (!Uri.TryCreate(
                    endpointText,
                    UriKind.Absolute,
                    out var commandEndpoint))
            {
                return GrpcCommandEndpointResolution.Failed(
                    AiGrpcRuntimeDispatchFailureReasons.EndpointInvalid,
                    $"Runtime instance gRPC endpoint '{endpointText}' is not a valid absolute URI.");
            }

            return GrpcCommandEndpointResolution.Succeeded(
                commandEndpoint);
        }

        /// <summary>
        /// Gets a metadata value using case-insensitive key matching.
        /// </summary>
        private static string? GetMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            string key)
        {
            if (metadata is null)
            {
                return null;
            }

            if (metadata.TryGetValue(
                    key,
                    out var value))
            {
                return value;
            }

            foreach (var item in metadata)
            {
                if (string.Equals(
                        item.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return item.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Formats metadata for diagnostics.
        /// </summary>
        private static string FormatMetadata(
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null || metadata.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                ";",
                metadata
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key}={item.Value}"));
        }

        /// <summary>
        /// Resolves the runtime instance identifier.
        /// </summary>
        private static string ResolveRuntimeInstanceId(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            string? fallbackRuntimeInstanceId)
        {
            return string.IsNullOrWhiteSpace(descriptor.RuntimeInstanceId)
                ? fallbackRuntimeInstanceId ?? string.Empty
                : descriptor.RuntimeInstanceId;
        }

        /// <summary>
        /// Resolves the circuit breaker key for a gRPC runtime command endpoint.
        /// </summary>
        private static string ResolveCircuitBreakerKey(
            string runtimeInstanceId,
            Uri endpoint)
        {
            var normalizedRuntimeInstanceId =
                string.IsNullOrWhiteSpace(runtimeInstanceId)
                    ? "unknown-runtime-instance"
                    : runtimeInstanceId.Trim();

            return $"{normalizedRuntimeInstanceId}|{endpoint.AbsoluteUri}";
        }

        /// <summary>
        /// Creates command metadata.
        /// </summary>
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
                AiGrpcRuntimeProviderConstants.ProviderName;

            result["provider"] =
                AiGrpcRuntimeProviderConstants.ProviderName;

            result[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] =
                AiGrpcRuntimeProviderConstants.TransportName;

            result[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] =
                runtimeInstanceId;

            result[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                endpoint;

            return result;
        }

        /// <summary>
        /// Creates a failed command result.
        /// </summary>
        private static AiRuntimeInstanceCommandResult CreateFailedCommandResult(
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
                    ["transport.name"] = AiGrpcRuntimeProviderConstants.TransportName
                };

            if (exception is not null)
            {
                metadata["exception.type"] =
                    exception.GetType().FullName ??
                    exception.GetType().Name;

                metadata["exception.message"] =
                    exception.Message;
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
                DurationMs = Math.Max(
                    0,
                    (long)(completedAtUtc - startedAtUtc).TotalMilliseconds),
                Metadata = metadata
            };
        }

        /// <summary>
        /// Creates a failed dispatch result.
        /// </summary>
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
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] =
                        AiGrpcRuntimeProviderConstants.ProviderName
                }
            };
        }

        /// <summary>
        /// Creates a failed queue control-plane result.
        /// </summary>
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
                    ? new[] { message }
                    : Array.Empty<string>()
            };
        }

        /// <summary>
        /// Represents a resolved gRPC command endpoint.
        /// </summary>
        private sealed class GrpcCommandEndpointResolution
        {
            private GrpcCommandEndpointResolution(
                bool success,
                Uri? endpoint,
                string? failureReason,
                string? message)
            {
                Success = success;
                Endpoint = endpoint;
                FailureReason = failureReason;
                Message = message;
            }

            /// <summary>
            /// Gets a value indicating whether endpoint resolution succeeded.
            /// </summary>
            public bool Success { get; }

            /// <summary>
            /// Gets the resolved endpoint.
            /// </summary>
            public Uri? Endpoint { get; }

            /// <summary>
            /// Gets the failure reason.
            /// </summary>
            public string? FailureReason { get; }

            /// <summary>
            /// Gets the failure message.
            /// </summary>
            public string? Message { get; }

            /// <summary>
            /// Creates a successful endpoint resolution.
            /// </summary>
            public static GrpcCommandEndpointResolution Succeeded(
                Uri endpoint)
            {
                return new GrpcCommandEndpointResolution(
                    true,
                    endpoint,
                    null,
                    null);
            }

            /// <summary>
            /// Creates a failed endpoint resolution.
            /// </summary>
            public static GrpcCommandEndpointResolution Failed(
                string failureReason,
                string message)
            {
                return new GrpcCommandEndpointResolution(
                    false,
                    null,
                    failureReason,
                    message);
            }
        }
    }
}