using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Policies
{
    public sealed class RuntimeAnalysisScenarioPolicyEvaluation
    {
        public RuntimeAnalysisScenarioPolicyDefinition Definition { get; init; } =
            new RuntimeAnalysisScenarioPolicyDefinition();

        public IReadOnlyList<AiConfiguredPolicyDefinition>
            ConfiguredPolicies { get; init; } =
                Array.Empty<AiConfiguredPolicyDefinition>();

        public IReadOnlyCollection<AiPolicyResult> Results { get; init; } =
            Array.Empty<AiPolicyResult>();
    }
}
