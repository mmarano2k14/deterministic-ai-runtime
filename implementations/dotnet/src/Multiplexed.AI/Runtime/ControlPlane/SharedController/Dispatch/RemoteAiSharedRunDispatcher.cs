using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Dispatch
{
    /// <summary>
    /// Dispatches shared runs to runtime instances through provider-based runtime hosting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PURPOSE:
    /// - Bridges the shared queue / shared controller layer to concrete runtime instances.
    /// - Resolves the target runtime instance capacity descriptor from
    ///   <see cref="IAiRuntimeInstanceCapacityStore"/>.
    /// - Resolves the correct runtime instance dispatch provider through
    ///   <see cref="IAiRuntimeInstanceProviderRouter"/>.
    /// - Dispatches the shared run through the selected provider.
    /// </para>
    ///
    /// <para>
    /// WHY THIS EXISTS:
    /// - The shared controller should not know whether a runtime instance is local,
    ///   Redis-command-queue based, HTTP-based, gRPC-based, Kubernetes-backed, or
    ///   provided by another future transport.
    /// - Admission decides which runtime instance should receive the run.
    /// - The provider router decides how to communicate with that runtime instance.
    /// </para>
    ///
    /// <para>
    /// LOCAL QUEUE GUARANTEE:
    /// - This dispatcher does not replace local runtime queues.
    /// - Providers must still dispatch into the selected runtime instance local queue.
    /// - The DAG execution engine and workers remain owned by the target runtime instance.
    /// </para>
    /// </remarks>
    public sealed class RemoteAiSharedRunDispatcher : IAiSharedRunDispatcher
    {
        private readonly IAiRuntimeInstanceCapacityStore capacityStore;
        private readonly IAiRuntimeInstanceProviderRouter providerRouter;
        private readonly ILogger<RemoteAiSharedRunDispatcher> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteAiSharedRunDispatcher"/> class.
        /// </summary>
        /// <param name="capacityStore">
        /// The runtime instance capacity store used to resolve the target runtime instance descriptor.
        /// </param>
        /// <param name="providerRouter">
        /// The provider router used to resolve the dispatch provider for the target runtime instance.
        /// </param>
        /// <param name="logger">The logger used for diagnostics.</param>
        public RemoteAiSharedRunDispatcher(
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeInstanceProviderRouter providerRouter,
            ILogger<RemoteAiSharedRunDispatcher> logger)
        {
            this.capacityStore =
                capacityStore
                ?? throw new ArgumentNullException(nameof(capacityStore));

            this.providerRouter =
                providerRouter
                ?? throw new ArgumentNullException(nameof(providerRouter));

            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<AiSharedRunDispatchResult> DispatchAsync(
            AiSharedRunDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(request.SharedRun);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            logger.LogInformation(
                "REMOTE DISPATCH START RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId}",
                request.RuntimeInstanceId,
                request.SharedRun.SharedRunId);

            Console.WriteLine(
                $"[REMOTE DISPATCH] START RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}'");

            if (request.SharedRun.RunRequest is null)
            {
                logger.LogWarning(
                    "REMOTE DISPATCH FAILED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId,
                    "missing-run-request");

                Console.WriteLine(
                    $"[REMOTE DISPATCH] FAILED RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' Reason='missing-run-request'");

                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    request.RuntimeInstanceId,
                    "missing-run-request",
                    "Shared run does not contain a runtime pipeline run request.");
            }

            var descriptor =
                await capacityStore
                    .GetAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            logger.LogInformation(
                "REMOTE DISPATCH CAPACITY RuntimeInstanceId={RuntimeInstanceId} Found={Found}",
                request.RuntimeInstanceId,
                descriptor is not null);

            Console.WriteLine(
                $"[REMOTE DISPATCH] CAPACITY RuntimeInstanceId='{request.RuntimeInstanceId}' Found='{descriptor is not null}'");

            if (descriptor is null)
            {
                logger.LogWarning(
                    "REMOTE DISPATCH FAILED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId,
                    "runtime-instance-capacity-not-found");

                Console.WriteLine(
                    $"[REMOTE DISPATCH] FAILED RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' Reason='runtime-instance-capacity-not-found'");

                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    request.RuntimeInstanceId,
                    "runtime-instance-capacity-not-found",
                    $"Runtime instance capacity descriptor '{request.RuntimeInstanceId}' was not found.");
            }

            if (!providerRouter.TryGetProvider<IAiRuntimeInstanceDispatchProvider>(
                    descriptor,
                    out var provider))
            {
                logger.LogWarning(
                    "REMOTE DISPATCH FAILED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId,
                    "runtime-instance-provider-not-found");

                Console.WriteLine(
                    $"[REMOTE DISPATCH] FAILED RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' Reason='runtime-instance-provider-not-found'");

                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    request.RuntimeInstanceId,
                    "runtime-instance-provider-not-found",
                    $"No dispatch provider was found for runtime instance '{request.RuntimeInstanceId}'.");
            }

            var providerTypeName =
                provider.GetType().FullName ?? provider.GetType().Name;

            logger.LogInformation(
                "REMOTE DISPATCH PROVIDER RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} ProviderType={ProviderType}",
                request.RuntimeInstanceId,
                request.SharedRun.SharedRunId,
                providerTypeName);

            Console.WriteLine(
                $"[REMOTE DISPATCH] PROVIDER RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' ProviderType='{providerTypeName}'");

            var dispatchMetadata =
                MergeMetadata(
                    request.Metadata,
                    request.SharedRun.Metadata,
                    request.SharedRun.SharedRunId,
                    request.RuntimeInstanceId,
                    request.ClaimToken,
                    providerTypeName);

            AiSharedRuntimeInstanceDispatchResult instanceResult;

            try
            {
                logger.LogInformation(
                    "REMOTE DISPATCH CALL RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId);

                Console.WriteLine(
                    $"[REMOTE DISPATCH] CALL RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}'");

                instanceResult =
                    await provider
                        .DispatchAsync(
                            descriptor,
                            new AiSharedRuntimeInstanceDispatchRequest
                            {
                                RuntimeInstanceId = request.RuntimeInstanceId,
                                SharedRun = request.SharedRun,
                                RunRequest = request.SharedRun.RunRequest,
                                ClaimToken = request.ClaimToken,
                                CorrelationId =
                                    request.CorrelationId ??
                                    request.SharedRun.CorrelationId,
                                RequestedBy = request.RequestedBy,
                                Source = request.Source,
                                Reason = request.Reason,
                                Metadata = dispatchMetadata
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                logger.LogInformation(
                    "REMOTE DISPATCH RESULT RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Success={Success} LocalRunId={LocalRunId} ExecutionId={ExecutionId} FailureReason={FailureReason}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId,
                    instanceResult.Success,
                    instanceResult.LocalRunId,
                    instanceResult.ExecutionId,
                    instanceResult.FailureReason);

                Console.WriteLine(
                    $"[REMOTE DISPATCH] RESULT RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' Success='{instanceResult.Success}' LocalRunId='{instanceResult.LocalRunId}' ExecutionId='{instanceResult.ExecutionId}' Failure='{instanceResult.FailureReason}'");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(
                    exception,
                    "REMOTE DISPATCH EXCEPTION RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId);

                Console.WriteLine(
                    $"[REMOTE DISPATCH] EXCEPTION RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' Exception='{exception}'");

                return CreateFailedResult(
                    request,
                    startedAtUtc,
                    request.RuntimeInstanceId,
                    "exception",
                    exception.Message,
                    exception);
            }

            var completedAtUtcFinal =
                DateTimeOffset.UtcNow;

            var durationMs =
                Math.Max(
                    0,
                    (long)(completedAtUtcFinal - startedAtUtc).TotalMilliseconds);

            var resultMetadata =
                MergeResultMetadata(
                    dispatchMetadata,
                    instanceResult.Metadata,
                    instanceResult.LocalRunId,
                    instanceResult.ExecutionId,
                    instanceResult.Success,
                    instanceResult.FailureReason);

            return new AiSharedRunDispatchResult
            {
                Success = instanceResult.Success,
                SharedRunId =
                    instanceResult.SharedRunId ??
                    request.SharedRun.SharedRunId,
                RuntimeInstanceId = request.RuntimeInstanceId,
                LocalRunId = instanceResult.LocalRunId,
                ExecutionId = instanceResult.ExecutionId,
                ClaimToken =
                    instanceResult.ClaimToken ??
                    request.ClaimToken,
                Message = instanceResult.Message,
                FailureReason = instanceResult.FailureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtcFinal,
                DurationMs = durationMs,
                Metadata = resultMetadata
            };
        }

        /// <summary>
        /// Creates a failed shared run dispatch result.
        /// </summary>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="startedAtUtc">The UTC timestamp when dispatch started.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="failureCode">The structured failure code.</param>
        /// <param name="failureReason">The human-readable failure reason.</param>
        /// <param name="exception">The optional exception that caused the failure.</param>
        /// <returns>The failed shared run dispatch result.</returns>
        private static AiSharedRunDispatchResult CreateFailedResult(
            AiSharedRunDispatchRequest request,
            DateTimeOffset startedAtUtc,
            string runtimeInstanceId,
            string failureCode,
            string failureReason,
            Exception? exception = null)
        {
            var completedAtUtc =
                DateTimeOffset.UtcNow;

            return new AiSharedRunDispatchResult
            {
                Success = false,
                SharedRunId = request.SharedRun.SharedRunId,
                RuntimeInstanceId = runtimeInstanceId,
                ClaimToken = request.ClaimToken,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs = Math.Max(
                    0,
                    (long)(completedAtUtc - startedAtUtc).TotalMilliseconds),
                FailureReason = failureReason,
                Metadata = CreateFailureMetadata(
                    request,
                    failureCode,
                    exception)
            };
        }

        /// <summary>
        /// Merges dispatch metadata and shared run metadata into a single dictionary.
        /// </summary>
        /// <param name="dispatchMetadata">The dispatch metadata.</param>
        /// <param name="sharedRunMetadata">The shared run metadata.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="claimToken">The optional claim token.</param>
        /// <param name="providerTypeName">The optional provider type name.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? dispatchMetadata,
            IReadOnlyDictionary<string, string>? sharedRunMetadata,
            string sharedRunId,
            string runtimeInstanceId,
            string? claimToken,
            string? providerTypeName = null)
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

            if (dispatchMetadata is not null)
            {
                foreach (var item in dispatchMetadata)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            metadata["shared.run.id"] = sharedRunId;
            metadata["runtime.instance.id"] = runtimeInstanceId;
            metadata["remote.dispatch"] = "true";
            metadata["remote.dispatch.provider.model"] = "true";

            if (!string.IsNullOrWhiteSpace(providerTypeName))
            {
                metadata["remote.dispatch.provider.type"] = providerTypeName;
            }

            if (!string.IsNullOrWhiteSpace(claimToken))
            {
                metadata["claim.token"] = claimToken;
            }

            return metadata;
        }

        /// <summary>
        /// Merges remote dispatch metadata with metadata returned by the target runtime instance.
        /// </summary>
        /// <param name="dispatchMetadata">The metadata created by the dispatch operation.</param>
        /// <param name="instanceMetadata">The metadata returned by the target runtime instance.</param>
        /// <param name="localRunId">The optional local run identifier.</param>
        /// <param name="executionId">The optional execution identifier.</param>
        /// <param name="success">A value indicating whether dispatch succeeded.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeResultMetadata(
            IReadOnlyDictionary<string, string> dispatchMetadata,
            IReadOnlyDictionary<string, string>? instanceMetadata,
            string? localRunId,
            string? executionId,
            bool success,
            string? failureReason)
        {
            var metadata = new Dictionary<string, string>(
                dispatchMetadata,
                StringComparer.Ordinal);

            if (instanceMetadata is not null)
            {
                foreach (var item in instanceMetadata)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            metadata["remote.dispatch.success"] = success.ToString();
            metadata["remote.dispatch.local.run.id"] = localRunId ?? string.Empty;
            metadata["remote.dispatch.execution.id"] = executionId ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(failureReason))
            {
                metadata["remote.dispatch.failure.reason"] = failureReason;
            }

            return metadata;
        }

        /// <summary>
        /// Creates metadata for a failed remote dispatch operation.
        /// </summary>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="failureCode">The structured failure code.</param>
        /// <param name="exception">The optional exception.</param>
        /// <returns>The failure metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CreateFailureMetadata(
            AiSharedRunDispatchRequest request,
            string failureCode,
            Exception? exception = null)
        {
            var metadata =
                MergeMetadata(
                    request.Metadata,
                    request.SharedRun.Metadata,
                    request.SharedRun.SharedRunId,
                    request.RuntimeInstanceId,
                    request.ClaimToken);

            var result = new Dictionary<string, string>(
                metadata,
                StringComparer.Ordinal)
            {
                ["remote.dispatch.success"] = "False",
                ["remote.dispatch.failure.code"] = failureCode
            };

            if (exception is not null)
            {
                result["remote.dispatch.exception.type"] =
                    exception.GetType().FullName ?? exception.GetType().Name;
            }

            return result;
        }
    }
}