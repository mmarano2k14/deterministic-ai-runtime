using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Providers;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Services
{
    public sealed class RuntimeAnalysisReanalysisResultValidator
    {
        private static readonly HashSet<string> Conclusions =
            new(
                new[]
                {
                    RuntimeAnalysisReanalysisConclusions.Confirmed,
                    RuntimeAnalysisReanalysisConclusions.Weakened,
                    RuntimeAnalysisReanalysisConclusions.NotReproduced,
                    RuntimeAnalysisReanalysisConclusions.Inconclusive
                },
                StringComparer.Ordinal);

        private readonly RuntimeAnalysisResultValidator _analysisValidator;

        public RuntimeAnalysisReanalysisResultValidator(
            RuntimeAnalysisResultValidator analysisValidator)
        {
            _analysisValidator =
                analysisValidator
                ?? throw new ArgumentNullException(
                    nameof(analysisValidator));
        }

        public void Validate(
            RuntimeAnalysisReanalysisResult result,
            RuntimeAnalysisSnapshot originalSnapshot)
        {
            ArgumentNullException.ThrowIfNull(
                result);
            ArgumentNullException.ThrowIfNull(
                originalSnapshot);

            if (!Conclusions.Contains(
                    result.Conclusion))
            {
                throw new RuntimeAnalysisProviderException(
                    $"Unsupported re-analysis conclusion '{result.Conclusion}'.");
            }

            RequireText(
                result.Answer,
                nameof(result.Answer));
            RequireText(
                result.Summary,
                nameof(result.Summary));

            if (result.Confidence < 0
                || result.Confidence > 1)
            {
                throw new RuntimeAnalysisProviderException(
                    "Re-analysis confidence must be between 0 and 1.");
            }

            if (result.Reasons.Count > 6)
            {
                throw new RuntimeAnalysisProviderException(
                    "Re-analysis cannot contain more than 6 reasons.");
            }

            foreach (var reason in result.Reasons)
            {
                RequireText(
                    reason,
                    "Reason");
            }

            // Reuse the established scenario-domain validation boundary by
            // wrapping the proposal in the existing result contract.
            _analysisValidator.Validate(
                new RuntimeAnalysisResult
                {
                    Answer = result.Answer,
                    Summary = result.Summary,
                    Severity = "info",
                    Confidence = result.Confidence,
                    Observations = Array.Empty<RuntimeAnalysisObservation>(),
                    SuggestedScenario = result.SuggestedScenario
                },
                originalSnapshot);
        }

        private static void RequireText(
            string value,
            string name)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                throw new RuntimeAnalysisProviderException(
                    $"Re-analysis field '{name}' cannot be empty.");
            }
        }
    }
}
