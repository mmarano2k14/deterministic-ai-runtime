using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
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
        private readonly AiRunAdmissionOptions _options;
        private long _admissionSequence;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRunAdmissionController"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry used to discover visible runtime instances.</param>
        /// <param name="options">The run admission options.</param>
        public AiRunAdmissionController(
            IAiRuntimeInstanceRegistry registry,
            IOptions<AiRunAdmissionOptions> options)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
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

            if (!_options.Enabled)
            {
                return CreateDecision(
                    AiRunAdmissionDecisionType.Reject,
                    reason: "Run admission is disabled.",
                    visibleInstances: Array.Empty<AiRuntimeInstanceSnapshot>(),
                    availableInstances: Array.Empty<AiRuntimeInstanceSnapshot>());
            }

            var instances = await _registry
                .ListAsync(includeStopped: false, cancellationToken)
                .ConfigureAwait(false);

            var runtimeCandidates = instances
                .Where(instance => instance.Role == AiRuntimeInstanceRole.Runtime)
                .Where(IsEligibleForAdmission)
                .ToArray();

            var available = runtimeCandidates
                .Where(instance => instance.CanAcceptRun)
                .OrderByDescending(instance => instance.AvailableRunSlots ?? 0)
                .ThenByDescending(instance => instance.AvailableWorkerCount ?? 0)
                .ThenBy(instance => instance.ActiveWorkerCount ?? 0)
                .ThenBy(instance => instance.RunningRunCount)
                .ThenBy(instance => instance.QueuedRunCount)
                .ThenBy(instance => instance.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();

            var preferred = TrySelectPreferredInstance(
                request,
                available);

            if (preferred is not null)
            {
                return CreateAssignmentDecision(
                    preferred,
                    instances,
                    available,
                    "Preferred runtime instance selected for run admission.");
            }

            var selected =
                SelectRuntimeInstanceForAdmission(
                    available);

            if (selected is not null)
            {
                return CreateAssignmentDecision(
                    selected,
                    instances,
                    available,
                    "Runtime instance selected for run admission.");
            }

            if (ShouldRequestScaleOut(runtimeCandidates.Length))
            {
                return CreateDecision(
                    AiRunAdmissionDecisionType.RequestScaleOut,
                    reason: "No runtime instance can currently accept the run and scale-out is allowed.",
                    visibleInstances: instances,
                    availableInstances: available);
            }

            if (_options.EnableGlobalQueueFallback)
            {
                return CreateDecision(
                    AiRunAdmissionDecisionType.QueueGlobally,
                    reason: "No runtime instance can currently accept the run; global queue fallback is allowed.",
                    visibleInstances: instances,
                    availableInstances: available);
            }

            if (_options.RejectWhenNoCapacity)
            {
                return CreateDecision(
                    AiRunAdmissionDecisionType.Reject,
                    reason: "No runtime instance can currently accept the run.",
                    visibleInstances: instances,
                    availableInstances: available);
            }

            return CreateDecision(
                AiRunAdmissionDecisionType.Unknown,
                reason: "No admission policy produced a terminal decision.",
                visibleInstances: instances,
                availableInstances: available);
        }

        /// <summary>
        /// Determines whether a runtime instance is eligible to participate in admission decisions.
        /// </summary>
        /// <param name="instance">The runtime instance snapshot to evaluate.</param>
        /// <returns><see langword="true"/> if the runtime instance can be considered for admission; otherwise, <see langword="false"/>.</returns>
        private bool IsEligibleForAdmission(
            AiRuntimeInstanceSnapshot instance)
        {
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
        /// <param name="availableInstances">The currently available runtime instances.</param>
        /// <returns>The preferred runtime instance when it is available and allowed; otherwise, <see langword="null"/>.</returns>
        private AiRuntimeInstanceSnapshot? TrySelectPreferredInstance(
            AiRunAdmissionRequest request,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> availableInstances)
        {
            if (!_options.PreferRequestedRuntimeInstance ||
                string.IsNullOrWhiteSpace(request.PreferredRuntimeInstanceId))
            {
                return null;
            }

            return availableInstances.FirstOrDefault(instance =>
                string.Equals(
                    instance.RuntimeInstanceId,
                    request.PreferredRuntimeInstanceId,
                    StringComparison.Ordinal));
        }

        /// <summary>
        /// Selects the best runtime instance for admission from the already-ranked available instances.
        /// </summary>
        /// <remarks>
        /// Runtime instances are first ranked by capacity before this method is called.
        /// When several instances have the same admission rank, this method rotates between them
        /// using an in-process admission sequence. This prevents all equal-capacity runs from
        /// being assigned to the first runtime instance in lexical order.
        ///
        /// This is not the final distributed reservation model. A future implementation should
        /// reserve run slots and worker capacity atomically with a TTL before dispatch.
        /// </remarks>
        /// <param name="availableInstances">The ranked available runtime instances.</param>
        /// <returns>The selected runtime instance, or <see langword="null"/> when none is available.</returns>
        private AiRuntimeInstanceSnapshot? SelectRuntimeInstanceForAdmission(
            IReadOnlyList<AiRuntimeInstanceSnapshot> availableInstances)
        {
            if (availableInstances.Count == 0)
            {
                return null;
            }

            var highestRanked =
                availableInstances[0];

            var equallyRankedInstances =
                availableInstances
                    .Where(instance => HasSameAdmissionRank(instance, highestRanked))
                    .ToArray();

            if (equallyRankedInstances.Length == 1)
            {
                return equallyRankedInstances[0];
            }

            var sequence =
                Interlocked.Increment(
                    ref _admissionSequence);

            var index =
                (int)((sequence - 1) % equallyRankedInstances.Length);

            return equallyRankedInstances[index];
        }

        /// <summary>
        /// Determines whether two runtime instances have the same admission rank.
        /// </summary>
        /// <param name="instance">The runtime instance snapshot to compare.</param>
        /// <param name="baseline">The baseline runtime instance snapshot.</param>
        /// <returns><see langword="true"/> if both snapshots have the same admission rank; otherwise, <see langword="false"/>.</returns>
        private static bool HasSameAdmissionRank(
            AiRuntimeInstanceSnapshot instance,
            AiRuntimeInstanceSnapshot baseline)
        {
            return (instance.AvailableRunSlots ?? 0) == (baseline.AvailableRunSlots ?? 0) &&
                   (instance.AvailableWorkerCount ?? 0) == (baseline.AvailableWorkerCount ?? 0) &&
                   (instance.ActiveWorkerCount ?? 0) == (baseline.ActiveWorkerCount ?? 0) &&
                   instance.RunningRunCount == baseline.RunningRunCount &&
                   instance.QueuedRunCount == baseline.QueuedRunCount;
        }

        /// <summary>
        /// Determines whether the admission controller should request scale-out.
        /// </summary>
        /// <param name="currentRuntimeInstanceCount">The number of currently visible runtime instances.</param>
        /// <returns><see langword="true"/> when scale-out should be requested; otherwise, <see langword="false"/>.</returns>
        private bool ShouldRequestScaleOut(
            int currentRuntimeInstanceCount)
        {
            if (!_options.EnableScaleOutRequest)
            {
                return false;
            }

            if (!_options.MaxInstanceCount.HasValue)
            {
                return true;
            }

            return currentRuntimeInstanceCount < _options.MaxInstanceCount.Value;
        }

        /// <summary>
        /// Creates an assignment decision for a selected runtime instance.
        /// </summary>
        /// <param name="instance">The selected runtime instance.</param>
        /// <param name="visibleInstances">All visible runtime instances.</param>
        /// <param name="availableInstances">The available runtime instances considered for assignment.</param>
        /// <param name="reason">The decision reason.</param>
        /// <returns>The assignment admission decision.</returns>
        private AiRunAdmissionDecision CreateAssignmentDecision(
            AiRuntimeInstanceSnapshot instance,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> visibleInstances,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> availableInstances,
            string reason)
        {
            return new AiRunAdmissionDecision
            {
                DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                AssignedRuntimeInstanceId = instance.RuntimeInstanceId,
                AssignedInstance = instance,
                Reason = reason,
                VisibleInstanceCount = visibleInstances.Count,
                AvailableInstanceCount = availableInstances.Count,
                CurrentInstanceCount = visibleInstances.Count,
                MaxInstanceCount = _options.MaxInstanceCount,
                Metadata = new Dictionary<string, string>
                {
                    ["assigned.runtime.instance.id"] = instance.RuntimeInstanceId,
                    ["assigned.runtime.instance.status"] = instance.Status.ToString(),
                    ["assigned.runtime.instance.queued"] = instance.QueuedRunCount.ToString(),
                    ["assigned.runtime.instance.running"] = instance.RunningRunCount.ToString(),
                    ["assigned.runtime.instance.available.run.slots"] =
                        instance.AvailableRunSlots?.ToString() ?? string.Empty,
                    ["assigned.runtime.instance.active.workers"] =
                        instance.ActiveWorkerCount?.ToString() ?? string.Empty,
                    ["assigned.runtime.instance.available.workers"] =
                        instance.AvailableWorkerCount?.ToString() ?? string.Empty,
                    ["assigned.runtime.instance.max.local.workers.per.execution"] =
                        instance.MaxLocalWorkersPerExecution?.ToString() ?? string.Empty
                }
            };
        }

        /// <summary>
        /// Creates a non-assignment admission decision.
        /// </summary>
        /// <param name="decisionType">The admission decision type.</param>
        /// <param name="reason">The decision reason.</param>
        /// <param name="visibleInstances">All visible runtime instances.</param>
        /// <param name="availableInstances">The available runtime instances.</param>
        /// <returns>The admission decision.</returns>
        private AiRunAdmissionDecision CreateDecision(
            AiRunAdmissionDecisionType decisionType,
            string reason,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> visibleInstances,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> availableInstances)
        {
            return new AiRunAdmissionDecision
            {
                DecisionType = decisionType,
                Reason = reason,
                VisibleInstanceCount = visibleInstances.Count,
                AvailableInstanceCount = availableInstances.Count,
                CurrentInstanceCount = visibleInstances.Count,
                MaxInstanceCount = _options.MaxInstanceCount,
                Metadata = new Dictionary<string, string>
                {
                    ["visible.instance.count"] = visibleInstances.Count.ToString(),
                    ["available.instance.count"] = availableInstances.Count.ToString(),
                    ["max.instance.count"] = _options.MaxInstanceCount?.ToString() ?? string.Empty
                }
            };
        }
    }
}