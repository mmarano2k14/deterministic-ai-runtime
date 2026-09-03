namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public static class RuntimeAnalysisStepKeys
    {
        public const string AnalyzeWithOpenAi =
            "runtime-analysis.openai";

        public const string ValidateSuggestedScenario =
            "runtime-analysis.validate-suggested-scenario";

        public const string AwaitHumanApproval =
            "runtime-analysis.await-human-approval";
    }

    public static class RuntimeAnalysisStepInputKeys
    {
        public const string AnalysisResultJson =
            "analysisResultJson";

        public const string PolicyValidationJson =
            "policyValidationJson";
    }

    public static class RuntimeAnalysisStepConfigKeys
    {
        public const string ProviderRequestJson = "providerRequestJson";

        public const string ScenarioPolicyDefinition =
            "scenarioPolicyValidation";
    }
}
