using Multiplexed.Abstractions.AI.Policies;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Policies
{
    /// <summary>
    /// Declarative scenario-policy configuration attached to the validation step.
    /// The runtime resolves this object from step configuration at execution time.
    /// </summary>
    public sealed class RuntimeAnalysisScenarioPolicyDefinition
    {
        public string PlanKey { get; init; } = string.Empty;

        public bool RequireHumanApproval { get; init; } = true;

        public List<AiConfiguredPolicyDefinition> Policies { get; set; } =
            new();
    }
}
