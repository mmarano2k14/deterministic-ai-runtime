namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    public sealed class RuntimeAnalysisScenarioPolicyDecision
    {
        public string PolicyKey { get; init; } = string.Empty;

        public string ResultKind { get; init; } = string.Empty;

        public bool Allowed { get; init; }

        public string Message { get; init; } = string.Empty;
    }
}
