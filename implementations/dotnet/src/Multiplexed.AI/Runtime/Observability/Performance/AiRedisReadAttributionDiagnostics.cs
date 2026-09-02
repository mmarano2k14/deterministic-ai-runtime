using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.Observability.Performance
{
    /// <summary>
    /// Bounded semantic operation names used by PERF1 Redis read attribution.
    /// </summary>
    public static class AiRedisReadAttributionOperations
    {
        public const string ControlPlaneDiscoveryLoad = "ControlPlaneDiscovery.Load";
        public const string RuntimeCapacityPublishCompareExchangeLoad = "RuntimeCapacity.PublishCompareExchange.Load";
        public const string RuntimeCapacityIndexLoad = "RuntimeCapacity.Index.Load";
        public const string RuntimeCapacityDescriptorLoad = "RuntimeCapacity.Descriptor.Load";
        public const string RuntimeRegistryIndexLoad = "RuntimeRegistry.Index.Load";
        public const string RuntimeRegistryEntryLoad = "RuntimeRegistry.Entry.Load";
        public const string RuntimeRunIndexEntryLoad = "RuntimeRunIndex.Entry.Load";
        public const string SharedQueueItemLoad = "SharedQueue.Item.Load";
        public const string SharedRunRecordLoad = "SharedRun.Record.Load";
        public const string SharedRunPublicGetRecordLoad = "SharedRun.PublicGet.Record.Load";
        public const string TestHarnessSharedRunPublicGetRecordLoad = "TestHarness.SharedRun.PublicGet.Record.Load";
        public const string SharedRunListRecordLoad = "SharedRun.List.Record.Load";
        public const string ScaleOutRequestRecordLoad = "ScaleOutRequest.Record.Load";
        public const string ScaleOutRequestTransitionLoad = "ScaleOutRequest.Transition.Load";
        public const string ScaleOutRequestListLoad = "ScaleOutRequest.List.Load";
        public const string ScaleOutRequestControlPlaneIndexLoad = "ScaleOutRequest.ControlPlaneIndex.Load";
        public const string ExecutionRecordLoad = "Execution.Record.Load";
        public const string ExecutionStateLoad = "Execution.State.Load";
        public const string ExecutionControlStateLoad = "ExecutionControl.State.Load";
        public const string DagExecutionRecordLoad = "Dag.ExecutionRecord.Load";
        public const string DagStateBlobLoad = "Dag.StateBlob.Load";
        public const string DagRecordStateLoadMany = "Dag.RecordState.LoadMany";
        public const string DagStepIndexLoad = "Dag.StepIndex.Load";
        public const string DagStepLoadMany = "Dag.Step.LoadMany";
        public const string DagStepLoadCluster = "Dag.Step.Load.Cluster";
        public const string DagStepIndexSaveStateLoad = "Dag.StepIndex.SaveState.Load";
        public const string DagStepRepairLoad = "Dag.Step.RepairLoad";
        public const string DagStepIndexCompletedCleanupLoad = "Dag.StepIndex.CompletedCleanup.Load";
        public const string DagDistributedCleanupStepIndexLoad = "Dag.DistributedCleanup.StepIndex.Load";
        public const string PayloadRedisLoad = "Payload.Redis.Load";
        public const string PayloadCacheLoad = "Payload.Cache.Load";
        public const string StepPayloadIndexLoad = "StepPayloadIndex.Load";
        public const string StepPayloadIndexLoadMany = "StepPayloadIndex.LoadMany";
        public const string TestHarnessCrashCheckpointState = "TestHarness.CrashCheckpoint.State";
        public const string TestHarnessRuntimePoolWorkloadSharedRunLoad = "TestHarness.RuntimePoolWorkload.SharedRun.Load";
        public const string RbacExecutionContextLoad = "Rbac.ExecutionContext.Load";
        public const string LuaDag = "Lua.Dag";
        public const string LuaDagClaim = "Lua.Dag.Claim";
        public const string LuaDagClaimBatch = "Lua.Dag.ClaimBatch";
        public const string LuaDagClaimSpecific = "Lua.Dag.ClaimSpecific";
        public const string LuaDagComplete = "Lua.Dag.Complete";
        public const string LuaDagPark = "Lua.Dag.Park";
        public const string LuaDagResumeExternalWait = "Lua.Dag.ResumeExternalWait";
        public const string LuaDagFail = "Lua.Dag.Fail";
        public const string LuaDagRecover = "Lua.Dag.Recover";
        public const string LuaDagRecoverRunningForRecovery = "Lua.Dag.RecoverRunningForRecovery";
        public const string LuaDagFinalize = "Lua.Dag.Finalize";
        public const string LuaDagRetention = "Lua.Dag.Retention";
        public const string LuaExecution = "Lua.Execution";
        public const string LuaExecutionControl = "Lua.ExecutionControl";
        public const string LuaSharedQueue = "Lua.SharedQueue";
        public const string LuaSharedRun = "Lua.SharedRun";
        public const string LuaRuntimeRunIndex = "Lua.RuntimeRunIndex";
        public const string LuaScaleOutRequest = "Lua.ScaleOutRequest";
        public const string LuaRbacContext = "Lua.RbacContext";
    }

    /// <summary>
    /// One bounded PERF1 semantic Redis operation aggregate.
    /// </summary>
    public sealed record AiRedisReadAttributionOperationSnapshot(
        string Operation,
        string Command,
        long Calls,
        long ResponsePayloadBytes);

    /// <summary>
    /// Latest bounded PERF1 semantic Redis operation snapshot published by one process.
    /// </summary>
    public sealed record AiRedisReadAttributionProcessSnapshot(
        string ProcessIdentity,
        long PublicationSequence,
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<AiRedisReadAttributionOperationSnapshot> Operations);

    /// <summary>
    /// Cross-process aggregate collected for one PERF1 scope.
    /// </summary>
    public sealed record AiRedisReadAttributionAggregate(
        int ProcessSnapshotCount,
        long PublicationSequenceTotal,
        IReadOnlyList<AiRedisReadAttributionOperationSnapshot> Operations)
    {
        /// <summary>
        /// Gets the latest valid process snapshots that were already read while building this aggregate.
        /// </summary>
        public IReadOnlyList<AiRedisReadAttributionProcessSnapshot> ProcessSnapshots { get; init; } =
            Array.Empty<AiRedisReadAttributionProcessSnapshot>();
    }

    /// <summary>
    /// Diagnostic-only Redis read attribution for PERF1.
    /// </summary>
    /// <remarks>
    /// The hot path records counters in process memory only. Each active process publishes one
    /// absolute snapshot approximately every two seconds into a scope-specific Redis hash.
    /// Publication is best-effort and never changes application success/failure semantics.
    /// Payload bytes are the UTF-8 bytes represented by returned Redis values at the application
    /// call site; RESP framing and transport overhead are intentionally excluded.
    /// </remarks>
    public static class AiRedisReadAttributionDiagnostics
    {
        public const string EnabledEnvironmentVariable =
            "MULTIPLEXED_PERF1_REDIS_ATTRIBUTION";

        public const string ScopeEnvironmentVariable =
            "MULTIPLEXED_PERF1_REDIS_ATTRIBUTION_SCOPE";

        public static readonly TimeSpan FlushInterval =
            TimeSpan.FromSeconds(2);

        private static readonly TimeSpan SnapshotTtl =
            TimeSpan.FromHours(2);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly HashSet<string> AllowedOperations = new(
            new[]
            {
                AiRedisReadAttributionOperations.ControlPlaneDiscoveryLoad,
                AiRedisReadAttributionOperations.RuntimeCapacityPublishCompareExchangeLoad,
                AiRedisReadAttributionOperations.RuntimeCapacityIndexLoad,
                AiRedisReadAttributionOperations.RuntimeCapacityDescriptorLoad,
                AiRedisReadAttributionOperations.RuntimeRegistryIndexLoad,
                AiRedisReadAttributionOperations.RuntimeRegistryEntryLoad,
                AiRedisReadAttributionOperations.RuntimeRunIndexEntryLoad,
                AiRedisReadAttributionOperations.SharedQueueItemLoad,
                AiRedisReadAttributionOperations.SharedRunRecordLoad,
                AiRedisReadAttributionOperations.SharedRunPublicGetRecordLoad,
                AiRedisReadAttributionOperations.TestHarnessSharedRunPublicGetRecordLoad,
                AiRedisReadAttributionOperations.SharedRunListRecordLoad,
                AiRedisReadAttributionOperations.ScaleOutRequestRecordLoad,
                AiRedisReadAttributionOperations.ScaleOutRequestTransitionLoad,
                AiRedisReadAttributionOperations.ScaleOutRequestListLoad,
                AiRedisReadAttributionOperations.ScaleOutRequestControlPlaneIndexLoad,
                AiRedisReadAttributionOperations.ExecutionRecordLoad,
                AiRedisReadAttributionOperations.ExecutionStateLoad,
                AiRedisReadAttributionOperations.ExecutionControlStateLoad,
                AiRedisReadAttributionOperations.DagExecutionRecordLoad,
                AiRedisReadAttributionOperations.DagStateBlobLoad,
                AiRedisReadAttributionOperations.DagRecordStateLoadMany,
                AiRedisReadAttributionOperations.DagStepIndexLoad,
                AiRedisReadAttributionOperations.DagStepLoadMany,
                AiRedisReadAttributionOperations.DagStepLoadCluster,
                AiRedisReadAttributionOperations.DagStepIndexSaveStateLoad,
                AiRedisReadAttributionOperations.DagStepRepairLoad,
                AiRedisReadAttributionOperations.DagStepIndexCompletedCleanupLoad,
                AiRedisReadAttributionOperations.DagDistributedCleanupStepIndexLoad,
                AiRedisReadAttributionOperations.PayloadRedisLoad,
                AiRedisReadAttributionOperations.PayloadCacheLoad,
                AiRedisReadAttributionOperations.StepPayloadIndexLoad,
                AiRedisReadAttributionOperations.StepPayloadIndexLoadMany,
                AiRedisReadAttributionOperations.TestHarnessCrashCheckpointState,
                AiRedisReadAttributionOperations.TestHarnessRuntimePoolWorkloadSharedRunLoad,
                AiRedisReadAttributionOperations.RbacExecutionContextLoad,
                AiRedisReadAttributionOperations.LuaDag,
                AiRedisReadAttributionOperations.LuaDagClaim,
                AiRedisReadAttributionOperations.LuaDagClaimBatch,
                AiRedisReadAttributionOperations.LuaDagClaimSpecific,
                AiRedisReadAttributionOperations.LuaDagComplete,
                AiRedisReadAttributionOperations.LuaDagPark,
                AiRedisReadAttributionOperations.LuaDagResumeExternalWait,
                AiRedisReadAttributionOperations.LuaDagFail,
                AiRedisReadAttributionOperations.LuaDagRecover,
                AiRedisReadAttributionOperations.LuaDagRecoverRunningForRecovery,
                AiRedisReadAttributionOperations.LuaDagFinalize,
                AiRedisReadAttributionOperations.LuaDagRetention,
                AiRedisReadAttributionOperations.LuaExecution,
                AiRedisReadAttributionOperations.LuaExecutionControl,
                AiRedisReadAttributionOperations.LuaSharedQueue,
                AiRedisReadAttributionOperations.LuaSharedRun,
                AiRedisReadAttributionOperations.LuaRuntimeRunIndex,
                AiRedisReadAttributionOperations.LuaScaleOutRequest,
                AiRedisReadAttributionOperations.LuaRbacContext
            },
            StringComparer.Ordinal);

        private static readonly HashSet<string> AllowedCommands = new(
            new[]
            {
                "GET",
                "MGET",
                "HGET",
                "HMGET",
                "HGETALL",
                "SMEMBERS",
                "LUA"
            },
            StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<MetricKey, Counter> Counters = new();
        private static readonly AsyncLocal<OperationOverrideState?> OperationOverride = new();
        private static readonly object StateGate = new();
        private static readonly SemaphoreSlim FlushGate = new(1, 1);

        private static string? activeScope;
        private static IDatabase? publisherDatabase;
        private static Timer? publisherTimer;
        private static long publicationSequence;
        private static int ttlApplied;

        /// <summary>
        /// Gets the stable identity used by PERF1 for the current process snapshot.
        /// </summary>
        public static string CurrentProcessIdentity => BuildProcessIdentity();

        /// <summary>
        /// Gets whether PERF1 Redis attribution is enabled for this process.
        /// </summary>
        public static bool IsEnabled
        {
            get
            {
                var value = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
                return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Starts one parent PERF1 attribution scope and exposes it to subsequently spawned child processes.
        /// </summary>
        /// <returns>The scope identifier when enabled; otherwise, <c>null</c>.</returns>
        public static string? BeginScope()
        {
            if (!IsEnabled)
            {
                Environment.SetEnvironmentVariable(ScopeEnvironmentVariable, null);
                ResetState(scope: null);
                return null;
            }

            var scope = Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(ScopeEnvironmentVariable, scope);
            ResetState(scope);
            return scope;
        }

        /// <summary>
        /// Stops local recording for a completed scope. Remote child-process snapshots expire by TTL.
        /// </summary>
        public static void EndScope(string? scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                return;
            }

            var environmentScope =
                Environment.GetEnvironmentVariable(ScopeEnvironmentVariable);

            if (string.Equals(environmentScope, scope, StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable(ScopeEnvironmentVariable, null);
            }

            lock (StateGate)
            {
                if (string.Equals(activeScope, scope, StringComparison.Ordinal))
                {
                    ResetStateLocked(scope: null);
                }
            }
        }

        /// <summary>
        /// Records a single Redis value without registering a publisher. Useful for deterministic diagnostics tests.
        /// </summary>
        public static void Record(
            string operation,
            string command,
            RedisValue value)
        {
            TryRecord(operation, command, Measure(value), database: null);
        }

        /// <summary>
        /// Records multiple Redis values without registering a publisher.
        /// </summary>
        public static void Record(
            string operation,
            string command,
            IReadOnlyCollection<RedisValue> values)
        {
            TryRecord(operation, command, Measure(values), database: null);
        }

        /// <summary>
        /// Records Redis hash entries without registering a publisher.
        /// </summary>
        public static void Record(
            string operation,
            string command,
            IReadOnlyCollection<HashEntry> entries)
        {
            TryRecord(operation, command, Measure(entries), database: null);
        }

        /// <summary>
        /// Records a single Redis value and registers the database used by the periodic publisher.
        /// </summary>
        public static void Record(
            IDatabase database,
            string operation,
            string command,
            RedisValue value)
        {
            ArgumentNullException.ThrowIfNull(database);
            TryRecord(operation, command, Measure(value), database);
        }

        /// <summary>
        /// Records multiple Redis values and registers the database used by the periodic publisher.
        /// </summary>
        public static void Record(
            IDatabase database,
            string operation,
            string command,
            IReadOnlyCollection<RedisValue> values)
        {
            ArgumentNullException.ThrowIfNull(database);
            TryRecord(operation, command, Measure(values), database);
        }

        /// <summary>
        /// Records Redis hash entries and registers the database used by the periodic publisher.
        /// </summary>
        public static void Record(
            IDatabase database,
            string operation,
            string command,
            IReadOnlyCollection<HashEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(database);
            TryRecord(operation, command, Measure(entries), database);
        }

        /// <summary>
        /// Records one successful atomic Lua script invocation without registering a publisher.
        /// </summary>
        public static void RecordInvocation(string operation)
        {
            TryRecord(operation, "LUA", responsePayloadBytes: 0L, database: null);
        }

        /// <summary>
        /// Records one successful atomic Lua script invocation and registers the periodic publisher.
        /// </summary>
        public static void RecordInvocation(
            IDatabase database,
            string operation)
        {
            ArgumentNullException.ThrowIfNull(database);
            TryRecord(operation, "LUA", responsePayloadBytes: 0L, database);
        }

        /// <summary>
        /// Returns a deterministic snapshot of this process' current bounded counters.
        /// </summary>
        public static IReadOnlyList<AiRedisReadAttributionOperationSnapshot> SnapshotCurrentProcess()
        {
            return Counters
                .Select(pair => new AiRedisReadAttributionOperationSnapshot(
                    pair.Key.Operation,
                    pair.Key.Command,
                    Interlocked.Read(ref pair.Value.Calls),
                    Interlocked.Read(ref pair.Value.ResponsePayloadBytes)))
                .OrderBy(item => item.Command, StringComparer.Ordinal)
                .ThenBy(item => item.Operation, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Resets current-process counters while retaining the active scope.
        /// </summary>
        public static void ResetCurrentProcess()
        {
            Counters.Clear();
            Interlocked.Exchange(ref publicationSequence, 0L);
        }

        /// <summary>
        /// Temporarily reclassifies one bounded semantic operation in the current async call context.
        /// </summary>
        /// <remarks>
        /// This is intended for PERF1 measurement boundaries such as test-harness polling that calls
        /// production stores directly. It changes attribution labels only; it does not change Redis
        /// commands, persistence, runtime behavior, or cross-process propagation.
        /// </remarks>
        public static IDisposable OverrideOperation(
            string sourceOperation,
            string overrideOperation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceOperation);
            ArgumentException.ThrowIfNullOrWhiteSpace(overrideOperation);

            if (!AllowedOperations.Contains(sourceOperation))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceOperation),
                    sourceOperation,
                    "The source PERF1 operation is not part of the bounded attribution taxonomy.");
            }

            if (!AllowedOperations.Contains(overrideOperation))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(overrideOperation),
                    overrideOperation,
                    "The override PERF1 operation is not part of the bounded attribution taxonomy.");
            }

            var previous = OperationOverride.Value;
            var current = new OperationOverrideState(
                sourceOperation,
                overrideOperation,
                previous);
            OperationOverride.Value = current;

            return new OperationOverrideLease(current, previous);
        }

        /// <summary>
        /// Temporarily reclassifies one operation only when no outer PERF1 boundary has already
        /// reclassified the same source operation in the current async call context.
        /// </summary>
        /// <remarks>
        /// This preserves stronger outer classifications, such as explicit test-harness labels,
        /// while allowing a lower-level store boundary to classify otherwise-unlabeled reads.
        /// </remarks>
        public static IDisposable OverrideOperationIfUnchanged(
            string sourceOperation,
            string overrideOperation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceOperation);
            ArgumentException.ThrowIfNullOrWhiteSpace(overrideOperation);

            if (!AllowedOperations.Contains(sourceOperation))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceOperation),
                    sourceOperation,
                    "The source PERF1 operation is not part of the bounded attribution taxonomy.");
            }

            if (!AllowedOperations.Contains(overrideOperation))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(overrideOperation),
                    overrideOperation,
                    "The override PERF1 operation is not part of the bounded attribution taxonomy.");
            }

            if (!IsEnabled ||
                !string.Equals(
                    ResolveOperationOverride(sourceOperation),
                    sourceOperation,
                    StringComparison.Ordinal))
            {
                return NoopOperationOverrideLease.Instance;
            }

            return OverrideOperation(sourceOperation, overrideOperation);
        }

        /// <summary>
        /// Publishes the current process' absolute snapshot immediately.
        /// </summary>
        public static Task FlushCurrentProcessAsync(
            IDatabase database,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(database);
            if (!TryResolveScope(out var scope))
            {
                return Task.CompletedTask;
            }

            EnsureScope(scope);
            EnsurePublisher(database, scope);
            return FlushAsync(
                database,
                scope,
                waitForGate: true,
                cancellationToken);
        }

        /// <summary>
        /// Collects and aggregates the latest absolute snapshot published by each process in one scope.
        /// </summary>
        public static async Task<AiRedisReadAttributionAggregate?> CollectAsync(
            IConnectionMultiplexer connection,
            string scope,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentException.ThrowIfNullOrWhiteSpace(scope);
            cancellationToken.ThrowIfCancellationRequested();

            var entries = await connection
                .GetDatabase()
                .HashGetAllAsync(BuildRedisKey(scope))
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            var aggregates = new Dictionary<MetricKey, (long Calls, long Bytes)>();
            var processSnapshots = new List<AiRedisReadAttributionProcessSnapshot>();
            var processSnapshotCount = 0;
            var publicationSequenceTotal = 0L;

            foreach (var entry in entries)
            {
                ProcessSnapshotEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<ProcessSnapshotEnvelope>(
                        entry.Value.ToString(),
                        JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (envelope is null ||
                    !string.Equals(envelope.Scope, scope, StringComparison.Ordinal))
                {
                    continue;
                }

                var processOperations = envelope.Operations
                    .Where(operation => IsAllowed(operation.Operation, operation.Command))
                    .OrderByDescending(operation => operation.Calls)
                    .ThenBy(operation => operation.Command, StringComparer.Ordinal)
                    .ThenBy(operation => operation.Operation, StringComparer.Ordinal)
                    .ToArray();

                processSnapshotCount++;
                publicationSequenceTotal += envelope.PublicationSequence;
                processSnapshots.Add(
                    new AiRedisReadAttributionProcessSnapshot(
                        envelope.ProcessIdentity,
                        envelope.PublicationSequence,
                        envelope.CapturedAtUtc,
                        processOperations));

                foreach (var operation in processOperations)
                {
                    var key = new MetricKey(operation.Operation, operation.Command);
                    var current = aggregates.GetValueOrDefault(key);
                    aggregates[key] = (
                        current.Calls + operation.Calls,
                        current.Bytes + operation.ResponsePayloadBytes);
                }
            }

            var operations = aggregates
                .Select(pair => new AiRedisReadAttributionOperationSnapshot(
                    pair.Key.Operation,
                    pair.Key.Command,
                    pair.Value.Calls,
                    pair.Value.Bytes))
                .OrderByDescending(item => item.Calls)
                .ThenBy(item => item.Command, StringComparer.Ordinal)
                .ThenBy(item => item.Operation, StringComparer.Ordinal)
                .ToArray();

            return new AiRedisReadAttributionAggregate(
                processSnapshotCount,
                publicationSequenceTotal,
                operations)
            {
                ProcessSnapshots = processSnapshots
                    .OrderBy(snapshot => snapshot.ProcessIdentity, StringComparer.Ordinal)
                    .ToArray()
            };
        }

        private static void TryRecord(
            string operation,
            string command,
            long responsePayloadBytes,
            IDatabase? database)
        {
            try
            {
                if (!TryResolveScope(out var scope))
                {
                    return;
                }

                var effectiveOperation = ResolveOperationOverride(operation);
                var normalizedCommand = command?.Trim().ToUpperInvariant() ?? string.Empty;
                if (!IsAllowed(effectiveOperation, normalizedCommand))
                {
                    return;
                }

                EnsureScope(scope);
                if (database is not null)
                {
                    EnsurePublisher(database, scope);
                }

                var counter = Counters.GetOrAdd(
                    new MetricKey(effectiveOperation, normalizedCommand),
                    static _ => new Counter());

                Interlocked.Increment(ref counter.Calls);
                Interlocked.Add(
                    ref counter.ResponsePayloadBytes,
                    Math.Max(0L, responsePayloadBytes));
            }
            catch
            {
                // PERF1 attribution is diagnostic-only and must never change runtime behavior.
            }
        }

        private static string ResolveOperationOverride(string operation)
        {
            var current = OperationOverride.Value;
            while (current is not null)
            {
                if (string.Equals(
                        current.SourceOperation,
                        operation,
                        StringComparison.Ordinal))
                {
                    return current.OverrideOperation;
                }

                current = current.Previous;
            }

            return operation;
        }

        private static bool TryResolveScope(out string scope)
        {
            scope = string.Empty;
            if (!IsEnabled)
            {
                return false;
            }

            var candidate =
                Environment.GetEnvironmentVariable(ScopeEnvironmentVariable);

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            scope = candidate.Trim();
            return true;
        }

        private static bool IsAllowed(
            string operation,
            string command)
        {
            return !string.IsNullOrWhiteSpace(operation) &&
                   AllowedOperations.Contains(operation) &&
                   AllowedCommands.Contains(command);
        }

        private static void EnsureScope(string scope)
        {
            if (string.Equals(
                    Volatile.Read(ref activeScope),
                    scope,
                    StringComparison.Ordinal))
            {
                return;
            }

            lock (StateGate)
            {
                if (!string.Equals(activeScope, scope, StringComparison.Ordinal))
                {
                    ResetStateLocked(scope);
                }
            }
        }

        private static void EnsurePublisher(
            IDatabase database,
            string scope)
        {
            lock (StateGate)
            {
                if (!string.Equals(activeScope, scope, StringComparison.Ordinal))
                {
                    ResetStateLocked(scope);
                }

                publisherDatabase ??= database;
                publisherTimer ??= new Timer(
                    static _ => TriggerBackgroundFlush(),
                    state: null,
                    dueTime: FlushInterval,
                    period: FlushInterval);
            }
        }

        private static void TriggerBackgroundFlush()
        {
            IDatabase? database;
            string? scope;
            lock (StateGate)
            {
                database = publisherDatabase;
                scope = activeScope;
            }

            if (database is null || string.IsNullOrWhiteSpace(scope))
            {
                return;
            }

            _ = FlushAsync(
                database,
                scope,
                waitForGate: false,
                CancellationToken.None);
        }

        private static async Task FlushAsync(
            IDatabase database,
            string scope,
            bool waitForGate,
            CancellationToken cancellationToken)
        {
            var entered = false;
            try
            {
                if (waitForGate)
                {
                    await FlushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;
                }
                else
                {
                    entered = FlushGate.Wait(0);
                    if (!entered)
                    {
                        return;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                        Environment.GetEnvironmentVariable(ScopeEnvironmentVariable),
                        scope,
                        StringComparison.Ordinal))
                {
                    return;
                }

                var operations = SnapshotCurrentProcess();
                if (operations.Count == 0)
                {
                    return;
                }

                var sequence = Interlocked.Increment(ref publicationSequence);
                var envelope = new ProcessSnapshotEnvelope
                {
                    Scope = scope,
                    ProcessIdentity = BuildProcessIdentity(),
                    PublicationSequence = sequence,
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Operations = operations.ToArray()
                };

                var key = BuildRedisKey(scope);
                var payload = JsonSerializer.Serialize(envelope, JsonOptions);
                await database
                    .HashSetAsync(
                        key,
                        envelope.ProcessIdentity,
                        payload)
                    .ConfigureAwait(false);

                if (Interlocked.CompareExchange(ref ttlApplied, 1, 0) == 0)
                {
                    await database
                        .KeyExpireAsync(key, SnapshotTtl)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // PERF1 attribution is best-effort and must not affect application behavior.
            }
            finally
            {
                if (entered)
                {
                    FlushGate.Release();
                }
            }
        }

        private static void ResetState(string? scope)
        {
            lock (StateGate)
            {
                ResetStateLocked(scope);
            }
        }

        private static void ResetStateLocked(string? scope)
        {
            publisherTimer?.Dispose();
            publisherTimer = null;
            publisherDatabase = null;
            activeScope = scope;
            Counters.Clear();
            Interlocked.Exchange(ref publicationSequence, 0L);
            Interlocked.Exchange(ref ttlApplied, 0);
        }

        private static long Measure(RedisValue value)
        {
            if (value.IsNull)
            {
                return 0L;
            }

            return Encoding.UTF8.GetByteCount(value.ToString());
        }

        private static long Measure(IReadOnlyCollection<RedisValue> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var total = 0L;
            foreach (var value in values)
            {
                total += Measure(value);
            }

            return total;
        }

        private static long Measure(IReadOnlyCollection<HashEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);
            var total = 0L;
            foreach (var entry in entries)
            {
                total += Measure(entry.Name);
                total += Measure(entry.Value);
            }

            return total;
        }

        private static RedisKey BuildRedisKey(string scope)
        {
            return $"multiplexed:perf1:redis-read-attribution:{{{scope}}}";
        }

        private static string BuildProcessIdentity()
        {
            return $"{Environment.MachineName}:{Environment.ProcessId}";
        }

        private sealed record OperationOverrideState(
            string SourceOperation,
            string OverrideOperation,
            OperationOverrideState? Previous);

        private sealed class NoopOperationOverrideLease : IDisposable
        {
            public static readonly NoopOperationOverrideLease Instance = new();

            private NoopOperationOverrideLease()
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class OperationOverrideLease : IDisposable
        {
            private readonly OperationOverrideState current;
            private readonly OperationOverrideState? previous;
            private int disposed;

            public OperationOverrideLease(
                OperationOverrideState current,
                OperationOverrideState? previous)
            {
                this.current = current;
                this.previous = previous;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                if (ReferenceEquals(OperationOverride.Value, current))
                {
                    OperationOverride.Value = previous;
                }
            }
        }

        private readonly record struct MetricKey(
            string Operation,
            string Command);

        private sealed class Counter
        {
            public long Calls;
            public long ResponsePayloadBytes;
        }

        private sealed class ProcessSnapshotEnvelope
        {
            public ProcessSnapshotEnvelope()
            {
            }

            public string Scope { get; init; } = string.Empty;
            public string ProcessIdentity { get; init; } = string.Empty;
            public long PublicationSequence { get; init; }
            public DateTimeOffset CapturedAtUtc { get; init; }
            public AiRedisReadAttributionOperationSnapshot[] Operations { get; init; } =
                Array.Empty<AiRedisReadAttributionOperationSnapshot>();
        }
    }
}
