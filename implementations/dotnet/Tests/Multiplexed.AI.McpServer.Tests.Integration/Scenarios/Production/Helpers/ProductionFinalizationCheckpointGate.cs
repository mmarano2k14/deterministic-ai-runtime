using Multiplexed.AI.McpServer.Host.Bootstrap;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Arms and observes the test-only checkpoint located after durable terminal
    /// call-site persistence and before parent DAG finalization.
    /// </summary>
    public sealed class ProductionFinalizationCheckpointGate
    {
        private const string ReleaseTransitionScript =
            """
            local current = redis.call('GET', KEYS[1])
            if current == ARGV[1] then
                return 0
            end
            if not current then
                return -1
            end
            if current == ARGV[2] or string.sub(current, 1, string.len(ARGV[3])) == ARGV[3] then
                redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[4])
                return 1
            end
            return -2
            """;

        private readonly IConnectionMultiplexer connectionMultiplexer;
        private readonly ITestOutputHelper output;
        private readonly TimeSpan stateTtl;
        private int releaseStarted;

        private ProductionFinalizationCheckpointGate(
            IConnectionMultiplexer connectionMultiplexer,
            ITestOutputHelper output,
            string executionId,
            TimeSpan stateTtl)
        {
            this.connectionMultiplexer =
                connectionMultiplexer ??
                throw new ArgumentNullException(nameof(connectionMultiplexer));
            this.output =
                output ??
                throw new ArgumentNullException(nameof(output));
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            this.ExecutionId = executionId;
            this.StateKey =
                FinalizationCheckpointAiDecisionLedger.BuildStateKey(
                    executionId);
            this.ReachedChannel =
                FinalizationCheckpointAiDecisionLedger.BuildReachedChannel(
                    executionId);
            this.stateTtl = stateTtl;
        }

        public string ExecutionId { get; }

        public string StateKey { get; }

        public string ReachedChannel { get; }

        public static async Task<ProductionFinalizationCheckpointGate> ArmAsync(
            IConnectionMultiplexer connectionMultiplexer,
            ITestOutputHelper output,
            string executionId,
            TimeSpan stateTtl)
        {
            ArgumentNullException.ThrowIfNull(connectionMultiplexer);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            if (stateTtl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stateTtl),
                    stateTtl,
                    "The finalization checkpoint TTL must be greater than zero.");
            }

            var gate =
                new ProductionFinalizationCheckpointGate(
                    connectionMultiplexer,
                    output,
                    executionId,
                    stateTtl);

            var armed = await connectionMultiplexer
                .GetDatabase()
                .StringSetAsync(
                    gate.StateKey,
                    FinalizationCheckpointAiDecisionLedger.ArmedState,
                    stateTtl,
                    When.Always)
                .ConfigureAwait(false);

            if (!armed)
            {
                throw new InvalidOperationException(
                    $"Could not arm finalization checkpoint for execution '{executionId}'.");
            }

            output.WriteLine(
                $"[FINALIZATION CHECKPOINT ARMED] ExecutionId='{executionId}', StateKey='{gate.StateKey}', Ttl='{stateTtl}'.");

            return gate;
        }

        public async Task<ProductionFinalizationCheckpointReached>
            WaitUntilReachedAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The finalization checkpoint reached timeout must be greater than zero.");
            }

            var database = this.connectionMultiplexer.GetDatabase();
            var subscriber = this.connectionMultiplexer.GetSubscriber();
            var reachedSignal =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var reachedChannel = RedisChannel.Literal(this.ReachedChannel);

            await subscriber
                .SubscribeAsync(
                    reachedChannel,
                    (_, _) => reachedSignal.TrySetResult(true))
                .ConfigureAwait(false);

            try
            {
                var deadline = DateTimeOffset.UtcNow.Add(timeout);

                while (DateTimeOffset.UtcNow < deadline)
                {
                    var state = await database
                        .StringGetAsync(this.StateKey)
                        .ConfigureAwait(false);
                    Multiplexed.AI.Runtime.Observability.Performance
                        .AiRedisReadAttributionDiagnostics.Record(
                            database,
                            Multiplexed.AI.Runtime.Observability.Performance
                                .AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState,
                            "GET",
                            state);
                    var stateText = state.ToString();

                    if (FinalizationCheckpointAiDecisionLedger
                        .TryParseReachedState(
                            stateText,
                            out var runtimeInstanceId,
                            out var ledgerEntryId,
                            out var reachedAtUtc))
                    {
                        this.output.WriteLine(
                            $"[FINALIZATION CHECKPOINT REACHED] ExecutionId='{this.ExecutionId}', RuntimeInstanceId='{runtimeInstanceId}', LedgerEntryId='{ledgerEntryId}', ReachedAtUtc='{reachedAtUtc:O}'.");

                        return new ProductionFinalizationCheckpointReached(
                            runtimeInstanceId,
                            ledgerEntryId,
                            reachedAtUtc);
                    }

                    if (StringComparer.Ordinal.Equals(
                        stateText,
                        FinalizationCheckpointAiDecisionLedger.ReleasedState))
                    {
                        throw new InvalidOperationException(
                            $"Finalization checkpoint for execution '{this.ExecutionId}' was released before it was observed.");
                    }

                    if (!state.HasValue)
                    {
                        throw new InvalidOperationException(
                            $"Finalization checkpoint state disappeared before it was reached. ExecutionId='{this.ExecutionId}', StateKey='{this.StateKey}'.");
                    }

                    if (!StringComparer.Ordinal.Equals(
                        stateText,
                        FinalizationCheckpointAiDecisionLedger.ArmedState))
                    {
                        throw new InvalidOperationException(
                            $"Finalization checkpoint entered an invalid state before reach. ExecutionId='{this.ExecutionId}', StateKey='{this.StateKey}', State='{stateText}'.");
                    }

                    var remaining = deadline - DateTimeOffset.UtcNow;

                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var wakeTask = reachedSignal.Task;
                    var delayTask =
                        Task.Delay(
                            remaining < TimeSpan.FromSeconds(5)
                                ? remaining
                                : TimeSpan.FromSeconds(5));

                    await Task
                        .WhenAny(wakeTask, delayTask)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                await subscriber
                    .UnsubscribeAsync(reachedChannel)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Finalization checkpoint was not reached before timeout. ExecutionId='{this.ExecutionId}'.");
        }

        public async Task ReleaseAsync()
        {
            if (Interlocked.CompareExchange(ref this.releaseStarted, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var database = this.connectionMultiplexer.GetDatabase();
                var result = await database
                    .ScriptEvaluateAsync(
                        ReleaseTransitionScript,
                        new RedisKey[] { this.StateKey },
                        new RedisValue[]
                        {
                            FinalizationCheckpointAiDecisionLedger.ReleasedState,
                            FinalizationCheckpointAiDecisionLedger.ArmedState,
                            FinalizationCheckpointAiDecisionLedger.ReachedStatePrefix,
                            checked((int)Math.Ceiling(this.stateTtl.TotalSeconds))
                        })
                    .ConfigureAwait(false);
                Multiplexed.AI.Runtime.Observability.Performance
                    .AiRedisReadAttributionDiagnostics.RecordInvocation(
                        database,
                        Multiplexed.AI.Runtime.Observability.Performance
                            .AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState);

                var transition = (long)result;

                if (transition is not 0 and not 1 and not -1)
                {
                    throw new InvalidOperationException(
                        $"Finalization checkpoint release observed an incompatible state. ExecutionId='{this.ExecutionId}', Transition='{transition}'.");
                }

                this.output.WriteLine(
                    $"[FINALIZATION CHECKPOINT RELEASED] ExecutionId='{this.ExecutionId}', Transition='{transition}'.");
            }
            catch
            {
                Volatile.Write(ref this.releaseStarted, 0);
                throw;
            }
        }
    }

    public sealed record ProductionFinalizationCheckpointReached(
        string RuntimeInstanceId,
        string LedgerEntryId,
        DateTimeOffset ReachedAtUtc);
}
