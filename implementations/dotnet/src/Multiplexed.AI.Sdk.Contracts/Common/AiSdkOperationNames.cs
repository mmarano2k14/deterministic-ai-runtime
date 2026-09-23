namespace Multiplexed.AI.Sdk.Contracts.Common
{
    /// <summary>Stable operation names exposed by the public SDK server boundary.</summary>
    public static class AiSdkOperationNames
    {
        public const string PublishPipeline = "sdk.publish_pipeline";
        public const string SubmitExecution = "sdk.execution.submit";
        public const string ObserveExecution = "sdk.execution.observe";
        public const string WatchExecution = "sdk.execution.watch";
        public const string GetExecutionResult = "sdk.execution.result";
        public const string CancelExecution = "sdk.execution.cancel";
        public const string PauseExecution = "sdk.execution.pause";
        public const string ResumeExecution = "sdk.execution.resume";
        public const string SubmitExecutionInput = "sdk.execution.input.submit";
        public const string ReplayExecution = "sdk.execution.replay";
    }
}
