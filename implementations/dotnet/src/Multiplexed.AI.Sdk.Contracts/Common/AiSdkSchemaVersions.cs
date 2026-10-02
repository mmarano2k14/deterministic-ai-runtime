namespace Multiplexed.AI.Sdk.Contracts.Common
{
    /// <summary>Current schema versions for independently evolvable public SDK wire documents.</summary>
    public static class AiSdkSchemaVersions
    {
        public const int PipelineDefinition = 1;
        public const int PipelinePublicationRequest = 1;
        public const int PipelinePublicationResponse = 1;
        public const int ExecutionSubmissionRequest = 1;
        public const int ExecutionSubmissionResponse = 1;
        public const int ExecutionObservation = 1;
        public const int ExecutionResult = 1;
        public const int ExecutionCancellationRequest = 1;
        public const int ExecutionCancellationResponse = 1;
        public const int ExecutionControlRequest = 1;
        public const int ExecutionControlResponse = 1;
        public const int ExecutionInputSubmissionRequest = 1;
        public const int ExecutionReplayRequest = 1;
        public const int ExecutionReplayResponse = 1;
        public const int ExecutionWatchRequest = 1;
        public const int ExecutionWatchEvent = 1;
        public const int ExecutionWatchResyncRequired = 1;
    }
}
