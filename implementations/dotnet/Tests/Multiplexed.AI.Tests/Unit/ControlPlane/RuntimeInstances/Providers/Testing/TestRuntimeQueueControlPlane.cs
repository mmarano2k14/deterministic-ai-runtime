using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Tests.Unit.Runtime.ControlPlane.RuntimeInstances.Providers.Testing
{
    /// <summary>
    /// Test runtime queue control-plane used by provider unit tests.
    /// </summary>
    internal sealed class TestRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
    {
        /// <summary>
        /// Gets the number of get-run-status calls.
        /// </summary>
        public int GetRunStatusCallCount { get; private set; }

        /// <summary>
        /// Gets the number of get-queue-status calls.
        /// </summary>
        public int GetQueueStatusCallCount { get; private set; }

        /// <summary>
        /// Gets the number of pause-queue calls.
        /// </summary>
        public int PauseQueueCallCount { get; private set; }

        /// <summary>
        /// Gets the number of resume-queue calls.
        /// </summary>
        public int ResumeQueueCallCount { get; private set; }

        /// <summary>
        /// Gets the number of cancel-run calls.
        /// </summary>
        public int CancelRunCallCount { get; private set; }

        /// <summary>
        /// Gets the number of cancel-queued-run calls.
        /// </summary>
        public int CancelQueuedRunCallCount { get; private set; }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            return request.Operation switch
            {
                AiRuntimeQueueControlPlaneOperation.GetRunStatus =>
                    GetRunStatusAsync(request, cancellationToken),

                AiRuntimeQueueControlPlaneOperation.GetQueueStatus =>
                    GetQueueStatusAsync(request, cancellationToken),

                AiRuntimeQueueControlPlaneOperation.PauseQueue =>
                    PauseQueueAsync(request, cancellationToken),

                AiRuntimeQueueControlPlaneOperation.ResumeQueue =>
                    ResumeQueueAsync(request, cancellationToken),

                AiRuntimeQueueControlPlaneOperation.CancelRun =>
                    CancelRunAsync(request, cancellationToken),

                AiRuntimeQueueControlPlaneOperation.CancelQueuedRun =>
                    CancelQueuedRunAsync(request, cancellationToken),

                AiRuntimeQueueControlPlaneOperation.EnqueueRun =>
                    EnqueueRunAsync(request, cancellationToken),

                _ => throw new NotSupportedException(
                    $"Operation '{request.Operation}' is not supported by this test control-plane.")
            };
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                CreateResult(request));
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            CancelRunCallCount++;

            return Task.FromResult(
                CreateResult(request));
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            CancelQueuedRunCallCount++;

            return Task.FromResult(
                CreateResult(request));
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            PauseQueueCallCount++;

            return Task.FromResult(
                CreateResult(request));
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ResumeQueueCallCount++;

            return Task.FromResult(
                CreateResult(request));
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            GetRunStatusCallCount++;

            return Task.FromResult(
                CreateResult(request));
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            GetQueueStatusCallCount++;

            return Task.FromResult(
                CreateResult(request));
        }

        /// <summary>
        /// Creates a successful runtime queue control-plane result.
        /// </summary>
        /// <param name="request">The runtime queue request.</param>
        /// <returns>The runtime queue result.</returns>
        private static AiRuntimeQueueControlPlaneResult CreateResult(
            AiRuntimeQueueControlPlaneRequest request)
        {
            var now =
                DateTimeOffset.UtcNow;

            return new AiRuntimeQueueControlPlaneResult
            {
                Operation = request.Operation,
                Success = true,
                Message = "Test operation completed.",
                RunId = request.RunId,
                CorrelationId = request.CorrelationId,
                RuntimeInstanceId = request.RuntimeInstanceId,
                RequestedBy = request.RequestedBy,
                StartedAtUtc = now,
                CompletedAtUtc = now,
                DurationMs = 0
            };
        }
    }
}