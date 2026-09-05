using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.Observability.Performance
{
    /// <summary>
    /// Bounded semantic MongoDB operation names used by PERF2 attribution.
    /// </summary>
    public static class AiMongoAttributionOperations
    {
        public const string LedgerSequenceNext = "Mongo.Ledger.Sequence.Next";
        public const string LedgerEntryAppend = "Mongo.Ledger.Entry.Append";
        public const string LedgerExecutionLoad = "Mongo.Ledger.Execution.Load";
        public const string LedgerQuery = "Mongo.Ledger.Query";

        public const string TraceAppend = "Mongo.Trace.Append";
        public const string TraceExecutionLoad = "Mongo.Trace.Execution.Load";
        public const string MetricAppend = "Mongo.Metric.Append";

        public const string SnapshotUpsert = "Mongo.Snapshot.Upsert";
        public const string SnapshotLoad = "Mongo.Snapshot.Load";
        public const string SnapshotDelete = "Mongo.Snapshot.Delete";
        public const string ReplayMetadataUpsert = "Mongo.ReplayMetadata.Upsert";
        public const string ReplayMetadataLoad = "Mongo.ReplayMetadata.Load";

        public const string ChildRelationIdentityLoad = "Mongo.ChildRelation.Identity.Load";
        public const string ChildRelationChildExecutionLoad = "Mongo.ChildRelation.ChildExecution.Load";
        public const string ChildRelationQuery = "Mongo.ChildRelation.Query";
        public const string ChildRelationAppend = "Mongo.ChildRelation.Append";
        public const string ChildRelationTransition = "Mongo.ChildRelation.Transition";

        public const string PayloadSave = "Mongo.Payload.Save";
        public const string PayloadLoad = "Mongo.Payload.Load";
        public const string PayloadDelete = "Mongo.Payload.Delete";
        public const string StepPayloadIndexUpsert = "Mongo.StepPayloadIndex.Upsert";
        public const string StepPayloadIndexLoad = "Mongo.StepPayloadIndex.Load";
        public const string StepPayloadIndexExecutionLoad = "Mongo.StepPayloadIndex.Execution.Load";
        public const string StepPayloadIndexLoadMany = "Mongo.StepPayloadIndex.LoadMany";
        public const string StepPayloadIndexDelete = "Mongo.StepPayloadIndex.Delete";

        public const string RuntimeLifecycleAppend = "Mongo.RuntimeLifecycle.Append";
        public const string RuntimeLifecycleQuery = "Mongo.RuntimeLifecycle.Query";
        public const string RecoveryForensicsLoad = "Mongo.RecoveryForensics.Load";
        public const string RecoveryForensicsAppend = "Mongo.RecoveryForensics.Append";
        public const string RecoveryForensicsReplace = "Mongo.RecoveryForensics.Replace";
        public const string RecoveryForensicsQuery = "Mongo.RecoveryForensics.Query";
        public const string PoolFailureJournalAppend = "Mongo.PoolFailureJournal.Append";
        public const string PoolFailureJournalQuery = "Mongo.PoolFailureJournal.Query";

        public const string TestHarnessLedgerExecutionLoad = "TestHarness.Mongo.Ledger.Execution.Load";
        public const string TestHarnessLedgerQuery = "TestHarness.Mongo.Ledger.Query";
        public const string TestHarnessTraceExecutionLoad = "TestHarness.Mongo.Trace.Execution.Load";
        public const string TestHarnessReplayMetadataLoad = "TestHarness.Mongo.ReplayMetadata.Load";
        public const string TestHarnessChildRelationIdentityLoad = "TestHarness.Mongo.ChildRelation.Identity.Load";
        public const string TestHarnessChildRelationChildExecutionLoad = "TestHarness.Mongo.ChildRelation.ChildExecution.Load";
        public const string TestHarnessChildRelationQuery = "TestHarness.Mongo.ChildRelation.Query";
        public const string TestHarnessRuntimeLifecycleQuery = "TestHarness.Mongo.RuntimeLifecycle.Query";
        public const string TestHarnessRecoveryForensicsLoad = "TestHarness.Mongo.RecoveryForensics.Load";
        public const string TestHarnessRecoveryForensicsQuery = "TestHarness.Mongo.RecoveryForensics.Query";
        public const string TestHarnessPoolFailureJournalQuery = "TestHarness.Mongo.PoolFailureJournal.Query";
    }

    /// <summary>
    /// Bounded MongoDB command names used by PERF2 semantic and driver attribution.
    /// </summary>
    public static class AiMongoAttributionCommands
    {
        public const string Insert = "INSERT";
        public const string FindAndModify = "FINDANDMODIFY";
        public const string Find = "FIND";
        public const string GetMore = "GETMORE";
        public const string Update = "UPDATE";
        public const string Delete = "DELETE";
        public const string CreateIndexes = "CREATEINDEXES";
        public const string ListIndexes = "LISTINDEXES";
        public const string Hello = "HELLO";
        public const string IsMaster = "ISMASTER";
        public const string Aggregate = "AGGREGATE";
        public const string Count = "COUNT";
        public const string Distinct = "DISTINCT";
        public const string KillCursors = "KILLCURSORS";
        public const string EndSessions = "ENDSESSIONS";
        public const string Ping = "PING";
        public const string BuildInfo = "BUILDINFO";
        public const string SaslStart = "SASLSTART";
        public const string SaslContinue = "SASLCONTINUE";
        public const string Other = "OTHER";
    }

    /// <summary>
    /// Bounded process-local MongoClient roles used by PERF2 driver attribution.
    /// </summary>
    public static class AiMongoAttributionClientRoles
    {
        public const string SharedRuntime = "SharedRuntime";
        public const string Snapshot = "Snapshot";
        public const string MetricStore = "MetricStore";
        public const string PayloadStore = "PayloadStore";
        public const string StepPayloadIndexStore = "StepPayloadIndexStore";
        public const string RuntimeLifecycle = "RuntimeLifecycle";
        public const string RecoveryForensics = "RecoveryForensics";
        public const string PoolFailureJournal = "PoolFailureJournal";
        public const string Other = "Other";
    }

    /// <summary>
    /// One bounded PERF2 semantic MongoDB operation aggregate.
    /// </summary>
    public sealed record AiMongoAttributionOperationSnapshot(
        string Operation,
        string Command,
        long Calls,
        long RequestedDocuments,
        long ReturnedDocuments,
        long RequestPayloadBytes,
        long ResponsePayloadBytes,
        long Successes,
        long Failures,
        long Cancellations,
        long DuplicateKeyRetries,
        long AggregateDurationTicks,
        long LatencyLe1Ms,
        long LatencyLe2Ms,
        long LatencyLe5Ms,
        long LatencyLe10Ms,
        long LatencyLe25Ms,
        long LatencyLe50Ms,
        long LatencyLe100Ms,
        long LatencyLe250Ms,
        long LatencyGt250Ms);

    /// <summary>
    /// One bounded PERF2 MongoDB driver command aggregate.
    /// </summary>
    public sealed record AiMongoAttributionDriverCommandSnapshot(
        string ClientRole,
        string Command,
        long Started,
        long Succeeded,
        long Failed,
        long AggregateDurationTicks);

    /// <summary>
    /// One bounded PERF2 MongoDB driver pool/connection aggregate.
    /// </summary>
    public sealed record AiMongoAttributionDriverPoolSnapshot(
        string ClientRole,
        long ClientInstancesObserved,
        long PoolsOpened,
        long PoolsClosed,
        long ConnectionsOpened,
        long ConnectionsClosed,
        long ConnectionOpenFailures,
        long Checkouts,
        long CheckoutFailures);

    /// <summary>
    /// Latest bounded PERF2 MongoDB snapshot published by one process.
    /// </summary>
    public sealed record AiMongoAttributionProcessSnapshot(
        string ProcessIdentity,
        long PublicationSequence,
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<AiMongoAttributionOperationSnapshot> Operations,
        IReadOnlyList<AiMongoAttributionDriverCommandSnapshot> DriverCommands,
        IReadOnlyList<AiMongoAttributionDriverPoolSnapshot> DriverPools);

    /// <summary>
    /// Cross-process PERF2 MongoDB aggregate collected for one measurement scope.
    /// </summary>
    public sealed record AiMongoAttributionAggregate(
        int ProcessSnapshotCount,
        long PublicationSequenceTotal,
        IReadOnlyList<AiMongoAttributionOperationSnapshot> Operations,
        IReadOnlyList<AiMongoAttributionDriverCommandSnapshot> DriverCommands,
        IReadOnlyList<AiMongoAttributionDriverPoolSnapshot> DriverPools)
    {
        public IReadOnlyList<AiMongoAttributionProcessSnapshot> ProcessSnapshots { get; init; } =
            Array.Empty<AiMongoAttributionProcessSnapshot>();
    }

    /// <summary>
    /// Allocation-free handle for one semantic MongoDB driver call.
    /// </summary>
    public readonly struct AiMongoAttributionMeasurement
    {
        private readonly AiMongoAttributionDiagnostics.SemanticCounter? counter;
        private readonly long startedTimestamp;

        internal AiMongoAttributionMeasurement(
            AiMongoAttributionDiagnostics.SemanticCounter? counter,
            long startedTimestamp)
        {
            this.counter = counter;
            this.startedTimestamp = startedTimestamp;
        }

        public bool IsActive => counter is not null;

        public void Succeed(
            long returnedDocuments = 0,
            long responsePayloadBytes = 0)
        {
            AiMongoAttributionDiagnostics.CompleteSemanticOperation(
                counter,
                startedTimestamp,
                AiMongoAttributionDiagnostics.SemanticOutcome.Success,
                returnedDocuments,
                responsePayloadBytes,
                duplicateKeyRetry: false);
        }

        public void Fail(bool duplicateKeyRetry = false)
        {
            AiMongoAttributionDiagnostics.CompleteSemanticOperation(
                counter,
                startedTimestamp,
                AiMongoAttributionDiagnostics.SemanticOutcome.Failure,
                returnedDocuments: 0,
                responsePayloadBytes: 0,
                duplicateKeyRetry);
        }

        public void Cancel()
        {
            AiMongoAttributionDiagnostics.CompleteSemanticOperation(
                counter,
                startedTimestamp,
                AiMongoAttributionDiagnostics.SemanticOutcome.Cancellation,
                returnedDocuments: 0,
                responsePayloadBytes: 0,
                duplicateKeyRetry: false);
        }
    }

    /// <summary>
    /// Diagnostic-only PERF2 MongoDB semantic and driver attribution.
    /// </summary>
    /// <remarks>
    /// Semantic counters are recorded in process memory at bounded operation names. Driver events
    /// are attached only when PERF2 is enabled and retain bounded command/client-role labels only.
    /// Absolute process snapshots are published best-effort to a scope-specific Redis hash using
    /// an already-active runtime Redis database; MongoDB is never used to publish PERF2 diagnostics.
    /// </remarks>
    public static class AiMongoAttributionDiagnostics
    {
        public const string EnabledEnvironmentVariable =
            "MULTIPLEXED_PERF2_MONGO_ATTRIBUTION";

        public const string ScopeEnvironmentVariable =
            "MULTIPLEXED_PERF2_MONGO_ATTRIBUTION_SCOPE";

        public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan SnapshotTtl = TimeSpan.FromHours(2);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly HashSet<string> AllowedOperations = new(
            new[]
            {
                AiMongoAttributionOperations.LedgerSequenceNext,
                AiMongoAttributionOperations.LedgerEntryAppend,
                AiMongoAttributionOperations.LedgerExecutionLoad,
                AiMongoAttributionOperations.LedgerQuery,
                AiMongoAttributionOperations.TraceAppend,
                AiMongoAttributionOperations.TraceExecutionLoad,
                AiMongoAttributionOperations.MetricAppend,
                AiMongoAttributionOperations.SnapshotUpsert,
                AiMongoAttributionOperations.SnapshotLoad,
                AiMongoAttributionOperations.SnapshotDelete,
                AiMongoAttributionOperations.ReplayMetadataUpsert,
                AiMongoAttributionOperations.ReplayMetadataLoad,
                AiMongoAttributionOperations.ChildRelationIdentityLoad,
                AiMongoAttributionOperations.ChildRelationChildExecutionLoad,
                AiMongoAttributionOperations.ChildRelationQuery,
                AiMongoAttributionOperations.ChildRelationAppend,
                AiMongoAttributionOperations.ChildRelationTransition,
                AiMongoAttributionOperations.PayloadSave,
                AiMongoAttributionOperations.PayloadLoad,
                AiMongoAttributionOperations.PayloadDelete,
                AiMongoAttributionOperations.StepPayloadIndexUpsert,
                AiMongoAttributionOperations.StepPayloadIndexLoad,
                AiMongoAttributionOperations.StepPayloadIndexExecutionLoad,
                AiMongoAttributionOperations.StepPayloadIndexLoadMany,
                AiMongoAttributionOperations.StepPayloadIndexDelete,
                AiMongoAttributionOperations.RuntimeLifecycleAppend,
                AiMongoAttributionOperations.RuntimeLifecycleQuery,
                AiMongoAttributionOperations.RecoveryForensicsLoad,
                AiMongoAttributionOperations.RecoveryForensicsAppend,
                AiMongoAttributionOperations.RecoveryForensicsReplace,
                AiMongoAttributionOperations.RecoveryForensicsQuery,
                AiMongoAttributionOperations.PoolFailureJournalAppend,
                AiMongoAttributionOperations.PoolFailureJournalQuery,
                AiMongoAttributionOperations.TestHarnessLedgerExecutionLoad,
                AiMongoAttributionOperations.TestHarnessLedgerQuery,
                AiMongoAttributionOperations.TestHarnessTraceExecutionLoad,
                AiMongoAttributionOperations.TestHarnessReplayMetadataLoad,
                AiMongoAttributionOperations.TestHarnessChildRelationIdentityLoad,
                AiMongoAttributionOperations.TestHarnessChildRelationChildExecutionLoad,
                AiMongoAttributionOperations.TestHarnessChildRelationQuery,
                AiMongoAttributionOperations.TestHarnessRuntimeLifecycleQuery,
                AiMongoAttributionOperations.TestHarnessRecoveryForensicsLoad,
                AiMongoAttributionOperations.TestHarnessRecoveryForensicsQuery,
                AiMongoAttributionOperations.TestHarnessPoolFailureJournalQuery
            },
            StringComparer.Ordinal);

        private static readonly HashSet<string> AllowedSemanticCommands = new(
            new[]
            {
                AiMongoAttributionCommands.Insert,
                AiMongoAttributionCommands.FindAndModify,
                AiMongoAttributionCommands.Find,
                AiMongoAttributionCommands.Update,
                AiMongoAttributionCommands.Delete
            },
            StringComparer.Ordinal);

        private static readonly HashSet<string> AllowedDriverCommands = new(
            new[]
            {
                AiMongoAttributionCommands.Insert,
                AiMongoAttributionCommands.FindAndModify,
                AiMongoAttributionCommands.Find,
                AiMongoAttributionCommands.GetMore,
                AiMongoAttributionCommands.Update,
                AiMongoAttributionCommands.Delete,
                AiMongoAttributionCommands.CreateIndexes,
                AiMongoAttributionCommands.ListIndexes,
                AiMongoAttributionCommands.Hello,
                AiMongoAttributionCommands.IsMaster,
                AiMongoAttributionCommands.Aggregate,
                AiMongoAttributionCommands.Count,
                AiMongoAttributionCommands.Distinct,
                AiMongoAttributionCommands.KillCursors,
                AiMongoAttributionCommands.EndSessions,
                AiMongoAttributionCommands.Ping,
                AiMongoAttributionCommands.BuildInfo,
                AiMongoAttributionCommands.SaslStart,
                AiMongoAttributionCommands.SaslContinue,
                AiMongoAttributionCommands.Other
            },
            StringComparer.Ordinal);

        private static readonly HashSet<string> AllowedClientRoles = new(
            new[]
            {
                AiMongoAttributionClientRoles.SharedRuntime,
                AiMongoAttributionClientRoles.Snapshot,
                AiMongoAttributionClientRoles.MetricStore,
                AiMongoAttributionClientRoles.PayloadStore,
                AiMongoAttributionClientRoles.StepPayloadIndexStore,
                AiMongoAttributionClientRoles.RuntimeLifecycle,
                AiMongoAttributionClientRoles.RecoveryForensics,
                AiMongoAttributionClientRoles.PoolFailureJournal,
                AiMongoAttributionClientRoles.Other
            },
            StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<SemanticMetricKey, SemanticCounter> SemanticCounters = new();
        private static readonly ConcurrentDictionary<DriverCommandMetricKey, DriverCommandCounter> DriverCommandCounters = new();
        private static readonly ConcurrentDictionary<string, DriverPoolCounter> DriverPoolCounters = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<ObservedClientKey, byte> ObservedClients = new();
        private static readonly AsyncLocal<OperationOverrideState?> OperationOverride = new();
        private static readonly object StateGate = new();
        private static readonly SemaphoreSlim FlushGate = new(1, 1);

        private static string? activeScope;
        private static IDatabase? publisherDatabase;
        private static Timer? publisherTimer;
        private static long publicationSequence;
        private static int ttlApplied;

        public static string CurrentProcessIdentity => BuildProcessIdentity();

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

        public static void EndScope(string? scope)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                return;
            }

            if (string.Equals(
                    Environment.GetEnvironmentVariable(ScopeEnvironmentVariable),
                    scope,
                    StringComparison.Ordinal))
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
        /// Creates a MongoClient with PERF2 driver events attached only when attribution is enabled.
        /// No MongoDB connection, write concern, read preference, retry, TLS, timeout, or credential
        /// setting is modified beyond parsing the same supplied connection string.
        /// </summary>
        public static MongoClient CreateMongoClient(
            string connectionString,
            string clientRole)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

            if (!IsEnabled)
            {
                return new MongoClient(connectionString);
            }

            var normalizedRole = NormalizeClientRole(clientRole);
            var clientIdentity = Guid.NewGuid().ToString("N");
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            var existingConfigurator = settings.ClusterConfigurator;

            settings.ClusterConfigurator = clusterBuilder =>
            {
                existingConfigurator?.Invoke(clusterBuilder);

                clusterBuilder.Subscribe<CommandStartedEvent>(
                    evt => RecordDriverCommandStarted(clientIdentity, normalizedRole, evt.CommandName));
                clusterBuilder.Subscribe<CommandSucceededEvent>(
                    evt => RecordDriverCommandSucceeded(clientIdentity, normalizedRole, evt.CommandName, evt.Duration));
                clusterBuilder.Subscribe<CommandFailedEvent>(
                    evt => RecordDriverCommandFailed(clientIdentity, normalizedRole, evt.CommandName, evt.Duration));

                clusterBuilder.Subscribe<ConnectionPoolOpenedEvent>(
                    _ => RecordDriverPoolEvent(clientIdentity, normalizedRole, DriverPoolEvent.PoolOpened));
                clusterBuilder.Subscribe<ConnectionPoolClosedEvent>(
                    _ => RecordDriverPoolEvent(clientIdentity, normalizedRole, DriverPoolEvent.PoolClosed));
                clusterBuilder.Subscribe<ConnectionOpenedEvent>(
                    _ => RecordDriverPoolEvent(clientIdentity, normalizedRole, DriverPoolEvent.ConnectionOpened));
                clusterBuilder.Subscribe<ConnectionClosedEvent>(
                    _ => RecordDriverPoolEvent(clientIdentity, normalizedRole, DriverPoolEvent.ConnectionClosed));
                clusterBuilder.Subscribe<ConnectionOpeningFailedEvent>(
                    _ => RecordDriverPoolEvent(clientIdentity, normalizedRole, DriverPoolEvent.ConnectionOpenFailed));
                clusterBuilder.Subscribe<ConnectionPoolCheckedOutConnectionEvent>(
                    _ => RecordDriverPoolEvent(clientIdentity, normalizedRole, DriverPoolEvent.Checkout));
                clusterBuilder.Subscribe<ConnectionPoolCheckingOutConnectionFailedEvent>(
                    _ => RecordDriverPoolEvent(clientIdentity, normalizedRole, DriverPoolEvent.CheckoutFailed));
            };

            return new MongoClient(settings);
        }

        public static AiMongoAttributionMeasurement StartOperation(
            string operation,
            string command,
            long requestedDocuments = 0,
            long requestPayloadBytes = 0)
        {
            try
            {
                if (!TryResolveScope(out var scope))
                {
                    return default;
                }

                var effectiveOperation = ResolveOperationOverride(operation);
                var normalizedCommand = NormalizeSemanticCommand(command);
                if (!IsAllowedSemanticOperation(effectiveOperation, normalizedCommand))
                {
                    return default;
                }

                EnsureScope(scope);

                var counter = SemanticCounters.GetOrAdd(
                    new SemanticMetricKey(effectiveOperation, normalizedCommand),
                    static _ => new SemanticCounter());

                Interlocked.Increment(ref counter.Calls);
                Interlocked.Add(ref counter.RequestedDocuments, Math.Max(0L, requestedDocuments));
                Interlocked.Add(ref counter.RequestPayloadBytes, Math.Max(0L, requestPayloadBytes));

                return new AiMongoAttributionMeasurement(
                    counter,
                    Stopwatch.GetTimestamp());
            }
            catch
            {
                return default;
            }
        }

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
                    "The source PERF2 Mongo operation is not part of the bounded attribution taxonomy.");
            }

            if (!AllowedOperations.Contains(overrideOperation))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(overrideOperation),
                    overrideOperation,
                    "The override PERF2 Mongo operation is not part of the bounded attribution taxonomy.");
            }

            if (!IsEnabled)
            {
                return NoopOperationOverrideLease.Instance;
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
        /// Reclassifies read-only production store calls initiated by the production test harness.
        /// The override is AsyncLocal and therefore does not relabel independent background runtime work.
        /// </summary>
        public static IDisposable OverrideForTestHarnessAudit()
        {
            if (!IsEnabled)
            {
                return NoopOperationOverrideLease.Instance;
            }

            return new CompositeOperationOverrideLease(
                new[]
                {
                    OverrideOperation(AiMongoAttributionOperations.LedgerExecutionLoad, AiMongoAttributionOperations.TestHarnessLedgerExecutionLoad),
                    OverrideOperation(AiMongoAttributionOperations.LedgerQuery, AiMongoAttributionOperations.TestHarnessLedgerQuery),
                    OverrideOperation(AiMongoAttributionOperations.TraceExecutionLoad, AiMongoAttributionOperations.TestHarnessTraceExecutionLoad),
                    OverrideOperation(AiMongoAttributionOperations.ReplayMetadataLoad, AiMongoAttributionOperations.TestHarnessReplayMetadataLoad),
                    OverrideOperation(AiMongoAttributionOperations.ChildRelationIdentityLoad, AiMongoAttributionOperations.TestHarnessChildRelationIdentityLoad),
                    OverrideOperation(AiMongoAttributionOperations.ChildRelationChildExecutionLoad, AiMongoAttributionOperations.TestHarnessChildRelationChildExecutionLoad),
                    OverrideOperation(AiMongoAttributionOperations.ChildRelationQuery, AiMongoAttributionOperations.TestHarnessChildRelationQuery),
                    OverrideOperation(AiMongoAttributionOperations.RuntimeLifecycleQuery, AiMongoAttributionOperations.TestHarnessRuntimeLifecycleQuery),
                    OverrideOperation(AiMongoAttributionOperations.RecoveryForensicsLoad, AiMongoAttributionOperations.TestHarnessRecoveryForensicsLoad),
                    OverrideOperation(AiMongoAttributionOperations.RecoveryForensicsQuery, AiMongoAttributionOperations.TestHarnessRecoveryForensicsQuery),
                    OverrideOperation(AiMongoAttributionOperations.PoolFailureJournalQuery, AiMongoAttributionOperations.TestHarnessPoolFailureJournalQuery)
                });
        }

        /// <summary>
        /// Registers an already-active runtime Redis database as the best-effort cross-process publisher.
        /// This method never creates a Redis connection and is a no-op unless PERF2 and a scope are active.
        /// </summary>
        public static void RegisterRedisPublisher(IDatabase database)
        {
            ArgumentNullException.ThrowIfNull(database);

            try
            {
                if (!TryResolveScope(out var scope))
                {
                    return;
                }

                EnsureScope(scope);
                EnsurePublisher(database, scope);
            }
            catch
            {
                // PERF2 attribution is diagnostic-only and must never change runtime behavior.
            }
        }

        public static IReadOnlyList<AiMongoAttributionOperationSnapshot> SnapshotCurrentProcessOperations()
        {
            return SemanticCounters
                .Select(pair => ToSnapshot(pair.Key, pair.Value))
                .OrderByDescending(item => item.Calls)
                .ThenBy(item => item.Command, StringComparer.Ordinal)
                .ThenBy(item => item.Operation, StringComparer.Ordinal)
                .ToArray();
        }

        public static IReadOnlyList<AiMongoAttributionDriverCommandSnapshot> SnapshotCurrentProcessDriverCommands()
        {
            return DriverCommandCounters
                .Select(pair => new AiMongoAttributionDriverCommandSnapshot(
                    pair.Key.ClientRole,
                    pair.Key.Command,
                    Interlocked.Read(ref pair.Value.Started),
                    Interlocked.Read(ref pair.Value.Succeeded),
                    Interlocked.Read(ref pair.Value.Failed),
                    Interlocked.Read(ref pair.Value.AggregateDurationTicks)))
                .OrderByDescending(item => item.Started)
                .ThenBy(item => item.Command, StringComparer.Ordinal)
                .ThenBy(item => item.ClientRole, StringComparer.Ordinal)
                .ToArray();
        }

        public static IReadOnlyList<AiMongoAttributionDriverPoolSnapshot> SnapshotCurrentProcessDriverPools()
        {
            var clientsByRole = ObservedClients
                .Keys
                .GroupBy(key => key.ClientRole, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.Ordinal);

            return DriverPoolCounters
                .Select(pair => new AiMongoAttributionDriverPoolSnapshot(
                    pair.Key,
                    clientsByRole.GetValueOrDefault(pair.Key),
                    Interlocked.Read(ref pair.Value.PoolsOpened),
                    Interlocked.Read(ref pair.Value.PoolsClosed),
                    Interlocked.Read(ref pair.Value.ConnectionsOpened),
                    Interlocked.Read(ref pair.Value.ConnectionsClosed),
                    Interlocked.Read(ref pair.Value.ConnectionOpenFailures),
                    Interlocked.Read(ref pair.Value.Checkouts),
                    Interlocked.Read(ref pair.Value.CheckoutFailures)))
                .Concat(
                    clientsByRole
                        .Where(pair => !DriverPoolCounters.ContainsKey(pair.Key))
                        .Select(pair => new AiMongoAttributionDriverPoolSnapshot(
                            pair.Key,
                            pair.Value,
                            0L,
                            0L,
                            0L,
                            0L,
                            0L,
                            0L,
                            0L)))
                .OrderByDescending(item => item.ClientInstancesObserved)
                .ThenBy(item => item.ClientRole, StringComparer.Ordinal)
                .ToArray();
        }

        public static void ResetCurrentProcess()
        {
            SemanticCounters.Clear();
            DriverCommandCounters.Clear();
            DriverPoolCounters.Clear();
            ObservedClients.Clear();
            Interlocked.Exchange(ref publicationSequence, 0L);
        }

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
            return FlushAsync(database, scope, waitForGate: true, cancellationToken);
        }

        public static async Task<AiMongoAttributionAggregate?> CollectAsync(
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

            var semanticAggregates = new Dictionary<SemanticMetricKey, SemanticAggregate>();
            var driverCommandAggregates = new Dictionary<DriverCommandMetricKey, DriverCommandAggregate>();
            var driverPoolAggregates = new Dictionary<string, DriverPoolAggregate>(StringComparer.Ordinal);
            var processSnapshots = new List<AiMongoAttributionProcessSnapshot>();
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

                var operations = envelope.Operations
                    .Where(item => IsAllowedSemanticOperation(item.Operation, item.Command))
                    .ToArray();
                var driverCommands = envelope.DriverCommands
                    .Where(item => IsAllowedDriverCommand(item.Command) && AllowedClientRoles.Contains(item.ClientRole))
                    .ToArray();
                var driverPools = envelope.DriverPools
                    .Where(item => AllowedClientRoles.Contains(item.ClientRole))
                    .ToArray();

                processSnapshotCount++;
                publicationSequenceTotal += envelope.PublicationSequence;
                processSnapshots.Add(
                    new AiMongoAttributionProcessSnapshot(
                        envelope.ProcessIdentity,
                        envelope.PublicationSequence,
                        envelope.CapturedAtUtc,
                        operations,
                        driverCommands,
                        driverPools));

                foreach (var operation in operations)
                {
                    var key = new SemanticMetricKey(operation.Operation, operation.Command);
                    if (!semanticAggregates.TryGetValue(key, out var aggregate))
                    {
                        aggregate = new SemanticAggregate();
                        semanticAggregates.Add(key, aggregate);
                    }

                    aggregate.Add(operation);
                }

                foreach (var command in driverCommands)
                {
                    var key = new DriverCommandMetricKey(command.ClientRole, command.Command);
                    if (!driverCommandAggregates.TryGetValue(key, out var aggregate))
                    {
                        aggregate = new DriverCommandAggregate();
                        driverCommandAggregates.Add(key, aggregate);
                    }

                    aggregate.Add(command);
                }

                foreach (var pool in driverPools)
                {
                    if (!driverPoolAggregates.TryGetValue(pool.ClientRole, out var aggregate))
                    {
                        aggregate = new DriverPoolAggregate();
                        driverPoolAggregates.Add(pool.ClientRole, aggregate);
                    }

                    aggregate.Add(pool);
                }
            }

            var aggregateOperations = semanticAggregates
                .Select(pair => pair.Value.ToSnapshot(pair.Key))
                .OrderByDescending(item => item.Calls)
                .ThenBy(item => item.Command, StringComparer.Ordinal)
                .ThenBy(item => item.Operation, StringComparer.Ordinal)
                .ToArray();

            var aggregateDriverCommands = driverCommandAggregates
                .Select(pair => pair.Value.ToSnapshot(pair.Key))
                .OrderByDescending(item => item.Started)
                .ThenBy(item => item.Command, StringComparer.Ordinal)
                .ThenBy(item => item.ClientRole, StringComparer.Ordinal)
                .ToArray();

            var aggregateDriverPools = driverPoolAggregates
                .Select(pair => pair.Value.ToSnapshot(pair.Key))
                .OrderByDescending(item => item.ClientInstancesObserved)
                .ThenBy(item => item.ClientRole, StringComparer.Ordinal)
                .ToArray();

            return new AiMongoAttributionAggregate(
                processSnapshotCount,
                publicationSequenceTotal,
                aggregateOperations,
                aggregateDriverCommands,
                aggregateDriverPools)
            {
                ProcessSnapshots = processSnapshots
                    .OrderBy(snapshot => snapshot.ProcessIdentity, StringComparer.Ordinal)
                    .ToArray()
            };
        }

        internal enum SemanticOutcome
        {
            Success,
            Failure,
            Cancellation
        }

        internal sealed class SemanticCounter
        {
            public long Calls;
            public long RequestedDocuments;
            public long ReturnedDocuments;
            public long RequestPayloadBytes;
            public long ResponsePayloadBytes;
            public long Successes;
            public long Failures;
            public long Cancellations;
            public long DuplicateKeyRetries;
            public long AggregateDurationTicks;
            public long LatencyLe1Ms;
            public long LatencyLe2Ms;
            public long LatencyLe5Ms;
            public long LatencyLe10Ms;
            public long LatencyLe25Ms;
            public long LatencyLe50Ms;
            public long LatencyLe100Ms;
            public long LatencyLe250Ms;
            public long LatencyGt250Ms;
        }

        internal static void CompleteSemanticOperation(
            SemanticCounter? counter,
            long startedTimestamp,
            SemanticOutcome outcome,
            long returnedDocuments,
            long responsePayloadBytes,
            bool duplicateKeyRetry)
        {
            if (counter is null)
            {
                return;
            }

            try
            {
                var duration = Stopwatch.GetElapsedTime(startedTimestamp);
                Interlocked.Add(ref counter.AggregateDurationTicks, duration.Ticks);
                Interlocked.Add(ref counter.ReturnedDocuments, Math.Max(0L, returnedDocuments));
                Interlocked.Add(ref counter.ResponsePayloadBytes, Math.Max(0L, responsePayloadBytes));

                switch (outcome)
                {
                    case SemanticOutcome.Success:
                        Interlocked.Increment(ref counter.Successes);
                        break;
                    case SemanticOutcome.Failure:
                        Interlocked.Increment(ref counter.Failures);
                        break;
                    case SemanticOutcome.Cancellation:
                        Interlocked.Increment(ref counter.Cancellations);
                        break;
                }

                if (duplicateKeyRetry)
                {
                    Interlocked.Increment(ref counter.DuplicateKeyRetries);
                }

                RecordLatencyBucket(counter, duration);
            }
            catch
            {
                // PERF2 attribution must never affect runtime behavior.
            }
        }

        private static void RecordLatencyBucket(
            SemanticCounter counter,
            TimeSpan duration)
        {
            var milliseconds = duration.TotalMilliseconds;
            if (milliseconds <= 1D)
            {
                Interlocked.Increment(ref counter.LatencyLe1Ms);
            }
            else if (milliseconds <= 2D)
            {
                Interlocked.Increment(ref counter.LatencyLe2Ms);
            }
            else if (milliseconds <= 5D)
            {
                Interlocked.Increment(ref counter.LatencyLe5Ms);
            }
            else if (milliseconds <= 10D)
            {
                Interlocked.Increment(ref counter.LatencyLe10Ms);
            }
            else if (milliseconds <= 25D)
            {
                Interlocked.Increment(ref counter.LatencyLe25Ms);
            }
            else if (milliseconds <= 50D)
            {
                Interlocked.Increment(ref counter.LatencyLe50Ms);
            }
            else if (milliseconds <= 100D)
            {
                Interlocked.Increment(ref counter.LatencyLe100Ms);
            }
            else if (milliseconds <= 250D)
            {
                Interlocked.Increment(ref counter.LatencyLe250Ms);
            }
            else
            {
                Interlocked.Increment(ref counter.LatencyGt250Ms);
            }
        }

        private static AiMongoAttributionOperationSnapshot ToSnapshot(
            SemanticMetricKey key,
            SemanticCounter counter)
        {
            return new AiMongoAttributionOperationSnapshot(
                key.Operation,
                key.Command,
                Interlocked.Read(ref counter.Calls),
                Interlocked.Read(ref counter.RequestedDocuments),
                Interlocked.Read(ref counter.ReturnedDocuments),
                Interlocked.Read(ref counter.RequestPayloadBytes),
                Interlocked.Read(ref counter.ResponsePayloadBytes),
                Interlocked.Read(ref counter.Successes),
                Interlocked.Read(ref counter.Failures),
                Interlocked.Read(ref counter.Cancellations),
                Interlocked.Read(ref counter.DuplicateKeyRetries),
                Interlocked.Read(ref counter.AggregateDurationTicks),
                Interlocked.Read(ref counter.LatencyLe1Ms),
                Interlocked.Read(ref counter.LatencyLe2Ms),
                Interlocked.Read(ref counter.LatencyLe5Ms),
                Interlocked.Read(ref counter.LatencyLe10Ms),
                Interlocked.Read(ref counter.LatencyLe25Ms),
                Interlocked.Read(ref counter.LatencyLe50Ms),
                Interlocked.Read(ref counter.LatencyLe100Ms),
                Interlocked.Read(ref counter.LatencyLe250Ms),
                Interlocked.Read(ref counter.LatencyGt250Ms));
        }

        private static void RecordDriverCommandStarted(
            string clientIdentity,
            string clientRole,
            string commandName)
        {
            try
            {
                if (!TryResolveScope(out var scope))
                {
                    return;
                }

                EnsureScope(scope);
                ObserveClient(clientIdentity, clientRole);
                var command = NormalizeDriverCommand(commandName);
                var counter = DriverCommandCounters.GetOrAdd(
                    new DriverCommandMetricKey(clientRole, command),
                    static _ => new DriverCommandCounter());
                Interlocked.Increment(ref counter.Started);
            }
            catch
            {
            }
        }

        private static void RecordDriverCommandSucceeded(
            string clientIdentity,
            string clientRole,
            string commandName,
            TimeSpan duration)
        {
            try
            {
                if (!TryResolveScope(out var scope))
                {
                    return;
                }

                EnsureScope(scope);
                ObserveClient(clientIdentity, clientRole);
                var command = NormalizeDriverCommand(commandName);
                var counter = DriverCommandCounters.GetOrAdd(
                    new DriverCommandMetricKey(clientRole, command),
                    static _ => new DriverCommandCounter());
                Interlocked.Increment(ref counter.Succeeded);
                Interlocked.Add(ref counter.AggregateDurationTicks, duration.Ticks);
            }
            catch
            {
            }
        }

        private static void RecordDriverCommandFailed(
            string clientIdentity,
            string clientRole,
            string commandName,
            TimeSpan duration)
        {
            try
            {
                if (!TryResolveScope(out var scope))
                {
                    return;
                }

                EnsureScope(scope);
                ObserveClient(clientIdentity, clientRole);
                var command = NormalizeDriverCommand(commandName);
                var counter = DriverCommandCounters.GetOrAdd(
                    new DriverCommandMetricKey(clientRole, command),
                    static _ => new DriverCommandCounter());
                Interlocked.Increment(ref counter.Failed);
                Interlocked.Add(ref counter.AggregateDurationTicks, duration.Ticks);
            }
            catch
            {
            }
        }

        private static void RecordDriverPoolEvent(
            string clientIdentity,
            string clientRole,
            DriverPoolEvent poolEvent)
        {
            try
            {
                if (!TryResolveScope(out var scope))
                {
                    return;
                }

                EnsureScope(scope);
                ObserveClient(clientIdentity, clientRole);
                var counter = DriverPoolCounters.GetOrAdd(
                    clientRole,
                    static _ => new DriverPoolCounter());

                switch (poolEvent)
                {
                    case DriverPoolEvent.PoolOpened:
                        Interlocked.Increment(ref counter.PoolsOpened);
                        break;
                    case DriverPoolEvent.PoolClosed:
                        Interlocked.Increment(ref counter.PoolsClosed);
                        break;
                    case DriverPoolEvent.ConnectionOpened:
                        Interlocked.Increment(ref counter.ConnectionsOpened);
                        break;
                    case DriverPoolEvent.ConnectionClosed:
                        Interlocked.Increment(ref counter.ConnectionsClosed);
                        break;
                    case DriverPoolEvent.ConnectionOpenFailed:
                        Interlocked.Increment(ref counter.ConnectionOpenFailures);
                        break;
                    case DriverPoolEvent.Checkout:
                        Interlocked.Increment(ref counter.Checkouts);
                        break;
                    case DriverPoolEvent.CheckoutFailed:
                        Interlocked.Increment(ref counter.CheckoutFailures);
                        break;
                }
            }
            catch
            {
            }
        }

        private static void ObserveClient(
            string clientIdentity,
            string clientRole)
        {
            ObservedClients.TryAdd(
                new ObservedClientKey(clientRole, clientIdentity),
                0);
        }

        private static string ResolveOperationOverride(string operation)
        {
            var current = OperationOverride.Value;
            while (current is not null)
            {
                if (string.Equals(current.SourceOperation, operation, StringComparison.Ordinal))
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

            var candidate = Environment.GetEnvironmentVariable(ScopeEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            scope = candidate.Trim();
            return true;
        }

        private static bool IsAllowedSemanticOperation(
            string operation,
            string command)
        {
            return !string.IsNullOrWhiteSpace(operation) &&
                   AllowedOperations.Contains(operation) &&
                   AllowedSemanticCommands.Contains(command);
        }

        private static bool IsAllowedDriverCommand(string command)
        {
            return AllowedDriverCommands.Contains(command);
        }

        private static string NormalizeSemanticCommand(string? command)
        {
            return command?.Trim().ToUpperInvariant() ?? string.Empty;
        }

        private static string NormalizeDriverCommand(string? command)
        {
            var normalized = command?.Trim().ToUpperInvariant() ?? string.Empty;
            return AllowedDriverCommands.Contains(normalized)
                ? normalized
                : AiMongoAttributionCommands.Other;
        }

        private static string NormalizeClientRole(string? clientRole)
        {
            var normalized = clientRole?.Trim() ?? string.Empty;
            return AllowedClientRoles.Contains(normalized)
                ? normalized
                : AiMongoAttributionClientRoles.Other;
        }

        private static void EnsureScope(string scope)
        {
            if (string.Equals(Volatile.Read(ref activeScope), scope, StringComparison.Ordinal))
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

            _ = FlushAsync(database, scope, waitForGate: false, CancellationToken.None);
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

                var operations = SnapshotCurrentProcessOperations();
                var driverCommands = SnapshotCurrentProcessDriverCommands();
                var driverPools = SnapshotCurrentProcessDriverPools();

                if (operations.Count == 0 &&
                    driverCommands.Count == 0 &&
                    driverPools.Count == 0)
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
                    Operations = operations.ToArray(),
                    DriverCommands = driverCommands.ToArray(),
                    DriverPools = driverPools.ToArray()
                };

                var key = BuildRedisKey(scope);
                var payload = JsonSerializer.Serialize(envelope, JsonOptions);
                await database
                    .HashSetAsync(key, envelope.ProcessIdentity, payload)
                    .ConfigureAwait(false);

                if (Interlocked.CompareExchange(ref ttlApplied, 1, 0) == 0)
                {
                    await database.KeyExpireAsync(key, SnapshotTtl).ConfigureAwait(false);
                }
            }
            catch
            {
                // PERF2 attribution is best-effort and must not affect application behavior.
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
            SemanticCounters.Clear();
            DriverCommandCounters.Clear();
            DriverPoolCounters.Clear();
            ObservedClients.Clear();
            Interlocked.Exchange(ref publicationSequence, 0L);
            Interlocked.Exchange(ref ttlApplied, 0);
        }

        private static RedisKey BuildRedisKey(string scope)
        {
            return $"multiplexed:perf2:mongo-attribution:{{{scope}}}";
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

        private sealed class CompositeOperationOverrideLease : IDisposable
        {
            private readonly IDisposable[] leases;
            private int disposed;

            public CompositeOperationOverrideLease(IDisposable[] leases)
            {
                this.leases = leases;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                for (var index = leases.Length - 1; index >= 0; index--)
                {
                    leases[index].Dispose();
                }
            }
        }

        private readonly record struct SemanticMetricKey(string Operation, string Command);
        private readonly record struct DriverCommandMetricKey(string ClientRole, string Command);
        private readonly record struct ObservedClientKey(string ClientRole, string ClientIdentity);

        private sealed class DriverCommandCounter
        {
            public long Started;
            public long Succeeded;
            public long Failed;
            public long AggregateDurationTicks;
        }

        private sealed class DriverPoolCounter
        {
            public long PoolsOpened;
            public long PoolsClosed;
            public long ConnectionsOpened;
            public long ConnectionsClosed;
            public long ConnectionOpenFailures;
            public long Checkouts;
            public long CheckoutFailures;
        }

        private enum DriverPoolEvent
        {
            PoolOpened,
            PoolClosed,
            ConnectionOpened,
            ConnectionClosed,
            ConnectionOpenFailed,
            Checkout,
            CheckoutFailed
        }

        private sealed class SemanticAggregate
        {
            public long Calls;
            public long RequestedDocuments;
            public long ReturnedDocuments;
            public long RequestPayloadBytes;
            public long ResponsePayloadBytes;
            public long Successes;
            public long Failures;
            public long Cancellations;
            public long DuplicateKeyRetries;
            public long AggregateDurationTicks;
            public long LatencyLe1Ms;
            public long LatencyLe2Ms;
            public long LatencyLe5Ms;
            public long LatencyLe10Ms;
            public long LatencyLe25Ms;
            public long LatencyLe50Ms;
            public long LatencyLe100Ms;
            public long LatencyLe250Ms;
            public long LatencyGt250Ms;

            public void Add(AiMongoAttributionOperationSnapshot snapshot)
            {
                Calls += snapshot.Calls;
                RequestedDocuments += snapshot.RequestedDocuments;
                ReturnedDocuments += snapshot.ReturnedDocuments;
                RequestPayloadBytes += snapshot.RequestPayloadBytes;
                ResponsePayloadBytes += snapshot.ResponsePayloadBytes;
                Successes += snapshot.Successes;
                Failures += snapshot.Failures;
                Cancellations += snapshot.Cancellations;
                DuplicateKeyRetries += snapshot.DuplicateKeyRetries;
                AggregateDurationTicks += snapshot.AggregateDurationTicks;
                LatencyLe1Ms += snapshot.LatencyLe1Ms;
                LatencyLe2Ms += snapshot.LatencyLe2Ms;
                LatencyLe5Ms += snapshot.LatencyLe5Ms;
                LatencyLe10Ms += snapshot.LatencyLe10Ms;
                LatencyLe25Ms += snapshot.LatencyLe25Ms;
                LatencyLe50Ms += snapshot.LatencyLe50Ms;
                LatencyLe100Ms += snapshot.LatencyLe100Ms;
                LatencyLe250Ms += snapshot.LatencyLe250Ms;
                LatencyGt250Ms += snapshot.LatencyGt250Ms;
            }

            public AiMongoAttributionOperationSnapshot ToSnapshot(SemanticMetricKey key)
            {
                return new AiMongoAttributionOperationSnapshot(
                    key.Operation,
                    key.Command,
                    Calls,
                    RequestedDocuments,
                    ReturnedDocuments,
                    RequestPayloadBytes,
                    ResponsePayloadBytes,
                    Successes,
                    Failures,
                    Cancellations,
                    DuplicateKeyRetries,
                    AggregateDurationTicks,
                    LatencyLe1Ms,
                    LatencyLe2Ms,
                    LatencyLe5Ms,
                    LatencyLe10Ms,
                    LatencyLe25Ms,
                    LatencyLe50Ms,
                    LatencyLe100Ms,
                    LatencyLe250Ms,
                    LatencyGt250Ms);
            }
        }

        private sealed class DriverCommandAggregate
        {
            public long Started;
            public long Succeeded;
            public long Failed;
            public long AggregateDurationTicks;

            public void Add(AiMongoAttributionDriverCommandSnapshot snapshot)
            {
                Started += snapshot.Started;
                Succeeded += snapshot.Succeeded;
                Failed += snapshot.Failed;
                AggregateDurationTicks += snapshot.AggregateDurationTicks;
            }

            public AiMongoAttributionDriverCommandSnapshot ToSnapshot(DriverCommandMetricKey key)
            {
                return new AiMongoAttributionDriverCommandSnapshot(
                    key.ClientRole,
                    key.Command,
                    Started,
                    Succeeded,
                    Failed,
                    AggregateDurationTicks);
            }
        }

        private sealed class DriverPoolAggregate
        {
            public long ClientInstancesObserved;
            public long PoolsOpened;
            public long PoolsClosed;
            public long ConnectionsOpened;
            public long ConnectionsClosed;
            public long ConnectionOpenFailures;
            public long Checkouts;
            public long CheckoutFailures;

            public void Add(AiMongoAttributionDriverPoolSnapshot snapshot)
            {
                ClientInstancesObserved += snapshot.ClientInstancesObserved;
                PoolsOpened += snapshot.PoolsOpened;
                PoolsClosed += snapshot.PoolsClosed;
                ConnectionsOpened += snapshot.ConnectionsOpened;
                ConnectionsClosed += snapshot.ConnectionsClosed;
                ConnectionOpenFailures += snapshot.ConnectionOpenFailures;
                Checkouts += snapshot.Checkouts;
                CheckoutFailures += snapshot.CheckoutFailures;
            }

            public AiMongoAttributionDriverPoolSnapshot ToSnapshot(string clientRole)
            {
                return new AiMongoAttributionDriverPoolSnapshot(
                    clientRole,
                    ClientInstancesObserved,
                    PoolsOpened,
                    PoolsClosed,
                    ConnectionsOpened,
                    ConnectionsClosed,
                    ConnectionOpenFailures,
                    Checkouts,
                    CheckoutFailures);
            }
        }

        private sealed class ProcessSnapshotEnvelope
        {
            public string Scope { get; init; } = string.Empty;
            public string ProcessIdentity { get; init; } = string.Empty;
            public long PublicationSequence { get; init; }
            public DateTimeOffset CapturedAtUtc { get; init; }
            public AiMongoAttributionOperationSnapshot[] Operations { get; init; } =
                Array.Empty<AiMongoAttributionOperationSnapshot>();
            public AiMongoAttributionDriverCommandSnapshot[] DriverCommands { get; init; } =
                Array.Empty<AiMongoAttributionDriverCommandSnapshot>();
            public AiMongoAttributionDriverPoolSnapshot[] DriverPools { get; init; } =
                Array.Empty<AiMongoAttributionDriverPoolSnapshot>();
        }
    }
}
