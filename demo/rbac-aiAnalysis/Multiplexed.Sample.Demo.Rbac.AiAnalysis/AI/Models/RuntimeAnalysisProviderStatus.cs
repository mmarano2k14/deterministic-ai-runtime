namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    public sealed class RuntimeAnalysisProviderStatus
    {
        public string Provider { get; init; } = string.Empty;

        public string Model { get; init; } = string.Empty;

        public bool Configured { get; init; }
    }
}
