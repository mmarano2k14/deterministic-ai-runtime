using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Coordinates one durable, test-only crash checkpoint between the external
    /// runtime process and the integration-test harness.
    /// </summary>
    public sealed class ProductionCrashCheckpointGate
    {
        private const string ArmedState = "armed";
        private const string ReleasedState = "released";
        private const string ReachedStatePrefix = "reached|";

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

        private ProductionCrashCheckpointGate(
            IConnectionMultiplexer connectionMultiplexer,
            ITestOutputHelper output,
            string controlPlaneId,
            string tenantId,
            string pipelineName,
            McpTestCrashCheckpointDefinition definition,
            TimeSpan stateTtl)
        {
            this.connectionMultiplexer =
                connectionMultiplexer ??
                throw new ArgumentNullException(nameof(connectionMultiplexer));

            this.output =
                output ??
                throw new ArgumentNullException(nameof(output));

            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            ControlPlaneId = controlPlaneId;
            TenantId = tenantId;
            PipelineName = pipelineName;
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            this.stateTtl = stateTtl;
        }

        /// <summary>
        /// Gets the logical control-plane identifier.
        /// </summary>
        public string ControlPlaneId { get; }

        /// <summary>
        /// Gets the tenant identifier.
        /// </summary>
        public string TenantId { get; }

        /// <summary>
        /// Gets the first-run pipeline name.
        /// </summary>
        public string PipelineName { get; }

        /// <summary>
        /// Gets the checkpoint definition embedded in the first-run DAG.
        /// </summary>
        public McpTestCrashCheckpointDefinition Definition { get; }

        /// <summary>
        /// Creates and durably arms one crash checkpoint.
        /// </summary>
        /// <param name="connectionMultiplexer">The shared control-plane Redis connection.</param>
        /// <param name="output">The test output helper.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="pipelineName">The first-run pipeline name prefix.</param>
        /// <param name="checkpointStepIndex">The one-based checkpoint step index.</param>
        /// <param name="stateTtl">The durable gate state time-to-live.</param>
        /// <returns>The armed gate.</returns>
        public static async Task<ProductionCrashCheckpointGate> ArmAsync(
            IConnectionMultiplexer connectionMultiplexer,
            ITestOutputHelper output,
            string controlPlaneId,
            string tenantId,
            string pipelineName,
            int checkpointStepIndex,
            TimeSpan stateTtl)
        {
            ArgumentNullException.ThrowIfNull(connectionMultiplexer);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointStepIndex);

            if (stateTtl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stateTtl),
                    stateTtl,
                    "The crash checkpoint state TTL must be greater than zero.");
            }

            var gateId =
                Guid.NewGuid().ToString("N");

            var keyPrefix =
                $"multiplexed:test:crash-checkpoint:{gateId}";

            var definition =
                new McpTestCrashCheckpointDefinition
                {
                    StepIndex = checkpointStepIndex,
                    StateKey = $"{keyPrefix}:state",
                    ReachedChannel = $"{keyPrefix}:reached",
                    ReleasedChannel = $"{keyPrefix}:released",
                    TtlSeconds = checked((int)Math.Ceiling(stateTtl.TotalSeconds))
                };

            var gate =
                new ProductionCrashCheckpointGate(
                    connectionMultiplexer,
                    output,
                    controlPlaneId,
                    tenantId,
                    pipelineName,
                    definition,
                    stateTtl);

            var armed = await connectionMultiplexer
                .GetDatabase()
                .StringSetAsync(
                    definition.StateKey,
                    ArmedState,
                    stateTtl,
                    When.Always)
                .ConfigureAwait(false);

            if (!armed)
            {
                throw new InvalidOperationException(
                    $"Could not arm crash checkpoint gate '{gateId}'.");
            }

            output.WriteLine(
                $"[REAL RUNTIME CRASH GATE ARMED] ControlPlaneId='{controlPlaneId}', TenantId='{tenantId}', PipelineName='{pipelineName}', CheckpointStepIndex='{checkpointStepIndex}', StateKey='{definition.StateKey}', Ttl='{stateTtl}'.");

            return gate;
        }

        /// <summary>
        /// Waits for the external runtime to durably reach the checkpoint.
        /// </summary>
        /// <param name="timeout">The maximum wait time.</param>
        /// <returns>A task representing the asynchronous wait.</returns>
        public async Task WaitUntilReachedAsync(
            TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The crash checkpoint reached timeout must be greater than zero.");
            }

            var database =
                this.connectionMultiplexer.GetDatabase();

            var subscriber =
                this.connectionMultiplexer.GetSubscriber();

            var reachedSignal =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var reachedChannel =
                RedisChannel.Literal(this.Definition.ReachedChannel);

            await subscriber
                .SubscribeAsync(
                    reachedChannel,
                    (_, _) => reachedSignal.TrySetResult(true))
                .ConfigureAwait(false);

            try
            {
                var deadline =
                    DateTimeOffset.UtcNow.Add(timeout);

                while (DateTimeOffset.UtcNow < deadline)
                {
                    var currentState =
                        await database
                            .StringGetAsync(this.Definition.StateKey)
                            .ConfigureAwait(false);
                    Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                        database,
                        Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState,
                        "GET",
                        currentState);

                    if (IsReached(currentState))
                    {
                        WriteReached(currentState);
                        return;
                    }

                    if (!currentState.HasValue)
                    {
                        throw new InvalidOperationException(
                            "The durable crash checkpoint state disappeared before it was reached. " +
                            $"ControlPlaneId='{this.ControlPlaneId}', " +
                            $"TenantId='{this.TenantId}', " +
                            $"PipelineName='{this.PipelineName}', " +
                            $"StateKey='{this.Definition.StateKey}'.");
                    }

                    if (!string.Equals(
                            currentState.ToString(),
                            ArmedState,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The durable crash checkpoint entered an invalid state before reach. " +
                            $"ControlPlaneId='{this.ControlPlaneId}', " +
                            $"TenantId='{this.TenantId}', " +
                            $"PipelineName='{this.PipelineName}', " +
                            $"StateKey='{this.Definition.StateKey}', " +
                            $"CurrentState='{currentState}'.");
                    }

                    var remaining =
                        deadline - DateTimeOffset.UtcNow;

                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var fallbackInterval =
                        remaining < TimeSpan.FromSeconds(5)
                            ? remaining
                            : TimeSpan.FromSeconds(5);

                    var fallbackSignal =
                        Task.Delay(fallbackInterval);

                    var completedTask =
                        await Task
                            .WhenAny(
                                reachedSignal.Task,
                                fallbackSignal)
                            .ConfigureAwait(false);

                    if (completedTask == reachedSignal.Task)
                    {
                        await reachedSignal.Task.ConfigureAwait(false);

                        var signaledState =
                            await database
                                .StringGetAsync(this.Definition.StateKey)
                                .ConfigureAwait(false);
                        Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                            database,
                            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState,
                            "GET",
                            signaledState);

                        if (IsReached(signaledState))
                        {
                            WriteReached(signaledState);
                            return;
                        }

                        throw new InvalidOperationException(
                            "A crash checkpoint reached signal was observed without durable reached state. " +
                            $"ControlPlaneId='{this.ControlPlaneId}', " +
                            $"TenantId='{this.TenantId}', " +
                            $"PipelineName='{this.PipelineName}', " +
                            $"StateKey='{this.Definition.StateKey}', " +
                            $"LastState='{signaledState}'.");
                    }
                }

                var lastState =
                    await database
                        .StringGetAsync(this.Definition.StateKey)
                        .ConfigureAwait(false);
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                    database,
                    Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState,
                    "GET",
                    lastState);

                if (IsReached(lastState))
                {
                    WriteReached(lastState);
                    return;
                }

                throw new TimeoutException(
                    "The durable crash checkpoint was not reached before the timeout. " +
                    $"ControlPlaneId='{this.ControlPlaneId}', " +
                    $"TenantId='{this.TenantId}', " +
                    $"PipelineName='{this.PipelineName}', " +
                    $"StateKey='{this.Definition.StateKey}', " +
                    $"LastState='{lastState}', " +
                    $"Timeout='{timeout}'.");
            }
            finally
            {
                await subscriber
                    .UnsubscribeAsync(reachedChannel)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Durably releases the checkpoint and publishes its wake-up signal.
        /// This operation is idempotent for one gate instance.
        /// </summary>
        /// <returns>A task representing the asynchronous release.</returns>
        public async Task ReleaseAsync()
        {
            if (Interlocked.Exchange(
                    ref this.releaseStarted,
                    1) != 0)
            {
                return;
            }

            try
            {
                var database =
                    this.connectionMultiplexer.GetDatabase();

                var transitionResult =
                    await database
                        .ScriptEvaluateAsync(
                            ReleaseTransitionScript,
                            new RedisKey[]
                            {
                                this.Definition.StateKey
                            },
                            new RedisValue[]
                            {
                                ReleasedState,
                                ArmedState,
                                ReachedStatePrefix,
                                checked((int)Math.Ceiling(this.stateTtl.TotalSeconds))
                            })
                        .ConfigureAwait(false);

                var transitionCode =
                    (long)transitionResult;

                if (transitionCode < 0)
                {
                    var invalidState =
                        await database
                            .StringGetAsync(this.Definition.StateKey)
                            .ConfigureAwait(false);
                    Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                        database,
                        Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState,
                        "GET",
                        invalidState);

                    throw new InvalidOperationException(
                        "The durable crash checkpoint could not transition to released state. " +
                        $"ControlPlaneId='{this.ControlPlaneId}', " +
                        $"TenantId='{this.TenantId}', " +
                        $"PipelineName='{this.PipelineName}', " +
                        $"StateKey='{this.Definition.StateKey}', " +
                        $"TransitionCode='{transitionCode}', " +
                        $"CurrentState='{invalidState}'.");
                }

                await this.connectionMultiplexer
                    .GetSubscriber()
                    .PublishAsync(
                        RedisChannel.Literal(this.Definition.ReleasedChannel),
                        ReleasedState)
                    .ConfigureAwait(false);

                this.output.WriteLine(
                    $"[REAL RUNTIME CRASH GATE RELEASED] ControlPlaneId='{this.ControlPlaneId}', TenantId='{this.TenantId}', PipelineName='{this.PipelineName}', StateKey='{this.Definition.StateKey}', TransitionCode='{transitionCode}'.");
            }
            catch
            {
                Volatile.Write(
                    ref this.releaseStarted,
                    0);

                throw;
            }
        }

        private static bool IsReached(
            RedisValue state)
        {
            return state.HasValue &&
                   state.ToString().StartsWith(
                       ReachedStatePrefix,
                       StringComparison.Ordinal);
        }

        private void WriteReached(
            RedisValue state)
        {
            this.output.WriteLine(
                $"[REAL RUNTIME CRASH GATE REACHED] ControlPlaneId='{this.ControlPlaneId}', TenantId='{this.TenantId}', PipelineName='{this.PipelineName}', CheckpointStepIndex='{this.Definition.StepIndex}', StateKey='{this.Definition.StateKey}', State='{state}'.");
        }
    }
}
