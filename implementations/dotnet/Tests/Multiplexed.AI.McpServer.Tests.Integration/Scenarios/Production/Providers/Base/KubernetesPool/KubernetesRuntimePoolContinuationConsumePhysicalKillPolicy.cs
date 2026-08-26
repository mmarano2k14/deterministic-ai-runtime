using Multiplexed.Abstractions.AI.Execution;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    internal static class KubernetesRuntimePoolContinuationConsumePhysicalKillPolicy
    {
        public static bool IsBoundaryPreserved(
            bool relationCompleted,
            bool continuationScheduled,
            bool parentIsTerminal,
            long scheduledStepVersion,
            AiStepState? callSite)
        {
            return
                relationCompleted &&
                continuationScheduled &&
                !parentIsTerminal &&
                callSite is not null &&
                callSite.Version > scheduledStepVersion &&
                callSite.Status is
                    AiStepExecutionStatus.Ready or
                    AiStepExecutionStatus.Running or
                    AiStepExecutionStatus.WaitingForRetry;
        }
    }
}
