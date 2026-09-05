using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Providers;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Services
{
    public sealed class RuntimeAnalysisResultValidator
    {
        private static readonly HashSet<string> SupportedSeverities =
            new(
                new[]
                {
                    "info",
                    "low",
                    "medium",
                    "high",
                    "critical"
                },
                StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SupportedScenarioTypes =
            new(
                new[]
                {
                    "single-burst",
                    "maintained-concurrency",
                    "wave-batches",
                    "wave-batches-staggered",
                    "custom"
                },
                StringComparer.OrdinalIgnoreCase);

        public void Validate(
            RuntimeAnalysisResult result,
            RuntimeAnalysisSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(
                result);
            ArgumentNullException.ThrowIfNull(
                snapshot);

            RequireText(
                result.Answer,
                nameof(result.Answer));
            RequireText(
                result.Summary,
                nameof(result.Summary));

            if (!SupportedSeverities.Contains(
                    result.Severity))
            {
                throw new RuntimeAnalysisProviderException(
                    $"Unsupported AI severity '{result.Severity}'.");
            }

            if (result.Confidence < 0
                || result.Confidence > 1)
            {
                throw new RuntimeAnalysisProviderException(
                    "AI confidence must be between 0 and 1.");
            }

            foreach (var observation in result.Observations)
            {
                ValidateObservation(
                    observation,
                    snapshot.Evidence.Count);
            }

            ValidateSuggestedScenario(
                result.SuggestedScenario);
        }

        private static void ValidateObservation(
            RuntimeAnalysisObservation observation,
            int evidenceCount)
        {
            RequireText(
                observation.Title,
                nameof(observation.Title));
            RequireText(
                observation.Detail,
                nameof(observation.Detail));

            foreach (var evidenceIndex in observation.EvidenceIndexes)
            {
                if (evidenceIndex < 0
                    || evidenceIndex >= evidenceCount)
                {
                    throw new RuntimeAnalysisProviderException(
                        $"AI evidence index {evidenceIndex} is outside the snapshot evidence range.");
                }
            }
        }

        private static void ValidateSuggestedScenario(
            RuntimeAnalysisSuggestedScenario scenario)
        {
            RequireText(
                scenario.Name,
                nameof(scenario.Name));
            RequireText(
                scenario.Rationale,
                nameof(scenario.Rationale));

            if (!SupportedScenarioTypes.Contains(
                    scenario.ScenarioType))
            {
                throw new RuntimeAnalysisProviderException(
                    $"Unsupported AI scenario type '{scenario.ScenarioType}'.");
            }

            EnsurePositive(
                scenario.TotalRequests,
                nameof(scenario.TotalRequests));
            EnsurePositive(
                scenario.Concurrency,
                nameof(scenario.Concurrency));
            EnsurePositive(
                scenario.BatchSize,
                nameof(scenario.BatchSize));
            EnsureNonNegative(
                scenario.DelayMs,
                nameof(scenario.DelayMs));
            EnsureNonNegative(
                scenario.WavePauseMs,
                nameof(scenario.WavePauseMs));
            EnsurePositive(
                scenario.MaxInFlight,
                nameof(scenario.MaxInFlight));
            EnsureNonNegative(
                scenario.RotationOverlapMs,
                nameof(scenario.RotationOverlapMs));
            EnsurePositive(
                scenario.DurationSeconds,
                nameof(scenario.DurationSeconds));
        }

        private static void RequireText(
            string value,
            string name)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                throw new RuntimeAnalysisProviderException(
                    $"AI result field '{name}' cannot be empty.");
            }
        }

        private static void EnsurePositive(
            int value,
            string name)
        {
            if (value < 1)
            {
                throw new RuntimeAnalysisProviderException(
                    $"AI result field '{name}' must be greater than zero.");
            }
        }

        private static void EnsurePositive(
            int? value,
            string name)
        {
            if (value.HasValue
                && value.Value < 1)
            {
                throw new RuntimeAnalysisProviderException(
                    $"AI result field '{name}' must be greater than zero when provided.");
            }
        }

        private static void EnsureNonNegative(
            int value,
            string name)
        {
            if (value < 0)
            {
                throw new RuntimeAnalysisProviderException(
                    $"AI result field '{name}' cannot be negative.");
            }
        }

        private static void EnsureNonNegative(
            int? value,
            string name)
        {
            if (value.HasValue
                && value.Value < 0)
            {
                throw new RuntimeAnalysisProviderException(
                    $"AI result field '{name}' cannot be negative when provided.");
            }
        }
    }
}
