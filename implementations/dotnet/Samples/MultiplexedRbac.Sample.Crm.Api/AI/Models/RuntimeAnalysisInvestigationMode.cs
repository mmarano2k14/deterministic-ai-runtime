namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public static class RuntimeAnalysisInvestigationModes
    {
        public const string StopWhenConclusive =
            "stop-when-conclusive";

        public const string ContinueUsefulExperiments =
            "continue-useful-experiments";

        public static bool IsSupported(
            string value)
        {
            return string.Equals(
                       value,
                       StopWhenConclusive,
                       StringComparison.Ordinal)
                   || string.Equals(
                       value,
                       ContinueUsefulExperiments,
                       StringComparison.Ordinal);
        }
    }
}
