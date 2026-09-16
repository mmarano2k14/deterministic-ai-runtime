using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Publication;

namespace Multiplexed.AI.McpServer.PublicSdk
{
    /// <summary>Server-owned public SDK operations. No runtime identity is accepted from the caller.</summary>
    public interface IAiPublicSdkBoundary
    {
        Task<AiSdkPipelinePublicationResponse> PublishAsync(AiSdkPipelinePublicationRequest request, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionSubmissionResponse> SubmitAsync(AiSdkExecutionSubmissionRequest request, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionObservation> ObserveAsync(string executionId, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionResult> GetResultAsync(string executionId, CancellationToken cancellationToken = default);
        Task<AiSdkExecutionCancellationResponse> CancelAsync(string executionId, AiSdkExecutionCancellationRequest request, CancellationToken cancellationToken = default);
    }
}
