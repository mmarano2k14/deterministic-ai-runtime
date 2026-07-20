using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Provides a test scale-out fulfilled run requeue service.
    /// </summary>
    public sealed class TestScaleOutFulfilledRunRequeueService :
        IAiScaleOutFulfilledRunRequeueService
    {
        /// <summary>
        /// Gets the requeue call count.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// Gets the last scale-out request.
        /// </summary>
        public AiRuntimeScaleOutRequestRecord? LastRequest { get; private set; }

        /// <summary>
        /// Gets the last runtime instance identifier.
        /// </summary>
        public string? LastRuntimeInstanceId { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether a linked shared run should be found.
        /// </summary>
        public bool LinkedSharedRunFound { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether requeue should succeed.
        /// </summary>
        public bool ShouldSucceed { get; set; } = true;

        /// <summary>
        /// Gets or sets the candidate count returned by the fake service.
        /// </summary>
        public int CandidateCount { get; set; } = 1;

        /// <summary>
        /// Gets or sets the optional result reason.
        /// </summary>
        public string? Reason { get; set; }

        /// <inheritdoc />
        public Task<AiScaleOutFulfilledRunRequeueResult> RequeueAsync(
            AiRuntimeScaleOutRequestRecord request,
            string? runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            this.CallCount++;
            this.LastRequest = request;
            this.LastRuntimeInstanceId = runtimeInstanceId;

            if (!this.LinkedSharedRunFound)
            {
                return Task.FromResult(
                    AiScaleOutFulfilledRunRequeueResult.NoLinkedSharedRun(
                        request.SharedRunId));
            }

            if (this.ShouldSucceed)
            {
                return Task.FromResult(
                    AiScaleOutFulfilledRunRequeueResult.Succeeded(
                        request.SharedRunId ?? string.Empty,
                        this.CandidateCount,
                        this.Reason ?? "Test requeue succeeded."));
            }

            return Task.FromResult(
                AiScaleOutFulfilledRunRequeueResult.Failed(
                    request.SharedRunId ?? string.Empty,
                    this.CandidateCount,
                    this.Reason ?? "Test requeue failed."));
        }
    }
}