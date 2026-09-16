namespace Multiplexed.AI.Sdk.Contracts.Common
{
    /// <summary>Stable operation names exposed by the public SDK server boundary.</summary>
    public static class AiSdkOperationNames
    {
        public const string PublishPipeline = "sdk.publish_pipeline";
        public const string SubmitExecution = "sdk.execution.submit";
        public const string ObserveExecution = "sdk.execution.observe";
        public const string GetExecutionResult = "sdk.execution.result";
        public const string CancelExecution = "sdk.execution.cancel";
    }
}
