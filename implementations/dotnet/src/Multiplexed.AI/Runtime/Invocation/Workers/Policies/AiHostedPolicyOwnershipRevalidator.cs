using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    /// <summary>
    /// Centralizes the security and lifecycle invariants shared by hosted custom-policy
    /// preparation without merging policy-family business semantics.
    /// </summary>
    internal static class AiHostedPolicyOwnershipRevalidator
    {
        internal static ExecutionContextSnapshot ValidateInitial(
            AiExecutionRecord parent,
            string expectedExecutionId,
            string expectedTenantId,
            string expectedTenantGroupId,
            string ownershipMessage,
            string terminalMessage)
        {
            ArgumentNullException.ThrowIfNull(parent);

            var snapshot = parent.ExecutionContextSnapshot;
            if (!string.Equals(parent.ExecutionId, expectedExecutionId, StringComparison.Ordinal) ||
                snapshot is null ||
                !string.Equals(snapshot.TenantId, expectedTenantId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.TenantGroupId, expectedTenantGroupId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(snapshot.ContextKey) ||
                string.IsNullOrWhiteSpace(snapshot.TenantGroupId))
            {
                throw new UnauthorizedAccessException(ownershipMessage);
            }

            if (parent.IsTerminal)
            {
                throw new InvalidOperationException(terminalMessage);
            }

            return snapshot;
        }

        internal static void RequireCurrent(
            AiExecutionRecord? current,
            string expectedExecutionId,
            ExecutionContextSnapshot baseline,
            string changedMessage)
        {
            ArgumentNullException.ThrowIfNull(baseline);

            var snapshot = current?.ExecutionContextSnapshot;
            if (current is null ||
                current.IsTerminal ||
                !string.Equals(current.ExecutionId, expectedExecutionId, StringComparison.Ordinal) ||
                snapshot is null ||
                !string.Equals(snapshot.TenantId, baseline.TenantId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.TenantGroupId, baseline.TenantGroupId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.UserId, baseline.UserId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.Project, baseline.Project, StringComparison.Ordinal) ||
                !string.Equals(snapshot.CurrentNamespace, baseline.CurrentNamespace, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(changedMessage);
            }
        }
    }
}
