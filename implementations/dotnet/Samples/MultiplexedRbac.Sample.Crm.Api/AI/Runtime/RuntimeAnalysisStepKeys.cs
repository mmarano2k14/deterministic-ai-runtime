namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public static class RuntimeAnalysisStepKeys
    {
        public const string AnalyzeWithOpenAi = "runtime-analysis.openai";

        public const string ValidateSuggestedScenario =
            "runtime-analysis.validate-suggested-scenario";
    }

    public static class RuntimeAnalysisStepConfigKeys
    {
        public const string ProviderRequestJson = "providerRequestJson";

        public const string SuggestedScenarioJson = "suggestedScenarioJson";

        public const string ScenarioPolicyDefinition =
            "scenarioPolicyValidation";
    }
}
