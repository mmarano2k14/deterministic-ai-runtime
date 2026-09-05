namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    public static class RuntimeAnalysisChildDagStatuses
    {
        public const string NotStarted = "NotStarted";

        public const string Running = "Running";

        public const string Completed = "Completed";

        public const string Failed = "Failed";
    }

    public sealed class RuntimeAnalysisChildDagResult
    {
        public string Status { get; init; } =
            RuntimeAnalysisChildDagStatuses.NotStarted;

        public int ExpectedDepth { get; init; }

        public int ObservedDepth { get; init; }

        public bool AllRelationsCompleted { get; init; }

        public bool AllContinuationsResumed { get; init; }

        public bool AllInvocationGenerationsZero { get; init; }

        public bool ChildExecutionIdsUnique { get; init; }

        public IReadOnlyList<RuntimeAnalysisChildDagRelationResult> Relations
        {
            get;
            init;
        } = Array.Empty<RuntimeAnalysisChildDagRelationResult>();

        public string Summary { get; init; } = string.Empty;
    }

    public sealed class RuntimeAnalysisChildDagRelationResult
    {
        public int Depth { get; init; }

        public string TenantId { get; init; } = string.Empty;

        public string ParentExecutionId { get; init; } = string.Empty;

        public string? ChildExecutionId { get; init; }

        public string ChildInvocationKey { get; init; } = string.Empty;

        public string ChildDagId { get; init; } = string.Empty;

        public string ChildDagDefinitionVersion { get; init; } = string.Empty;

        public int InvocationGeneration { get; init; }

        public string RelationStatus { get; init; } = string.Empty;

        public string ContinuationStatus { get; init; } = string.Empty;

        public string? ChildResultDigest { get; init; }

        public string? ChildFailureReason { get; init; }

        public DateTimeOffset CreatedAtUtc { get; init; }

        public DateTimeOffset? CompletedAtUtc { get; init; }

        public DateTimeOffset? ParentResumedAtUtc { get; init; }

        public string RuntimeStatus { get; init; } = string.Empty;

        public string CurrentStep { get; init; } = string.Empty;

        public string InvestigationMode { get; init; } =
            RuntimeAnalysisInvestigationModes.StopWhenConclusive;

        public RuntimeAnalysisReanalysisResult? Reanalysis { get; init; }

        public RuntimeAnalysisScenarioPolicyValidationResult? PolicyValidation
        {
            get;
            init;
        }

        public RuntimeAnalysisHumanApprovalResult? HumanApproval { get; init; }

        public RuntimeAnalysisScenarioExecutionResult? ScenarioExecution
        {
            get;
            init;
        }

        public RuntimeAnalysisVerificationResult? Verification { get; init; }
    }

    public sealed class RuntimeAnalysisChildDagNodeEvidence
    {
        public int Depth { get; init; }

        public string RootExecutionId { get; init; } = string.Empty;

        public string CurrentExecutionId { get; init; } = string.Empty;

        public string ScenarioName { get; init; } = string.Empty;

        public string ScenarioExecutionStatus { get; init; } = string.Empty;

        public int ObservedCompleted { get; init; }

        public int ObservedInFlight { get; init; }

        public int ObservedErrors { get; init; }

        public string AiSeverity { get; init; } = string.Empty;

        public bool PolicyAllowed { get; init; }
    }
}
