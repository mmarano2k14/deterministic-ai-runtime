using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake runtime instance readiness waiter used by unit tests.
    /// </summary>
    public sealed class FakeRuntimeInstanceReadinessWaiter : IAiRuntimeInstanceReadinessWaiter
    {
        /// <summary>
        /// Gets the readiness requests observed by the fake waiter.
        /// </summary>
        public List<AiRuntimeInstanceReadinessRequest> Requests { get; } = [];

        /// <summary>
        /// Gets or sets the explicit readiness result returned by the fake waiter.
        /// </summary>
        public AiRuntimeInstanceReadinessResult? Result { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether readiness should succeed.
        /// </summary>
        public bool Success { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether readiness should time out.
        /// </summary>
        public bool TimedOut { get; set; }

        /// <summary>
        /// Gets or sets the readiness failure reason.
        /// </summary>
        public string? FailureReason { get; set; }

        /// <summary>
        /// Gets or sets the readiness message.
        /// </summary>
        public string? Message { get; set; }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
            AiRuntimeInstanceReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(request);

            if (Result is not null)
            {
                return Task.FromResult(
                    new AiRuntimeInstanceReadinessResult
                    {
                        Success = Result.Success,
                        TimedOut = Result.TimedOut,
                        RuntimeInstanceId = Result.RuntimeInstanceId ?? request.RuntimeInstanceId,
                        ProviderName = Result.ProviderName ?? request.ProviderName,
                        TransportName = Result.TransportName ?? request.TransportName,
                        TransportEndpoint = Result.TransportEndpoint ?? request.TransportEndpoint,
                        FailureReason = Result.FailureReason,
                        ExecutionContextSnapshot = Result.ExecutionContextSnapshot ?? request.ExecutionContextSnapshot
                    });
            }

            return Task.FromResult(
                new AiRuntimeInstanceReadinessResult
                {
                    Success = Success,
                    TimedOut = TimedOut,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    ProviderName = request.ProviderName,
                    TransportName = request.TransportName,
                    TransportEndpoint = request.TransportEndpoint,
                    FailureReason = Success ? null : FailureReason ?? "fake-runtime-readiness-failed",
                    ExecutionContextSnapshot = request.ExecutionContextSnapshot
                });
        }
    }
}