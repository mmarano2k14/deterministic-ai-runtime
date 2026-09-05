using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Policies
{
    public sealed class RuntimeAnalysisScenarioSafetyPolicy :
        AiPolicyBase<RuntimeAnalysisScenarioPolicyContext>
    {
        public override string Key =>
            RuntimeAnalysisScenarioPolicyKeys.Safety;

        public override AiPolicyKind Kind =>
            AiPolicyKind.Validation;

        public override Task<AiPolicyResult> ExecuteAsync(
            RuntimeAnalysisScenarioPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            cancellationToken.ThrowIfCancellationRequested();

            var allowedScenarioTypes =
                context.GetRequiredPolicyConfig<string[]>(
                    Key,
                    "allowedScenarioTypes");

            var allowedPlanKeys =
                context.GetRequiredPolicyConfig<string[]>(
                    Key,
                    "allowedPlanKeys");

            var allowedScenarioTypeSet =
                new HashSet<string>(
                    allowedScenarioTypes,
                    StringComparer.Ordinal);

            var allowedPlanKeySet =
                new HashSet<string>(
                    allowedPlanKeys,
                    StringComparer.Ordinal);

            var scenario = context.Scenario;

            if (!allowedPlanKeySet.Contains(
                    context.PlanKey))
            {
                return Block(
                    $"Plan '{context.PlanKey}' is not allowed by the configured safety policy.");
            }

            if (!allowedScenarioTypeSet.Contains(
                    scenario.ScenarioType))
            {
                return Block(
                    $"Scenario type '{scenario.ScenarioType}' is not allowed by the configured safety policy.");
            }

            if (string.IsNullOrWhiteSpace(
                    scenario.Name))
            {
                return Block(
                    "Suggested scenario must have a name.");
            }

            if (string.Equals(
                    scenario.ScenarioType,
                    "maintained-concurrency",
                    StringComparison.Ordinal)
                && scenario.Concurrency is null or < 1)
            {
                return Block(
                    "Maintained-concurrency scenarios require a positive concurrency value.");
            }

            if ((string.Equals(
                        scenario.ScenarioType,
                        "wave-batches",
                        StringComparison.Ordinal)
                    || string.Equals(
                        scenario.ScenarioType,
                        "wave-batches-staggered",
                        StringComparison.Ordinal))
                && scenario.BatchSize is null or < 1)
            {
                return Block(
                    "Wave scenarios require a positive batch size.");
            }

            return Task.FromResult(
                AiPolicyResult.Success(
                    $"Scenario uses pipeline-approved mode '{scenario.ScenarioType}' and plan '{context.PlanKey}'."));
        }

        private static Task<AiPolicyResult> Block(
            string message)
        {
            return Task.FromResult(
                AiPolicyResult.Block(
                    message));
        }
    }
}
