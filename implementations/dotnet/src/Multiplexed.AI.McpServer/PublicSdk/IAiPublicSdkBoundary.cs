using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Sdk.Contracts.Replay;
using Multiplexed.AI.Sdk.Contracts.Watch;

namespace Multiplexed.AI.McpServer.PublicSdk
{
    /// <summary>Server-owned public SDK operations. No runtime identity is accepted from the caller.</summary>
    public interface IAiPublicSdkBoundary
    {
        Task<AiSdkPipelinePublicationResponse> PublishAsync(AiSdkPipelinePublicationRequest request, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionSubmissionResponse> SubmitAsync(AiSdkExecutionSubmissionRequest request, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionObservation> ObserveAsync(string executionId, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionWatchEvent> WatchAsync(AiSdkExecutionWatchRequest request, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionResult> GetResultAsync(string executionId, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionCancellationResponse> CancelAsync(string executionId, AiSdkExecutionCancellationRequest request, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionControlResponse> PauseAsync(string executionId, AiSdkExecutionControlRequest request, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionControlResponse> ResumeAsync(string executionId, AiSdkExecutionControlRequest request, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionControlResponse> SubmitInputAsync(string executionId, AiSdkExecutionInputSubmissionRequest request, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionReplayResponse> ReplayAsync(string executionId, AiSdkExecutionReplayRequest request, CancellationToken cancellationToken = default);
    }
}
