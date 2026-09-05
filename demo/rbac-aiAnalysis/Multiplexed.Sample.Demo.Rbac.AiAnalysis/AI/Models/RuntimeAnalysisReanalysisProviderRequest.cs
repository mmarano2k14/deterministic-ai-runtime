namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    /// <summary>
    /// Bounded post-execution evidence sent to the AI re-analysis provider.
    /// </summary>
    public sealed class RuntimeAnalysisReanalysisProviderRequest
    {
        public string RootExecutionId { get; init; } = string.Empty;

        public int Depth { get; init; }

        public string InvestigationMode { get; init; } =
            RuntimeAnalysisInvestigationModes.StopWhenConclusive;

        public int MaximumApprovedChildDepth { get; init; }

        public bool CanCreateAnotherChild { get; init; }

        public RuntimeAnalysisProviderRequest OriginalRequest { get; init; } =
            new RuntimeAnalysisProviderRequest();

        public RuntimeAnalysisResult RootAnalysis { get; init; } =
            new RuntimeAnalysisResult();

        public RuntimeAnalysisReanalysisResult? PreviousReanalysis { get; init; }

        public RuntimeAnalysisChildDagNodeEvidence CurrentChildEvidence { get; init; } =
            new RuntimeAnalysisChildDagNodeEvidence();

        public RuntimeAnalysisScenarioExecutionResult PreviousScenarioExecution
        {
            get;
            init;
        } = new RuntimeAnalysisScenarioExecutionResult();

        public RuntimeAnalysisVerificationResult PreviousVerification { get; init; } =
            new RuntimeAnalysisVerificationResult();
    }
}
