using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Observes pending runtime scale-out requests and forwards them to a scale-out provider selector.
    /// </summary>
    /// <remarks>
    /// This hosted service does not decide admission and does not create scale-out
    /// requests. It only processes requests already persisted in
    /// <see cref="IAiRuntimeScaleOutRequestStore" />.
    ///
    /// The actual scale-out implementation is delegated to
    /// <see cref="IAiRuntimeScaleOutProviderSelector" />, which reuses the existing
    /// runtime instance provider system to resolve a provider supporting
    /// <see cref="IAiRuntimeScaleOutProvider" />.
    ///
    /// When a scale-out request is fulfilled, the linked shared run is requeued
    /// through <see cref="IAiScaleOutFulfilledRunRequeueService" /> so the normal
    /// shared queue pump can dispatch it to the newly available runtime capacity.
    /// </remarks>
    public sealed class AiRuntimeScaleOutRequestWatcherHostedService : BackgroundService
    {
        private const string RuntimeScaleOutRequestWatchOperation = "runtime-scale-out-request-watch";
        private readonly IAiRuntimeScaleOutRequestStore store;
        private readonly IAiRuntimeScaleOutProviderSelector providerSelector;
        private readonly IAiScaleOutFulfilledRunRequeueService fulfilledRunRequeueService;
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;
        private readonly AiRuntimeScaleOutRequestWatcherOptions options;
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeScaleOutRequestWatcherHostedService" /> class.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="providerSelector">The scale-out provider selector.</param>
        /// <param name="fulfilledRunRequeueService">The service used to requeue shared runs after scale-out fulfillment.</param>
        /// <param name="controlPlaneIdResolver">The control-plane id resolver.</param>
        /// <param name="options">The watcher options.</param>
        public AiRuntimeScaleOutRequestWatcherHostedService(
            IAiRuntimeScaleOutRequestStore store,
            IAiRuntimeScaleOutProviderSelector providerSelector,
            IAiScaleOutFulfilledRunRequeueService fulfilledRunRequeueService,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IOptions<AiRuntimeScaleOutRequestWatcherOptions> options)
            : this(
                store,
                providerSelector,
                fulfilledRunRequeueService,
                controlPlaneIdResolver,
                options,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeScaleOutRequestWatcherHostedService" /> class.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="providerSelector">The scale-out provider selector.</param>
        /// <param name="fulfilledRunRequeueService">The service used to requeue shared runs after scale-out fulfillment.</param>
        /// <param name="controlPlaneIdResolver">The control-plane id resolver.</param>
        /// <param name="options">The watcher options.</param>
        /// <param name="observer">The control-plane observer.</param>
        public AiRuntimeScaleOutRequestWatcherHostedService(
            IAiRuntimeScaleOutRequestStore store,
            IAiRuntimeScaleOutProviderSelector providerSelector,
            IAiScaleOutFulfilledRunRequeueService fulfilledRunRequeueService,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IOptions<AiRuntimeScaleOutRequestWatcherOptions> options,
            IAiControlPlaneObserver observer)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.providerSelector = providerSelector ?? throw new ArgumentNullException(nameof(providerSelector));
            this.fulfilledRunRequeueService = fulfilledRunRequeueService ?? throw new ArgumentNullException(nameof(fulfilledRunRequeueService));
            this.controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            if (!this.options.Enabled)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await this.ProcessCycleAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(this.options.Interval, stoppingToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Processes one watcher cycle.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task ProcessCycleAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await this.ResolveControlPlaneIdAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                if (this.options.IgnoreWhenControlPlaneIdMissing)
                {
                    return;
                }

                throw new InvalidOperationException(
                    "Scale-out request watcher control-plane id cannot be resolved.");
            }

            var pendingRequests =
                await this.store
                    .ListPendingAsync(
                        new AiRuntimeScaleOutRequestQuery
                        {
                            ControlPlaneId = controlPlaneId,
                            MaxResults = this.options.MaxRequestsPerCycle
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            foreach (var request in pendingRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await this.ProcessRequestAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Processes one pending scale-out request.
        /// </summary>
        /// <param name="request">The pending scale-out request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ProcessRequestAsync(
            AiRuntimeScaleOutRequestRecord request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc = DateTimeOffset.UtcNow;

            await this.RecordScaleOutWatcherEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["watcherId"] = this.options.WatcherId,
                        ["rejectOnProviderFailure"] = this.options.RejectOnProviderFailure,
                        ["maxRequestsPerCycle"] = this.options.MaxRequestsPerCycle
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var observed =
                    await this.store
                        .MarkObservedAsync(
                            request.RequestId,
                            this.options.WatcherId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (!observed)
                {
                    await this.RecordScaleOutWatcherEventAsync(
                            AiControlPlaneEventType.OperationFailed,
                            request,
                            AiControlPlaneOperationOutcome.Denied,
                            "scale-out-request-not-observed",
                            null,
                            CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow),
                            new Dictionary<string, object?>
                            {
                                ["watcherId"] = this.options.WatcherId,
                                ["observed"] = false
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                var providerRequest =
                    await this.CreateProviderRequestAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                var providerResult =
                    await this.providerSelector
                        .RequestScaleOutAsync(
                            providerRequest,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (providerResult.Success)
                {
                    await this.ProcessSuccessfulProviderResultAsync(
                            request,
                            providerResult,
                            startedAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                await this.ProcessFailedProviderResultAsync(
                        request,
                        providerResult,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (this.options.RejectOnProviderFailure)
                {
                    await this.store
                        .MarkRejectedAsync(
                            request.RequestId,
                            this.options.WatcherId,
                            exception.Message,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await this.RecordScaleOutWatcherEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        null,
                        CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow),
                        new Dictionary<string, object?>
                        {
                            ["watcherId"] = this.options.WatcherId,
                            ["rejectOnProviderFailure"] = this.options.RejectOnProviderFailure,
                            ["storeMarkedRejected"] = this.options.RejectOnProviderFailure,
                            ["exception.type"] = exception.GetType().FullName,
                            ["exception.message"] = exception.Message,
                            ["requeueSucceeded"] = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!this.options.RejectOnProviderFailure)
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Processes a successful provider result.
        /// </summary>
        /// <param name="request">The pending scale-out request.</param>
        /// <param name="providerResult">The provider result.</param>
        /// <param name="startedAtUtc">The start timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ProcessSuccessfulProviderResultAsync(
            AiRuntimeScaleOutRequestRecord request,
            AiRuntimeScaleOutProviderResult providerResult,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            var fulfilledRuntimeInstanceId =
                providerResult.RuntimeInstanceId;

            if (string.IsNullOrWhiteSpace(fulfilledRuntimeInstanceId))
            {
                var rejectionReason =
                    providerResult.FailureReason ??
                    providerResult.Message ??
                    "Scale-out provider returned success without a fulfilled runtime instance id.";

                await this.store
                    .MarkRejectedAsync(
                        request.RequestId,
                        this.options.WatcherId,
                        rejectionReason,
                        cancellationToken)
                    .ConfigureAwait(false);

                await this.RecordScaleOutWatcherEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        AiControlPlaneOperationOutcome.CompletedWithIssues,
                        rejectionReason,
                        null,
                        CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow),
                        new Dictionary<string, object?>
                        {
                            ["watcherId"] = this.options.WatcherId,
                            ["observed"] = true,
                            ["providerSuccess"] = true,
                            ["providerRejected"] = providerResult.Rejected,
                            ["providerMessage"] = providerResult.Message,
                            ["providerFailureReason"] = providerResult.FailureReason,
                            ["storeMarkedRejected"] = true,
                            ["requeueSucceeded"] = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            var requeueResult =
                await this.fulfilledRunRequeueService
                    .RequeueAsync(
                        request,
                        fulfilledRuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (requeueResult.LinkedSharedRunFound &&
                !requeueResult.RequeueSucceeded)
            {
                var failureReason =
                    requeueResult.Reason ??
                    $"Scale-out request was fulfilled by runtime '{fulfilledRuntimeInstanceId}', but linked shared run '{request.SharedRunId}' was not requeued.";

                await this.store
                    .MarkRejectedAsync(
                        request.RequestId,
                        this.options.WatcherId,
                        failureReason,
                        cancellationToken)
                    .ConfigureAwait(false);

                await this.RecordScaleOutWatcherEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        AiControlPlaneOperationOutcome.CompletedWithIssues,
                        failureReason,
                        fulfilledRuntimeInstanceId,
                        CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow),
                        new Dictionary<string, object?>
                        {
                            ["watcherId"] = this.options.WatcherId,
                            ["observed"] = true,
                            ["providerSuccess"] = true,
                            ["providerRejected"] = providerResult.Rejected,
                            ["providerMessage"] = providerResult.Message,
                            ["providerFailureReason"] = providerResult.FailureReason,
                            ["storeMarkedFulfilled"] = false,
                            ["storeMarkedRejected"] = true,
                            ["linkedSharedRunFound"] = requeueResult.LinkedSharedRunFound,
                            ["requeueSucceeded"] = requeueResult.RequeueSucceeded,
                            ["requeueCandidateCount"] = requeueResult.CandidateCount,
                            ["requeueReason"] = requeueResult.Reason
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            await this.store
                .MarkFulfilledAsync(
                    request.RequestId,
                    this.options.WatcherId,
                    fulfilledRuntimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            await this.RecordScaleOutWatcherEventAsync(
                    AiControlPlaneEventType.OperationCompleted,
                    request,
                    AiControlPlaneOperationOutcome.Succeeded,
                    null,
                    fulfilledRuntimeInstanceId,
                    CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow),
                    new Dictionary<string, object?>
                    {
                        ["watcherId"] = this.options.WatcherId,
                        ["observed"] = true,
                        ["providerSuccess"] = true,
                        ["providerRejected"] = providerResult.Rejected,
                        ["providerMessage"] = providerResult.Message,
                        ["providerFailureReason"] = providerResult.FailureReason,
                        ["storeMarkedFulfilled"] = true,
                        ["linkedSharedRunFound"] = requeueResult.LinkedSharedRunFound,
                        ["requeueSucceeded"] = requeueResult.RequeueSucceeded,
                        ["requeueCandidateCount"] = requeueResult.CandidateCount,
                        ["requeueReason"] = requeueResult.Reason
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Processes a failed provider result.
        /// </summary>
        /// <param name="request">The pending scale-out request.</param>
        /// <param name="providerResult">The provider result.</param>
        /// <param name="startedAtUtc">The start timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ProcessFailedProviderResultAsync(
            AiRuntimeScaleOutRequestRecord request,
            AiRuntimeScaleOutProviderResult providerResult,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (this.options.RejectOnProviderFailure || providerResult.Rejected)
            {
                var rejectionReason =
                    providerResult.FailureReason ??
                    providerResult.Message ??
                    "Scale-out provider did not fulfill the request.";

                await this.store
                    .MarkRejectedAsync(
                        request.RequestId,
                        this.options.WatcherId,
                        rejectionReason,
                        cancellationToken)
                    .ConfigureAwait(false);

                await this.RecordScaleOutWatcherEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        AiControlPlaneOperationOutcome.Denied,
                        rejectionReason,
                        null,
                        CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow),
                        new Dictionary<string, object?>
                        {
                            ["watcherId"] = this.options.WatcherId,
                            ["observed"] = true,
                            ["providerSuccess"] = false,
                            ["providerRejected"] = providerResult.Rejected,
                            ["providerMessage"] = providerResult.Message,
                            ["providerFailureReason"] = providerResult.FailureReason,
                            ["storeMarkedRejected"] = true,
                            ["requeueSucceeded"] = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            await this.RecordScaleOutWatcherEventAsync(
                    AiControlPlaneEventType.OperationCompleted,
                    request,
                    AiControlPlaneOperationOutcome.CompletedWithIssues,
                    providerResult.FailureReason ?? providerResult.Message ?? "Scale-out provider did not fulfill the request.",
                    null,
                    CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow),
                    new Dictionary<string, object?>
                    {
                        ["watcherId"] = this.options.WatcherId,
                        ["observed"] = true,
                        ["providerSuccess"] = false,
                        ["providerRejected"] = providerResult.Rejected,
                        ["providerMessage"] = providerResult.Message,
                        ["providerFailureReason"] = providerResult.FailureReason,
                        ["storeMarkedRejected"] = false,
                        ["requeueSucceeded"] = false
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records a scale-out watcher control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The persisted scale-out request.</param>
        /// <param name="outcome">The optional operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="runtimeInstanceId">The optional fulfilled runtime instance identifier.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <param name="properties">The event properties.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the event has been recorded.</returns>
        private async Task RecordScaleOutWatcherEventAsync(
            AiControlPlaneEventType eventType,
            AiRuntimeScaleOutRequestRecord request,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            string? runtimeInstanceId,
            long? durationMs,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.observer.RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.Scaling,
                            Operation = RuntimeScaleOutRequestWatchOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                                    ? request.RequestId
                                    : request.CorrelationId,
                                RunId = request.SharedRunId,
                                RuntimeInstanceId = runtimeInstanceId,
                                PipelineKey = request.PipelineKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                BuildScaleOutWatcherProperties(
                                    request,
                                    runtimeInstanceId,
                                    durationMs))
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break scale-out request watching.
            }
        }

        /// <summary>
        /// Builds scale-out watcher control-plane event properties.
        /// </summary>
        /// <param name="request">The persisted scale-out request.</param>
        /// <param name="runtimeInstanceId">The optional fulfilled runtime instance identifier.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <returns>The event properties.</returns>
        private static IReadOnlyDictionary<string, object?> BuildScaleOutWatcherProperties(
            AiRuntimeScaleOutRequestRecord request,
            string? runtimeInstanceId,
            long? durationMs)
        {
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["requestId"] = request.RequestId,
                ["scaleOutRequestId"] = request.RequestId,
                ["sharedRunId"] = request.SharedRunId,
                ["controlPlaneId"] = request.ControlPlaneId,
                ["tenantId"] = request.TenantId,
                ["tenantGroupId"] = request.TenantGroupId,
                ["pipelineKey"] = request.PipelineKey,
                ["providerHint"] = request.ProviderHint,
                ["runtimeInstanceId"] = runtimeInstanceId,
                ["requestedTargetInstanceCount"] = request.RequestedTargetInstanceCount,
                ["visibleInstanceCount"] = request.VisibleInstanceCount,
                ["availableInstanceCount"] = request.AvailableInstanceCount,
                ["currentInstanceCount"] = request.CurrentInstanceCount,
                ["maxInstanceCount"] = request.MaxInstanceCount,
                ["requestedBy"] = request.RequestedBy,
                ["source"] = request.Source,
                ["reason"] = request.Reason,
                ["durationMs"] = durationMs
            };

            foreach (var item in request.Metadata)
            {
                properties[item.Key] = item.Value;
                properties[$"scaleOut.{item.Key}"] = item.Value;
            }

            return properties;
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

            foreach (var item in additionalProperties)
            {
                merged[item.Key] = item.Value;
            }

            if (properties is not null)
            {
                foreach (var item in properties)
                {
                    merged[item.Key] = item.Value;
                }
            }

            return merged;
        }

        /// <summary>
        /// Calculates duration in milliseconds.
        /// </summary>
        /// <param name="startedAtUtc">The start timestamp.</param>
        /// <param name="completedAtUtc">The completion timestamp.</param>
        /// <returns>The duration in milliseconds.</returns>
        private static long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }

        /// <summary>
        /// Resolves the logical control-plane id watched by this service.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane id.</returns>
        private async Task<string?> ResolveControlPlaneIdAsync(
            CancellationToken cancellationToken)
        {
            return await this.controlPlaneIdResolver
                .ResolveAsync(
                    new AiControlPlaneIdResolutionRequest
                    {
                        RequestedControlPlaneId = this.options.ControlPlaneId,
                        Source = "runtime-scale-out-request-watcher",
                        AllowGeneratedFallback = false
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a provider request from a persisted scale-out request record.
        /// </summary>
        /// <param name="request">The persisted scale-out request record.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The provider request.</returns>
        private async Task<AiRuntimeScaleOutProviderRequest> CreateProviderRequestAsync(
            AiRuntimeScaleOutRequestRecord request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var metadata =
                new Dictionary<string, string>(
                    request.Metadata,
                    StringComparer.OrdinalIgnoreCase);

            var controlPlaneMetadata =
                await this.controlPlaneIdResolver
                    .ResolveMetadataAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = request.ControlPlaneId,
                            Metadata = metadata,
                            Source = "runtime-scale-out-request-watcher-provider-request",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            foreach (var pair in controlPlaneMetadata)
            {
                metadata[pair.Key] = pair.Value;
            }

            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = request.RequestId,
                ControlPlaneId = request.ControlPlaneId,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                SharedRunId = request.SharedRunId,
                TenantId = request.TenantId,
                TenantGroupId = request.TenantGroupId,
                PipelineKey = request.PipelineKey,
                IsolationMode = request.IsolationMode,
                PreferDedicatedCapacity = request.PreferDedicatedCapacity,
                AllowSharedFallback = request.AllowSharedFallback,
                MaxRuntimeInstances = request.MaxRuntimeInstances,
                RuntimeInstanceIdPrefix = request.RuntimeInstanceIdPrefix,
                WorkerCountPerInstance = request.WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance = request.MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = request.LocalQueueCapacity,
                VisibleInstanceCount = request.VisibleInstanceCount,
                AvailableInstanceCount = request.AvailableInstanceCount,
                CurrentInstanceCount = request.CurrentInstanceCount,
                MaxInstanceCount = request.MaxInstanceCount,
                RequestedTargetInstanceCount = request.RequestedTargetInstanceCount,
                ProviderHint = request.ProviderHint,
                CorrelationId = request.CorrelationId,
                RequestedBy = request.RequestedBy,
                Source = request.Source,
                Reason = request.Reason,
                Metadata = metadata
            };
        }
    }
}