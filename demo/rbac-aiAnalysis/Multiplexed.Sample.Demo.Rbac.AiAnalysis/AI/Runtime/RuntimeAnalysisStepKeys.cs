namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
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

        public const string CaptureChildDagEvidence =
            "runtime-analysis.capture-child-dag-evidence";

        public const string ReanalyzeVerifiedOutcome =
            "runtime-analysis.reanalyze-verified-outcome";

        public const string ValidateReanalysisScenario =
            "runtime-analysis.validate-reanalysis-scenario";

        public const string ExecuteApprovedChildDag =
            "runtime-analysis.execute-approved-child-dag";

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

        public const string RootExecutionId =
            "rootExecutionId";

        public const string ProviderRequestJson =
            "providerRequestJson";

        public const string RootAnalysisResultJson =
            "rootAnalysisResultJson";

        public const string PreviousReanalysisJson =
            "previousReanalysisJson";

        public const string ReanalysisResultJson =
            "reanalysisResultJson";

        public const string ChildDagEvidenceJson =
            "childDagEvidenceJson";

        public const string VerificationJson =
            "verificationJson";
    }

    public static class RuntimeAnalysisStepConfigKeys
    {
        public const string ProviderRequestJson =
            "providerRequestJson";

        public const string ScenarioPolicyDefinition =
            "scenarioPolicyValidation";

        public const string ChildDagDepth =
            "childDagDepth";
    }
}
