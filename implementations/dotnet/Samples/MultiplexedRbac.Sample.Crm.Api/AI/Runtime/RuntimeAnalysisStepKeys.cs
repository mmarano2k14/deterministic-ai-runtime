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

        public const string ExecuteApprovedScenario =
            "runtime-analysis.execute-approved-scenario";

        public const string VerifyScenarioOutcome =
            "runtime-analysis.verify-scenario-outcome";
    }

    public static class RuntimeAnalysisStepInputKeys
    {
        public const string AnalysisResultJson =
            "analysisResultJson";

        public const string PolicyValidationJson =
            "policyValidationJson";

        public const string HumanApprovalJson =
            "humanApprovalJson";

        public const string ScenarioExecutionJson =
            "scenarioExecutionJson";
    }

    public static class RuntimeAnalysisStepConfigKeys
    {
        public const string ProviderRequestJson =
            "providerRequestJson";

        public const string ScenarioPolicyDefinition =
            "scenarioPolicyValidation";
    }
}
