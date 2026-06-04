using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Runtime.ControlPlane.Admission
{
    /// <summary>
    /// Default runtime implementation of the run admission controller.
    ///
    /// This controller evaluates visible runtime instances and decides whether a run
    /// should be assigned to an instance, queued globally, trigger scale-out, or be rejected.
    ///
    /// Important:
    /// This class does not enqueue runs, modify local queues, execute DAG steps,
    /// claim work, or create Kubernetes replicas.
    /// </summary>
    public sealed class AiRunAdmissionController : IAiRunAdmissionController
    {
        private readonly IAiRuntimeInstanceRegistry _registry;
        private readonly AiRunAdmissionOptions _options;

        public AiRunAdmissionController(
            IAiRuntimeInstanceRegistry registry,
            IOptions<AiRunAdmissionOptions> options)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

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

            var selected = available.FirstOrDefault();

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
                        instance.AvailableRunSlots?.ToString() ?? string.Empty
                }
            };
        }

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