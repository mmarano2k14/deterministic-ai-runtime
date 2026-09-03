using System.Globalization;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using StackExchange.Redis;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Decorates the configured durable ledger with one explicitly enabled, test-only
    /// pre-finalization checkpoint. Normal hosts never register this decorator.
    /// </summary>
    /// <remarks>
    /// The wrapped ledger append remains authoritative and completes first. When the appended
    /// fact is <see cref="AiEngineEvents.Finalization.Started"/> and the exact execution has an
    /// armed Redis checkpoint, one runtime atomically claims that checkpoint and waits. The
    /// parent call-site is already durably terminal at this point, while
    /// <c>TryFinalizeExecutionAsync</c> has not run yet.
    /// </remarks>
    public sealed class FinalizationCheckpointAiDecisionLedger : IAiDecisionLedger
    {
        public const string EnabledConfigurationKey =
            "Tests:EnableFinalizationCheckpointGate";

        public const string MaximumHoldSecondsConfigurationKey =
            "Tests:FinalizationCheckpointGate:MaximumHoldSeconds";

        public const string ArmedState = "armed";
        public const string ReleasedState = "released";
        public const string ReachedStatePrefix = "reached|";

        private const int DefaultMaximumHoldSeconds = 180;
        private const int MinimumFallbackStateTtlSeconds = 240;

        private const string TryReachScript =
            """
            local current = redis.call('GET', KEYS[1])
            if current ~= ARGV[1] then
                return 0
            end

            local ttl = redis.call('TTL', KEYS[1])
            if not ttl or ttl <= 0 then
                ttl = tonumber(ARGV[3])
            end

            redis.call('SET', KEYS[1], ARGV[2], 'EX', ttl)
            redis.call('PUBLISH', KEYS[2], ARGV[2])
            return 1
            """;

        private const string ReleaseReachedScript =
            """
            local current = redis.call('GET', KEYS[1])
            if current ~= ARGV[1] then
                return 0
            end

            redis.call('SET', KEYS[1], ARGV[2], 'EX', ARGV[3])
            return 1
            """;

        private readonly IAiDecisionLedger inner;
        private readonly IConnectionMultiplexer connectionMultiplexer;
        private readonly ILogger<FinalizationCheckpointAiDecisionLedger> logger;
        private readonly TimeSpan maximumHold;

        public FinalizationCheckpointAiDecisionLedger(
            IAiDecisionLedger inner,
            IConnectionMultiplexer connectionMultiplexer,
            IConfiguration configuration,
            ILogger<FinalizationCheckpointAiDecisionLedger> logger)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.connectionMultiplexer =
                connectionMultiplexer ??
                throw new ArgumentNullException(nameof(connectionMultiplexer));
            ArgumentNullException.ThrowIfNull(configuration);
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var maximumHoldSeconds =
                configuration.GetValue<int?>(
                    MaximumHoldSecondsConfigurationKey)
                ?? DefaultMaximumHoldSeconds;

            if (maximumHoldSeconds <= 0)
            {
                throw new InvalidOperationException(
                    $"{MaximumHoldSecondsConfigurationKey} must be greater than zero.");
            }

            this.maximumHold = TimeSpan.FromSeconds(maximumHoldSeconds);
        }

        public async Task AppendAsync(
            AiDecisionLedgerEntry entry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);

            await this.inner
                .AppendAsync(entry, cancellationToken)
                .ConfigureAwait(false);

            if (entry.Category != AiDecisionLedgerCategory.Finalization ||
                !StringComparer.Ordinal.Equals(
                    entry.EventType,
                    AiEngineEvents.Finalization.Started))
            {
                return;
            }

            var executionId = entry.CorrelationContext.ExecutionId;
            var runtimeInstanceId =
                entry.CorrelationContext.RuntimeInstanceId ??
                entry.CorrelationContext.WorkerId;

            if (string.IsNullOrWhiteSpace(executionId) ||
                string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return;
            }

            var stateKey = BuildStateKey(executionId);
            var reachedChannel = BuildReachedChannel(executionId);
            var reachedAtUtc = DateTimeOffset.UtcNow;
            var reachedState = BuildReachedState(
                runtimeInstanceId,
                entry.EntryId,
                reachedAtUtc);

            var fallbackTtlSeconds =
                Math.Max(
                    MinimumFallbackStateTtlSeconds,
                    checked((int)Math.Ceiling(this.maximumHold.TotalSeconds)) + 60);

            var database = this.connectionMultiplexer.GetDatabase();
            var reached = await database
                .ScriptEvaluateAsync(
                    TryReachScript,
                    new RedisKey[] { stateKey, reachedChannel },
                    new RedisValue[]
                    {
                        ArmedState,
                        reachedState,
                        fallbackTtlSeconds
                    })
                .ConfigureAwait(false);
            Multiplexed.AI.Runtime.Observability.Performance
                .AiRedisReadAttributionDiagnostics.RecordInvocation(
                    database,
                    Multiplexed.AI.Runtime.Observability.Performance
                        .AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState);

            if ((long)reached != 1L)
            {
                return;
            }

            this.logger.LogInformation(
                "Test-only finalization checkpoint reached. ExecutionId='{ExecutionId}', RuntimeInstanceId='{RuntimeInstanceId}', LedgerEntryId='{LedgerEntryId}'.",
                executionId,
                runtimeInstanceId,
                entry.EntryId);

            var deadline = DateTimeOffset.UtcNow.Add(this.maximumHold);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentState = await database
                    .StringGetAsync(stateKey)
                    .ConfigureAwait(false);
                Multiplexed.AI.Runtime.Observability.Performance
                    .AiRedisReadAttributionDiagnostics.Record(
                        database,
                        Multiplexed.AI.Runtime.Observability.Performance
                            .AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState,
                        "GET",
                        currentState);

                if (currentState.IsNullOrEmpty ||
                    StringComparer.Ordinal.Equals(
                        currentState.ToString(),
                        ReleasedState))
                {
                    return;
                }

                if (!StringComparer.Ordinal.Equals(
                        currentState.ToString(),
                        reachedState))
                {
                    this.logger.LogWarning(
                        "Test-only finalization checkpoint state changed unexpectedly; finalization will continue. ExecutionId='{ExecutionId}', State='{State}'.",
                        executionId,
                        currentState.ToString());
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                    .ConfigureAwait(false);
            }

            var watchdogRelease = await database
                .ScriptEvaluateAsync(
                    ReleaseReachedScript,
                    new RedisKey[] { stateKey },
                    new RedisValue[]
                    {
                        reachedState,
                        ReleasedState,
                        fallbackTtlSeconds
                    })
                .ConfigureAwait(false);
            Multiplexed.AI.Runtime.Observability.Performance
                .AiRedisReadAttributionDiagnostics.RecordInvocation(
                    database,
                    Multiplexed.AI.Runtime.Observability.Performance
                        .AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState);

            this.logger.LogWarning(
                "Test-only finalization checkpoint reached its hard watchdog; finalization will continue. ExecutionId='{ExecutionId}', RuntimeInstanceId='{RuntimeInstanceId}', MaximumHold='{MaximumHold}', Released='{Released}'.",
                executionId,
                runtimeInstanceId,
                this.maximumHold,
                (long)watchdogRelease == 1L);
        }

        public Task<IReadOnlyList<AiDecisionLedgerEntry>> GetByExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return this.inner.GetByExecutionAsync(
                executionId,
                cancellationToken);
        }

        public Task<IReadOnlyList<AiDecisionLedgerEntry>> QueryAsync(
            AiDecisionLedgerQuery query,
            CancellationToken cancellationToken = default)
        {
            return this.inner.QueryAsync(query, cancellationToken);
        }

        public static string BuildStateKey(string executionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            return $"multiplexed:test:finalization-checkpoint:{executionId}:state";
        }

        public static string BuildReachedChannel(string executionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            return $"multiplexed:test:finalization-checkpoint:{executionId}:reached";
        }

        public static bool TryParseReachedState(
            string? state,
            out string runtimeInstanceId,
            out string ledgerEntryId,
            out DateTimeOffset reachedAtUtc)
        {
            runtimeInstanceId = string.Empty;
            ledgerEntryId = string.Empty;
            reachedAtUtc = default;

            if (string.IsNullOrWhiteSpace(state) ||
                !state.StartsWith(
                    ReachedStatePrefix,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var parts = state.Split('|');

            if (parts.Length != 4 ||
                string.IsNullOrWhiteSpace(parts[1]) ||
                string.IsNullOrWhiteSpace(parts[2]) ||
                !DateTimeOffset.TryParseExact(
                    parts[3],
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out reachedAtUtc))
            {
                return false;
            }

            runtimeInstanceId = parts[1];
            ledgerEntryId = parts[2];
            return true;
        }

        private static string BuildReachedState(
            string runtimeInstanceId,
            string ledgerEntryId,
            DateTimeOffset reachedAtUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(ledgerEntryId);

            return string.Concat(
                ReachedStatePrefix,
                runtimeInstanceId,
                "|",
                ledgerEntryId,
                "|",
                reachedAtUtc.ToString(
                    "O",
                    CultureInfo.InvariantCulture));
        }
    }
}
