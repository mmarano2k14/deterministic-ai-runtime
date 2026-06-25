using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Runtime.ControlPlane.Admission
{
    /// <summary>
    /// Default runtime implementation of the run admission controller.
    /// </summary>
    /// <remarks>
    /// This controller evaluates visible runtime instances and decides whether a run
    /// should be assigned to an instance, queued globally, trigger scale-out, or be rejected.
    ///
    /// Important:
    /// This class does not enqueue runs, modify local queues, execute DAG steps,
    /// claim work, or create Kubernetes replicas.
    /// </remarks>
    public sealed class AiRunAdmissionController : IAiRunAdmissionController
    {
        private readonly IAiRuntimeInstanceRegistry _registry;
        private readonly IAiRuntimeAdmissionReservationStore _reservationStore;
        private readonly IAiRuntimeInstanceCapacityStore _capacityStore;
        private readonly IAiTenantRuntimeSettingsProvider _tenantRuntimeSettingsProvider;
        private readonly AiRunAdmissionOptions _options;
        private readonly ILogger<AiRunAdmissionController> _logger;
        private long _admissionSequence;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRunAdmissionController"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry used to discover visible runtime instances.</param>
        /// <param name="reservationStore">The admission reservation store used to account for temporary reserved capacity.</param>
        /// <param name="capacityStore">The runtime instance capacity store used to verify dispatchable runtime capacity.</param>
        /// <param name="tenantRuntimeSettingsProvider">The tenant runtime settings provider.</param>
        /// <param name="options">The run admission options.</param>
        /// <param name="logger">The logger.</param>
        public AiRunAdmissionController(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeAdmissionReservationStore reservationStore,
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider,
            IOptions<AiRunAdmissionOptions> options,
            ILogger<AiRunAdmissionController> logger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _reservationStore = reservationStore ?? throw new ArgumentNullException(nameof(reservationStore));
            _capacityStore = capacityStore ?? throw new ArgumentNullException(nameof(capacityStore));
            _tenantRuntimeSettingsProvider = tenantRuntimeSettingsProvider ?? throw new ArgumentNullException(nameof(tenantRuntimeSettingsProvider));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Evaluates the current runtime instance registry and produces an admission decision for a run.
        /// </summary>
        /// <param name="request">The run admission request.</param>
        /// <param name="cancellationToken">A token used to cancel the admission operation.</param>
        /// <returns>The run admission decision.</returns>
        public async Task<AiRunAdmissionDecision> AdmitAsync(
            AiRunAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.RunRequest);

            cancellationToken.ThrowIfCancellationRequested();

            var tenantRuntimeSettings =
                _tenantRuntimeSettingsProvider.GetSettings(
                    request.TenantId,
                    ResolveTenantGroupId(request));

            var effectiveMaxInstanceCount =
                ResolveEffectiveMaxInstanceCount(
                    tenantRuntimeSettings);

            if (!_options.Enabled)
            {
                _logger.LogWarning(
                    "Admission rejected because run admission is disabled. RunId={RunId}, TenantId={TenantId}, PipelineKey={PipelineKey}",
                    request.RunId,
                    request.TenantId,
                    request.PipelineKey);

                return CreateDecision(
                    AiRunAdmissionDecisionType.Reject,
                    reason: "Run admission is disabled.",
                    visibleInstances: Array.Empty<AiRuntimeInstanceSnapshot>(),
                    availableInstances: Array.Empty<AiRuntimeInstanceSnapshot>(),
                    currentInstanceCount: 0,
                    maxInstanceCount: effectiveMaxInstanceCount,
                    tenantRuntimeSettings: tenantRuntimeSettings);
            }

            var instances = await _registry
                .ListAsync(includeStopped: false, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Admission started. RunId={RunId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, PipelineKey={PipelineKey}, PreferredRuntimeInstanceId={PreferredRuntimeInstanceId}, VisibleInstanceCount={VisibleInstanceCount}, EnableScaleOutRequest={EnableScaleOutRequest}, MaxInstanceCount={MaxInstanceCount}, TenantIsolationMode={TenantIsolationMode}, TenantMaxRuntimeInstances={TenantMaxRuntimeInstances}, EffectiveMaxInstanceCount={EffectiveMaxInstanceCount}, EnableGlobalQueueFallback={EnableGlobalQueueFallback}, RejectWhenNoCapacity={RejectWhenNoCapacity}",
                request.RunId,
                tenantRuntimeSettings.TenantId,
                tenantRuntimeSettings.TenantGroupId,
                request.PipelineKey,
                request.PreferredRuntimeInstanceId,
                instances.Count,
                _options.EnableScaleOutRequest,
                _options.MaxInstanceCount,
                tenantRuntimeSettings.IsolationMode,
                tenantRuntimeSettings.MaxRuntimeInstances,
                effectiveMaxInstanceCount,
                _options.EnableGlobalQueueFallback,
                _options.RejectWhenNoCapacity);

            foreach (var instance in instances)
            {
                _logger.LogInformation(
                    "Admission visible instance. RunId={RunId}, RuntimeInstanceId={RuntimeInstanceId}, Role={Role}, Status={Status}, CanAcceptRun={CanAcceptRun}, IsQueuePaused={IsQueuePaused}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, ActiveRunCount={ActiveRunCount}, AvailableRunSlots={AvailableRunSlots}, WorkerCount={WorkerCount}, ActiveWorkerCount={ActiveWorkerCount}, AvailableWorkerCount={AvailableWorkerCount}",
                    request.RunId,
                    instance.RuntimeInstanceId,
                    instance.Role,
                    instance.Status,
                    instance.CanAcceptRun,
                    instance.IsQueuePaused,
                    instance.QueuedRunCount,
                    instance.RunningRunCount,
                    instance.ActiveRunCount,
                    instance.AvailableRunSlots,
                    instance.WorkerCount,
                    instance.ActiveWorkerCount,
                    instance.AvailableWorkerCount);
            }

            var countableRuntimeInstances = instances
                .Where(instance => instance.Role == AiRuntimeInstanceRole.Runtime)
                .Where(IsCountableForMaxRuntimeInstances)
                .ToArray();

            var runtimeCandidates = instances
                .Where(instance => instance.Role == AiRuntimeInstanceRole.Runtime)
                .Where(instance =>
                {
                    var eligible = IsEligibleForAdmission(instance);

                    if (!eligible)
                    {
                        _logger.LogInformation(
                            "Admission runtime instance rejected before capacity evaluation. RunId={RunId}, RuntimeInstanceId={RuntimeInstanceId}, Status={Status}, CanAcceptRun={CanAcceptRun}, IsQueuePaused={IsQueuePaused}, Reason={Reason}",
                            request.RunId,
                            instance.RuntimeInstanceId,
                            instance.Status,
                            instance.CanAcceptRun,
                            instance.IsQueuePaused,
                            "Runtime instance snapshot is not eligible for admission.");
                    }

                    return eligible;
                })
                .ToArray();

            var availableCandidates = await BuildAvailableAdmissionCandidatesAsync(
                    request,
                    runtimeCandidates,
                    cancellationToken)
                .ConfigureAwait(false);

            var availableInstances =
                availableCandidates
                    .Select(candidate => candidate.Instance)
                    .ToArray();

            _logger.LogInformation(
                "Admission candidates resolved. RunId={RunId}, VisibleInstanceCount={VisibleInstanceCount}, CountableRuntimeInstanceCount={CountableRuntimeInstanceCount}, RuntimeCandidateCount={RuntimeCandidateCount}, AvailableCandidateCount={AvailableCandidateCount}",
                request.RunId,
                instances.Count,
                countableRuntimeInstances.Length,
                runtimeCandidates.Length,
                availableCandidates.Count);

            var preferred = TrySelectPreferredInstance(
                request,
                availableCandidates);

            if (preferred is not null)
            {
                _logger.LogInformation(
                    "Admission selected preferred runtime instance. RunId={RunId}, RuntimeInstanceId={RuntimeInstanceId}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, ReservedRunCount={ReservedRunCount}",
                    request.RunId,
                    preferred.Instance.RuntimeInstanceId,
                    preferred.EffectiveAvailableRunSlots,
                    preferred.ReservedRunCount);

                return CreateAssignmentDecision(
                    preferred,
                    instances,
                    availableInstances,
                    countableRuntimeInstances.Length,
                    effectiveMaxInstanceCount,
                    tenantRuntimeSettings,
                    "Preferred runtime instance selected for run admission.");
            }

            var selected =
                SelectRuntimeInstanceForAdmission(
                    availableCandidates);

            if (selected is not null)
            {
                _logger.LogInformation(
                    "Admission selected runtime instance. RunId={RunId}, RuntimeInstanceId={RuntimeInstanceId}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, ReservedRunCount={ReservedRunCount}, AvailableWorkerCount={AvailableWorkerCount}, RunningRunCount={RunningRunCount}, QueuedRunCount={QueuedRunCount}",
                    request.RunId,
                    selected.Instance.RuntimeInstanceId,
                    selected.EffectiveAvailableRunSlots,
                    selected.ReservedRunCount,
                    GetAvailableWorkerCount(selected),
                    GetRunningRunCount(selected),
                    GetQueuedRunCount(selected));

                return CreateAssignmentDecision(
                    selected,
                    instances,
                    availableInstances,
                    countableRuntimeInstances.Length,
                    effectiveMaxInstanceCount,
                    tenantRuntimeSettings,
                    "Runtime instance selected for run admission.");
            }

            if (ShouldRequestScaleOut(countableRuntimeInstances.Length, effectiveMaxInstanceCount))
            {
                _logger.LogWarning(
                    "Admission requesting scale-out. RunId={RunId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, TenantIsolationMode={TenantIsolationMode}, VisibleInstanceCount={VisibleInstanceCount}, RuntimeCandidateCount={RuntimeCandidateCount}, AvailableCandidateCount={AvailableCandidateCount}, CurrentRuntimeInstanceCount={CurrentRuntimeInstanceCount}, MaxInstanceCount={MaxInstanceCount}, Reason={Reason}",
                    request.RunId,
                    tenantRuntimeSettings.TenantId,
                    tenantRuntimeSettings.TenantGroupId,
                    tenantRuntimeSettings.IsolationMode,
                    instances.Count,
                    runtimeCandidates.Length,
                    availableCandidates.Count,
                    countableRuntimeInstances.Length,
                    effectiveMaxInstanceCount,
                    "No runtime instance can currently accept the run and scale-out is allowed.");

                return CreateDecision(
                    AiRunAdmissionDecisionType.RequestScaleOut,
                    reason: "No runtime instance can currently accept the run and scale-out is allowed.",
                    visibleInstances: instances,
                    availableInstances: availableInstances,
                    currentInstanceCount: countableRuntimeInstances.Length,
                    maxInstanceCount: effectiveMaxInstanceCount,
                    tenantRuntimeSettings: tenantRuntimeSettings);
            }

            if (_options.EnableGlobalQueueFallback)
            {
                _logger.LogWarning(
                    "Admission falling back to global queue. RunId={RunId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, VisibleInstanceCount={VisibleInstanceCount}, RuntimeCandidateCount={RuntimeCandidateCount}, AvailableCandidateCount={AvailableCandidateCount}, Reason={Reason}",
                    request.RunId,
                    tenantRuntimeSettings.TenantId,
                    tenantRuntimeSettings.TenantGroupId,
                    instances.Count,
                    runtimeCandidates.Length,
                    availableCandidates.Count,
                    "No runtime instance can currently accept the run; global queue fallback is allowed.");

                return CreateDecision(
                    AiRunAdmissionDecisionType.QueueGlobally,
                    reason: "No runtime instance can currently accept the run; global queue fallback is allowed.",
                    visibleInstances: instances,
                    availableInstances: availableInstances,
                    currentInstanceCount: countableRuntimeInstances.Length,
                    maxInstanceCount: effectiveMaxInstanceCount,
                    tenantRuntimeSettings: tenantRuntimeSettings);
            }

            if (_options.RejectWhenNoCapacity)
            {
                _logger.LogWarning(
                    "Admission rejecting run because no capacity is available. RunId={RunId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, VisibleInstanceCount={VisibleInstanceCount}, RuntimeCandidateCount={RuntimeCandidateCount}, AvailableCandidateCount={AvailableCandidateCount}",
                    request.RunId,
                    tenantRuntimeSettings.TenantId,
                    tenantRuntimeSettings.TenantGroupId,
                    instances.Count,
                    runtimeCandidates.Length,
                    availableCandidates.Count);

                return CreateDecision(
                    AiRunAdmissionDecisionType.Reject,
                    reason: "No runtime instance can currently accept the run.",
                    visibleInstances: instances,
                    availableInstances: availableInstances,
                    currentInstanceCount: countableRuntimeInstances.Length,
                    maxInstanceCount: effectiveMaxInstanceCount,
                    tenantRuntimeSettings: tenantRuntimeSettings);
            }

            _logger.LogWarning(
                "Admission produced unknown decision. RunId={RunId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, VisibleInstanceCount={VisibleInstanceCount}, RuntimeCandidateCount={RuntimeCandidateCount}, AvailableCandidateCount={AvailableCandidateCount}",
                request.RunId,
                tenantRuntimeSettings.TenantId,
                tenantRuntimeSettings.TenantGroupId,
                instances.Count,
                runtimeCandidates.Length,
                availableCandidates.Count);

            return CreateDecision(
                AiRunAdmissionDecisionType.Unknown,
                reason: "No admission policy produced a terminal decision.",
                visibleInstances: instances,
                availableInstances: availableInstances,
                currentInstanceCount: countableRuntimeInstances.Length,
                maxInstanceCount: effectiveMaxInstanceCount,
                tenantRuntimeSettings: tenantRuntimeSettings);
        }

        /// <summary>
        /// Builds ranked admission candidates after subtracting temporary reserved run capacity.
        /// </summary>
        /// <param name="request">The admission request.</param>
        /// <param name="runtimeCandidates">The eligible runtime instances.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The ranked available admission candidates.</returns>
        private async Task<IReadOnlyList<AdmissionCandidate>> BuildAvailableAdmissionCandidatesAsync(
            AiRunAdmissionRequest request,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> runtimeCandidates,
            CancellationToken cancellationToken)
        {
            var candidates =
                new List<AdmissionCandidate>(runtimeCandidates.Count);

            foreach (var instance in runtimeCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var capacityDescriptor =
                    await _capacityStore
                        .GetAsync(
                            instance.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                var currentInstance =
                    await _registry
                        .GetAsync(
                            instance.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (currentInstance is null)
                {
                    LogAdmissionCandidateRejected(
                        request,
                        instance,
                        capacityDescriptor,
                        reservedRunCount: 0,
                        availableRunSlots: capacityDescriptor?.AvailableRunSlots ?? instance.AvailableRunSlots ?? 0,
                        effectiveAvailableRunSlots: capacityDescriptor?.AvailableRunSlots ?? instance.AvailableRunSlots ?? 0,
                        reason: "Runtime instance disappeared from registry before admission assignment.");

                    continue;
                }

                if (!currentInstance.CanAcceptRun)
                {
                    LogAdmissionCandidateRejected(
                        request,
                        currentInstance,
                        capacityDescriptor,
                        reservedRunCount: 0,
                        availableRunSlots: capacityDescriptor?.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        effectiveAvailableRunSlots: capacityDescriptor?.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        reason: "Runtime instance cannot accept run according to current registry snapshot.");

                    continue;
                }

                if (currentInstance.IsQueuePaused)
                {
                    LogAdmissionCandidateRejected(
                        request,
                        currentInstance,
                        capacityDescriptor,
                        reservedRunCount: 0,
                        availableRunSlots: capacityDescriptor?.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        effectiveAvailableRunSlots: capacityDescriptor?.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        reason: "Runtime instance queue is paused according to current registry snapshot.");

                    continue;
                }

                if (!IsEligibleForAdmission(currentInstance))
                {
                    LogAdmissionCandidateRejected(
                        request,
                        currentInstance,
                        capacityDescriptor,
                        reservedRunCount: 0,
                        availableRunSlots: capacityDescriptor?.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        effectiveAvailableRunSlots: capacityDescriptor?.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        reason: "Current runtime instance snapshot is not eligible for admission.");

                    continue;
                }

                if (capacityDescriptor is null &&
                    IsProbablyStaleRuntimeInstance(currentInstance))
                {
                    LogAdmissionCandidateRejected(
                        request,
                        currentInstance,
                        capacityDescriptor,
                        reservedRunCount: 0,
                        availableRunSlots: currentInstance.AvailableRunSlots ?? 0,
                        effectiveAvailableRunSlots: currentInstance.AvailableRunSlots ?? 0,
                        reason: "Runtime instance has no capacity descriptor and looks stale.");

                    continue;
                }

                if (capacityDescriptor is not null &&
                    capacityDescriptor.Role != AiRuntimeInstanceRole.Runtime)
                {
                    LogAdmissionCandidateRejected(
                        request,
                        currentInstance,
                        capacityDescriptor,
                        reservedRunCount: 0,
                        availableRunSlots: capacityDescriptor.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        effectiveAvailableRunSlots: capacityDescriptor.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        reason: "Capacity descriptor role is not Runtime.");

                    continue;
                }

                if (capacityDescriptor is not null &&
                    !IsCapacityDescriptorStatusEligibleForAdmission(capacityDescriptor.Status))
                {
                    LogAdmissionCandidateRejected(
                        request,
                        currentInstance,
                        capacityDescriptor,
                        reservedRunCount: 0,
                        availableRunSlots: capacityDescriptor.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        effectiveAvailableRunSlots: capacityDescriptor.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        reason: "Capacity descriptor status is not eligible for admission.");

                    continue;
                }

                if (capacityDescriptor is not null &&
                    !capacityDescriptor.CanAcceptRun)
                {
                    LogAdmissionCandidateRejected(
                        request,
                        currentInstance,
                        capacityDescriptor,
                        reservedRunCount: 0,
                        availableRunSlots: capacityDescriptor.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        effectiveAvailableRunSlots: capacityDescriptor.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        reason: "Runtime instance cannot accept run according to capacity descriptor.");

                    continue;
                }

                var isQueuePaused =
                    capacityDescriptor?.IsQueuePaused ??
                    false;

                if (isQueuePaused)
                {
                    LogAdmissionCandidateRejected(
                        request,
                        currentInstance,
                        capacityDescriptor,
                        reservedRunCount: 0,
                        availableRunSlots: capacityDescriptor?.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        effectiveAvailableRunSlots: capacityDescriptor?.AvailableRunSlots ?? currentInstance.AvailableRunSlots ?? 0,
                        reason: "Runtime instance queue is paused according to capacity descriptor.");

                    continue;
                }

                var reservedRunCount =
                    await _reservationStore
                        .GetReservedRunCountAsync(
                            currentInstance.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                var availableRunSlots =
                    capacityDescriptor?.AvailableRunSlots ??
                    currentInstance.AvailableRunSlots ??
                    0;

                var effectiveAvailableRunSlots =
                    Math.Max(
                        0,
                        availableRunSlots - reservedRunCount);

                _logger.LogInformation(
                    "Admission candidate accepted. RunId={RunId}, RuntimeInstanceId={RuntimeInstanceId}, Role={Role}, RegistryStatus={RegistryStatus}, CapacityStatus={CapacityStatus}, RegistryCanAcceptRun={RegistryCanAcceptRun}, CapacityCanAcceptRun={CapacityCanAcceptRun}, EffectiveCanAcceptRun={EffectiveCanAcceptRun}, RegistryIsQueuePaused={RegistryIsQueuePaused}, CapacityIsQueuePaused={CapacityIsQueuePaused}, AvailableRunSlots={AvailableRunSlots}, ReservedRunCount={ReservedRunCount}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, WorkerCount={WorkerCount}, ActiveWorkerCount={ActiveWorkerCount}, AvailableWorkerCount={AvailableWorkerCount}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, QueueFirstAdmission={QueueFirstAdmission}",
                    request.RunId,
                    currentInstance.RuntimeInstanceId,
                    currentInstance.Role,
                    currentInstance.Status,
                    capacityDescriptor?.Status,
                    currentInstance.CanAcceptRun,
                    capacityDescriptor?.CanAcceptRun,
                    true,
                    currentInstance.IsQueuePaused,
                    capacityDescriptor?.IsQueuePaused,
                    availableRunSlots,
                    reservedRunCount,
                    effectiveAvailableRunSlots,
                    capacityDescriptor?.WorkerCount ?? currentInstance.WorkerCount,
                    capacityDescriptor?.ActiveWorkerCount ?? currentInstance.ActiveWorkerCount,
                    capacityDescriptor?.AvailableWorkerCount ?? currentInstance.AvailableWorkerCount,
                    capacityDescriptor?.QueuedRunCount ?? currentInstance.QueuedRunCount,
                    capacityDescriptor?.RunningRunCount ?? currentInstance.RunningRunCount,
                    effectiveAvailableRunSlots <= 0);

                candidates.Add(
                    new AdmissionCandidate(
                        currentInstance,
                        capacityDescriptor,
                        reservedRunCount,
                        effectiveAvailableRunSlots));
            }

            return candidates
                .OrderByDescending(candidate => candidate.EffectiveAvailableRunSlots)
                .ThenByDescending(candidate => GetAvailableWorkerCount(candidate))
                .ThenBy(candidate => GetActiveWorkerCount(candidate))
                .ThenBy(candidate => GetRunningRunCount(candidate))
                .ThenBy(candidate => GetQueuedRunCount(candidate))
                .ThenBy(candidate => candidate.Instance.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Determines whether a runtime instance without capacity descriptor is likely
        /// to be a stale test runtime left behind by a previous Redis-backed test run.
        /// </summary>
        /// <param name="instance">The runtime instance snapshot.</param>
        /// <returns><see langword="true"/> when the instance is likely stale; otherwise, <see langword="false"/>.</returns>
        private static bool IsProbablyStaleRuntimeInstance(
            AiRuntimeInstanceSnapshot instance)
        {
            return !string.IsNullOrWhiteSpace(instance.RuntimeInstanceId) &&
                   instance.RuntimeInstanceId.StartsWith(
                       "test-runtime-",
                       StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether a runtime instance should count against the runtime instance limit.
        /// </summary>
        /// <param name="instance">The runtime instance snapshot.</param>
        /// <returns><see langword="true"/> when the instance should count against the max runtime instance limit; otherwise, <see langword="false"/>.</returns>
        private static bool IsCountableForMaxRuntimeInstances(
            AiRuntimeInstanceSnapshot instance)
        {
            if (instance.Role != AiRuntimeInstanceRole.Runtime)
            {
                return false;
            }

            return instance.Status is not AiRuntimeInstanceStatus.Stopped
                and not AiRuntimeInstanceStatus.Unhealthy;
        }

        /// <summary>
        /// Determines whether a capacity descriptor status is eligible for admission.
        /// </summary>
        /// <param name="status">The capacity descriptor runtime status.</param>
        /// <returns><see langword="true"/> when eligible; otherwise, <see langword="false"/>.</returns>
        private bool IsCapacityDescriptorStatusEligibleForAdmission(
            AiRuntimeInstanceStatus status)
        {
            if (status == AiRuntimeInstanceStatus.Stopped)
            {
                return false;
            }

            if (status == AiRuntimeInstanceStatus.Paused && !_options.AllowPausedInstances)
            {
                return false;
            }

            if (status == AiRuntimeInstanceStatus.Draining && !_options.AllowDrainingInstances)
            {
                return false;
            }

            if (status == AiRuntimeInstanceStatus.Unhealthy && !_options.AllowUnhealthyInstances)
            {
                return false;
            }

            return status is
                AiRuntimeInstanceStatus.Ready or
                AiRuntimeInstanceStatus.Busy or
                AiRuntimeInstanceStatus.Paused or
                AiRuntimeInstanceStatus.Draining or
                AiRuntimeInstanceStatus.Unknown;
        }

        /// <summary>
        /// Determines whether a runtime instance is eligible to participate in admission decisions.
        /// </summary>
        /// <param name="instance">The runtime instance snapshot to evaluate.</param>
        /// <returns><see langword="true"/> if the runtime instance can be considered for admission; otherwise, <see langword="false"/>.</returns>
        private bool IsEligibleForAdmission(
            AiRuntimeInstanceSnapshot instance)
        {
            if (!instance.CanAcceptRun)
            {
                return false;
            }

            if (instance.IsQueuePaused)
            {
                return false;
            }

            if (instance.Status == AiRuntimeInstanceStatus.Stopped)
            {
                return false;
            }

            if (instance.Status == AiRuntimeInstanceStatus.Paused && !_options.AllowPausedInstances)
            {
                return false;
            }

            if (instance.Status == AiRuntimeInstanceStatus.Draining && !_options.AllowDrainingInstances)
            {
                return false;
            }

            if (instance.Status == AiRuntimeInstanceStatus.Unhealthy && !_options.AllowUnhealthyInstances)
            {
                return false;
            }

            return instance.Status is
                AiRuntimeInstanceStatus.Ready or
                AiRuntimeInstanceStatus.Busy or
                AiRuntimeInstanceStatus.Paused or
                AiRuntimeInstanceStatus.Draining or
                AiRuntimeInstanceStatus.Unknown;
        }

        /// <summary>
        /// Attempts to select the preferred runtime instance requested by the caller.
        /// </summary>
        /// <param name="request">The run admission request.</param>
        /// <param name="availableCandidates">The currently available runtime admission candidates.</param>
        /// <returns>The preferred runtime instance when it is available and allowed; otherwise, <see langword="null"/>.</returns>
        private AdmissionCandidate? TrySelectPreferredInstance(
            AiRunAdmissionRequest request,
            IReadOnlyCollection<AdmissionCandidate> availableCandidates)
        {
            if (!_options.PreferRequestedRuntimeInstance ||
                string.IsNullOrWhiteSpace(request.PreferredRuntimeInstanceId))
            {
                return null;
            }

            return availableCandidates.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Instance.RuntimeInstanceId,
                    request.PreferredRuntimeInstanceId,
                    StringComparison.Ordinal));
        }

        /// <summary>
        /// Selects the best runtime instance for admission from the already-ranked available candidates.
        /// </summary>
        private AdmissionCandidate? SelectRuntimeInstanceForAdmission(
            IReadOnlyList<AdmissionCandidate> availableCandidates)
        {
            if (availableCandidates.Count == 0)
            {
                return null;
            }

            var highestRanked =
                availableCandidates[0];

            var equallyRankedCandidates =
                availableCandidates
                    .Where(candidate => HasSameAdmissionRank(candidate, highestRanked))
                    .ToArray();

            if (equallyRankedCandidates.Length == 1)
            {
                return equallyRankedCandidates[0];
            }

            var sequence =
                Interlocked.Increment(
                    ref _admissionSequence);

            var index =
                (int)((sequence - 1) % equallyRankedCandidates.Length);

            return equallyRankedCandidates[index];
        }

        /// <summary>
        /// Determines whether two admission candidates have the same admission rank.
        /// </summary>
        private static bool HasSameAdmissionRank(
            AdmissionCandidate candidate,
            AdmissionCandidate baseline)
        {
            return candidate.EffectiveAvailableRunSlots == baseline.EffectiveAvailableRunSlots &&
                   GetAvailableWorkerCount(candidate) == GetAvailableWorkerCount(baseline) &&
                   GetActiveWorkerCount(candidate) == GetActiveWorkerCount(baseline) &&
                   GetRunningRunCount(candidate) == GetRunningRunCount(baseline) &&
                   GetQueuedRunCount(candidate) == GetQueuedRunCount(baseline);
        }

        /// <summary>
        /// Resolves the tenant group identifier from the admission request.
        /// </summary>
        private static string? ResolveTenantGroupId(
            AiRunAdmissionRequest request)
        {
            return request.RunRequest.ExecutionContextSnapshot?.TenantGroupId;
        }

        /// <summary>
        /// Resolves the effective maximum runtime instance count for this admission decision.
        /// </summary>
        private int? ResolveEffectiveMaxInstanceCount(
            AiTenantRuntimeSettings tenantRuntimeSettings)
        {
            if (tenantRuntimeSettings.IsolationMode is
                AiRuntimeInstanceIsolationMode.Dedicated or
                AiRuntimeInstanceIsolationMode.Hybrid)
            {
                return tenantRuntimeSettings.MaxRuntimeInstances;
            }

            return _options.MaxInstanceCount;
        }

        /// <summary>
        /// Determines whether the admission controller should request scale-out.
        /// </summary>
        private bool ShouldRequestScaleOut(
            int currentRuntimeInstanceCount,
            int? maxInstanceCount)
        {
            if (!_options.EnableScaleOutRequest)
            {
                return false;
            }

            if (!maxInstanceCount.HasValue)
            {
                return true;
            }

            return currentRuntimeInstanceCount < maxInstanceCount.Value;
        }

        /// <summary>
        /// Creates an assignment decision for a selected runtime instance.
        /// </summary>
        private AiRunAdmissionDecision CreateAssignmentDecision(
            AdmissionCandidate candidate,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> visibleInstances,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> availableInstances,
            int currentInstanceCount,
            int? maxInstanceCount,
            AiTenantRuntimeSettings tenantRuntimeSettings,
            string reason)
        {
            var instance =
                candidate.Instance;

            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["assigned.runtime.instance.id"] = instance.RuntimeInstanceId,
                    ["assigned.runtime.instance.status"] = (candidate.CapacityDescriptor?.Status ?? instance.Status).ToString(),
                    ["assigned.runtime.instance.queued"] = GetQueuedRunCount(candidate).ToString(),
                    ["assigned.runtime.instance.running"] = GetRunningRunCount(candidate).ToString(),
                    ["assigned.runtime.instance.available.run.slots"] =
                        GetAvailableRunSlots(candidate).ToString(),
                    ["assigned.runtime.instance.reserved.run.count"] =
                        candidate.ReservedRunCount.ToString(),
                    ["assigned.runtime.instance.effective.available.run.slots"] =
                        candidate.EffectiveAvailableRunSlots.ToString(),
                    ["assigned.runtime.instance.active.workers"] =
                        GetActiveWorkerCount(candidate).ToString(),
                    ["assigned.runtime.instance.available.workers"] =
                        GetAvailableWorkerCount(candidate).ToString(),
                    ["assigned.runtime.instance.max.local.workers.per.execution"] =
                        GetMaxWorkersPerRun(candidate).ToString(),
                    ["assigned.runtime.instance.queue.first"] =
                        (candidate.EffectiveAvailableRunSlots <= 0).ToString(),
                    ["max.instance.count"] =
                        maxInstanceCount?.ToString() ?? string.Empty
                };

            AddTenantRuntimeSettingsMetadata(
                metadata,
                tenantRuntimeSettings);

            return new AiRunAdmissionDecision
            {
                DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                AssignedRuntimeInstanceId = instance.RuntimeInstanceId,
                AssignedInstance = instance,
                TenantId = tenantRuntimeSettings.TenantId,
                TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                TenantRuntimeSettings = tenantRuntimeSettings,
                Reason = reason,
                VisibleInstanceCount = visibleInstances.Count,
                AvailableInstanceCount = availableInstances.Count,
                CurrentInstanceCount = currentInstanceCount,
                MaxInstanceCount = maxInstanceCount,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Creates a non-assignment admission decision.
        /// </summary>
        private AiRunAdmissionDecision CreateDecision(
            AiRunAdmissionDecisionType decisionType,
            string reason,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> visibleInstances,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> availableInstances,
            int currentInstanceCount,
            int? maxInstanceCount,
            AiTenantRuntimeSettings tenantRuntimeSettings)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["visible.instance.count"] = visibleInstances.Count.ToString(),
                    ["available.instance.count"] = availableInstances.Count.ToString(),
                    ["current.instance.count"] = currentInstanceCount.ToString(),
                    ["max.instance.count"] = maxInstanceCount?.ToString() ?? string.Empty
                };

            AddTenantRuntimeSettingsMetadata(
                metadata,
                tenantRuntimeSettings);

            return new AiRunAdmissionDecision
            {
                DecisionType = decisionType,
                Reason = reason,
                TenantId = tenantRuntimeSettings.TenantId,
                TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                TenantRuntimeSettings = tenantRuntimeSettings,
                VisibleInstanceCount = visibleInstances.Count,
                AvailableInstanceCount = availableInstances.Count,
                CurrentInstanceCount = currentInstanceCount,
                MaxInstanceCount = maxInstanceCount,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Adds tenant runtime settings to admission metadata for diagnostics, logs, dashboards,
        /// and non-critical observability.
        /// </summary>
        private static void AddTenantRuntimeSettingsMetadata(
            IDictionary<string, string> metadata,
            AiTenantRuntimeSettings tenantRuntimeSettings)
        {
            metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] =
                tenantRuntimeSettings.TenantId ?? string.Empty;

            metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
                tenantRuntimeSettings.TenantGroupId ?? string.Empty;

            metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] =
                tenantRuntimeSettings.IsolationMode.ToString();

            metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] =
                tenantRuntimeSettings.PreferDedicatedCapacity.ToString();

            metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] =
                tenantRuntimeSettings.AllowSharedFallback.ToString();

            metadata["runtime.maxRuntimeInstances"] =
                tenantRuntimeSettings.MaxRuntimeInstances.ToString();

            metadata["runtime.workerCountPerInstance"] =
                tenantRuntimeSettings.WorkerCountPerInstance.ToString();

            metadata["runtime.maxConcurrentRunsPerInstance"] =
                tenantRuntimeSettings.MaxConcurrentRunsPerInstance.ToString();

            metadata["runtime.instanceIdPrefix"] =
                tenantRuntimeSettings.RuntimeInstanceIdPrefix ?? string.Empty;

            metadata["runtime.localQueueCapacity"] =
                tenantRuntimeSettings.LocalQueueCapacity?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Logs a rejected admission candidate.
        /// </summary>
        private void LogAdmissionCandidateRejected(
            AiRunAdmissionRequest request,
            AiRuntimeInstanceSnapshot instance,
            AiRuntimeInstanceCapacityDescriptor? capacityDescriptor,
            int reservedRunCount,
            int availableRunSlots,
            int effectiveAvailableRunSlots,
            string reason)
        {
            _logger.LogInformation(
                "Admission candidate rejected. RunId={RunId}, RuntimeInstanceId={RuntimeInstanceId}, Role={Role}, Status={Status}, RegistryCanAcceptRun={RegistryCanAcceptRun}, CapacityCanAcceptRun={CapacityCanAcceptRun}, IsQueuePaused={IsQueuePaused}, AvailableRunSlots={AvailableRunSlots}, ReservedRunCount={ReservedRunCount}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, WorkerCount={WorkerCount}, ActiveWorkerCount={ActiveWorkerCount}, AvailableWorkerCount={AvailableWorkerCount}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, Reason={Reason}",
                request.RunId,
                instance.RuntimeInstanceId,
                instance.Role,
                capacityDescriptor?.Status ?? instance.Status,
                instance.CanAcceptRun,
                capacityDescriptor?.CanAcceptRun,
                capacityDescriptor?.IsQueuePaused ?? instance.IsQueuePaused,
                availableRunSlots,
                reservedRunCount,
                effectiveAvailableRunSlots,
                capacityDescriptor?.WorkerCount ?? instance.WorkerCount,
                capacityDescriptor?.ActiveWorkerCount ?? instance.ActiveWorkerCount,
                capacityDescriptor?.AvailableWorkerCount ?? instance.AvailableWorkerCount,
                capacityDescriptor?.QueuedRunCount ?? instance.QueuedRunCount,
                capacityDescriptor?.RunningRunCount ?? instance.RunningRunCount,
                reason);
        }

        /// <summary>
        /// Gets the queued run count from a candidate capacity descriptor or registry snapshot.
        /// </summary>
        private static int GetQueuedRunCount(
            AdmissionCandidate candidate)
        {
            return candidate.CapacityDescriptor?.QueuedRunCount ??
                   candidate.Instance.QueuedRunCount;
        }

        /// <summary>
        /// Gets the running run count from a candidate capacity descriptor or registry snapshot.
        /// </summary>
        private static int GetRunningRunCount(
            AdmissionCandidate candidate)
        {
            return candidate.CapacityDescriptor?.RunningRunCount ??
                   candidate.Instance.RunningRunCount;
        }

        /// <summary>
        /// Gets the available run slot count from a candidate capacity descriptor or registry snapshot.
        /// </summary>
        private static int GetAvailableRunSlots(
            AdmissionCandidate candidate)
        {
            return candidate.CapacityDescriptor?.AvailableRunSlots ??
                   candidate.Instance.AvailableRunSlots ??
                   0;
        }

        /// <summary>
        /// Gets the active worker count from a candidate capacity descriptor or registry snapshot.
        /// </summary>
        private static int GetActiveWorkerCount(
            AdmissionCandidate candidate)
        {
            return candidate.CapacityDescriptor?.ActiveWorkerCount ??
                   candidate.Instance.ActiveWorkerCount ??
                   0;
        }

        /// <summary>
        /// Gets the available worker count from a candidate capacity descriptor or registry snapshot.
        /// </summary>
        private static int GetAvailableWorkerCount(
            AdmissionCandidate candidate)
        {
            return candidate.CapacityDescriptor?.AvailableWorkerCount ??
                   candidate.Instance.AvailableWorkerCount ??
                   0;
        }

        /// <summary>
        /// Gets the maximum worker count per run from a candidate capacity descriptor or registry snapshot.
        /// </summary>
        private static int? GetMaxWorkersPerRun(
            AdmissionCandidate candidate)
        {
            return candidate.CapacityDescriptor?.MaxWorkersPerRun ??
                   candidate.Instance.MaxLocalWorkersPerExecution;
        }

        /// <summary>
        /// Represents a runtime instance candidate for run admission after applying
        /// temporary admission reservations.
        /// </summary>
        /// <param name="Instance">The runtime instance snapshot.</param>
        /// <param name="CapacityDescriptor">The latest runtime instance capacity descriptor.</param>
        /// <param name="ReservedRunCount">The temporary reserved run count.</param>
        /// <param name="EffectiveAvailableRunSlots">The available run slots after subtracting reservations.</param>
        private sealed record AdmissionCandidate(
            AiRuntimeInstanceSnapshot Instance,
            AiRuntimeInstanceCapacityDescriptor? CapacityDescriptor,
            int ReservedRunCount,
            int EffectiveAvailableRunSlots);
    }
}