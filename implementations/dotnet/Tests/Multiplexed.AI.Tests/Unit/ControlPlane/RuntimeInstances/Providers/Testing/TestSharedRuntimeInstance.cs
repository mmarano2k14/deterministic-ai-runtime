using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Testing
{
    /// <summary>
    /// Test shared runtime instance used by provider unit tests.
    /// </summary>
    internal sealed class TestSharedRuntimeInstance : IAiSharedRuntimeInstance
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TestSharedRuntimeInstance"/> class.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="queueControlPlane">The queue control-plane.</param>
        public TestSharedRuntimeInstance(
            string runtimeInstanceId,
            IAiRuntimeQueueControlPlane queueControlPlane)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(queueControlPlane);

            RuntimeInstanceId = runtimeInstanceId;
            QueueControlPlane = queueControlPlane;
        }

        /// <inheritdoc />
        public string RuntimeInstanceId { get; }

        /// <inheritdoc />
        public IAiRuntimeQueueControlPlane QueueControlPlane { get; }

        /// <summary>
        /// Gets the number of dispatch calls received by this instance.
        /// </summary>
        public int DispatchCallCount { get; private set; }

        /// <inheritdoc />
        public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
            AiSharedRuntimeInstanceDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            DispatchCallCount++;

            var now =
                DateTimeOffset.UtcNow;

            return Task.FromResult(
                new AiSharedRuntimeInstanceDispatchResult
                {
                    Success = true,
                    RuntimeInstanceId = RuntimeInstanceId,
                    SharedRunId = request.SharedRun.SharedRunId,
                    LocalRunId = "local-run-1",
                    ExecutionId = "execution-1",
                    ClaimToken = request.ClaimToken,
                    StartedAtUtc = now,
                    CompletedAtUtc = now,
                    DurationMs = 0
                });
        }
    }
}