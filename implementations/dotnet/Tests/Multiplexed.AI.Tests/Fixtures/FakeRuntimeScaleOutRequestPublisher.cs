using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake scale-out request publisher used by tests that construct
    /// <c>AiSharedQueueDispatcher</c> directly.
    /// </summary>
    public sealed class FakeRuntimeScaleOutRequestPublisher : IAiRuntimeScaleOutRequestPublisher
    {
        /// <inheritdoc />
        public Task<AiRuntimeScaleOutRequestResult> PublishAsync(
            AiRuntimeScaleOutRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            var requestedTargetInstanceCount =
                request.MaxInstanceCount.HasValue
                    ? Math.Min(Math.Max(request.CurrentInstanceCount + 1, 1), request.MaxInstanceCount.Value)
                    : Math.Max(request.CurrentInstanceCount + 1, 1);

            return Task.FromResult(
                new AiRuntimeScaleOutRequestResult
                {
                    Success = true,
                    SharedRunId = request.SharedRunId,
                    ScaleOutRequestId = $"scale-out-{request.SharedRunId}",
                    RequestedTargetInstanceCount = requestedTargetInstanceCount,
                    Message = "fake-scale-out-request-published",
                    PublishedAtUtc = DateTimeOffset.UtcNow
                });
        }
    }
}