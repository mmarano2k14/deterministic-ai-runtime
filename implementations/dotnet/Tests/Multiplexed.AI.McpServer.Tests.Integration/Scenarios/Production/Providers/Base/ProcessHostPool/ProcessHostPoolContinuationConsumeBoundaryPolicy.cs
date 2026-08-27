using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool
{
    internal static class ProcessHostPoolContinuationConsumeBoundaryPolicy
    {
        public static bool IsExactRunningContinuationIndex(
            AiRuntimeRunExecutionIndexEntry? index,
            string parentExecutionId,
            string runtimeInstanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parentExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return
                index is not null &&
                string.Equals(
                    index.Status,
                    AiRuntimeRunExecutionIndexStatuses.Running,
                    StringComparison.OrdinalIgnoreCase) &&
                StringComparer.Ordinal.Equals(
                    index.ExecutionId,
                    parentExecutionId) &&
                StringComparer.Ordinal.Equals(
                    index.RuntimeInstanceId,
                    runtimeInstanceId) &&
                index.CompletedAtUtc is null;
        }

        public static bool IsSemanticBoundaryPreserved(
            bool relationCompleted,
            bool continuationScheduled,
            bool parentTerminal,
            long scheduledStepVersion,
            long? callSiteVersion,
            AiStepExecutionStatus? callSiteStatus)
        {
            /*
             * Scheduled remains the durable liveness authority until the parent execution itself is terminal.
             * A continuation attempt may therefore die in either of two valid consume windows:
             *   - the call-site is still actively consumable (Ready/Running/WaitingForRetry), or
             *   - the call-site terminal write committed, but parent finalization has not committed yet
             *     (Completed/Failed while the parent record is still non-terminal).
             *
             * AiChildContinuationCoordinator deliberately re-drives the same deterministic continuation
             * identity in both cases. Do not require a non-terminal call-site here; doing so makes the
             * ProcessHost projection reject the exact finalization-pending crash window that production
             * recovery is designed to converge.
             */
            return
                relationCompleted &&
                continuationScheduled &&
                !parentTerminal &&
                callSiteVersion.HasValue &&
                callSiteVersion.Value > scheduledStepVersion &&
                callSiteStatus is
                    AiStepExecutionStatus.Ready or
                    AiStepExecutionStatus.Running or
                    AiStepExecutionStatus.WaitingForRetry or
                    AiStepExecutionStatus.Completed or
                    AiStepExecutionStatus.Failed;
        }
    }
}
