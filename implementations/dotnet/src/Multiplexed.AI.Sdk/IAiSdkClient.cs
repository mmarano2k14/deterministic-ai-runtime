using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Publication;

namespace Multiplexed.AI.Sdk
{
    /// <summary>Typed client surface over the portable public SDK boundary.</summary>
    public interface IAiSdkClient
    {
        Task<AiSdkPipelinePublicationResponse> PublishPipelineAsync(
            AiSdkPipelinePublicationRequest request,
            CancellationToken cancellationToken = default);

        Task<AiSdkExecutionSubmissionResponse> SubmitExecutionAsync(
            AiSdkExecutionSubmissionRequest request,
            CancellationToken cancellationToken = default);

        Task<AiSdkExecutionObservation> ObserveExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        Task<AiSdkExecutionResult> GetExecutionResultAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        Task<AiSdkExecutionCancellationResponse> CancelExecutionAsync(
            string executionId,
            AiSdkExecutionCancellationRequest request,
            CancellationToken cancellationToken = default);
    }
}
