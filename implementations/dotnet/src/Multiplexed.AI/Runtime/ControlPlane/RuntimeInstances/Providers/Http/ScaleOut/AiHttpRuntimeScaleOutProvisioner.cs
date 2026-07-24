using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using System.Collections.Concurrent;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut
{
    /// <summary>
    /// Provisions HTTP runtime capacity for HTTP provider scale-out requests.
    /// </summary>
    /// <remarks>
    /// This provisioner resolves tenant-aware runtime settings from <see cref="IAiTenantRuntimeSettingsProvider" />.
    ///
    /// Tenant runtime settings are treated as the source of truth for runtime prefix,
    /// worker count, queue capacity, maximum concurrency, maximum instance count, and isolation flags.
    ///
    /// Values carried by <see cref="AiRuntimeScaleOutProviderRequest" /> are kept as compatibility
    /// fallbacks for older request paths. HTTP scale-out options remain provider technical defaults only.
    /// </remarks>
    public sealed class AiHttpRuntimeScaleOutProvisioner : IAiHttpRuntimeScaleOutProvisioner
    {
        /// <summary>
        /// HTTP provider name.
        /// </summary>
        private const string ProviderName = "http";

        /// <summary>
        /// Default HTTP runtime instance id prefix used only as a technical fallback.
        /// </summary>
        private const string DefaultRuntimeInstanceIdPrefix = "http-runtime";

        /// <summary>
        /// Default HTTP runtime endpoint used only as a technical fallback.
        /// </summary>
        private const string DefaultEndpointTemplate = "http://localhost";

        /// <summary>
        /// Default worker count used only when neither tenant settings nor the request provide one.
        /// </summary>
        private const int DefaultWorkerCountPerInstance = 1;

        /// <summary>
        /// Default maximum concurrent run count used only when neither tenant settings nor the request provide one.
        /// </summary>
        private const int DefaultMaxConcurrentRunsPerInstance = 1;

        /// <summary>
        /// Default local queue capacity used only when neither tenant settings nor the request provide one.
        /// </summary>
        private const int DefaultQueueCapacity = 100;

        /// <summary>
        /// Metadata key carrying the runtime instance id that replacement scale-out must not reuse.
        /// </summary>
        private const string ScaleOutExcludedRuntimeInstanceIdMetadataKey =
            "scaleout.excludedRuntimeInstanceId";

        /// <summary>
        /// Metadata key carrying the runtime instance id being replaced.
        /// </summary>
        private const string ScaleOutReplacementForRuntimeInstanceIdMetadataKey =
            "scaleout.replacementForRuntimeInstanceId";

        /// <summary>
        /// Recovery metadata key carrying the failed runtime instance id.
        /// </summary>
        private const string RecoveryFailedRuntimeInstanceIdMetadataKey =
            "recovery.failedRuntimeInstanceId";

        private static readonly ConcurrentDictionary<string, byte> RuntimeInstanceIdReservations =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly IAiRuntimeInstanceRegistry registry;
        private readonly IAiRuntimeInstanceCapacityStore capacityStore;
        private readonly IAiRuntimeHostManager runtimeHostManager;
        private readonly IAiRuntimeHostProcessControl? runtimeHostProcessControl;
        private readonly IAiRuntimeInstanceReadinessWaiter readinessWaiter;
        private readonly IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider;
        private readonly AiHttpRuntimeScaleOutOptions options;
        private readonly ILogger<AiHttpRuntimeScaleOutProvisioner> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiHttpRuntimeScaleOutProvisioner"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="capacityStore">The runtime instance capacity store.</param>
        /// <param name="runtimeHostManager">The runtime host manager.</param>
        /// <param name="readinessWaiter">The runtime instance readiness waiter.</param>
        /// <param name="tenantRuntimeSettingsProvider">The tenant runtime settings provider.</param>
        /// <param name="options">The HTTP scale-out technical options.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="runtimeHostProcessControl">
        /// Optional process-host lifecycle control used only to clean up a process host
        /// when provider-level readiness fails. Kubernetes and non-process host creation
        /// modes are never affected by this dependency.
        /// </param>
        public AiHttpRuntimeScaleOutProvisioner(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeHostManager runtimeHostManager,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter,
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider,
            IOptions<AiHttpRuntimeScaleOutOptions> options,
            ILogger<AiHttpRuntimeScaleOutProvisioner> logger,
            IAiRuntimeHostProcessControl? runtimeHostProcessControl = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.capacityStore = capacityStore ?? throw new ArgumentNullException(nameof(capacityStore));
            this.runtimeHostManager = runtimeHostManager ?? throw new ArgumentNullException(nameof(runtimeHostManager));
            this.runtimeHostProcessControl = runtimeHostProcessControl;
            this.readinessWaiter = readinessWaiter ?? throw new ArgumentNullException(nameof(readinessWaiter));
            this.tenantRuntimeSettingsProvider = tenantRuntimeSettingsProvider ?? throw new ArgumentNullException(nameof(tenantRuntimeSettingsProvider));

            ArgumentNullException.ThrowIfNull(options);

            this.options = options.Value ?? throw new ArgumentException("HTTP runtime scale-out options must be provided.", nameof(options));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeScaleOutProviderResult> ProvisionAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc = DateTimeOffset.UtcNow;

            if (!this.options.Enabled)
            {
                return CreateRejectedResult(request, "http-runtime-scaleout-disabled", "HTTP runtime scale-out is disabled.");
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return CreateRejectedResult(request, "http-runtime-scaleout-request-id-missing", "HTTP runtime scale-out request id is missing.");
            }

            if (string.IsNullOrWhiteSpace(request.ControlPlaneId))
            {
                return CreateRejectedResult(request, "http-runtime-scaleout-control-plane-id-missing", "HTTP runtime scale-out control-plane id is missing.");
            }

            var context =
                await this.CreateProvisioningContextAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            try
            {
                if (IsHostManagerMode(this.options.Mode))
                {
                    return await this.ProvisionWithHostManagerAsync(request, context, startedAtUtc, cancellationToken).ConfigureAwait(false);
                }

                this.logger.LogInformation(
                    "HTTP SCALE-OUT PROVISION START RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} TenantId={TenantId} TenantGroupId={TenantGroupId} IsolationMode={IsolationMode} WorkerCount={WorkerCount} MaxConcurrentRuns={MaxConcurrentRuns} QueueCapacity={QueueCapacity}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    context.Endpoint,
                    context.TenantId,
                    context.TenantGroupId,
                    context.IsolationMode,
                    context.WorkerCount,
                    context.MaxConcurrentRuns,
                    context.QueueCapacity);

                await this.registry.RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = context.RuntimeInstanceId,
                        ControlPlaneId = request.ControlPlaneId,
                        ControlPlaneHostId = $"http-scaleout-{request.ControlPlaneId}",
                        HostId = $"http-host-{context.RuntimeInstanceId}",
                        RuntimeId = context.RuntimeInstanceId,
                        Role = AiRuntimeInstanceRole.Runtime,
                        WorkerCount = context.WorkerCount,
                        MaxConcurrentRuns = context.MaxConcurrentRuns,
                        QueueCapacity = context.QueueCapacity,
                        RegisteredAtUtc = startedAtUtc,
                        Metadata = context.Metadata
                    },
                    cancellationToken).ConfigureAwait(false);

                await this.capacityStore.PublishAsync(
                    new AiRuntimeInstanceCapacityDescriptor
                    {
                        RuntimeInstanceId = context.RuntimeInstanceId,
                        ControlPlaneId = request.ControlPlaneId,
                        ControlPlaneHostId = $"http-scaleout-{request.ControlPlaneId}",
                        Role = AiRuntimeInstanceRole.Runtime,
                        Status = AiRuntimeInstanceStatus.Ready,
                        WorkerCount = context.WorkerCount,
                        ActiveWorkerCount = 0,
                        AvailableWorkerCount = context.WorkerCount,
                        MaxWorkersPerRun = context.WorkerCount,
                        MinWorkersRequiredPerRun = 1,
                        QueuedRunCount = 0,
                        RunningRunCount = 0,
                        ActiveRunCount = 0,
                        MaxConcurrentRuns = context.MaxConcurrentRuns,
                        MaxRunSlots = context.MaxConcurrentRuns,
                        AvailableRunSlots = context.MaxConcurrentRuns,
                        ReservedRunSlots = 0,
                        EffectiveAvailableRunSlots = context.MaxConcurrentRuns,
                        IsQueuePaused = false,
                        CanAcceptRun = true,
                        LastHeartbeatAtUtc = startedAtUtc,
                        Metadata = context.Metadata
                    },
                    cancellationToken).ConfigureAwait(false);

                this.logger.LogInformation(
                    "HTTP SCALE-OUT PROVISION FULFILLED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    context.Endpoint);

                return CreateFulfilledResult(
                    request,
                    context.RuntimeInstanceId,
                    $"http-scaleout-{request.RequestId}",
                    "HTTP runtime scale-out request was fulfilled.",
                    context.Metadata);
            }
            finally
            {
                if (this.ShouldReserveRuntimeInstanceId())
                {
                    ReleaseRuntimeInstanceIdReservation(context.RuntimeInstanceId);
                }
            }
        }

        /// <summary>
        /// Provisions HTTP runtime capacity by delegating runtime lifecycle to the runtime host manager.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="context">The resolved provisioning context.</param>
        /// <param name="startedAtUtc">The UTC timestamp when provisioning started.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out provider result.</returns>
        private async Task<AiRuntimeScaleOutProviderResult> ProvisionWithHostManagerAsync(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeScaleOutProvisioningContext context,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            if (!this.ShouldUseProcessHostStartupGate())
            {
                return await this.ProvisionWithHostManagerCoreAsync(
                        request,
                        context,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var concurrencyKey =
                this.ResolveProcessHostStartupConcurrencyKey();

            var requestedMaxConcurrency =
                this.options.MaxConcurrentProcessHostStartups;

            this.logger.LogInformation(
                "HTTP PROCESS HOST STARTUP GATE WAIT RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} ConcurrencyKey={ConcurrencyKey} RequestedMaxConcurrency={RequestedMaxConcurrency}",
                request.RequestId,
                request.SharedRunId,
                context.RuntimeInstanceId,
                concurrencyKey,
                requestedMaxConcurrency);

            var lease =
                await AiHttpProcessHostStartupConcurrencyGate
                    .AcquireAsync(
                        concurrencyKey,
                        requestedMaxConcurrency,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (lease.RequestedMaxConcurrency != lease.EffectiveMaxConcurrency)
            {
                this.logger.LogWarning(
                    "HTTP PROCESS HOST STARTUP GATE LIMIT MISMATCH RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} ConcurrencyKey={ConcurrencyKey} RequestedMaxConcurrency={RequestedMaxConcurrency} EffectiveMaxConcurrency={EffectiveMaxConcurrency}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    lease.ConcurrencyKey,
                    lease.RequestedMaxConcurrency,
                    lease.EffectiveMaxConcurrency);
            }

            var acquiredAtUtc = DateTimeOffset.UtcNow;

            this.logger.LogInformation(
                "HTTP PROCESS HOST STARTUP GATE ACQUIRED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} ConcurrencyKey={ConcurrencyKey} EffectiveMaxConcurrency={EffectiveMaxConcurrency} ActiveCount={ActiveCount} WaitingCount={WaitingCount} WaitDurationMs={WaitDurationMs}",
                request.RequestId,
                request.SharedRunId,
                context.RuntimeInstanceId,
                lease.ConcurrencyKey,
                lease.EffectiveMaxConcurrency,
                lease.ActiveCount,
                lease.WaitingCount,
                lease.WaitDuration.TotalMilliseconds);

            try
            {
                return await this.ProvisionWithHostManagerCoreAsync(
                        request,
                        context,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                lease.Dispose();

                this.logger.LogInformation(
                    "HTTP PROCESS HOST STARTUP GATE RELEASED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} ConcurrencyKey={ConcurrencyKey} EffectiveMaxConcurrency={EffectiveMaxConcurrency} ActiveCount={ActiveCount} WaitingCount={WaitingCount} HeldDurationMs={HeldDurationMs}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    lease.ConcurrencyKey,
                    lease.EffectiveMaxConcurrency,
                    lease.ActiveCount,
                    lease.WaitingCount,
                    (DateTimeOffset.UtcNow - acquiredAtUtc).TotalMilliseconds);
            }
        }

        /// <summary>
        /// Executes host-manager startup and readiness after any process-host startup gate has been acquired.
        /// </summary>
        private async Task<AiRuntimeScaleOutProviderResult> ProvisionWithHostManagerCoreAsync(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeScaleOutProvisioningContext context,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            var maxAttempts =
                this.ResolveProcessHostStartupMaxAttempts();

            AiRuntimeScaleOutProviderResult? lastResult = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.logger.LogInformation(
                    "HTTP PROCESS HOST STARTUP ATTEMPT RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    attempt,
                    maxAttempts);

                var attemptResult =
                    await this.ProvisionWithHostManagerAttemptAsync(
                            request,
                            context,
                            startedAtUtc,
                            attempt,
                            maxAttempts,
                            cancellationToken)
                        .ConfigureAwait(false);

                lastResult = attemptResult.Result;

                if (attemptResult.Result.Success ||
                    !attemptResult.CanRetryProcessRegistrationFailure ||
                    attempt >= maxAttempts)
                {
                    return attemptResult.Result;
                }

                this.logger.LogWarning(
                    "HTTP PROCESS HOST STARTUP RETRY RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} CompletedAttempt={CompletedAttempt} NextAttempt={NextAttempt} MaxAttempts={MaxAttempts} FailureReason={FailureReason}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    attempt,
                    attempt + 1,
                    maxAttempts,
                    attemptResult.Result.FailureReason);
            }

            return lastResult ??
                   CreateRejectedResult(
                       request,
                       "http-process-host-startup-attempts-exhausted",
                       "HTTP process-host startup attempts were exhausted.");
        }

        /// <summary>
        /// Executes one HTTP host-manager process startup and readiness attempt.
        /// </summary>
        private async Task<HttpProcessHostProvisionAttemptResult> ProvisionWithHostManagerAttemptAsync(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeScaleOutProvisioningContext context,
            DateTimeOffset startedAtUtc,
            int attempt,
            int maxAttempts,
            CancellationToken cancellationToken)
        {
            this.logger.LogInformation(
                "HTTP SCALE-OUT HOST-MANAGER START RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} HostCreationMode={HostCreationMode} TenantId={TenantId} TenantGroupId={TenantGroupId} IsolationMode={IsolationMode} WorkerCount={WorkerCount} MaxConcurrentRuns={MaxConcurrentRuns} QueueCapacity={QueueCapacity} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                request.RequestId,
                request.SharedRunId,
                context.RuntimeInstanceId,
                context.Endpoint,
                this.options.HostCreationMode,
                context.TenantId,
                context.TenantGroupId,
                context.IsolationMode,
                context.WorkerCount,
                context.MaxConcurrentRuns,
                context.QueueCapacity,
                attempt,
                maxAttempts);

            var startResult =
                await this.runtimeHostManager.StartRuntimeAsync(
                    new AiRuntimeHostStartRequest
                    {
                        RequestId = request.RequestId,
                        ControlPlaneId = request.ControlPlaneId,
                        ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                        RuntimeInstanceId = context.RuntimeInstanceId,
                        RuntimeInstanceIdPrefix = context.RuntimeInstanceIdPrefix,
                        ProviderName = ProviderName,
                        TransportName = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                        TransportEndpoint = context.Endpoint,
                        HostCreationMode = this.options.HostCreationMode,
                        TenantId = context.TenantId,
                        TenantGroupId = context.TenantGroupId,
                        IsolationMode = context.IsolationMode.ToString(),
                        PreferDedicatedCapacity = context.PreferDedicatedCapacity,
                        AllowSharedFallback = context.AllowSharedFallback,
                        WorkerCountPerInstance = context.WorkerCount,
                        MaxConcurrentRunsPerInstance = context.MaxConcurrentRuns,
                        LocalQueueCapacity = context.QueueCapacity,
                        MaxRuntimeInstances = context.MaxRuntimeInstances,
                        Metadata = context.Metadata
                    },
                    cancellationToken).ConfigureAwait(false);

            this.logger.LogInformation(
                "HTTP SCALE-OUT HOST-MANAGER START RESULT RequestId={RequestId} SharedRunId={SharedRunId} Success={Success} RuntimeInstanceId={RuntimeInstanceId} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint} FailureReason={FailureReason} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                request.RequestId,
                request.SharedRunId,
                startResult.Success,
                startResult.RuntimeInstanceId,
                startResult.ProviderName,
                startResult.TransportName,
                startResult.TransportEndpoint,
                startResult.FailureReason ?? "(none)",
                attempt,
                maxAttempts);

            if (!startResult.Success)
            {
                this.logger.LogWarning(
                    "HTTP SCALE-OUT HOST-MANAGER REJECTED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    this.options.HostCreationMode,
                    startResult.FailureReason,
                    attempt,
                    maxAttempts);

                return new HttpProcessHostProvisionAttemptResult(
                    CreateRejectedResult(
                        request,
                        startResult.FailureReason ?? "runtime-host-start-failed",
                        "HTTP runtime scale-out host manager start failed."),
                    CanRetryProcessRegistrationFailure: false);
            }

            var excludedRuntimeInstanceId =
                ResolveExcludedRuntimeInstanceId(
                    context.Metadata);

            var fulfilledRuntimeInstanceId =
                !string.IsNullOrWhiteSpace(startResult.RuntimeInstanceId) &&
                !string.Equals(
                    startResult.RuntimeInstanceId,
                    excludedRuntimeInstanceId,
                    StringComparison.Ordinal)
                    ? startResult.RuntimeInstanceId
                    : context.RuntimeInstanceId;

            var fulfilledTransportEndpoint =
                !string.IsNullOrWhiteSpace(startResult.TransportEndpoint)
                    ? startResult.TransportEndpoint
                    : context.Endpoint;

            if (string.IsNullOrWhiteSpace(fulfilledRuntimeInstanceId))
            {
                this.logger.LogWarning(
                    "HTTP SCALE-OUT HOST-MANAGER REJECTED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    this.options.HostCreationMode,
                    "runtime-host-started-without-runtime-instance-id",
                    attempt,
                    maxAttempts);

                return new HttpProcessHostProvisionAttemptResult(
                    CreateRejectedResult(
                        request,
                        "runtime-host-started-without-runtime-instance-id",
                        "HTTP runtime scale-out host manager returned success without a runtime instance id."),
                    CanRetryProcessRegistrationFailure: false);
            }

            if (string.Equals(
                    fulfilledRuntimeInstanceId,
                    excludedRuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                this.logger.LogWarning(
                    "HTTP SCALE-OUT HOST-MANAGER REJECTED EXCLUDED RUNTIME RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} ExcludedRuntimeInstanceId={ExcludedRuntimeInstanceId} HostCreationMode={HostCreationMode} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                    request.RequestId,
                    request.SharedRunId,
                    fulfilledRuntimeInstanceId,
                    excludedRuntimeInstanceId,
                    this.options.HostCreationMode,
                    attempt,
                    maxAttempts);

                return new HttpProcessHostProvisionAttemptResult(
                    CreateRejectedResult(
                        request,
                        "runtime-host-started-with-excluded-runtime-instance-id",
                        "HTTP runtime scale-out host manager returned the excluded failed runtime instance id for a recovery replacement."),
                    CanRetryProcessRegistrationFailure: false);
            }

            if (this.options.RequireReadiness)
            {
                var requireTransportEndpoint =
                    this.ShouldRequireTransportEndpointForReadiness();

                this.logger.LogInformation(
                    "HTTP SCALE-OUT HOST-MANAGER READINESS WAIT RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} RequireTransportEndpoint={RequireTransportEndpoint} TransportEndpoint={TransportEndpoint} TimeoutSeconds={TimeoutSeconds} PollIntervalMilliseconds={PollIntervalMilliseconds} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                    request.RequestId,
                    request.SharedRunId,
                    fulfilledRuntimeInstanceId,
                    this.options.HostCreationMode,
                    requireTransportEndpoint,
                    fulfilledTransportEndpoint,
                    Math.Max(1, this.options.ReadinessTimeoutSeconds),
                    Math.Max(1, this.options.ReadinessPollIntervalMilliseconds),
                    attempt,
                    maxAttempts);

                var readinessResult =
                    await this.readinessWaiter.WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            ControlPlaneId = request.ControlPlaneId,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            RuntimeInstanceId = fulfilledRuntimeInstanceId,
                            ProviderName = ProviderName,
                            TransportName = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                            TransportEndpoint = fulfilledTransportEndpoint,
                            RequireTransportEndpoint = requireTransportEndpoint,
                            Timeout = TimeSpan.FromSeconds(Math.Max(1, this.options.ReadinessTimeoutSeconds)),
                            PollInterval = TimeSpan.FromMilliseconds(Math.Max(1, this.options.ReadinessPollIntervalMilliseconds))
                        },
                        cancellationToken).ConfigureAwait(false);

                this.logger.LogInformation(
                    "HTTP SCALE-OUT HOST-MANAGER READINESS RESULT RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} Success={Success} TimedOut={TimedOut} FailureReason={FailureReason} TransportEndpoint={TransportEndpoint} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                    request.RequestId,
                    request.SharedRunId,
                    readinessResult.RuntimeInstanceId,
                    this.options.HostCreationMode,
                    readinessResult.Success,
                    readinessResult.TimedOut,
                    readinessResult.FailureReason ?? "(none)",
                    readinessResult.TransportEndpoint ?? "(null)",
                    attempt,
                    maxAttempts);

                if (!readinessResult.Success)
                {
                    this.logger.LogWarning(
                        "HTTP SCALE-OUT HOST-MANAGER READINESS FAILED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason} TimedOut={TimedOut} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                        request.RequestId,
                        request.SharedRunId,
                        fulfilledRuntimeInstanceId,
                        this.options.HostCreationMode,
                        readinessResult.FailureReason,
                        readinessResult.TimedOut,
                        attempt,
                        maxAttempts);

                    var cleanupSucceeded =
                        await this.TryCleanupFailedProcessHostAsync(
                                request,
                                fulfilledRuntimeInstanceId)
                            .ConfigureAwait(false);

                    var canRetry =
                        cleanupSucceeded &&
                        attempt < maxAttempts &&
                        this.IsRetryableProcessRegistrationFailure(
                            readinessResult.FailureReason);

                    return new HttpProcessHostProvisionAttemptResult(
                        CreateRejectedResult(
                            request,
                            readinessResult.FailureReason ?? "runtime-readiness-failed",
                            "HTTP runtime scale-out readiness check failed."),
                        canRetry);
                }

                if (!string.IsNullOrWhiteSpace(readinessResult.RuntimeInstanceId))
                {
                    fulfilledRuntimeInstanceId =
                        readinessResult.RuntimeInstanceId;
                }

                if (!string.IsNullOrWhiteSpace(readinessResult.TransportEndpoint))
                {
                    fulfilledTransportEndpoint =
                        readinessResult.TransportEndpoint;
                }

                if (string.Equals(
                        fulfilledRuntimeInstanceId,
                        excludedRuntimeInstanceId,
                        StringComparison.Ordinal))
                {
                    this.logger.LogWarning(
                        "HTTP SCALE-OUT HOST-MANAGER READINESS RETURNED EXCLUDED RUNTIME RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} ExcludedRuntimeInstanceId={ExcludedRuntimeInstanceId} HostCreationMode={HostCreationMode} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                        request.RequestId,
                        request.SharedRunId,
                        fulfilledRuntimeInstanceId,
                        excludedRuntimeInstanceId,
                        this.options.HostCreationMode,
                        attempt,
                        maxAttempts);

                    await this.TryCleanupFailedProcessHostAsync(
                            request,
                            fulfilledRuntimeInstanceId)
                        .ConfigureAwait(false);

                    return new HttpProcessHostProvisionAttemptResult(
                        CreateRejectedResult(
                            request,
                            "runtime-readiness-returned-excluded-runtime-instance-id",
                            "HTTP runtime readiness returned the excluded failed runtime instance id for a recovery replacement."),
                        CanRetryProcessRegistrationFailure: false);
                }
            }

            var metadata =
                CreateFulfilledHostManagerMetadata(
                    request,
                    context,
                    startResult,
                    fulfilledRuntimeInstanceId,
                    fulfilledTransportEndpoint);

            metadata["processHost.startupAttempt"] = attempt.ToString();
            metadata["processHost.startupMaxAttempts"] = maxAttempts.ToString();

            this.logger.LogInformation(
                "HTTP SCALE-OUT HOST-MANAGER FULFILLED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} HostCreationMode={HostCreationMode} DurationMs={DurationMs} Attempt={Attempt} MaxAttempts={MaxAttempts}",
                request.RequestId,
                request.SharedRunId,
                fulfilledRuntimeInstanceId,
                fulfilledTransportEndpoint,
                this.options.HostCreationMode,
                (DateTimeOffset.UtcNow - startedAtUtc).TotalMilliseconds,
                attempt,
                maxAttempts);

            return new HttpProcessHostProvisionAttemptResult(
                CreateFulfilledResult(
                    request,
                    fulfilledRuntimeInstanceId,
                    $"http-host-manager-scaleout-{request.RequestId}",
                    "HTTP runtime scale-out request was fulfilled by the runtime host manager.",
                    metadata),
                CanRetryProcessRegistrationFailure: false);
        }

        /// <summary>
        /// Resolves the bounded number of process-host startup attempts.
        /// </summary>
        private int ResolveProcessHostStartupMaxAttempts()
        {
            if (this.options.HostCreationMode != AiRuntimeHostCreationMode.Process)
            {
                return 1;
            }

            return 1 +
                   Math.Clamp(
                       this.options.ProcessHostStartupRetryCount,
                       0,
                       1);
        }

        /// <summary>
        /// Determines whether the failed HTTP readiness result represents the single
        /// process-registration failure that is safe to retry.
        /// </summary>
        private bool IsRetryableProcessRegistrationFailure(
            string? failureReason)
        {
            return this.options.HostCreationMode == AiRuntimeHostCreationMode.Process &&
                   string.Equals(
                       failureReason,
                       "runtime-readiness-compatible-registry-missing",
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether process-host startup gating is enabled for the current host-manager mode.
        /// </summary>
        private bool ShouldUseProcessHostStartupGate()
        {
            return this.options.HostCreationMode == AiRuntimeHostCreationMode.Process &&
                   this.options.MaxConcurrentProcessHostStartups > 0;
        }

        /// <summary>
        /// Resolves the process-wide startup gate key.
        /// </summary>
        private string ResolveProcessHostStartupConcurrencyKey()
        {
            return string.IsNullOrWhiteSpace(this.options.ProcessHostStartupConcurrencyKey)
                ? "http-process-host-startup"
                : this.options.ProcessHostStartupConcurrencyKey.Trim();
        }

        /// <summary>
        /// Determines whether atomic runtime id reservation is required for the current mode.
        /// </summary>
        private bool ShouldReserveRuntimeInstanceId()
        {
            return IsHostManagerMode(this.options.Mode) &&
                   this.options.HostCreationMode == AiRuntimeHostCreationMode.Process;
        }

        /// <summary>
        /// Cleans up a process host that was started successfully but failed
        /// provider-level readiness.
        /// </summary>
        /// <remarks>
        /// Cleanup is intentionally restricted to process host creation. Kubernetes
        /// lifecycle remains owned by the Kubernetes host creation strategy.
        /// Cleanup is best-effort so it cannot hide the original readiness failure.
        /// </remarks>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceId">The process runtime instance id.</param>
        /// <returns>A task representing the cleanup attempt.</returns>
        private async Task<bool> TryCleanupFailedProcessHostAsync(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceId)
        {
            if (this.options.HostCreationMode != AiRuntimeHostCreationMode.Process)
            {
                return false;
            }

            if (this.runtimeHostProcessControl is null)
            {
                this.logger.LogWarning(
                    "HTTP SCALE-OUT PROCESS READINESS CLEANUP SKIPPED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Reason={Reason}",
                    request.RequestId,
                    request.SharedRunId,
                    runtimeInstanceId,
                    "process-control-unavailable");

                return false;
            }

            try
            {
                var cleaned =
                    await this.runtimeHostProcessControl
                        .KillAsync(
                            runtimeInstanceId,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                if (cleaned)
                {
                    this.logger.LogWarning(
                        "HTTP SCALE-OUT PROCESS READINESS CLEANUP COMPLETED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId}",
                        request.RequestId,
                        request.SharedRunId,
                        runtimeInstanceId);
                }
                else
                {
                    this.logger.LogWarning(
                        "HTTP SCALE-OUT PROCESS READINESS CLEANUP NOT FOUND RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId}",
                        request.RequestId,
                        request.SharedRunId,
                        runtimeInstanceId);
                }

                return cleaned;
            }
            catch (Exception exception)
            {
                this.logger.LogWarning(
                    exception,
                    "HTTP SCALE-OUT PROCESS READINESS CLEANUP FAILED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId}",
                    request.RequestId,
                    request.SharedRunId,
                    runtimeInstanceId);

                return false;
            }
        }

        /// <summary>
        /// Determines whether readiness must verify direct transport endpoint
        /// reachability.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> for direct process/attach transports;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        private bool ShouldRequireTransportEndpointForReadiness()
        {
            if (this.options.HostCreationMode == AiRuntimeHostCreationMode.Fixture)
            {
                return false;
            }

            if (this.options.HostCreationMode == AiRuntimeHostCreationMode.Kubernetes)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Creates the tenant-aware provisioning context for the scale-out request.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved provisioning context.</returns>
        private async Task<AiRuntimeScaleOutProvisioningContext> CreateProvisioningContextAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken)
        {
            var tenantSettings =
                this.tenantRuntimeSettingsProvider.GetSettings(
                    request.TenantId,
                    request.TenantGroupId);

            var tenantId =
                ResolveText(
                    request.TenantId,
                    tenantSettings.TenantId,
                    "shared");

            var tenantGroupId =
                ResolveText(
                    request.TenantGroupId,
                    tenantSettings.TenantGroupId,
                    string.Empty);

            var isolationMode =
                ResolveIsolationMode(
                    request,
                    tenantSettings);

            var preferDedicatedCapacity =
                ResolveBoolean(
                    request.PreferDedicatedCapacity,
                    tenantSettings.PreferDedicatedCapacity);

            var allowSharedFallback =
                ResolveBoolean(
                    request.AllowSharedFallback,
                    tenantSettings.AllowSharedFallback);

            var runtimeInstanceIdPrefix =
                ResolveRuntimeInstanceIdPrefix(
                    request,
                    tenantSettings);

            this.logger.LogInformation(
                "HTTP SCALE-OUT TENANT SETTINGS RESOLVED RequestId={RequestId} TenantId={TenantId} RequestedPrefix={RequestedPrefix} TenantSettingsPrefix={TenantSettingsPrefix} ResolvedPrefix={ResolvedPrefix} IsolationMode={IsolationMode} PreferDedicatedCapacity={PreferDedicatedCapacity} AllowSharedFallback={AllowSharedFallback}",
                request.RequestId,
                request.TenantId,
                request.RuntimeInstanceIdPrefix,
                tenantSettings.RuntimeInstanceIdPrefix,
                runtimeInstanceIdPrefix,
                tenantSettings.IsolationMode,
                tenantSettings.PreferDedicatedCapacity,
                tenantSettings.AllowSharedFallback);

            var workerCount =
                ResolvePositiveOrDefault(
                    tenantSettings.WorkerCountPerInstance,
                    request.WorkerCountPerInstance,
                    DefaultWorkerCountPerInstance);

            var maxConcurrentRuns =
                ResolvePositiveOrDefault(
                    tenantSettings.MaxConcurrentRunsPerInstance,
                    request.MaxConcurrentRunsPerInstance,
                    DefaultMaxConcurrentRunsPerInstance);

            var queueCapacity =
                ResolvePositiveOrDefault(
                    tenantSettings.LocalQueueCapacity,
                    request.LocalQueueCapacity,
                    DefaultQueueCapacity);

            var maxRuntimeInstances =
                ResolvePositiveOrNullableDefault(
                    tenantSettings.MaxRuntimeInstances,
                    request.MaxRuntimeInstances);

            string runtimeInstanceId;

            if (this.ShouldReserveRuntimeInstanceId())
            {
                runtimeInstanceId =
                    await this.ReserveRuntimeInstanceIdAsync(
                            request,
                            runtimeInstanceIdPrefix,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            else
            {
                runtimeInstanceId =
                    ResolveRuntimeInstanceId(
                        request,
                        runtimeInstanceIdPrefix);
            }

            try
            {
                var endpoint =
                    ResolveEndpoint(
                        request,
                        runtimeInstanceId,
                        runtimeInstanceIdPrefix);

                var metadata =
                    CreateMetadata(
                        request,
                        tenantSettings,
                        tenantId,
                        tenantGroupId,
                        isolationMode,
                        preferDedicatedCapacity,
                        allowSharedFallback,
                        runtimeInstanceId,
                        runtimeInstanceIdPrefix,
                        endpoint,
                        workerCount,
                        maxConcurrentRuns,
                        queueCapacity,
                        maxRuntimeInstances);

                return new AiRuntimeScaleOutProvisioningContext
                {
                    TenantId = tenantId,
                    TenantGroupId = tenantGroupId,
                    IsolationMode = isolationMode,
                    PreferDedicatedCapacity = preferDedicatedCapacity,
                    AllowSharedFallback = allowSharedFallback,
                    RuntimeInstanceId = runtimeInstanceId,
                    RuntimeInstanceIdPrefix = runtimeInstanceIdPrefix,
                    Endpoint = endpoint,
                    WorkerCount = workerCount,
                    MaxConcurrentRuns = maxConcurrentRuns,
                    QueueCapacity = queueCapacity,
                    MaxRuntimeInstances = maxRuntimeInstances,
                    Metadata = metadata
                };
            }
            catch
            {
                if (this.ShouldReserveRuntimeInstanceId())
                {
                    ReleaseRuntimeInstanceIdReservation(runtimeInstanceId);
                }

                throw;
            }
        }

        /// <summary>
        /// Resolves the tenant-aware runtime instance prefix from tenant settings, request, or HTTP technical options.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="tenantSettings">The tenant runtime settings.</param>
        /// <returns>The runtime instance id prefix.</returns>
        /// <remarks>
        /// Tenant runtime settings are the source of truth because they represent the resolved
        /// tenant isolation policy. The request value is only a carried copy and may still
        /// contain legacy technical defaults such as <c>runtime-instance</c>.
        /// </remarks>
        private string ResolveRuntimeInstanceIdPrefix(
            AiRuntimeScaleOutProviderRequest request,
            AiTenantRuntimeSettings tenantSettings)
        {
            if (!string.IsNullOrWhiteSpace(tenantSettings.RuntimeInstanceIdPrefix))
            {
                return tenantSettings.RuntimeInstanceIdPrefix.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.RuntimeInstanceIdPrefix))
            {
                return request.RuntimeInstanceIdPrefix.Trim();
            }

            if (!string.IsNullOrWhiteSpace(this.options.DefaultRuntimeInstanceIdPrefix))
            {
                return this.options.DefaultRuntimeInstanceIdPrefix.Trim();
            }

            return DefaultRuntimeInstanceIdPrefix;
        }

        /// <summary>
        /// Resolves the runtime instance id for the scale-out request.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceIdPrefix">The resolved runtime instance id prefix.</param>
        /// <returns>The runtime instance id.</returns>
        private static string ResolveRuntimeInstanceId(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceIdPrefix)
        {
            var excludedRuntimeInstanceId =
                ResolveExcludedRuntimeInstanceId(
                    request.Metadata);

            var requestedTargetInstanceCount =
                Convert.ToInt32(
                    request.RequestedTargetInstanceCount);

            var currentInstanceCount =
                Convert.ToInt32(
                    request.CurrentInstanceCount);

            var maxInstanceCount =
                Convert.ToInt32(
                    request.MaxInstanceCount);

            var maxRuntimeInstances =
                Convert.ToInt32(
                    request.MaxRuntimeInstances.HasValue
                        ? request.MaxRuntimeInstances.Value
                        : 0);

            var effectiveMaxRuntimeInstances =
                maxInstanceCount > 0
                    ? maxInstanceCount
                    : maxRuntimeInstances;

            var firstTarget =
                requestedTargetInstanceCount > 0
                    ? requestedTargetInstanceCount
                    : Math.Max(
                        1,
                        currentInstanceCount + 1);

            var upperBound =
                effectiveMaxRuntimeInstances > 0
                    ? Math.Max(
                        firstTarget,
                        effectiveMaxRuntimeInstances)
                    : firstTarget + 10;

            for (var target = firstTarget; target <= upperBound; target++)
            {
                var candidate =
                    $"{request.ControlPlaneId}:{runtimeInstanceIdPrefix}-{target}";

                if (!string.Equals(
                        candidate,
                        excludedRuntimeInstanceId,
                        StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return $"{request.ControlPlaneId}:{runtimeInstanceIdPrefix}-recovery-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Reserves the next free runtime instance id for the scale-out request.
        /// </summary>
        /// <remarks>
        /// Requested target instance count is a desired capacity count, not an identity suffix.
        /// Existing registry snapshots, capacity descriptors, and in-process reservations are all
        /// excluded so concurrent recovery requests cannot converge on the same runtime id.
        /// </remarks>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceIdPrefix">The runtime instance id prefix.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reserved runtime instance id.</returns>
        private async Task<string> ReserveRuntimeInstanceIdAsync(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceIdPrefix,
            CancellationToken cancellationToken)
        {
            var excludedRuntimeInstanceId =
                ResolveExcludedRuntimeInstanceId(
                    request.Metadata);

            var registrySnapshots =
                await this.registry
                    .ListAsync(
                        includeStopped: true,
                        cancellationToken)
                    .ConfigureAwait(false);

            var capacityDescriptors =
                await this.capacityStore
                    .ListAsync(cancellationToken)
                    .ConfigureAwait(false);

            var existingRuntimeInstanceIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            existingRuntimeInstanceIds.UnionWith(
                registrySnapshots.Select(snapshot => snapshot.RuntimeInstanceId));

            existingRuntimeInstanceIds.UnionWith(
                capacityDescriptors.Select(descriptor => descriptor.RuntimeInstanceId));

            var currentInstanceCount =
                Math.Max(
                    0,
                    Convert.ToInt32(request.CurrentInstanceCount));

            var requestedTargetInstanceCount =
                Math.Max(
                    0,
                    Convert.ToInt32(request.RequestedTargetInstanceCount));

            var target = Math.Max(1, currentInstanceCount + 1);

            while (target > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var candidate =
                    $"{request.ControlPlaneId}:{runtimeInstanceIdPrefix}-{target}";

                var isExcluded =
                    string.Equals(
                        candidate,
                        excludedRuntimeInstanceId,
                        StringComparison.OrdinalIgnoreCase);

                if (!isExcluded &&
                    !existingRuntimeInstanceIds.Contains(candidate) &&
                    RuntimeInstanceIdReservations.TryAdd(candidate, 0))
                {
                    this.logger.LogInformation(
                        "HTTP SCALE-OUT RUNTIME ID RESERVED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} CurrentInstanceCount={CurrentInstanceCount} RequestedTargetInstanceCount={RequestedTargetInstanceCount} ExistingRuntimeInstanceCount={ExistingRuntimeInstanceCount} ExcludedRuntimeInstanceId={ExcludedRuntimeInstanceId}",
                        request.RequestId,
                        request.SharedRunId,
                        candidate,
                        currentInstanceCount,
                        requestedTargetInstanceCount,
                        existingRuntimeInstanceIds.Count,
                        excludedRuntimeInstanceId ?? "(none)");

                    return candidate;
                }

                if (target == int.MaxValue)
                {
                    break;
                }

                target++;
            }

            for (var attempt = 0; attempt < 32; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var candidate =
                    $"{request.ControlPlaneId}:{runtimeInstanceIdPrefix}-recovery-{Guid.NewGuid():N}";

                if (!string.Equals(
                        candidate,
                        excludedRuntimeInstanceId,
                        StringComparison.OrdinalIgnoreCase) &&
                    !existingRuntimeInstanceIds.Contains(candidate) &&
                    RuntimeInstanceIdReservations.TryAdd(candidate, 0))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"No free runtime instance id could be reserved for control plane '{request.ControlPlaneId}' and prefix '{runtimeInstanceIdPrefix}'.");
        }

        /// <summary>
        /// Releases an in-process runtime instance id reservation.
        /// </summary>
        /// <param name="runtimeInstanceId">The reserved runtime instance id.</param>
        private static void ReleaseRuntimeInstanceIdReservation(
            string runtimeInstanceId)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return;
            }

            RuntimeInstanceIdReservations.TryRemove(runtimeInstanceId, out _);
        }

        /// <summary>
        /// Resolves the runtime instance id that replacement scale-out must not reuse.
        /// </summary>
        /// <param name="metadata">The scale-out metadata.</param>
        /// <returns>The excluded runtime instance id, or <c>null</c>.</returns>
        private static string? ResolveExcludedRuntimeInstanceId(
            IReadOnlyDictionary<string, string>? metadata)
        {
            return ResolveMetadataValue(
                       metadata,
                       ScaleOutExcludedRuntimeInstanceIdMetadataKey) ??
                   ResolveMetadataValue(
                       metadata,
                       ScaleOutReplacementForRuntimeInstanceIdMetadataKey) ??
                   ResolveMetadataValue(
                       metadata,
                       RecoveryFailedRuntimeInstanceIdMetadataKey);
        }

        /// <summary>
        /// Resolves a metadata value using case-insensitive key matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value, or <c>null</c>.</returns>
        private static string? ResolveMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            string key)
        {
            if (metadata is null)
            {
                return null;
            }

            if (metadata.TryGetValue(
                    key,
                    out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            foreach (var item in metadata)
            {
                if (string.Equals(
                        item.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.Value))
                {
                    return item.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves the HTTP endpoint for the newly materialized runtime instance.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="runtimeInstanceIdPrefix">The resolved runtime instance id prefix.</param>
        /// <returns>The HTTP endpoint.</returns>
        private string ResolveEndpoint(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceId,
            string runtimeInstanceIdPrefix)
        {
            var endpointTemplate =
                string.IsNullOrWhiteSpace(this.options.EndpointTemplate)
                    ? DefaultEndpointTemplate
                    : this.options.EndpointTemplate.Trim();

            return endpointTemplate
                .Replace("{runtimeInstanceId}", runtimeInstanceId, StringComparison.OrdinalIgnoreCase)
                .Replace("{tenantId}", request.TenantId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{tenantGroupId}", request.TenantGroupId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{controlPlaneId}", request.ControlPlaneId, StringComparison.OrdinalIgnoreCase)
                .Replace("{runtimeInstanceIdPrefix}", runtimeInstanceIdPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the isolation mode from the request or tenant settings.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="tenantSettings">The tenant runtime settings.</param>
        /// <returns>The resolved isolation mode.</returns>
        private static AiRuntimeInstanceIsolationMode ResolveIsolationMode(
            AiRuntimeScaleOutProviderRequest request,
            AiTenantRuntimeSettings tenantSettings)
        {
            return request.IsolationMode == default
                ? tenantSettings.IsolationMode
                : request.IsolationMode;
        }

        /// <summary>
        /// Resolves a boolean value using logical OR compatibility semantics.
        /// </summary>
        /// <param name="requestValue">The request value.</param>
        /// <param name="tenantValue">The tenant settings value.</param>
        /// <returns>The resolved boolean value.</returns>
        private static bool ResolveBoolean(
            bool requestValue,
            bool tenantValue)
        {
            return requestValue || tenantValue;
        }

        /// <summary>
        /// Resolves the first non-empty text value.
        /// </summary>
        /// <param name="first">The first candidate.</param>
        /// <param name="second">The second candidate.</param>
        /// <param name="fallback">The fallback value.</param>
        /// <returns>The resolved text.</returns>
        private static string ResolveText(
            string? first,
            string? second,
            string fallback)
        {
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first.Trim();
            }

            if (!string.IsNullOrWhiteSpace(second))
            {
                return second.Trim();
            }

            return fallback;
        }

        /// <summary>
        /// Resolves the first positive integer value from tenant settings, request, or hard default.
        /// </summary>
        /// <param name="tenantValue">The tenant settings value.</param>
        /// <param name="requestValue">The request value.</param>
        /// <param name="hardDefault">The hard fallback value.</param>
        /// <returns>The resolved positive value.</returns>
        private static int ResolvePositiveOrDefault(
            int? tenantValue,
            int? requestValue,
            int hardDefault)
        {
            if (tenantValue.HasValue && tenantValue.Value > 0)
            {
                return tenantValue.Value;
            }

            if (requestValue.HasValue && requestValue.Value > 0)
            {
                return requestValue.Value;
            }

            return hardDefault;
        }

        /// <summary>
        /// Resolves the first positive nullable integer value from tenant settings or request.
        /// </summary>
        /// <param name="tenantValue">The tenant settings value.</param>
        /// <param name="requestValue">The request value.</param>
        /// <returns>The resolved positive value, or <c>null</c> when no positive value exists.</returns>
        private static int? ResolvePositiveOrNullableDefault(
            int tenantValue,
            int? requestValue)
        {
            if (tenantValue > 0)
            {
                return tenantValue;
            }

            if (requestValue.HasValue && requestValue.Value > 0)
            {
                return requestValue.Value;
            }

            return null;
        }

        /// <summary>
        /// Determines whether the configured HTTP scale-out mode uses the runtime host manager.
        /// </summary>
        /// <param name="mode">The configured scale-out mode.</param>
        /// <returns><c>true</c> when host-manager mode is enabled; otherwise, <c>false</c>.</returns>
        private static bool IsHostManagerMode(string? mode)
        {
            return string.Equals(mode, AiHttpRuntimeScaleOutModes.HostManager, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates runtime metadata for the HTTP runtime registration and capacity descriptor.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="tenantSettings">The resolved tenant runtime settings.</param>
        /// <param name="tenantId">The resolved tenant id.</param>
        /// <param name="tenantGroupId">The resolved tenant group id.</param>
        /// <param name="isolationMode">The resolved isolation mode.</param>
        /// <param name="preferDedicatedCapacity">Whether dedicated capacity is preferred.</param>
        /// <param name="allowSharedFallback">Whether shared fallback is allowed.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="runtimeInstanceIdPrefix">The runtime instance id prefix.</param>
        /// <param name="endpoint">The HTTP endpoint.</param>
        /// <param name="workerCount">The resolved worker count.</param>
        /// <param name="maxConcurrentRuns">The resolved maximum concurrent runs.</param>
        /// <param name="queueCapacity">The resolved queue capacity.</param>
        /// <param name="maxRuntimeInstances">The resolved max runtime instances.</param>
        /// <returns>The metadata.</returns>
        private static Dictionary<string, string> CreateMetadata(
            AiRuntimeScaleOutProviderRequest request,
            AiTenantRuntimeSettings tenantSettings,
            string tenantId,
            string tenantGroupId,
            AiRuntimeInstanceIsolationMode isolationMode,
            bool preferDedicatedCapacity,
            bool allowSharedFallback,
            string runtimeInstanceId,
            string runtimeInstanceIdPrefix,
            string endpoint,
            int workerCount,
            int maxConcurrentRuns,
            int queueCapacity,
            int? maxRuntimeInstances)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            CopyMetadata(metadata, request.Metadata);
            CopyMetadata(metadata, tenantSettings.Metadata);

            metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName;
            metadata["provider.name"] = ProviderName;
            metadata["provider"] = ProviderName;

            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = runtimeInstanceId;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = endpoint;

            metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenantId;
            metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = tenantGroupId;
            metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = isolationMode.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = preferDedicatedCapacity.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = allowSharedFallback.ToString();

            metadata["runtime.maxRuntimeInstances"] = maxRuntimeInstances?.ToString() ?? string.Empty;
            metadata["runtime.instanceIdPrefix"] = runtimeInstanceIdPrefix;
            metadata["runtime.workerCountPerInstance"] = workerCount.ToString();
            metadata["runtime.maxConcurrentRunsPerInstance"] = maxConcurrentRuns.ToString();
            metadata["runtime.localQueueCapacity"] = queueCapacity.ToString();

            metadata["scaleout.provider"] = ProviderName;
            metadata["scaleout.requestId"] = request.RequestId;
            metadata["scaleout.sharedRunId"] = request.SharedRunId;
            metadata["controlPlaneId"] = request.ControlPlaneId;

            return metadata;
        }

        /// <summary>
        /// Creates fulfilled metadata for host-manager scale-out.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="context">The resolved provisioning context.</param>
        /// <param name="startResult">The runtime host start result.</param>
        /// <param name="fulfilledRuntimeInstanceId">The fulfilled runtime instance id.</param>
        /// <param name="fulfilledTransportEndpoint">The fulfilled transport endpoint.</param>
        /// <returns>The fulfilled metadata.</returns>
        private static Dictionary<string, string> CreateFulfilledHostManagerMetadata(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeScaleOutProvisioningContext context,
            AiRuntimeHostStartResult startResult,
            string fulfilledRuntimeInstanceId,
            string fulfilledTransportEndpoint)
        {
            var metadata =
                new Dictionary<string, string>(
                    startResult.Metadata ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);

            CopyMetadata(metadata, context.Metadata);

            metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName;
            metadata["provider.name"] = ProviderName;
            metadata["provider"] = ProviderName;

            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = fulfilledRuntimeInstanceId;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = fulfilledTransportEndpoint;

            metadata["scaleOutRequestId"] = request.RequestId;
            metadata["sharedRunId"] = request.SharedRunId;
            metadata["controlPlaneId"] = request.ControlPlaneId;
            metadata["hostCreation.mode"] = metadata.TryGetValue("hostCreation.mode", out var hostCreationMode)
                ? hostCreationMode
                : "HostManager";

            return metadata;
        }

        /// <summary>
        /// Copies metadata into the target dictionary.
        /// </summary>
        /// <param name="target">The target metadata dictionary.</param>
        /// <param name="source">The optional source metadata dictionary.</param>
        private static void CopyMetadata(
            IDictionary<string, string> target,
            IReadOnlyDictionary<string, string>? source)
        {
            if (source is null)
            {
                return;
            }

            foreach (var item in source.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
            {
                target[item.Key] = item.Value ?? string.Empty;
            }
        }

        /// <summary>
        /// Represents the result of one bounded HTTP process-host provisioning attempt.
        /// </summary>
        private sealed record HttpProcessHostProvisionAttemptResult(
            AiRuntimeScaleOutProviderResult Result,
            bool CanRetryProcessRegistrationFailure);

        /// <summary>
        /// Creates a fulfilled scale-out provider result.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="providerOperationId">The provider operation id.</param>
        /// <param name="message">The result message.</param>
        /// <param name="metadata">The result metadata.</param>
        /// <returns>The fulfilled scale-out provider result.</returns>
        private static AiRuntimeScaleOutProviderResult CreateFulfilledResult(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceId,
            string providerOperationId,
            string message,
            IReadOnlyDictionary<string, string> metadata)
        {
            return new AiRuntimeScaleOutProviderResult
            {
                Success = true,
                Rejected = false,
                RuntimeInstanceId = runtimeInstanceId,
                ProviderOperationId = providerOperationId,
                Message = message,
                Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
                {
                    ["scaleOutRequestId"] = request.RequestId,
                    ["sharedRunId"] = request.SharedRunId,
                    ["controlPlaneId"] = request.ControlPlaneId
                }
            };
        }

        /// <summary>
        /// Creates a rejected scale-out provider result.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="message">The message.</param>
        /// <returns>The rejected scale-out provider result.</returns>
        private static AiRuntimeScaleOutProviderResult CreateRejectedResult(
            AiRuntimeScaleOutProviderRequest request,
            string failureReason,
            string message)
        {
            return new AiRuntimeScaleOutProviderResult
            {
                Success = false,
                Rejected = true,
                RuntimeInstanceId = null,
                ProviderOperationId = string.IsNullOrWhiteSpace(request.RequestId)
                    ? "http-scaleout-rejected"
                    : $"http-scaleout-rejected-{request.RequestId}",
                FailureReason = failureReason,
                Message = message,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName,
                    ["provider.name"] = ProviderName,
                    ["provider"] = ProviderName,
                    ["scaleOutRequestId"] = request.RequestId,
                    ["sharedRunId"] = request.SharedRunId,
                    ["controlPlaneId"] = request.ControlPlaneId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId ?? string.Empty,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId ?? string.Empty
                }
            };
        }
    }
}
