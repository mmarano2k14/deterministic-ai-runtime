using Multiplexed.Abstractions.AI.ControlPlane.Admission;

namespace Multiplexed.AI.Tests.Fixtures
{
    public sealed class FakeRunAdmissionController : IAiRunAdmissionController
    {
        private readonly string? assignedRuntimeInstanceId;

        public FakeRunAdmissionController(
            string? assignedRuntimeInstanceId = null)
        {
            this.assignedRuntimeInstanceId = assignedRuntimeInstanceId;
        }

        public Task<AiRunAdmissionDecision> AdmitAsync(
            AiRunAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var resolvedRuntimeInstanceId =
                assignedRuntimeInstanceId ??
                request.PreferredRuntimeInstanceId ??
                "runtime-1";

            return Task.FromResult(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = resolvedRuntimeInstanceId,
                    Reason = "Fake admission decision for shared queue dispatcher unit tests.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 1,
                    CurrentInstanceCount = 1,
                    Metadata = new Dictionary<string, string>
                    {
                        ["fake"] = "true",
                        ["assigned.runtime.instance.id"] = resolvedRuntimeInstanceId
                    }
                });
        }
    }
}