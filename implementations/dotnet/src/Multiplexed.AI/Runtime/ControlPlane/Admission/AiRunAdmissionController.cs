using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;

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
        private const string RuntimeAdmissionDecisionOperation = "runtime-admission-decision";
        private const string RecoveryFailedRuntimeInstanceIdMetadataKey = "recovery.failedRuntimeInstanceId";

        private readonly IAiRuntimeInstanceRegistry _registry;
        private readonly IAiRuntimeAdmissionReservationStore _reservationStore;
        private readonly IAiRuntimeInstanceCapacityStore _capacityStore;
        private readonly IAiTenantRuntimeSettingsProvider _tenantRuntimeSettingsProvider;
        private readonly AiRunAdmissionOptions _options;
        private readonly ILogger<AiRunAdmissionController> _logger;
        private readonly IAiControlPlaneObserver _observer;
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
            : this(
                registry,
                reservationStore,
                capacityStore,
                tenantRuntimeSettingsProvider,
                options,
                logger,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRunAdmissionController"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry used to discover visible runtime instances.</param>
        /// <param name="reservationStore">The admission reservation store used to account for temporary reserved capacity.</param>
        /// <param name="capacityStore">The runtime instance capacity store used to verify dispatchable runtime capacity.</param>
        /// <param name="tenantRuntimeSettingsProvider">The tenant runtime settings provider.</param>
        /// <param name="options">The run admission options.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="observer">The control-plane observer.</param>
        public AiRunAdmissionController(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeAdmissionReservationStore reservationStore,
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider,
            IOptions<AiRunAdmissionOptions> options,
            ILogger<AiRunAdmissionController> logger,
            IAiControlPlaneObserver observer)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _reservationStore = reservationStore ?? throw new ArgumentNullException(nameof(reservationStore));
            _capacityStore = capacityStore ?? throw new ArgumentNullException(nameof(capacityStore));
            _tenantRuntimeSettingsProvider = tenantRuntimeSettingsProvider ?? throw new ArgumentNullException(nameof(tenantRuntimeSettingsProvider));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
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

            if (request.Placement is not null)
            {
                ArgumentNullException.ThrowIfNull(request.Placement.Target);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc = DateTimeOffset.UtcNow;

            var tenantRuntimeSettings =
                _tenantRuntimeSettingsProvider.GetSettings(
                    request.TenantId,
                    ResolveTenantGroupId(request));

            var effectiveMaxInstanceCount =
                ResolveEffectiveMaxInstanceCount(
                    tenantRuntimeSettings);

            await RecordAdmissionEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["tenantId"] = tenantRuntimeSettings.TenantId,
                        ["tenantGroupId"] = tenantRuntimeSettings.TenantGroupId,
                        ["pipelineKey"] = request.PipelineKey,
                        ["preferredRuntimeInstanceId"] = request.PreferredRuntimeInstanceId,
                        ["placementRuntimeInstanceId"] = request.Placement?.Target.RuntimeInstanceId,
                        ["placementHostId"] = request.Placement?.Target.HostId,
                        ["placementPoolId"] = request.Placement?.Target.PoolId,
                        ["placementNodeId"] = request.Placement?.Target.NodeId,
                        ["placementRequirement"] = request.Placement?.Requirement.ToString(),
                        ["placementFallback"] = request.Placement?.Fallback.ToString(),
                        ["enableScaleOutRequest"] = _options.EnableScaleOutRequest,
                        ["enableGlobalQueueFallback"] = _options.EnableGlobalQueueFallback,
                        ["rejectWhenNoCapacity"] = _options.RejectWhenNoCapacity,
                        ["maxInstanceCount"] = effectiveMaxInstanceCount,
                        ["tenantIsolationMode"] = tenantRuntimeSettings.IsolationMode.ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (!_options.Enabled)
                {
                    _logger.LogWarning(
                        "Admission rejected because run admission is disabled. RunId={RunId}, TenantId={TenantId}, PipelineKey={PipelineKey}",
                        request.RunId,
                        request.TenantId,
                        request.PipelineKey);

                    return await RecordAdmissionDecisionAsync(
                        request,
                        CreateDecision(
                        AiRunAdmissionDecisionType.Reject,
                        reason: "Run admission is disabled.",
                        visibleInstances: Array.Empty<AiRuntimeInstanceSnapshot>(),
                        availableInstances: Array.Empty<AiRuntimeInstanceSnapshot>(),
                        currentInstanceCount: 0,
                        maxInstanceCount: effectiveMaxInstanceCount,
                        tenantRuntimeSettings: tenantRuntimeSettings),
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                }

                var instances = await _registry
                    .ListAsync(includeStopped: false, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(                "Admission registry list resolved. RunId={RunId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}, Count={Count}, RuntimeInstanceIds={RuntimeInstanceIds}",
                    request.RunId,
                    _registry.GetType().FullName,
                    _registry.GetHashCode(),
                    instances.Count,
                    string.Join(",", instances.Select(item => item.RuntimeInstanceId)));
    

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
                        if (IsExcludedRecoveryRuntimeInstance(
                                request,
                                instance.RuntimeInstanceId))
                        {
                            _logger.LogWarning(
                                "Admission runtime instance rejected because it is the failed runtime instance for this recovery redispatch. RunId={RunId}, RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                                request.RunId,
                                instance.RuntimeInstanceId,
                                "recovery-failed-runtime-instance-excluded");

                            return false;
                        }

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
                    var placementReason =
                        request.Placement is null
                            ? "Preferred runtime instance selected for run admission."
                            : request.Placement.Requirement == AiRunPlacementRequirement.Required
                                ? "Required runtime placement selected for run admission."
                                : "Preferred runtime placement selected for run admission.";

                    _logger.LogInformation(
                        "Admission selected requested runtime placement. RunId={RunId}, RuntimeInstanceId={RuntimeInstanceId}, PlacementRequirement={PlacementRequirement}, PlacementFallback={PlacementFallback}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, ReservedRunCount={ReservedRunCount}",
                        request.RunId,
                        preferred.Instance.RuntimeInstanceId,
                        request.Placement?.Requirement,
                        request.Placement?.Fallback,
                        preferred.EffectiveAvailableRunSlots,
                        preferred.ReservedRunCount);

                    return await RecordAdmissionDecisionAsync(
                        request,
                        CreateAssignmentDecision(
                        preferred,
                        instances,
                        availableInstances,
                        countableRuntimeInstances.Length,
                        effectiveMaxInstanceCount,
                        tenantRuntimeSettings,
                        placementReason),
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                }

                var placementFallbackDecision =
                    CreatePlacementFallbackDecision(
                        request,
                        instances,
                        availableInstances,
                        countableRuntimeInstances.Length,
                        effectiveMaxInstanceCount,
                        tenantRuntimeSettings);

                if (placementFallbackDecision is not null)
                {
                    _logger.LogWarning(
                        "Admission could not select requested placement target. RunId={RunId}, RuntimeInstanceId={RuntimeInstanceId}, HostId={HostId}, PoolId={PoolId}, NodeId={NodeId}, PlacementRequirement={PlacementRequirement}, PlacementFallback={PlacementFallback}, DecisionType={DecisionType}, Reason={Reason}",
                        request.RunId,
                        request.Placement?.Target.RuntimeInstanceId,
                        request.Placement?.Target.HostId,
                        request.Placement?.Target.PoolId,
                        request.Placement?.Target.NodeId,
                        request.Placement?.Requirement,
                        request.Placement?.Fallback,
                        placementFallbackDecision.DecisionType,
                        placementFallbackDecision.Reason);

                    return await RecordAdmissionDecisionAsync(
                            request,
                            placementFallbackDecision,
                            startedAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
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

                    return await RecordAdmissionDecisionAsync(
                        request,
                        CreateAssignmentDecision(
                        selected,
                        instances,
                        availableInstances,
                        countableRuntimeInstances.Length,
                        effectiveMaxInstanceCount,
                        tenantRuntimeSettings,
                        "Runtime instance selected for run admission."),
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
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

                    return await RecordAdmissionDecisionAsync(
                        request,
                        CreateDecision(
                        AiRunAdmissionDecisionType.RequestScaleOut,
                        reason: "No runtime instance can currently accept the run and scale-out is allowed.",
                        visibleInstances: instances,
                        availableInstances: availableInstances,
                        currentInstanceCount: countableRuntimeInstances.Length,
                        maxInstanceCount: effectiveMaxInstanceCount,
                        tenantRuntimeSettings: tenantRuntimeSettings),
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
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

                    return await RecordAdmissionDecisionAsync(
                        request,
                        CreateDecision(
                        AiRunAdmissionDecisionType.QueueGlobally,
                        reason: "No runtime instance can currently accept the run; global queue fallback is allowed.",
                        visibleInstances: instances,
                        availableInstances: availableInstances,
                        currentInstanceCount: countableRuntimeInstances.Length,
                        maxInstanceCount: effectiveMaxInstanceCount,
                        tenantRuntimeSettings: tenantRuntimeSettings),
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
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

                    return await RecordAdmissionDecisionAsync(
                        request,
                        CreateDecision(
                        AiRunAdmissionDecisionType.Reject,
                        reason: "No runtime instance can currently accept the run.",
                        visibleInstances: instances,
                        availableInstances: availableInstances,
                        currentInstanceCount: countableRuntimeInstances.Length,
                        maxInstanceCount: effectiveMaxInstanceCount,
                        tenantRuntimeSettings: tenantRuntimeSettings),
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                }

                _logger.LogWarning(
                    "Admission produced unknown decision. RunId={RunId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, VisibleInstanceCount={VisibleInstanceCount}, RuntimeCandidateCount={RuntimeCandidateCount}, AvailableCandidateCount={AvailableCandidateCount}",
                    request.RunId,
                    tenantRuntimeSettings.TenantId,
                    tenantRuntimeSettings.TenantGroupId,
                    instances.Count,
                    runtimeCandidates.Length,
                    availableCandidates.Count);

                return await RecordAdmissionDecisionAsync(
                        request,
                        CreateDecision(
                    AiRunAdmissionDecisionType.Unknown,
                    reason: "No admission policy produced a terminal decision.",
                    visibleInstances: instances,
                    availableInstances: availableInstances,
                    currentInstanceCount: countableRuntimeInstances.Length,
                    maxInstanceCount: effectiveMaxInstanceCount,
                    tenantRuntimeSettings: tenantRuntimeSettings),
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await RecordAdmissionEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        null,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        durationMs,
                        new Dictionary<string, object?>
                        {
                            ["runId"] = request.RunId,
                            ["tenantId"] = tenantRuntimeSettings.TenantId ?? request.TenantId,
                            ["tenantGroupId"] = tenantRuntimeSettings.TenantGroupId ?? request.RunRequest.ExecutionContextSnapshot?.TenantGroupId,
                            ["pipelineKey"] = request.PipelineKey,
                            ["preferredRuntimeInstanceId"] = request.PreferredRuntimeInstanceId,
                            ["placementRuntimeInstanceId"] = request.Placement?.Target.RuntimeInstanceId,
                            ["placementHostId"] = request.Placement?.Target.HostId,
                            ["placementPoolId"] = request.Placement?.Target.PoolId,
                            ["placementNodeId"] = request.Placement?.Target.NodeId,
                            ["placementRequirement"] = request.Placement?.Requirement.ToString(),
                            ["placementFallback"] = request.Placement?.Fallback.ToString(),
                            ["durationMs"] = durationMs,
                            ["exception.type"] = exception.GetType().FullName,
                            ["exception.message"] = exception.Message
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <summary>
        /// Determines whether a runtime instance must be excluded because it is the failed runtime for a recovery redispatch.
        /// </summary>
        /// <param name="request">The admission request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <returns><see langword="true"/> when the runtime instance must be excluded; otherwise, <see langword="false"/>.</returns>
        private static bool IsExcludedRecoveryRuntimeInstance(
            AiRunAdmissionRequest request,
            string runtimeInstanceId)
        {
            return !string.IsNullOrWhiteSpace(runtimeInstanceId) &&
                   TryGetMetadataValue(
                       request.Metadata,
                       RecoveryFailedRuntimeInstanceIdMetadataKey,
                       out var failedRuntimeInstanceId) &&
                   string.Equals(
                       runtimeInstanceId,
                       failedRuntimeInstanceId,
                       StringComparison.Ordinal);
        }

        /// <summary>
        /// Attempts to read a metadata value by key using ordinal ignore-case matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The resolved value.</param>
        /// <returns><see langword="true"/> when a non-empty value is found; otherwise, <see langword="false"/>.</returns>
        private static bool TryGetMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            string key,
            out string value)
        {
            if (metadata is not null &&
                metadata.TryGetValue(
                    key,
                    out var directValue) &&
                !string.IsNullOrWhiteSpace(directValue))
            {
                value = directValue;
                return true;
            }

            if (metadata is not null)
            {
                foreach (var pair in metadata)
                {
                    if (string.Equals(
                            pair.Key,
                            key,
                            StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        value = pair.Value;
                        return true;
                    }
                }
            }

            value = string.Empty;
            return false;
        }

        /// <summary>
        /// Records a completed admission decision and returns it unchanged.
        /// </summary>
        /// <param name="request">The admission request.</param>
        /// <param name="decision">The admission decision.</param>
        /// <param name="startedAtUtc">The admission start timestamp.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The original admission decision.</returns>
        private async Task<AiRunAdmissionDecision> RecordAdmissionDecisionAsync(
            AiRunAdmissionRequest request,
            AiRunAdmissionDecision decision,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);
            var eventType = ResolveAdmissionEventType(decision);
            var outcome = ResolveAdmissionOutcome(decision);
            var failureReason = ResolveAdmissionFailureReason(decision);

            await RecordAdmissionEventAsync(
                    eventType,
                    request,
                    decision,
                    outcome,
                    failureReason,
                    durationMs,
                    BuildAdmissionDecisionProperties(request, decision, durationMs),
                    cancellationToken)
                .ConfigureAwait(false);

            return decision;
        }

        /// <summary>
        /// Records an admission control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The admission request.</param>
        /// <param name="decision">The optional admission decision.</param>
        /// <param name="outcome">The optional control-plane outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <param name="properties">The event properties.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the event has been recorded.</returns>
        private async Task RecordAdmissionEventAsync(
            AiControlPlaneEventType eventType,
            AiRunAdmissionRequest request,
            AiRunAdmissionDecision? decision,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            long? durationMs,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            try
            {
                await _observer.RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.Admission,
                            Operation = RuntimeAdmissionDecisionOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.RunId)
                                    ? Guid.NewGuid().ToString("N")
                                    : request.RunId,
                                RunId = request.RunId,
                                RuntimeInstanceId = decision?.AssignedRuntimeInstanceId,
                                PipelineKey = request.PipelineKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                new Dictionary<string, object?>
                                {
                                    ["tenantId"] = decision?.TenantId ?? request.TenantId,
                                    ["tenantGroupId"] = decision?.TenantGroupId ?? request.RunRequest.ExecutionContextSnapshot?.TenantGroupId,
                                    ["pipelineKey"] = request.PipelineKey,
                                    ["preferredRuntimeInstanceId"] = request.PreferredRuntimeInstanceId,
                                    ["assignedRuntimeInstanceId"] = decision?.AssignedRuntimeInstanceId,
                                    ["decisionType"] = decision?.DecisionType.ToString(),
                                    ["reason"] = decision?.Reason
                                })
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break admission decisions.
            }
        }

        /// <summary>
        /// Builds admission decision event properties.
        /// </summary>
        /// <param name="request">The admission request.</param>
        /// <param name="decision">The admission decision.</param>
        /// <param name="durationMs">The decision duration.</param>
        /// <returns>The event properties.</returns>
        private static IReadOnlyDictionary<string, object?> BuildAdmissionDecisionProperties(
            AiRunAdmissionRequest request,
            AiRunAdmissionDecision decision,
            long durationMs)
        {
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["runId"] = request.RunId,
                ["tenantId"] = decision.TenantId ?? request.TenantId,
                ["tenantGroupId"] = decision.TenantGroupId ?? request.RunRequest.ExecutionContextSnapshot?.TenantGroupId,
                ["pipelineKey"] = request.PipelineKey,
                ["preferredRuntimeInstanceId"] = request.PreferredRuntimeInstanceId,
                ["assignedRuntimeInstanceId"] = decision.AssignedRuntimeInstanceId,
                ["decisionType"] = decision.DecisionType.ToString(),
                ["reason"] = decision.Reason,
                ["visibleInstanceCount"] = decision.VisibleInstanceCount,
                ["availableInstanceCount"] = decision.AvailableInstanceCount,
                ["currentInstanceCount"] = decision.CurrentInstanceCount,
                ["maxInstanceCount"] = decision.MaxInstanceCount,
                ["durationMs"] = durationMs
            };

            foreach (var item in decision.Metadata)
            {
                properties[item.Key] = item.Value;
                properties[$"admission.{item.Key}"] = item.Value;
            }

            return properties;
        }

        /// <summary>
        /// Resolves the control-plane event type for an admission decision.
        /// </summary>
        /// <param name="decision">The admission decision.</param>
        /// <returns>The control-plane event type.</returns>
        private static AiControlPlaneEventType ResolveAdmissionEventType(
            AiRunAdmissionDecision decision)
        {
            return decision.DecisionType is AiRunAdmissionDecisionType.Reject or AiRunAdmissionDecisionType.Unknown
                ? AiControlPlaneEventType.OperationFailed
                : AiControlPlaneEventType.OperationCompleted;
        }

        /// <summary>
        /// Resolves the control-plane outcome for an admission decision.
        /// </summary>
        /// <param name="decision">The admission decision.</param>
        /// <returns>The control-plane outcome.</returns>
        private static AiControlPlaneOperationOutcome ResolveAdmissionOutcome(
            AiRunAdmissionDecision decision)
        {
            return decision.DecisionType switch
            {
                AiRunAdmissionDecisionType.AssignToInstance => AiControlPlaneOperationOutcome.Succeeded,
                AiRunAdmissionDecisionType.Reject => AiControlPlaneOperationOutcome.Denied,
                AiRunAdmissionDecisionType.RequestScaleOut => AiControlPlaneOperationOutcome.CompletedWithIssues,
                AiRunAdmissionDecisionType.QueueGlobally => AiControlPlaneOperationOutcome.CompletedWithIssues,
                _ => AiControlPlaneOperationOutcome.CompletedWithIssues
            };
        }

        /// <summary>
        /// Resolves the control-plane failure reason for an admission decision.
        /// </summary>
        /// <param name="decision">The admission decision.</param>
        /// <returns>The failure reason when relevant; otherwise, null.</returns>
        private static string? ResolveAdmissionFailureReason(
            AiRunAdmissionDecision decision)
        {
            return decision.DecisionType is AiRunAdmissionDecisionType.AssignToInstance
                ? null
                : decision.Reason;
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
        /// <remarks>
        /// Runtime instances that are unhealthy, busy, paused, or draining are not necessarily
        /// routable, but they still represent existing tenant capacity. Counting them prevents
        /// scale-out replacement from trying to recreate an already-started process with the
        /// same deterministic runtime instance id suffix.
        /// </remarks>
        private static bool IsCountableForMaxRuntimeInstances(
            AiRuntimeInstanceSnapshot instance)
        {
            if (instance.Role != AiRuntimeInstanceRole.Runtime)
            {
                return false;
            }

            return instance.Status is not AiRuntimeInstanceStatus.Stopped;
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
            var requestedRuntimeInstanceId =
                request.Placement is null
                    ? request.PreferredRuntimeInstanceId
                    : request.Placement.Target.RuntimeInstanceId;

            if (string.IsNullOrWhiteSpace(requestedRuntimeInstanceId))
            {
                return null;
            }

            if (request.Placement is null &&
                !_options.PreferRequestedRuntimeInstance)
            {
                return null;
            }

            return availableCandidates.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Instance.RuntimeInstanceId,
                    requestedRuntimeInstanceId,
                    StringComparison.Ordinal));
        }

        /// <summary>
        /// Creates the explicit placement fallback decision when a typed placement target
        /// cannot be selected by the current admission controller.
        /// </summary>
        private AiRunAdmissionDecision? CreatePlacementFallbackDecision(
            AiRunAdmissionRequest request,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> visibleInstances,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> availableInstances,
            int currentInstanceCount,
            int? maxInstanceCount,
            AiTenantRuntimeSettings tenantRuntimeSettings)
        {
            var placement = request.Placement;

            if (placement is null ||
                !HasPlacementTarget(placement.Target) ||
                placement.Fallback == AiRunPlacementFallback.AnyCompatibleCapacity)
            {
                return null;
            }

            var targetSummary =
                BuildPlacementTargetSummary(placement.Target);

            if (placement.Fallback == AiRunPlacementFallback.GlobalQueue &&
                _options.EnableGlobalQueueFallback)
            {
                return CreateDecision(
                    AiRunAdmissionDecisionType.QueueGlobally,
                    reason: $"Requested placement target '{targetSummary}' is not currently selectable; explicit global queue fallback was requested.",
                    visibleInstances: visibleInstances,
                    availableInstances: availableInstances,
                    currentInstanceCount: currentInstanceCount,
                    maxInstanceCount: maxInstanceCount,
                    tenantRuntimeSettings: tenantRuntimeSettings);
            }

            var reason =
                placement.Fallback == AiRunPlacementFallback.GlobalQueue
                    ? $"Requested placement target '{targetSummary}' is not currently selectable and global queue fallback is disabled."
                    : $"Requested placement target '{targetSummary}' is not currently selectable and explicit rejection was requested.";

            return CreateDecision(
                AiRunAdmissionDecisionType.Reject,
                reason: reason,
                visibleInstances: visibleInstances,
                availableInstances: availableInstances,
                currentInstanceCount: currentInstanceCount,
                maxInstanceCount: maxInstanceCount,
                tenantRuntimeSettings: tenantRuntimeSettings);
        }

        /// <summary>
        /// Determines whether a typed placement target contains at least one first-class identity.
        /// </summary>
        private static bool HasPlacementTarget(
            AiRunPlacementTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);

            return !string.IsNullOrWhiteSpace(target.RuntimeInstanceId) ||
                   !string.IsNullOrWhiteSpace(target.HostId) ||
                   !string.IsNullOrWhiteSpace(target.PoolId) ||
                   !string.IsNullOrWhiteSpace(target.NodeId);
        }

        /// <summary>
        /// Builds a compact typed placement target summary for diagnostics.
        /// </summary>
        private static string BuildPlacementTargetSummary(
            AiRunPlacementTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);

            return string.Join(
                ",",
                new[]
                {
                    $"RuntimeInstanceId={target.RuntimeInstanceId ?? string.Empty}",
                    $"HostId={target.HostId ?? string.Empty}",
                    $"PoolId={target.PoolId ?? string.Empty}",
                    $"NodeId={target.NodeId ?? string.Empty}"
                });
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