using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

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
        /// <summary>
        /// The scale-out request store.
        /// </summary>
        private readonly IAiRuntimeScaleOutRequestStore store;

        /// <summary>
        /// The scale-out provider selector.
        /// </summary>
        private readonly IAiRuntimeScaleOutProviderSelector providerSelector;

        /// <summary>
        /// The service used to requeue shared runs after scale-out fulfillment.
        /// </summary>
        private readonly IAiScaleOutFulfilledRunRequeueService fulfilledRunRequeueService;

        /// <summary>
        /// The control-plane id resolver.
        /// </summary>
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;

        /// <summary>
        /// The watcher options.
        /// </summary>
        private readonly AiRuntimeScaleOutRequestWatcherOptions options;

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
        {
            this.store =
                store
                ?? throw new ArgumentNullException(nameof(store));

            this.providerSelector =
                providerSelector
                ?? throw new ArgumentNullException(nameof(providerSelector));

            this.fulfilledRunRequeueService =
                fulfilledRunRequeueService
                ?? throw new ArgumentNullException(nameof(fulfilledRunRequeueService));

            this.controlPlaneIdResolver =
                controlPlaneIdResolver
                ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));

            this.options =
                options?.Value
                ?? throw new ArgumentNullException(nameof(options));
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
                await this.ProcessCycleAsync(
                        stoppingToken)
                    .ConfigureAwait(false);

                await Task
                    .Delay(
                        this.options.Interval,
                        stoppingToken)
                    .ConfigureAwait(false);
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
                await this.ResolveControlPlaneIdAsync(
                        cancellationToken)
                    .ConfigureAwait(false);

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

                await this.ProcessRequestAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
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

            var observed =
                await this.store
                    .MarkObservedAsync(
                        request.RequestId,
                        this.options.WatcherId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!observed)
            {
                return;
            }

            try
            {
                var providerResult =
                    await this.providerSelector
                        .RequestScaleOutAsync(
                            CreateProviderRequest(request),
                            cancellationToken)
                        .ConfigureAwait(false);

                if (providerResult.Success)
                {
                    var fulfilledRuntimeInstanceId =
                        providerResult.RuntimeInstanceId;

                    if (string.IsNullOrWhiteSpace(fulfilledRuntimeInstanceId))
                    {
                        await this.store
                            .MarkRejectedAsync(
                                request.RequestId,
                                this.options.WatcherId,
                                providerResult.FailureReason ??
                                providerResult.Message ??
                                "Scale-out provider returned success without a fulfilled runtime instance id.",
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

                    await this.fulfilledRunRequeueService
                        .RequeueAsync(
                            request,
                            fulfilledRuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                if (this.options.RejectOnProviderFailure || providerResult.Rejected)
                {
                    await this.store
                        .MarkRejectedAsync(
                            request.RequestId,
                            this.options.WatcherId,
                            providerResult.FailureReason ??
                            providerResult.Message ??
                            "Scale-out provider did not fulfill the request.",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (this.options.RejectOnProviderFailure)
            {
                await this.store
                    .MarkRejectedAsync(
                        request.RequestId,
                        this.options.WatcherId,
                        exception.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resolves the control-plane id watched by this service.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved control-plane id.</returns>
        private async Task<string?> ResolveControlPlaneIdAsync(
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(this.options.ControlPlaneId))
            {
                return this.options.ControlPlaneId;
            }

            return await this.controlPlaneIdResolver
                .ResolveAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a provider request from a persisted scale-out request record.
        /// </summary>
        /// <param name="request">The persisted scale-out request record.</param>
        /// <returns>The provider request.</returns>
        private static AiRuntimeScaleOutProviderRequest CreateProviderRequest(
            AiRuntimeScaleOutRequestRecord request)
        {
            ArgumentNullException.ThrowIfNull(request);

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

                Metadata = new Dictionary<string, string>(
                    request.Metadata,
                    StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}