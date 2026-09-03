namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public static class RuntimeAnalysisScopes
    {
        public const string CurrentRun = "current-run";
        public const string CurrentExecution = "current-execution";
        public const string SelectedLogs = "selected-logs";
        public const string Last30Seconds = "last-30s";

        private static readonly HashSet<string> SupportedScopes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                CurrentRun,
                CurrentExecution,
                SelectedLogs,
                Last30Seconds
            };

        public static bool IsSupported(
            string? scope)
        {
            return !string.IsNullOrWhiteSpace(scope)
                && SupportedScopes.Contains(scope);
        }
    }
}
