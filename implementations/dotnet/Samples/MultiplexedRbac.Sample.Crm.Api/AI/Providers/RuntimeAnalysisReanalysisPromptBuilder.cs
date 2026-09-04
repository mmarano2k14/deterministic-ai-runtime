using System.Text.Json;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Providers
{
    internal static class RuntimeAnalysisReanalysisPromptBuilder
    {
        public const string Instructions =
            """
            You are re-analyzing the result of a deterministic runtime experiment.

            The supplied evidence contains:
            - the original user question and bounded runtime snapshot;
            - the original AI finding;
            - the deterministic evidence receipt captured inside the current Child DAG execution;
            - the most recently executed approved scenario;
            - deterministic verification of that execution;
            - optionally, the previous re-analysis from the preceding approved depth;
            - the investigation mode chosen by the human before the durable analysis chain started.

            Your job is interpretation only. Deterministic verification remains the factual source of truth.

            Choose exactly one conclusion:
            - CONFIRMED: the deterministic experiment strongly supports the prior hypothesis.
            - WEAKENED: some evidence supports the hypothesis but important parts became less likely.
            - NOT_REPRODUCED: the experiment completed correctly but did not reproduce the predicted behavior.
            - INCONCLUSIVE: the supplied deterministic evidence is insufficient to decide.

            Important rules:
            - Never claim that the AI itself verified execution correctness.
            - Use the deterministic verification fields as authoritative execution facts.
            - Compare before/after counts and latency only when those values are present.
            - Do not invent DAG, policy, recovery, authorization, or HTTP facts that are absent from the supplied data.
            - shouldContinue means only that one additional bounded, materially distinct experiment may add useful evidence.
            - A suggestedScenario is only a proposal. It will still cross deterministic policy and human approval.
            - Never continue merely to repeat the same experiment, inflate Child DAG depth, or manufacture activity for the demo.
            - The supplied investigationGuidance is authoritative for whether to actively seek another experiment.
            - If canCreateAnotherChild=false, set shouldContinue=false because the deterministic child-depth boundary has been reached.
            - Even when shouldContinue=false, return a conservative bounded suggestedScenario for schema stability; it will not execute.
            """;

        public static string BuildInput(
            RuntimeAnalysisReanalysisProviderRequest request)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            var payload = new
            {
                rootExecutionId = request.RootExecutionId,
                depth = request.Depth,
                investigationMode = request.InvestigationMode,
                maximumApprovedChildDepth =
                    request.MaximumApprovedChildDepth,
                canCreateAnotherChild =
                    request.CanCreateAnotherChild,
                investigationGuidance =
                    BuildInvestigationGuidance(
                        request),
                originalQuestion =
                    request.OriginalRequest.Question,
                originalSnapshot =
                    request.OriginalRequest.Snapshot,
                rootAnalysis =
                    request.RootAnalysis,
                previousReanalysis =
                    request.PreviousReanalysis,
                currentChildEvidence =
                    request.CurrentChildEvidence,
                previousScenarioExecution =
                    request.PreviousScenarioExecution,
                deterministicVerification =
                    request.PreviousVerification
            };

            return
                "Re-analyze the deterministic experiment. Follow investigationMode and investigationGuidance exactly when deciding whether another bounded experiment is useful.\n"
                + JsonSerializer.Serialize(
                    payload);
        }

        private static string BuildInvestigationModeGuidance(
            RuntimeAnalysisReanalysisProviderRequest request)
        {
            if (!request.CanCreateAnotherChild)
            {
                return
                    $"The deterministic approval-driven Child DAG has reached its maximum depth of {request.MaximumApprovedChildDepth}. Conclude the analysis and set shouldContinue=false.";
            }

            if (string.Equals(
                    request.InvestigationMode,
                    RuntimeAnalysisInvestigationModes
                        .ContinueUsefulExperiments,
                    StringComparison.Ordinal))
            {
                return
                    "CONTINUE WITH ANOTHER USEFUL EXPERIMENT: Do not stop merely because the main hypothesis is already confirmed. Actively look for one materially different, bounded follow-up that can strengthen, challenge, extend, or test the robustness of the current conclusion. If such an experiment exists, set shouldContinue=true and propose it. Set shouldContinue=false only when no materially distinct bounded experiment would add useful evidence.";
            }

            return
                "STOP WHEN CONCLUSION IS STRONG: Prefer shouldContinue=false once the deterministic evidence is sufficient and no materially distinct bounded follow-up is needed to resolve a meaningful uncertainty.";
        }

        private static string BuildInvestigationGuidance(
            RuntimeAnalysisReanalysisProviderRequest request)
        {
            return BuildInvestigationModeGuidance(
                request);
        }
    }
}
