using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.ControlPlane;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Dispatch
{
    /// <summary>
    /// Dispatches shared runs to runtime instances through provider-based runtime hosting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PURPOSE:
    /// - Bridges the shared queue / shared controller layer to concrete runtime instances.
    /// - Resolves the target runtime instance dispatch provider through
    ///   <see cref="IAiRuntimeInstanceProviderCapabilityResolver"/>.
    /// - Dispatches the shared run through the selected provider.
    /// </para>
    ///
    /// <para>
    /// WHY THIS EXISTS:
    /// - The shared controller should not know whether a runtime instance is local,
    ///   Redis-command-queue based, HTTP-based, gRPC-based, Kubernetes-backed, or
    ///   provided by another future transport.
    /// - Admission decides which runtime instance should receive the run.
    /// - The provider capability resolver decides how to resolve the provider.
    /// - The provider decides how to communicate with the selected runtime instance.
    /// </para>
    ///
    /// <para>
    /// LOCAL QUEUE GUARANTEE:
    /// - This dispatcher does not replace local runtime queues.
    /// - Providers must still dispatch into the selected runtime instance local queue.
    /// - The DAG execution engine and workers remain owned by the target runtime instance.
    /// </para>
    ///
    /// <para>
    /// SAFETY GUARANTEE:
    /// - This dispatcher performs a final registry safety check before invoking a provider.
    /// - If the selected runtime instance is missing, unhealthy, draining, stopped, paused,
    ///   or cannot accept runs, dispatch is blocked and the caller can requeue the shared run.
    /// </para>
    /// </remarks>
    public sealed class RemoteAiSharedRunDispatcher : IAiSharedRunDispatcher
    {
        private const string RemoteSharedRunDispatchOperation = "remote-shared-run-dispatch";

        private readonly IAiRuntimeInstanceProviderCapabilityResolver providerCapabilityResolver;
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiControlPlaneObserver observer;
        private readonly ILogger<RemoteAiSharedRunDispatcher> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteAiSharedRunDispatcher"/> class.
        /// </summary>
        /// <param name="providerCapabilityResolver">The provider capability resolver used to resolve the dispatch provider for the target runtime instance.</param>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry used for final dispatch safety checks.</param>
        /// <param name="logger">The logger used for diagnostics.</param>
        public RemoteAiSharedRunDispatcher(
            IAiRuntimeInstanceProviderCapabilityResolver providerCapabilityResolver,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            ILogger<RemoteAiSharedRunDispatcher> logger)
            : this(
                providerCapabilityResolver,
                runtimeInstanceRegistry,
                logger,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteAiSharedRunDispatcher"/> class.
        /// </summary>
        /// <param name="providerCapabilityResolver">The provider capability resolver used to resolve the dispatch provider for the target runtime instance.</param>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry used for final dispatch safety checks.</param>
        /// <param name="logger">The logger used for diagnostics.</param>
        /// <param name="observer">The control-plane observer.</param>
        public RemoteAiSharedRunDispatcher(
            IAiRuntimeInstanceProviderCapabilityResolver providerCapabilityResolver,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            ILogger<RemoteAiSharedRunDispatcher> logger,
            IAiControlPlaneObserver observer)
            : this(
                providerCapabilityResolver,
                runtimeInstanceRegistry,
                logger,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteAiSharedRunDispatcher"/> class.
        /// </summary>
        /// <param name="providerCapabilityResolver">The provider capability resolver used to resolve the dispatch provider for the target runtime instance.</param>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry used for final dispatch safety checks.</param>
        /// <param name="logger">The logger used for diagnostics.</param>
        /// <param name="forensicsRecorder">The runtime recovery forensics recorder.</param>
        public RemoteAiSharedRunDispatcher(
            IAiRuntimeInstanceProviderCapabilityResolver providerCapabilityResolver,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            ILogger<RemoteAiSharedRunDispatcher> logger,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder)
            : this(
                providerCapabilityResolver,
                runtimeInstanceRegistry,
                logger,
                forensicsRecorder,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteAiSharedRunDispatcher"/> class.
        /// </summary>
        /// <param name="providerCapabilityResolver">The provider capability resolver used to resolve the dispatch provider for the target runtime instance.</param>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry used for final dispatch safety checks.</param>
        /// <param name="logger">The logger used for diagnostics.</param>
        /// <param name="forensicsRecorder">The runtime recovery forensics recorder.</param>
        /// <param name="observer">The control-plane observer.</param>
        public RemoteAiSharedRunDispatcher(
            IAiRuntimeInstanceProviderCapabilityResolver providerCapabilityResolver,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            ILogger<RemoteAiSharedRunDispatcher> logger,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder,
            IAiControlPlaneObserver observer)
        {
            this.providerCapabilityResolver = providerCapabilityResolver ?? throw new ArgumentNullException(nameof(providerCapabilityResolver));
            this.runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ArgumentNullException.ThrowIfNull(forensicsRecorder);
            ArgumentNullException.ThrowIfNull(observer);
            this.observer = AiRecoveryObservabilityCompatibility.Compose(observer, forensicsRecorder);
        }

        /// <inheritdoc />
        public async Task<AiSharedRunDispatchResult> DispatchAsync(
            AiSharedRunDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(request.SharedRun);

            var startedAtUtc = DateTimeOffset.UtcNow;

            await this.RecordRemoteDispatchEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?>
                    {
                        [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRun.SharedRunId,
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request.RuntimeInstanceId,
                        [AiExecutionClaimMetadataKeys.CamelCaseClaimToken] = request.ClaimToken,
                        [AiControlPlaneRequestMetadataKeys.RequestedBy] = request.RequestedBy,
                        ["source"] = request.Source,
                        ["reason"] = request.Reason,
                        [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "REMOTE DISPATCH START RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId}",
                request.RuntimeInstanceId,
                request.SharedRun.SharedRunId);

            Console.WriteLine(
                $"[REMOTE DISPATCH] START RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}'");

            if (request.SharedRun.RunRequest is null)
            {
                this.logger.LogWarning(
                    "REMOTE DISPATCH FAILED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId,
                    "missing-run-request");

                Console.WriteLine(
                    $"[REMOTE DISPATCH] FAILED RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' Reason='missing-run-request'");

                var failedResult = CreateFailedResult(
                    request,
                    startedAtUtc,
                    request.RuntimeInstanceId,
                    "missing-run-request",
                    "Shared run does not contain a runtime pipeline run request.");

                await this.RecordRemoteDispatchResultEventAsync(
                        request,
                        failedResult,
                        AiControlPlaneOperationOutcome.Failed,
                        "missing-run-request",
                        cancellationToken)
                    .ConfigureAwait(false);

                return failedResult;
            }

            var runtimeSafetySnapshot = await this.runtimeInstanceRegistry
                .GetAsync(
                    request.RuntimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (runtimeSafetySnapshot is null ||
                !runtimeSafetySnapshot.CanAcceptRun)
            {
                this.logger.LogWarning(
                    "REMOTE DISPATCH BLOCKED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Status={Status} CanAcceptRun={CanAcceptRun} Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId,
                    runtimeSafetySnapshot?.Status,
                    runtimeSafetySnapshot?.CanAcceptRun,
                    AiRuntimeInstanceFailureReasons.RuntimeInstanceNotRoutable);

                Console.WriteLine(
                    $"[REMOTE DISPATCH] BLOCKED RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' Status='{runtimeSafetySnapshot?.Status}' CanAcceptRun='{runtimeSafetySnapshot?.CanAcceptRun}' Reason='runtime-instance-not-routable'");

                var failedResult = CreateFailedResult(
                    request,
                    startedAtUtc,
                    request.RuntimeInstanceId,
                    AiRuntimeInstanceFailureReasons.RuntimeInstanceNotRoutable,
                    $"Runtime instance '{request.RuntimeInstanceId}' is not routable.");

                await this.RecordRemoteDispatchResultEventAsync(
                        request,
                        failedResult,
                        AiControlPlaneOperationOutcome.Denied,
                        AiRuntimeInstanceFailureReasons.RuntimeInstanceNotRoutable,
                        cancellationToken)
                    .ConfigureAwait(false);

                return failedResult;
            }

            var resolution = await this.providerCapabilityResolver
                .ResolveAsync<IAiRuntimeInstanceDispatchProvider>(
                    request.RuntimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "REMOTE DISPATCH CAPABILITY RuntimeInstanceId={RuntimeInstanceId} Success={Success} Reason={Reason}",
                request.RuntimeInstanceId,
                resolution.Success,
                resolution.FailureReason);

            Console.WriteLine(
                $"[REMOTE DISPATCH] CAPABILITY RuntimeInstanceId='{request.RuntimeInstanceId}' Success='{resolution.Success}' Reason='{resolution.FailureReason}'");

            if (!resolution.Success ||
                resolution.Provider is null ||
                resolution.Descriptor is null)
            {
                this.logger.LogWarning(
                    "REMOTE DISPATCH FAILED RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId,
                    "runtime-instance-dispatch-provider-not-found");

                Console.WriteLine(
                    $"[REMOTE DISPATCH] FAILED RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' Reason='runtime-instance-dispatch-provider-not-found'");

                var failedResult = CreateFailedResult(
                    request,
                    startedAtUtc,
                    request.RuntimeInstanceId,
                    "runtime-instance-dispatch-provider-not-found",
                    resolution.FailureReason ??
                    $"No dispatch provider was found for runtime instance '{request.RuntimeInstanceId}'.");

                await this.RecordRemoteDispatchResultEventAsync(
                        request,
                        failedResult,
                        AiControlPlaneOperationOutcome.Failed,
                        "runtime-instance-dispatch-provider-not-found",
                        cancellationToken)
                    .ConfigureAwait(false);

                return failedResult;
            }

            var descriptor = resolution.Descriptor;
            var provider = resolution.Provider;
            var providerTypeName = provider.GetType().FullName ?? provider.GetType().Name;

            this.logger.LogInformation(
                "REMOTE DISPATCH PROVIDER RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId} ProviderType={ProviderType}",
                request.RuntimeInstanceId,
                request.SharedRun.SharedRunId,
                providerTypeName);

            Console.WriteLine(
                $"[REMOTE DISPATCH] PROVIDER RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' ProviderType='{providerTypeName}'");

            var dispatchMetadata = MergeMetadata(
                request.Metadata,
                request.SharedRun.Metadata,
                request.SharedRun.SharedRunId,
                request.RuntimeInstanceId,
                request.ClaimToken,
                providerTypeName);

            AiSharedRuntimeInstanceDispatchResult instanceResult;

            try
            {
                this.logger.LogInformation(
                    "REMOTE DISPATCH CALL RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId);

                Console.WriteLine(
                    $"[REMOTE DISPATCH] CALL RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}'");

                instanceResult = await provider
                    .DispatchAsync(
                        descriptor,
                        new AiSharedRuntimeInstanceDispatchRequest
                        {
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            SharedRun = request.SharedRun,
                            RunRequest = request.SharedRun.RunRequest,
                            ClaimToken = request.ClaimToken,
                            CorrelationId = request.CorrelationId ?? request.SharedRun.CorrelationId,
                            RequestedBy = request.RequestedBy,
                            Source = request.Source,
                            Reason = request.Reason,
                            Metadata = dispatchMetadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                this.logger.LogInformation(
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
                this.logger.LogError(
                    exception,
                    "REMOTE DISPATCH EXCEPTION RuntimeInstanceId={RuntimeInstanceId} SharedRunId={SharedRunId}",
                    request.RuntimeInstanceId,
                    request.SharedRun.SharedRunId);

                Console.WriteLine(
                    $"[REMOTE DISPATCH] EXCEPTION RuntimeInstanceId='{request.RuntimeInstanceId}' SharedRunId='{request.SharedRun.SharedRunId}' Exception='{exception}'");

                var failedResult = CreateFailedResult(
                    request,
                    startedAtUtc,
                    request.RuntimeInstanceId,
                    "exception",
                    exception.Message,
                    exception);

                await this.RecordRemoteDispatchResultEventAsync(
                        request,
                        failedResult,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        cancellationToken)
                    .ConfigureAwait(false);

                return failedResult;
            }

            var completedAtUtcFinal = DateTimeOffset.UtcNow;

            var durationMs = Math.Max(
                0,
                (long)(completedAtUtcFinal - startedAtUtc).TotalMilliseconds);

            var acceptedRuntimeInstanceId =
                string.IsNullOrWhiteSpace(instanceResult.RuntimeInstanceId)
                    ? request.RuntimeInstanceId
                    : instanceResult.RuntimeInstanceId;

            var resultMetadata = MergeResultMetadata(
                dispatchMetadata,
                instanceResult.Metadata,
                instanceResult.LocalRunId,
                instanceResult.ExecutionId,
                instanceResult.Success,
                instanceResult.FailureReason);

            if (instanceResult.Success)
            {
                await this.RecordRemoteRecoveryDispatchForensicsAsync(
                        request,
                        resultMetadata,
                        acceptedRuntimeInstanceId,
                        instanceResult.LocalRunId,
                        instanceResult.ExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = new AiSharedRunDispatchResult
            {
                Success = instanceResult.Success,
                SharedRunId = instanceResult.SharedRunId ?? request.SharedRun.SharedRunId,
                RuntimeInstanceId = acceptedRuntimeInstanceId,
                LocalRunId = instanceResult.LocalRunId,
                ExecutionId = instanceResult.ExecutionId,
                ClaimToken = instanceResult.ClaimToken ?? request.ClaimToken,
                Message = instanceResult.Message,
                FailureReason = instanceResult.FailureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtcFinal,
                DurationMs = durationMs,
                Metadata = resultMetadata
            };

            await this.RecordRemoteDispatchResultEventAsync(
                    request,
                    result,
                    instanceResult.Success
                        ? AiControlPlaneOperationOutcome.Succeeded
                        : AiControlPlaneOperationOutcome.CompletedWithIssues,
                    instanceResult.Success ? null : instanceResult.FailureReason,
                    cancellationToken)
                .ConfigureAwait(false);

            return result;
        }

        /// <summary>
        /// Records a remote shared run dispatch result control-plane event.
        /// </summary>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="result">The shared run dispatch result.</param>
        /// <param name="outcome">The control-plane operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the control-plane event has been recorded.</returns>
        private Task RecordRemoteDispatchResultEventAsync(
            AiSharedRunDispatchRequest request,
            AiSharedRunDispatchResult result,
            AiControlPlaneOperationOutcome outcome,
            string? failureReason,
            CancellationToken cancellationToken)
        {
            return this.RecordRemoteDispatchEventAsync(
                result.Success ? AiControlPlaneEventType.OperationCompleted : AiControlPlaneEventType.OperationFailed,
                request,
                result.LocalRunId,
                result.ExecutionId,
                outcome,
                failureReason,
                result.DurationMs,
                new Dictionary<string, object?>
                {
                    [AiRunMetadataKeys.CamelCaseSharedRunId] = result.SharedRunId,
                    [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = result.RuntimeInstanceId,
                    [AiRunMetadataKeys.CamelCaseLocalRunId] = result.LocalRunId,
                    [AiExecutionMetadataKeys.CamelCaseExecutionId] = result.ExecutionId,
                    [AiExecutionClaimMetadataKeys.CamelCaseClaimToken] = result.ClaimToken,
                    ["success"] = result.Success,
                    ["message"] = result.Message,
                    [AiObservabilityMetadataKeys.FailureReason] = result.FailureReason,
                    [AiObservabilityMetadataKeys.DurationMs] = result.DurationMs,
                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId
                },
                cancellationToken,
                result.RuntimeInstanceId);
        }

        /// <summary>
        /// Records a remote shared run dispatch control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="localRunId">The optional local run identifier.</param>
        /// <param name="executionId">The optional execution identifier.</param>
        /// <param name="outcome">The optional control-plane operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <param name="properties">The optional event properties.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <param name="acceptedRuntimeInstanceId">The canonical runtime instance that accepted the run, when known.</param>
        /// <returns>A task that completes when the control-plane event has been recorded.</returns>
        private async Task RecordRemoteDispatchEventAsync(
            AiControlPlaneEventType eventType,
            AiSharedRunDispatchRequest request,
            string? localRunId,
            string? executionId,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            long? durationMs,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken,
            string? acceptedRuntimeInstanceId = null)
        {
            var effectiveRuntimeInstanceId =
                string.IsNullOrWhiteSpace(acceptedRuntimeInstanceId)
                    ? request.RuntimeInstanceId
                    : acceptedRuntimeInstanceId;

            try
            {
                await this.observer.RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.SharedController,
                            Operation = RemoteSharedRunDispatchOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                                    ? request.SharedRun.CorrelationId ?? Guid.NewGuid().ToString("N")
                                    : request.CorrelationId,
                                RunId = request.SharedRun.SharedRunId,
                                ExecutionId = executionId,
                                RuntimeInstanceId = effectiveRuntimeInstanceId,
                                PipelineKey = request.SharedRun.ExecutionContextSnapshot.ContextKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                new Dictionary<string, object?>
                                {
                                    [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRun.SharedRunId,
                                    [AiRunMetadataKeys.CamelCaseLocalRunId] = localRunId,
                                    [AiExecutionMetadataKeys.CamelCaseExecutionId] = executionId,
                                    [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = effectiveRuntimeInstanceId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId,
                                    [AiExecutionClaimMetadataKeys.CamelCaseClaimToken] = request.ClaimToken
                                })
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break remote shared run dispatch.
            }
        }

        /// <summary>
        /// Merges control-plane event properties.
        /// </summary>
        /// <param name="properties">The base event properties.</param>
        /// <param name="additionalProperties">The additional event properties.</param>
        /// <returns>The merged event properties.</returns>
        private static IReadOnlyDictionary<string, object?> MergeEventProperties(
            IReadOnlyDictionary<string, object?>? properties,
            IReadOnlyDictionary<string, object?> additionalProperties)
        {
            var merged = new Dictionary<string, object?>();

            if (properties is not null)
            {
                foreach (var item in properties)
                {
                    merged[item.Key] = item.Value;
                }
            }

            foreach (var item in additionalProperties)
            {
                merged[item.Key] = item.Value;
            }

            return merged;
        }

        /// <summary>
        /// Records remote runtime recovery dispatch forensics after a replacement local run has been created by the target runtime instance.
        /// </summary>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="metadata">The merged operation metadata.</param>
        /// <param name="runtimeInstanceId">The runtime instance that owns the canonical accepted local run.</param>
        /// <param name="localRunId">The replacement local run identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the forensics events have been recorded.</returns>
        private async Task RecordRemoteRecoveryDispatchForensicsAsync(
            AiSharedRunDispatchRequest request,
            IReadOnlyDictionary<string, string> metadata,
            string runtimeInstanceId,
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

            var durableExecutionId = !string.IsNullOrWhiteSpace(executionId)
                ? executionId
                : resolvedExecutionId;

            var recoveryMetadata = CreateRecoveryDispatchEventMetadata(
                request,
                metadata,
                runtimeInstanceId,
                localRunId,
                durableExecutionId,
                failedLocalRunId);
            var replacementRegisteredEventType =
                AiEngineEvents.Recovery.ReplacementLocalRunRegistered;

            await this.observer
                .RecordAsync(
                    AiRecoveryEngineEventFactory.Create(
                        semanticEventType: replacementRegisteredEventType,
                        eventId: string.Join(":", forensicsId, replacementRegisteredEventType, runtimeInstanceId, localRunId),
                        forensicsId: forensicsId,
                        timestampUtc: DateTimeOffset.UtcNow,
                        outcome: "registered",
                        reason: "remote-replacement-local-run-registered-for-recovery-redispatch",
                        executionId: durableExecutionId,
                        sharedRunId: request.SharedRun.SharedRunId,
                        localRunId: localRunId,
                        runtimeInstanceId: runtimeInstanceId,
                        metadata: recoveryMetadata),
                    cancellationToken)
                .ConfigureAwait(false);

            var resumeContextSeededEventType =
                AiEngineEvents.Recovery.ResumeContextSeeded;

            await this.observer
                .RecordAsync(
                    AiRecoveryEngineEventFactory.Create(
                        semanticEventType: resumeContextSeededEventType,
                        eventId: string.Join(":", forensicsId, resumeContextSeededEventType, runtimeInstanceId, localRunId),
                        forensicsId: forensicsId,
                        timestampUtc: DateTimeOffset.UtcNow,
                        outcome: "seeded",
                        reason: "remote-resume-context-seeded-from-shared-run-snapshot",
                        executionId: durableExecutionId,
                        sharedRunId: request.SharedRun.SharedRunId,
                        localRunId: localRunId,
                        runtimeInstanceId: runtimeInstanceId,
                        metadata: recoveryMetadata),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates metadata for remote recovery dispatch forensics events.
        /// </summary>
        /// <param name="request">The shared run dispatch request.</param>
        /// <param name="metadata">The merged operation metadata.</param>
        /// <param name="runtimeInstanceId">The runtime instance that owns the canonical accepted local run.</param>
        /// <param name="localRunId">The replacement local run identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="failedLocalRunId">The failed local run identifier.</param>
        /// <returns>The metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CreateRecoveryDispatchEventMetadata(
            AiSharedRunDispatchRequest request,
            IReadOnlyDictionary<string, string> metadata,
            string runtimeInstanceId,
            string localRunId,
            string? executionId,
            string? failedLocalRunId)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.SharedRun.ExecutionContextSnapshot.TenantId ?? string.Empty,
                [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.SharedRun.ExecutionContextSnapshot.TenantGroupId ?? string.Empty,
                [AiRuntimeRecoveryMetadataKeys.ReplacementRuntimeInstanceId] = runtimeInstanceId,
                [AiRuntimeRecoveryMetadataKeys.ReplacementLocalRunId] = localRunId,
                [AiRuntimeRecoveryMetadataKeys.ReplacementExecutionId] = executionId ?? string.Empty,
                [AiRuntimeRecoveryMetadataKeys.TransitionFailedRuntimeInstanceId] = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId),
                [AiRuntimeRecoveryMetadataKeys.TransitionFailedLocalRunId] = failedLocalRunId ?? string.Empty,
                [AiExecutionClaimMetadataKeys.ClaimToken] = request.ClaimToken ?? string.Empty,
                [AiRuntimeRecoveryMetadataKeys.ResumeContextKey] = request.SharedRun.ExecutionContextSnapshot.ContextKey ?? string.Empty,
                [AiRuntimeRecoveryMetadataKeys.ResumeSource] = AiRuntimeRecoveryResumeSources.SharedRunExecutionContextSnapshot,
                ["remote.dispatch"] = "true"
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
                    AiRuntimeRecoveryMetadataKeys.ForensicsId,
                    out var explicitForensicsId))
            {
                forensicsId = explicitForensicsId;
                executionId = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedExecutionId);
                failedLocalRunId = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedLocalRunId);

                return true;
            }

            executionId = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedExecutionId);
            failedLocalRunId = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedLocalRunId);

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
            if (metadata.TryGetValue(key, out var directValue) &&
                !string.IsNullOrWhiteSpace(directValue))
            {
                value = directValue;
                return true;
            }

            foreach (var pair in metadata)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
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
            var completedAtUtc = DateTimeOffset.UtcNow;

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

            metadata[AiRunMetadataKeys.SharedRunId] = sharedRunId;
            metadata[AiRuntimeInstanceMetadataKeys.RuntimeInstanceId] = runtimeInstanceId;
            metadata["remote.dispatch"] = "true";
            metadata["remote.dispatch.provider.model"] = "true";

            if (!string.IsNullOrWhiteSpace(providerTypeName))
            {
                metadata["remote.dispatch.provider.type"] = providerTypeName;
            }

            if (!string.IsNullOrWhiteSpace(claimToken))
            {
                metadata[AiExecutionClaimMetadataKeys.ClaimToken] = claimToken;
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
            var metadata = MergeMetadata(
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
