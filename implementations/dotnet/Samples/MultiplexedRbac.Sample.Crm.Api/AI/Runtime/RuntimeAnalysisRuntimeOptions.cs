namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisRuntimeOptions
    {
        public TimeSpan ExecutionTimeout { get; init; } =
            TimeSpan.FromSeconds(
                120);
    }
}
