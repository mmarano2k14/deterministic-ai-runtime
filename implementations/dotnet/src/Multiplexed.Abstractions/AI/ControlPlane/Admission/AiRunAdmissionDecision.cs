using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.Abstractions.AI.ControlPlane.Admission
{
    /// <summary>
    /// Represents the result of a run admission decision.
    ///
    /// This decision does not enqueue the run by itself.
    /// It only tells the caller what should happen next.
    /// </summary>
    public sealed class AiRunAdmissionDecision
    {
        /// <summary>
        /// Admission decision outcome.
        /// </summary>
        public required AiRunAdmissionDecisionType DecisionType { get; init; }

        /// <summary>
        /// Indicates whether the run was accepted by admission.
        /// </summary>
        public bool Accepted =>
            DecisionType == AiRunAdmissionDecisionType.AssignToInstance ||
            DecisionType == AiRunAdmissionDecisionType.QueueGlobally ||
            DecisionType == AiRunAdmissionDecisionType.RequestScaleOut;

        /// <summary>
        /// Runtime instance selected for assignment when <see cref="DecisionType"/>
        /// is <see cref="AiRunAdmissionDecisionType.AssignToInstance"/>.
        /// </summary>
        public string? AssignedRuntimeInstanceId { get; init; }

        /// <summary>
        /// Snapshot of the selected runtime instance when available.
        /// </summary>
        public AiRuntimeInstanceSnapshot? AssignedInstance { get; init; }

        /// <summary>
        /// Tenant identifier evaluated by admission.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Tenant group identifier evaluated by admission.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Runtime settings resolved for the tenant during admission.
        /// </summary>
        public AiTenantRuntimeSettings? TenantRuntimeSettings { get; init; }

        /// <summary>
        /// Indicates whether the caller should request runtime instance scale-out.
        /// </summary>
        public bool ShouldRequestScaleOut =>
            DecisionType == AiRunAdmissionDecisionType.RequestScaleOut;

        /// <summary>
        /// Indicates whether the run should be kept in a shared/global queue.
        /// </summary>
        public bool ShouldQueueGlobally =>
            DecisionType == AiRunAdmissionDecisionType.QueueGlobally;

        /// <summary>
        /// Indicates whether the run should be rejected.
        /// </summary>
        public bool Rejected =>
            DecisionType == AiRunAdmissionDecisionType.Reject;

        /// <summary>
        /// Human-readable reason explaining the admission decision.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Optional diagnostics produced by the admission decision.
        /// </summary>
        public IReadOnlyList<string> Diagnostics { get; init; } =
            Array.Empty<string>();

        /// <summary>
        /// Number of runtime instances visible to admission.
        /// </summary>
        public int VisibleInstanceCount { get; init; }

        /// <summary>
        /// Number of runtime instances currently able to accept a local run.
        /// </summary>
        public int AvailableInstanceCount { get; init; }

        /// <summary>
        /// Current registered runtime instance count.
        /// </summary>
        public int CurrentInstanceCount { get; init; }

        /// <summary>
        /// Maximum runtime instance count allowed by admission policy.
        /// </summary>
        public int? MaxInstanceCount { get; init; }

        /// <summary>
        /// UTC timestamp when the admission decision was produced.
        /// </summary>
        public DateTimeOffset DecidedAtUtc { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Optional metadata useful for logs, dashboards, and future shared admission.
        /// Metadata must not be used as the primary source for critical routing decisions.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}