using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;
using StackExchange.Redis;

namespace Multiplexed.Sample.External.Plugins.Steps.Steps
{
    /// <summary>
    /// Test-only distributed crash checkpoint that durably blocks one DAG
    /// execution until the integration harness releases it after process kill.
    /// </summary>
    [AiStep("distributed.chaos.crash-checkpoint")]
    public sealed class DistributedCrashCheckpointStep : IAiStep
    {
        private const string ReleasedState = "released";

        private const string ReachTransitionScript =
            """
            local current = redis.call('GET', KEYS[1])
            if current == ARGV[1] then
                return 0
            end
            if not current then
                return -1
            end
            if current == ARGV[2] then
                redis.call('SET', KEYS[1], ARGV[3], 'EX', ARGV[4])
                return 1
            end
            if string.sub(current, 1, string.len(ARGV[5])) == ARGV[5] then
                return 1
            end
            return -2
            """;

        private readonly IConnectionMultiplexer connectionMultiplexer;

        /// <summary>
        /// Initializes a new instance of the <see cref="DistributedCrashCheckpointStep"/> class.
        /// </summary>
        /// <param name="connectionMultiplexer">The shared runtime Redis connection.</param>
        public DistributedCrashCheckpointStep(
            IConnectionMultiplexer connectionMultiplexer)
        {
            this.connectionMultiplexer =
                connectionMultiplexer ??
                throw new ArgumentNullException(nameof(connectionMultiplexer));
        }

        /// <inheritdoc />
        public string Name => "distributed.chaos.crash-checkpoint";

        /// <inheritdoc />
        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var helper =
                context.GetHelper();

            var stateKey =
                await ReadRequiredConfigAsync(
                        helper,
                        "test.crashCheckpoint.stateKey",
                        cancellationToken)
                    .ConfigureAwait(false);

            var reachedChannelName =
                await ReadRequiredConfigAsync(
                        helper,
                        "test.crashCheckpoint.reachedChannel",
                        cancellationToken)
                    .ConfigureAwait(false);

            var releasedChannelName =
                await ReadRequiredConfigAsync(
                        helper,
                        "test.crashCheckpoint.releasedChannel",
                        cancellationToken)
                    .ConfigureAwait(false);

            var ttlSeconds =
                await helper
                    .GetConfigAsync<int?>(
                        "test.crashCheckpoint.ttlSeconds",
                        cancellationToken)
                    .ConfigureAwait(false) ??
                0;

            if (ttlSeconds <= 0)
            {
                throw new InvalidOperationException(
                    "Config value 'test.crashCheckpoint.ttlSeconds' must be greater than zero.");
            }

            var executionId =
                context.Record.ExecutionId;

            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new InvalidOperationException(
                    "ExecutionId is required by the distributed crash checkpoint.");
            }

            var database =
                this.connectionMultiplexer.GetDatabase();

            var subscriber =
                this.connectionMultiplexer.GetSubscriber();

            var releasedSignal =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var releasedChannel =
                RedisChannel.Literal(releasedChannelName);

            await subscriber
                .SubscribeAsync(
                    releasedChannel,
                    (_, message) =>
                    {
                        if (string.Equals(
                                message.ToString(),
                                ReleasedState,
                                StringComparison.Ordinal))
                        {
                            releasedSignal.TrySetResult(true);
                        }
                    })
                .ConfigureAwait(false);

            try
            {
                var reachedState =
                    $"reached|executionId={executionId}|reachedAtUtc={DateTimeOffset.UtcNow:O}";

                var transitionResult =
                    await database
                        .ScriptEvaluateAsync(
                            ReachTransitionScript,
                            new RedisKey[]
                            {
                                stateKey
                            },
                            new RedisValue[]
                            {
                                ReleasedState,
                                "armed",
                                reachedState,
                                ttlSeconds,
                                "reached|"
                            })
                        .ConfigureAwait(false);

                var transitionCode =
                    (long)transitionResult;

                if (transitionCode == 0)
                {
                    return CreateReleasedResult(
                        helper,
                        executionId,
                        stateKey,
                        resumedAfterRelease: true);
                }

                if (transitionCode < 0)
                {
                    var invalidState =
                        await database
                            .StringGetAsync(stateKey)
                            .ConfigureAwait(false);

                    throw new InvalidOperationException(
                        "The distributed crash checkpoint could not transition to reached state. " +
                        $"StateKey='{stateKey}', TransitionCode='{transitionCode}', " +
                        $"CurrentState='{invalidState}'.");
                }

                await subscriber
                    .PublishAsync(
                        RedisChannel.Literal(reachedChannelName),
                        reachedState)
                    .ConfigureAwait(false);

                var durableState =
                    await database
                        .StringGetAsync(stateKey)
                        .ConfigureAwait(false);

                if (string.Equals(
                        durableState.ToString(),
                        ReleasedState,
                        StringComparison.Ordinal))
                {
                    return CreateReleasedResult(
                        helper,
                        executionId,
                        stateKey,
                        resumedAfterRelease: true);
                }

                using var cancellationRegistration =
                    cancellationToken.Register(
                        () => releasedSignal.TrySetCanceled(cancellationToken));

                while (true)
                {
                    var durableFallbackDelay =
                        Task.Delay(
                            TimeSpan.FromSeconds(5),
                            cancellationToken);

                    var completedTask =
                        await Task
                            .WhenAny(
                                releasedSignal.Task,
                                durableFallbackDelay)
                            .ConfigureAwait(false);

                    var releaseSignalObserved =
                        completedTask == releasedSignal.Task;

                    if (releaseSignalObserved)
                    {
                        await releasedSignal.Task.ConfigureAwait(false);
                    }

                    durableState =
                        await database
                            .StringGetAsync(stateKey)
                            .ConfigureAwait(false);

                    if (string.Equals(
                            durableState.ToString(),
                            ReleasedState,
                            StringComparison.Ordinal))
                    {
                        return CreateReleasedResult(
                            helper,
                            executionId,
                            stateKey,
                            resumedAfterRelease: false);
                    }

                    if (releaseSignalObserved)
                    {
                        throw new InvalidOperationException(
                            "The distributed crash checkpoint received a release signal " +
                            $"without durable released state. StateKey='{stateKey}', " +
                            $"LastState='{durableState}'.");
                    }

                    if (!durableState.HasValue ||
                        !durableState.ToString().StartsWith(
                            "reached|",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The distributed crash checkpoint lost its durable reached state " +
                            $"while waiting for release. StateKey='{stateKey}', " +
                            $"LastState='{durableState}'.");
                    }
                }
            }
            finally
            {
                await subscriber
                    .UnsubscribeAsync(releasedChannel)
                    .ConfigureAwait(false);
            }
        }

        private static async Task<string> ReadRequiredConfigAsync(
            IAiStepContextHelper helper,
            string key,
            CancellationToken cancellationToken)
        {
            var value =
                await helper
                    .GetConfigAsync<string>(
                        key,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Missing required config value '{key}'.");
            }

            return value;
        }

        private static AiStepResult CreateReleasedResult(
            IAiStepContextHelper helper,
            string executionId,
            string stateKey,
            bool resumedAfterRelease)
        {
            return AiStepResult.Ok(
                output:
                    resumedAfterRelease
                        ? "Distributed crash checkpoint resumed after durable release."
                        : "Distributed crash checkpoint released by the integration harness.",
                data: helper.ToDictionary(new
                {
                    executionId,
                    stateKey,
                    resumedAfterRelease
                }));
        }
    }
}
