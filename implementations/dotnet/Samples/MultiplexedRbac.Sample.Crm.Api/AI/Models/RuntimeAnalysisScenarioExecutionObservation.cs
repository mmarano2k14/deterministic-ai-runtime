namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisScenarioExecutionObservation
    {
        public string ClientState { get; init; } = string.Empty;

        public DateTimeOffset StartedAtUtc { get; init; }

        public DateTimeOffset FinishedAtUtc { get; init; }

        public int Completed { get; init; }

        public int InFlight { get; init; }

        public int Ok { get; init; }

        public int Unauthorized { get; init; }

        public int Forbidden { get; init; }

        public int TooManyRequests { get; init; }

        public int OtherHttp { get; init; }

        public int Errors { get; init; }

        public double? P50Ms { get; init; }

        public double? P95Ms { get; init; }

        public double? ElapsedMs { get; init; }

        public string? Error { get; init; }
    }
}
