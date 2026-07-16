using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.AI.Runtime.ControlPlane.Signals;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Signals
{
    /// <summary>
    /// Provides Redis integration tests for internal runtime state-change signals.
    /// </summary>
    public sealed class RedisAiRuntimeSignalTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeSignalTests"/> class.
        /// </summary>
        /// <param name="output">The xUnit test output helper.</param>
        public RedisAiRuntimeSignalTests(
            ITestOutputHelper output)
        {
            ArgumentNullException.ThrowIfNull(output);

            _output = output;
        }

        /// <summary>
        /// Verifies that a targeted DAG progress subscriber receives the published signal.
        /// </summary>
        [Fact]
        public async Task Publish_DagProgressChanged_Should_Wake_Targeted_Subscriber()
        {
            using var multiplexer = await ConnectionMultiplexer
                .ConnectAsync("localhost:6379")
                .ConfigureAwait(false);

            var publisher = new RedisAiRuntimeSignalPublisher(
                multiplexer,
                NullLogger<RedisAiRuntimeSignalPublisher>.Instance);

            var subscriber = new RedisAiRuntimeSignalSubscriber(
                multiplexer,
                NullLogger<RedisAiRuntimeSignalSubscriber>.Instance);

            var controlPlaneId = $"signal-test-{Guid.NewGuid():N}";
            var executionId = Guid.NewGuid().ToString("N");

            await using var subscription = await subscriber
                .SubscribeAsync(
                    AiRuntimeSignalType.DagProgressChanged,
                    controlPlaneId,
                    executionId)
                .ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));

            var receiveTask = ReadNextAsync(
                subscription,
                timeout.Token);

            var expected = new AiRuntimeSignal
            {
                Type = AiRuntimeSignalType.DagProgressChanged,
                ControlPlaneId = controlPlaneId,
                TenantId = "tenant-001",
                ExecutionId = executionId,
                RuntimeInstanceId = "runtime-001",
                CompletedStepCount = 25,
                TotalStepCount = 50,
                ExecutionVersion = 26
            };

            await publisher
                .PublishAsync(expected)
                .ConfigureAwait(false);

            var received = await receiveTask.ConfigureAwait(false);

            _output.WriteLine(
                "Received DAG progress signal. " +
                $"ControlPlaneId='{received.ControlPlaneId}', " +
                $"ExecutionId='{received.ExecutionId}', " +
                $"RuntimeInstanceId='{received.RuntimeInstanceId}', " +
                $"CompletedStepCount='{received.CompletedStepCount}', " +
                $"TotalStepCount='{received.TotalStepCount}'.");

            Assert.Multiple(
                () => Assert.Equal(
                    AiRuntimeSignalType.DagProgressChanged,
                    received.Type),

                () => Assert.Equal(
                    controlPlaneId,
                    received.ControlPlaneId),

                () => Assert.Equal(
                    "tenant-001",
                    received.TenantId),

                () => Assert.Equal(
                    executionId,
                    received.ExecutionId),

                () => Assert.Equal(
                    "runtime-001",
                    received.RuntimeInstanceId),

                () => Assert.Equal(
                    25,
                    received.CompletedStepCount),

                () => Assert.Equal(
                    50,
                    received.TotalStepCount),

                () => Assert.Equal(
                    26,
                    received.ExecutionVersion));
        }

        /// <summary>
        /// Verifies that a targeted shared-run subscriber receives the dispatch signal.
        /// </summary>
        [Fact]
        public async Task Publish_SharedRunDispatched_Should_Wake_Targeted_Subscriber()
        {
            using var multiplexer = await ConnectionMultiplexer
                .ConnectAsync("localhost:6379")
                .ConfigureAwait(false);

            var publisher = new RedisAiRuntimeSignalPublisher(
                multiplexer,
                NullLogger<RedisAiRuntimeSignalPublisher>.Instance);

            var subscriber = new RedisAiRuntimeSignalSubscriber(
                multiplexer,
                NullLogger<RedisAiRuntimeSignalSubscriber>.Instance);

            var controlPlaneId = $"signal-test-{Guid.NewGuid():N}";
            var sharedRunId = Guid.NewGuid().ToString("N");
            var executionId = Guid.NewGuid().ToString("N");

            await using var subscription = await subscriber
                .SubscribeAsync(
                    AiRuntimeSignalType.SharedRunDispatched,
                    controlPlaneId,
                    sharedRunId)
                .ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));

            var receiveTask = ReadNextAsync(
                subscription,
                timeout.Token);

            var expected = new AiRuntimeSignal
            {
                Type = AiRuntimeSignalType.SharedRunDispatched,
                ControlPlaneId = controlPlaneId,
                TenantId = "tenant-002",
                SharedRunId = sharedRunId,
                LocalRunId = "local-run-002",
                ExecutionId = executionId,
                RuntimeInstanceId = "runtime-002"
            };

            await publisher
                .PublishAsync(expected)
                .ConfigureAwait(false);

            var received = await receiveTask.ConfigureAwait(false);

            _output.WriteLine(
                "Received shared-run dispatch signal. " +
                $"ControlPlaneId='{received.ControlPlaneId}', " +
                $"SharedRunId='{received.SharedRunId}', " +
                $"RuntimeInstanceId='{received.RuntimeInstanceId}', " +
                $"LocalRunId='{received.LocalRunId}', " +
                $"ExecutionId='{received.ExecutionId}'.");

            Assert.Multiple(
                () => Assert.Equal(
                    AiRuntimeSignalType.SharedRunDispatched,
                    received.Type),

                () => Assert.Equal(
                    controlPlaneId,
                    received.ControlPlaneId),

                () => Assert.Equal(
                    "tenant-002",
                    received.TenantId),

                () => Assert.Equal(
                    sharedRunId,
                    received.SharedRunId),

                () => Assert.Equal(
                    "runtime-002",
                    received.RuntimeInstanceId),

                () => Assert.Equal(
                    "local-run-002",
                    received.LocalRunId),

                () => Assert.Equal(
                    executionId,
                    received.ExecutionId));
        }

        /// <summary>
        /// Reads the next runtime signal from an active subscription.
        /// </summary>
        /// <param name="subscription">The active runtime signal subscription.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The next received runtime signal.</returns>
        private static async Task<AiRuntimeSignal> ReadNextAsync(
            IAiRuntimeSignalSubscription subscription,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subscription);

            await foreach (var signal in subscription.ReadAllAsync(cancellationToken))
            {
                return signal;
            }

            throw new InvalidOperationException(
                "The runtime signal subscription completed without receiving a signal.");
        }
    }
}