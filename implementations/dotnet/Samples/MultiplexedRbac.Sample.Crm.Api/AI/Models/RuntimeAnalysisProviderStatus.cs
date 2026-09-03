namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisProviderStatus
    {
        public string Provider { get; init; } = string.Empty;

        public string Model { get; init; } = string.Empty;

        public bool Configured { get; init; }
    }
}
