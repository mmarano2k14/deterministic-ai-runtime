namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public sealed class RuntimeAnalysisRuntimeOptions
    {
        public TimeSpan ExecutionTimeout { get; init; } =
            TimeSpan.FromSeconds(
                120);
    }
}
