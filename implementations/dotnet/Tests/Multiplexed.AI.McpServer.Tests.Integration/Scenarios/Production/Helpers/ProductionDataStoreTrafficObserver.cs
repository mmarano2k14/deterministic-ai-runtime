using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.AI.Runtime.Observability.Performance;
using StackExchange.Redis;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Captures server-side Redis and MongoDB traffic deltas around one production scenario batch.
    /// </summary>
    /// <remarks>
    /// The observer reads cumulative server counters before and after the batch. Because the counters
    /// live on the data-store servers, the resulting deltas include work issued by the parent test host,
    /// all child runtime processes, background pumps, watchers, heartbeats, DAG execution, recovery,
    /// ledger, tracing, replay, and forensics components sharing the same Redis and MongoDB instances.
    ///
    /// The observer is diagnostic-only. Snapshot failures are written to test output and never alter
    /// scenario behavior or replace the original test exception.
    /// </remarks>
    internal sealed class ProductionDataStoreTrafficObserver : IAsyncDisposable
    {
        private const string DefaultRedisConnectionString = "localhost:6379";
        private const string DefaultMongoConnectionString = "mongodb://localhost:27017";
        private const string RedisConnectionStringEnvironmentVariable =
            "MULTIPLEXED_TEST_REDIS_CONNECTION_STRING";
        private const string MongoConnectionStringEnvironmentVariable =
            "MULTIPLEXED_TEST_MONGO_CONNECTION_STRING";
        private const string StandardRedisConnectionStringEnvironmentVariable =
            "ConnectionStrings__Redis";
        private const string StandardMongoConnectionStringEnvironmentVariable =
            "ConnectionStrings__Mongo";
        private static readonly TimeSpan ObserverTimeout = TimeSpan.FromSeconds(15);

        private readonly ITestOutputHelper output;
        private readonly ConnectionMultiplexer? redisConnection;
        private readonly MongoClient? mongoClient;
        private readonly RedisTrafficSnapshot? redisStartSnapshot;
        private readonly MongoTrafficSnapshot? mongoStartSnapshot;
        private readonly string? redisStartError;
        private readonly string? mongoStartError;
        private readonly string? redisAttributionScope;
        private readonly string? mongoAttributionScope;
        private readonly Stopwatch stopwatch;
        private int completionState;

        private ProductionDataStoreTrafficObserver(
            ITestOutputHelper output,
            ConnectionMultiplexer? redisConnection,
            MongoClient? mongoClient,
            RedisTrafficSnapshot? redisStartSnapshot,
            MongoTrafficSnapshot? mongoStartSnapshot,
            string? redisStartError,
            string? mongoStartError,
            string? redisAttributionScope,
            string? mongoAttributionScope,
            Stopwatch stopwatch)
        {
            this.output = output;
            this.redisConnection = redisConnection;
            this.mongoClient = mongoClient;
            this.redisStartSnapshot = redisStartSnapshot;
            this.mongoStartSnapshot = mongoStartSnapshot;
            this.redisStartError = redisStartError;
            this.mongoStartError = mongoStartError;
            this.redisAttributionScope = redisAttributionScope;
            this.mongoAttributionScope = mongoAttributionScope;
            this.stopwatch = stopwatch;
        }

        /// <summary>
        /// Connects to the configured local data stores and captures the baseline counters.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The started observer.</returns>
        public static async Task<ProductionDataStoreTrafficObserver> StartAsync(
            ITestOutputHelper output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(output);

            var redisConnectionString = ResolveConnectionString(
                RedisConnectionStringEnvironmentVariable,
                StandardRedisConnectionStringEnvironmentVariable,
                DefaultRedisConnectionString);

            var mongoConnectionString = ResolveConnectionString(
                MongoConnectionStringEnvironmentVariable,
                StandardMongoConnectionStringEnvironmentVariable,
                DefaultMongoConnectionString);

            var redisAttributionScope = AiRedisReadAttributionDiagnostics.BeginScope();
            var mongoAttributionScope = AiMongoAttributionDiagnostics.BeginScope();

            ConnectionMultiplexer? redisConnection = null;
            RedisTrafficSnapshot? redisStartSnapshot = null;
            string? redisStartError = null;

            try
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
                redisOptions.AbortOnConnectFail = false;
                redisOptions.AllowAdmin = true;
                redisOptions.ConnectTimeout = (int)ObserverTimeout.TotalMilliseconds;
                redisOptions.AsyncTimeout = (int)ObserverTimeout.TotalMilliseconds;
                redisOptions.SyncTimeout = (int)ObserverTimeout.TotalMilliseconds;
                redisOptions.ClientName = "multiplexed-production-traffic-observer";

                redisConnection = await ConnectionMultiplexer
                    .ConnectAsync(redisOptions)
                    .ConfigureAwait(false);

                redisStartSnapshot = await CaptureRedisSnapshotAsync(
                        redisConnection,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                redisStartError = FormatException(exception);
                redisConnection?.Dispose();
                redisConnection = null;
            }

            MongoClient? mongoClient = null;
            MongoTrafficSnapshot? mongoStartSnapshot = null;
            string? mongoStartError = null;

            try
            {
                var mongoSettings = MongoClientSettings.FromConnectionString(mongoConnectionString);
                mongoSettings.ServerSelectionTimeout = ObserverTimeout;
                mongoSettings.ConnectTimeout = ObserverTimeout;
                mongoSettings.SocketTimeout = ObserverTimeout;
                mongoSettings.ApplicationName = "multiplexed-production-traffic-observer";

                mongoClient = new MongoClient(mongoSettings);
                mongoStartSnapshot = await CaptureMongoSnapshotAsync(
                        mongoClient,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                mongoStartError = FormatException(exception);
                mongoClient = null;
            }

            var stopwatch = Stopwatch.StartNew();

            output.WriteLine(string.Empty);
            output.WriteLine("[DATA STORE TRAFFIC OBSERVER START]");
            output.WriteLine($"StartedAtUtc='{DateTimeOffset.UtcNow:O}'");
            output.WriteLine($"Redis.Available='{(redisStartSnapshot is not null).ToString().ToLowerInvariant()}'");
            output.WriteLine($"Redis.ServerCount='{redisStartSnapshot?.ServerCount ?? 0}'");
            output.WriteLine($"Redis.Error='{Escape(redisStartError)}'");
            output.WriteLine($"Mongo.Available='{(mongoStartSnapshot is not null).ToString().ToLowerInvariant()}'");
            output.WriteLine($"Mongo.Error='{Escape(mongoStartError)}'");
            output.WriteLine($"RedisAttribution.Enabled='{(!string.IsNullOrWhiteSpace(redisAttributionScope)).ToString().ToLowerInvariant()}'");
            output.WriteLine($"RedisAttribution.Scope='{Escape(redisAttributionScope)}'");
            output.WriteLine($"MongoAttribution.Enabled='{(!string.IsNullOrWhiteSpace(mongoAttributionScope)).ToString().ToLowerInvariant()}'");
            output.WriteLine($"MongoAttribution.Scope='{Escape(mongoAttributionScope)}'");
            output.WriteLine("Scope='Server-wide counters for every process sharing the observed Redis and MongoDB instances.'");
            output.WriteLine("ObserverOverhead='The final delta includes approximately two Redis INFO commands and two MongoDB serverStatus commands. PERF1/PERF2 cross-process attribution uses bounded Redis HSET snapshots plus final HGETALL collection; PERF2 does not write attribution data to MongoDB.'");
            output.WriteLine("[DATA STORE TRAFFIC OBSERVER START END]");

            return new ProductionDataStoreTrafficObserver(
                output,
                redisConnection,
                mongoClient,
                redisStartSnapshot,
                mongoStartSnapshot,
                redisStartError,
                mongoStartError,
                redisAttributionScope,
                mongoAttributionScope,
                stopwatch);
        }

        /// <summary>
        /// Captures final counters and writes the traffic delta summary.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes after the summary has been written.</returns>
        public async Task CompleteAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref completionState, 1) != 0)
            {
                return;
            }

            stopwatch.Stop();
            var duration = stopwatch.Elapsed;

            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionAggregate? redisAttribution = null;
            string? redisAttributionError = null;

            if (redisConnection is not null &&
                !string.IsNullOrWhiteSpace(redisAttributionScope))
            {
                try
                {
                    await Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics
                        .FlushCurrentProcessAsync(
                            redisConnection.GetDatabase(),
                            cancellationToken)
                        .ConfigureAwait(false);

                    redisAttribution = await Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics
                        .CollectAsync(
                            redisConnection,
                            redisAttributionScope,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    redisAttributionError = FormatException(exception);
                }
            }

            AiMongoAttributionAggregate? mongoAttribution = null;
            string? mongoAttributionError = null;

            if (redisConnection is not null &&
                !string.IsNullOrWhiteSpace(mongoAttributionScope))
            {
                try
                {
                    await AiMongoAttributionDiagnostics
                        .FlushCurrentProcessAsync(
                            redisConnection.GetDatabase(),
                            cancellationToken)
                        .ConfigureAwait(false);

                    mongoAttribution = await AiMongoAttributionDiagnostics
                        .CollectAsync(
                            redisConnection,
                            mongoAttributionScope,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    mongoAttributionError = FormatException(exception);
                }
            }

            RedisTrafficSnapshot? redisEndSnapshot = null;
            string? redisEndError = null;

            if (redisConnection is not null && redisStartSnapshot is not null)
            {
                try
                {
                    redisEndSnapshot = await CaptureRedisSnapshotAsync(
                            redisConnection,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    redisEndError = FormatException(exception);
                }
            }

            MongoTrafficSnapshot? mongoEndSnapshot = null;
            string? mongoEndError = null;

            if (mongoClient is not null && mongoStartSnapshot is not null)
            {
                try
                {
                    mongoEndSnapshot = await CaptureMongoSnapshotAsync(
                            mongoClient,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    mongoEndError = FormatException(exception);
                }
            }

            try
            {
                WriteSummary(
                    duration,
                    redisStartSnapshot,
                    redisEndSnapshot,
                    redisEndError ?? redisStartError,
                    mongoStartSnapshot,
                    mongoEndSnapshot,
                    mongoEndError ?? mongoStartError);

                WriteRedisAttributionSummary(
                    redisStartSnapshot,
                    redisEndSnapshot,
                    redisAttribution,
                    redisAttributionError);

                WriteMongoAttributionSummary(
                    mongoStartSnapshot,
                    mongoEndSnapshot,
                    mongoAttribution,
                    mongoAttributionError);
            }
            catch (Exception exception)
            {
                output.WriteLine(string.Empty);
                output.WriteLine("[DATA STORE TRAFFIC OBSERVER SUMMARY FAILURE]");
                output.WriteLine($"ExceptionType='{exception.GetType().FullName}'");
                output.WriteLine($"Message='{Escape(exception.Message)}'");
                output.WriteLine("[DATA STORE TRAFFIC OBSERVER SUMMARY FAILURE END]");
            }
            finally
            {
                AiMongoAttributionDiagnostics.EndScope(mongoAttributionScope);
                AiRedisReadAttributionDiagnostics.EndScope(redisAttributionScope);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref completionState) == 0)
            {
                await CompleteAsync().ConfigureAwait(false);
            }

            try
            {
                redisConnection?.Dispose();
            }
            catch (Exception exception)
            {
                output.WriteLine(
                    $"[DATA STORE TRAFFIC OBSERVER DISPOSE WARNING] " +
                    $"ExceptionType='{exception.GetType().FullName}', " +
                    $"Message='{Escape(exception.Message)}'.");
            }
        }

        private void WriteSummary(
            TimeSpan duration,
            RedisTrafficSnapshot? redisStart,
            RedisTrafficSnapshot? redisEnd,
            string? redisError,
            MongoTrafficSnapshot? mongoStart,
            MongoTrafficSnapshot? mongoEnd,
            string? mongoError)
        {
            output.WriteLine(string.Empty);
            output.WriteLine("[DATA STORE TRAFFIC SUMMARY]");
            output.WriteLine($"CompletedAtUtc='{DateTimeOffset.UtcNow:O}'");
            output.WriteLine($"Duration='{duration}'");
            output.WriteLine("Scope='Server-wide deltas; concurrent external users of the same stores are included.'");
            output.WriteLine("ObserverOverhead='Approximately two Redis INFO snapshots and two MongoDB serverStatus snapshots bracket the run. PERF1/PERF2 Redis HSET publications and final HGETALL collection are included in raw Redis deltas; PERF2 attribution itself performs no MongoDB writes.'");

            WriteRedisSummary(duration, redisStart, redisEnd, redisError);
            WriteMongoSummary(duration, mongoStart, mongoEnd, mongoError);

            output.WriteLine("[DATA STORE TRAFFIC SUMMARY END]");
        }

        private void WriteRedisSummary(
            TimeSpan duration,
            RedisTrafficSnapshot? start,
            RedisTrafficSnapshot? end,
            string? error)
        {
            if (start is null || end is null)
            {
                output.WriteLine("Redis.Available='false'");
                output.WriteLine($"Redis.Error='{Escape(error)}'");
                return;
            }

            var totalCommands = Delta(start.TotalCommandsProcessed, end.TotalCommandsProcessed);
            var totalConnections = Delta(start.TotalConnectionsReceived, end.TotalConnectionsReceived);
            var inputBytes = Delta(start.TotalNetInputBytes, end.TotalNetInputBytes);
            var outputBytes = Delta(start.TotalNetOutputBytes, end.TotalNetOutputBytes);
            var errorReplies = Delta(start.TotalErrorReplies, end.TotalErrorReplies);
            var keyspaceHits = Delta(start.KeyspaceHits, end.KeyspaceHits);
            var keyspaceMisses = Delta(start.KeyspaceMisses, end.KeyspaceMisses);
            var expiredKeys = Delta(start.ExpiredKeys, end.ExpiredKeys);
            var evictedKeys = Delta(start.EvictedKeys, end.EvictedKeys);
            var commandsPerSecond = ResolveRate(totalCommands, duration);
            var counterResetDetected = HasCounterReset(start, end);

            output.WriteLine("Redis.Available='true'");
            output.WriteLine($"Redis.StartCapturedAtUtc='{start.CapturedAtUtc:O}'");
            output.WriteLine($"Redis.EndCapturedAtUtc='{end.CapturedAtUtc:O}'");
            output.WriteLine($"Redis.ServerCount='{end.ServerCount}'");
            output.WriteLine($"Redis.CounterResetDetected='{counterResetDetected.ToString().ToLowerInvariant()}'");
            output.WriteLine($"Redis.TotalCommands='{totalCommands}'");
            output.WriteLine($"Redis.CommandsPerSecond='{commandsPerSecond.ToString("F2", CultureInfo.InvariantCulture)}'");
            output.WriteLine($"Redis.NewConnections='{totalConnections}'");
            output.WriteLine($"Redis.NetworkInputBytes='{inputBytes}'");
            output.WriteLine($"Redis.NetworkOutputBytes='{outputBytes}'");
            output.WriteLine($"Redis.ErrorReplies='{errorReplies}'");
            output.WriteLine($"Redis.KeyspaceHits='{keyspaceHits}'");
            output.WriteLine($"Redis.KeyspaceMisses='{keyspaceMisses}'");
            output.WriteLine($"Redis.ExpiredKeys='{expiredKeys}'");
            output.WriteLine($"Redis.EvictedKeys='{evictedKeys}'");

            var commandDeltas = CreateDictionaryDelta(
                start.CommandCalls,
                end.CommandCalls);

            var nonZeroCommandDeltas = commandDeltas
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();

            output.WriteLine($"Redis.DistinctCommands='{nonZeroCommandDeltas.Length}'");
            output.WriteLine($"Redis.CommandCallsTotal='{nonZeroCommandDeltas.Sum(pair => pair.Value)}'");

            foreach (var command in nonZeroCommandDeltas)
            {
                output.WriteLine($"Redis.Command.{NormalizeMetricName(command.Key)}='{command.Value}'");
            }
        }

        private void WriteRedisAttributionSummary(
            RedisTrafficSnapshot? start,
            RedisTrafficSnapshot? end,
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionAggregate? aggregate,
            string? error)
        {
            output.WriteLine(string.Empty);
            output.WriteLine("[PERF1 REDIS ATTRIBUTION]");
            output.WriteLine($"Enabled='{(!string.IsNullOrWhiteSpace(redisAttributionScope)).ToString().ToLowerInvariant()}'");
            output.WriteLine($"Scope='{Escape(redisAttributionScope)}'");
            output.WriteLine("PayloadDefinition='UTF-8 bytes represented by RedisValue at the instrumented application call site; RESP framing and transport overhead are excluded.'");
            output.WriteLine($"FlushInterval='{Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.FlushInterval}'");
            output.WriteLine("InstrumentationOverhead='Each active process publishes one absolute HSET snapshot approximately once per flush interval; the first successful publication also applies a TTL. Final parent flush and HGETALL collection occur before the raw Redis end snapshot so attributed read counts cannot extend beyond the server measurement window.'");
            output.WriteLine("CoverageNote='Cross-process publication is periodic and best-effort. A hard-killed process may lose its final partial flush interval; reads after a process latest published snapshot remain residual. Collection precedes the Redis end snapshot, so residuals are conservative rather than allowing attribution beyond the server window.'");
            output.WriteLine("LuaAttributionNote='Command=LUA rows count successful atomic Lua script invocations by bounded semantic family. PERF1-1B does not decompose Lua scripts or subtract their Redis-side GET/HGET calls from server residuals.'");

            if (string.IsNullOrWhiteSpace(redisAttributionScope))
            {
                output.WriteLine("Available='false'");
                output.WriteLine("Reason='MULTIPLEXED_PERF1_REDIS_ATTRIBUTION is not enabled.'");
                output.WriteLine("[PERF1 REDIS ATTRIBUTION END]");
                return;
            }

            if (start is null || end is null || aggregate is null)
            {
                output.WriteLine("Available='false'");
                output.WriteLine($"Error='{Escape(error)}'");
                output.WriteLine("[PERF1 REDIS ATTRIBUTION END]");
                return;
            }

            output.WriteLine("Available='true'");
            output.WriteLine($"ProcessSnapshotCount='{aggregate.ProcessSnapshotCount}'");
            output.WriteLine($"PublicationSequenceTotal='{aggregate.PublicationSequenceTotal}'");

            var serverCommandDeltas = CreateDictionaryDelta(
                start.CommandCalls,
                end.CommandCalls);

            var attributedByCommand = aggregate.Operations
                .GroupBy(item => item.Command, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(item => item.Calls),
                    StringComparer.OrdinalIgnoreCase);

            var trackedCommands = new[]
            {
                "GET",
                "MGET",
                "HGET",
                "HMGET",
                "HGETALL",
                "SMEMBERS"
            };

            foreach (var command in trackedCommands)
            {
                var serverCalls = serverCommandDeltas.GetValueOrDefault(command);
                var attributedCalls = attributedByCommand.GetValueOrDefault(command);
                var residualCalls = serverCalls - attributedCalls;
                var coveragePercent = serverCalls <= 0
                    ? 0d
                    : (attributedCalls * 100d) / serverCalls;

                output.WriteLine($"Command.{command}.ServerCalls='{serverCalls}'");
                output.WriteLine($"Command.{command}.AttributedCalls='{attributedCalls}'");
                output.WriteLine($"Command.{command}.ResidualCalls='{residualCalls}'");
                output.WriteLine($"Command.{command}.CoveragePercent='{coveragePercent.ToString("F2", CultureInfo.InvariantCulture)}'");
            }

            foreach (var operation in aggregate.Operations)
            {
                output.WriteLine(
                    $"Operation='{Escape(operation.Operation)}', " +
                    $"Command='{Escape(operation.Command)}', " +
                    $"Calls='{operation.Calls}', " +
                    $"ResponsePayloadBytes='{operation.ResponsePayloadBytes}'.");
            }

            WriteSharedRunProcessAttribution(aggregate);
            output.WriteLine("[PERF1 REDIS ATTRIBUTION END]");
        }

        private void WriteSharedRunProcessAttribution(
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionAggregate aggregate)
        {
            var sharedRunOperations = new HashSet<string>(StringComparer.Ordinal)
            {
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.SharedRunRecordLoad,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.SharedRunPublicGetRecordLoad,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.TestHarnessSharedRunPublicGetRecordLoad,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.SharedRunListRecordLoad,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.TestHarnessRuntimePoolWorkloadSharedRunLoad
            };

            var observerParentProcessIdentity =
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.CurrentProcessIdentity;

            var rows = aggregate.ProcessSnapshots
                .SelectMany(
                    snapshot => snapshot.Operations
                        .Where(operation => sharedRunOperations.Contains(operation.Operation))
                        .Select(operation => new
                        {
                            snapshot.ProcessIdentity,
                            snapshot.PublicationSequence,
                            snapshot.CapturedAtUtc,
                            Operation = operation
                        }))
                .OrderByDescending(row => row.Operation.Calls)
                .ThenBy(row => row.ProcessIdentity, StringComparer.Ordinal)
                .ThenBy(row => row.Operation.Operation, StringComparer.Ordinal)
                .ToArray();

            var observerParentCalls = rows
                .Where(
                    row =>
                        string.Equals(
                            row.Operation.Operation,
                            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.SharedRunRecordLoad,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            row.ProcessIdentity,
                            observerParentProcessIdentity,
                            StringComparison.Ordinal))
                .Sum(row => row.Operation.Calls);

            var remoteOrChildCalls = rows
                .Where(
                    row =>
                        string.Equals(
                            row.Operation.Operation,
                            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.SharedRunRecordLoad,
                            StringComparison.Ordinal) &&
                        !string.Equals(
                            row.ProcessIdentity,
                            observerParentProcessIdentity,
                            StringComparison.Ordinal))
                .Sum(row => row.Operation.Calls);

            var publicGetObserverParentCalls = rows
                .Where(
                    row =>
                        string.Equals(
                            row.Operation.Operation,
                            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.SharedRunPublicGetRecordLoad,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            row.ProcessIdentity,
                            observerParentProcessIdentity,
                            StringComparison.Ordinal))
                .Sum(row => row.Operation.Calls);

            var publicGetRemoteOrChildCalls = rows
                .Where(
                    row =>
                        string.Equals(
                            row.Operation.Operation,
                            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.SharedRunPublicGetRecordLoad,
                            StringComparison.Ordinal) &&
                        !string.Equals(
                            row.ProcessIdentity,
                            observerParentProcessIdentity,
                            StringComparison.Ordinal))
                .Sum(row => row.Operation.Calls);

            var testHarnessPublicGetObserverParentCalls = rows
                .Where(
                    row =>
                        string.Equals(
                            row.Operation.Operation,
                            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.TestHarnessSharedRunPublicGetRecordLoad,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            row.ProcessIdentity,
                            observerParentProcessIdentity,
                            StringComparison.Ordinal))
                .Sum(row => row.Operation.Calls);

            var testHarnessPublicGetRemoteOrChildCalls = rows
                .Where(
                    row =>
                        string.Equals(
                            row.Operation.Operation,
                            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.TestHarnessSharedRunPublicGetRecordLoad,
                            StringComparison.Ordinal) &&
                        !string.Equals(
                            row.ProcessIdentity,
                            observerParentProcessIdentity,
                            StringComparison.Ordinal))
                .Sum(row => row.Operation.Calls);

            output.WriteLine(string.Empty);
            output.WriteLine("[PERF1 SHARED RUN PROCESS SPLIT]");
            output.WriteLine($"ObserverParentProcessIdentity='{Escape(observerParentProcessIdentity)}'");
            output.WriteLine($"ProcessRowCount='{rows.Length}'");
            output.WriteLine($"SharedRun.Record.Load.ObserverParentCalls='{observerParentCalls}'");
            output.WriteLine($"SharedRun.Record.Load.RemoteOrChildCalls='{remoteOrChildCalls}'");
            output.WriteLine($"SharedRun.PublicGet.Record.Load.ObserverParentCalls='{publicGetObserverParentCalls}'");
            output.WriteLine($"SharedRun.PublicGet.Record.Load.RemoteOrChildCalls='{publicGetRemoteOrChildCalls}'");
            output.WriteLine($"TestHarness.SharedRun.PublicGet.Record.Load.ObserverParentCalls='{testHarnessPublicGetObserverParentCalls}'");
            output.WriteLine($"TestHarness.SharedRun.PublicGet.Record.Load.RemoteOrChildCalls='{testHarnessPublicGetRemoteOrChildCalls}'");

            foreach (var row in rows)
            {
                var role = string.Equals(
                    row.ProcessIdentity,
                    observerParentProcessIdentity,
                    StringComparison.Ordinal)
                    ? "ObserverParent"
                    : "RemoteOrChild";

                output.WriteLine(
                    $"Process='{Escape(row.ProcessIdentity)}', " +
                    $"Role='{role}', " +
                    $"PublicationSequence='{row.PublicationSequence}', " +
                    $"CapturedAtUtc='{row.CapturedAtUtc:O}', " +
                    $"Operation='{Escape(row.Operation.Operation)}', " +
                    $"Command='{Escape(row.Operation.Command)}', " +
                    $"Calls='{row.Operation.Calls}', " +
                    $"ResponsePayloadBytes='{row.Operation.ResponsePayloadBytes}'.");
            }

            output.WriteLine("[PERF1 SHARED RUN PROCESS SPLIT END]");
        }

        private void WriteMongoAttributionSummary(
            MongoTrafficSnapshot? start,
            MongoTrafficSnapshot? end,
            AiMongoAttributionAggregate? aggregate,
            string? error)
        {
            output.WriteLine(string.Empty);
            output.WriteLine("[PERF2 MONGO ATTRIBUTION]");
            output.WriteLine($"Enabled='{(!string.IsNullOrWhiteSpace(mongoAttributionScope)).ToString().ToLowerInvariant()}'");
            output.WriteLine($"Scope='{Escape(mongoAttributionScope)}'");
            output.WriteLine($"FlushInterval='{AiMongoAttributionDiagnostics.FlushInterval}'");
            output.WriteLine("SemanticDefinition='Bounded application-level MongoDB store families measured immediately around driver calls. Tenant, execution, runtime, payload, collection, key, and query values are never labels.'");
            output.WriteLine("DriverDefinition='Bounded MongoDB driver command and pool events grouped only by command name and client role. Unknown commands are normalized to OTHER.'");
            output.WriteLine("CoverageNote='Semantic command coverage compares attributed application calls with serverStatus metrics.commands deltas. Driver retries, observer serverStatus, index initialization, uninstrumented framework calls, and the final partial interval of a hard-killed process can remain residual.'");
            output.WriteLine("CrossProcessPublication='Absolute process snapshots are published through the existing Redis diagnostic path; PERF2 writes no measurement record to MongoDB.'");

            if (string.IsNullOrWhiteSpace(mongoAttributionScope))
            {
                output.WriteLine("Available='false'");
                output.WriteLine("Reason='MULTIPLEXED_PERF2_MONGO_ATTRIBUTION is not enabled.'");
                output.WriteLine("[PERF2 MONGO ATTRIBUTION END]");
                return;
            }

            if (start is null || end is null || aggregate is null)
            {
                output.WriteLine("Available='false'");
                output.WriteLine($"Error='{Escape(error)}'");
                output.WriteLine("[PERF2 MONGO ATTRIBUTION END]");
                return;
            }

            output.WriteLine("Available='true'");
            output.WriteLine($"ProcessSnapshotCount='{aggregate.ProcessSnapshotCount}'");
            output.WriteLine($"PublicationSequenceTotal='{aggregate.PublicationSequenceTotal}'");

            var serverCommandDeltas = CreateDictionaryDelta(start.CommandCalls, end.CommandCalls);
            var attributedByCommand = aggregate.Operations
                .GroupBy(item => item.Command, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(item => item.Calls),
                    StringComparer.OrdinalIgnoreCase);

            var trackedCommands = new[]
            {
                (Semantic: AiMongoAttributionCommands.Insert, Server: "insert"),
                (Semantic: AiMongoAttributionCommands.FindAndModify, Server: "findAndModify"),
                (Semantic: AiMongoAttributionCommands.Find, Server: "find")
            };

            foreach (var command in trackedCommands)
            {
                var serverCalls = serverCommandDeltas.GetValueOrDefault(command.Server);
                var attributedCalls = attributedByCommand.GetValueOrDefault(command.Semantic);
                var residualCalls = serverCalls - attributedCalls;
                var coveragePercent = serverCalls <= 0
                    ? 0d
                    : (attributedCalls * 100d) / serverCalls;

                output.WriteLine($"Command.{command.Semantic}.ServerCalls='{serverCalls}'");
                output.WriteLine($"Command.{command.Semantic}.AttributedCalls='{attributedCalls}'");
                output.WriteLine($"Command.{command.Semantic}.ResidualCalls='{residualCalls}'");
                output.WriteLine($"Command.{command.Semantic}.CoveragePercent='{coveragePercent.ToString("F2", CultureInfo.InvariantCulture)}'");
            }

            foreach (var operation in aggregate.Operations)
            {
                var aggregateDurationMs = operation.AggregateDurationTicks / (double)TimeSpan.TicksPerMillisecond;
                output.WriteLine(
                    $"Operation='{Escape(operation.Operation)}', " +
                    $"Command='{Escape(operation.Command)}', " +
                    $"Calls='{operation.Calls}', " +
                    $"RequestedDocuments='{operation.RequestedDocuments}', " +
                    $"ReturnedDocuments='{operation.ReturnedDocuments}', " +
                    $"RequestPayloadBytes='{operation.RequestPayloadBytes}', " +
                    $"ResponsePayloadBytes='{operation.ResponsePayloadBytes}', " +
                    $"Successes='{operation.Successes}', " +
                    $"Failures='{operation.Failures}', " +
                    $"Cancellations='{operation.Cancellations}', " +
                    $"DuplicateKeyRetries='{operation.DuplicateKeyRetries}', " +
                    $"AggregateDurationMs='{aggregateDurationMs.ToString("F3", CultureInfo.InvariantCulture)}', " +
                    $"LatencyLe1Ms='{operation.LatencyLe1Ms}', " +
                    $"LatencyLe2Ms='{operation.LatencyLe2Ms}', " +
                    $"LatencyLe5Ms='{operation.LatencyLe5Ms}', " +
                    $"LatencyLe10Ms='{operation.LatencyLe10Ms}', " +
                    $"LatencyLe25Ms='{operation.LatencyLe25Ms}', " +
                    $"LatencyLe50Ms='{operation.LatencyLe50Ms}', " +
                    $"LatencyLe100Ms='{operation.LatencyLe100Ms}', " +
                    $"LatencyLe250Ms='{operation.LatencyLe250Ms}', " +
                    $"LatencyGt250Ms='{operation.LatencyGt250Ms}'.");
            }

            foreach (var command in aggregate.DriverCommands)
            {
                var aggregateDurationMs = command.AggregateDurationTicks / (double)TimeSpan.TicksPerMillisecond;
                output.WriteLine(
                    $"DriverCommand.Role='{Escape(command.ClientRole)}', " +
                    $"Command='{Escape(command.Command)}', " +
                    $"Started='{command.Started}', " +
                    $"Succeeded='{command.Succeeded}', " +
                    $"Failed='{command.Failed}', " +
                    $"AggregateDurationMs='{aggregateDurationMs.ToString("F3", CultureInfo.InvariantCulture)}'.");
            }

            foreach (var pool in aggregate.DriverPools)
            {
                output.WriteLine(
                    $"DriverPool.Role='{Escape(pool.ClientRole)}', " +
                    $"ClientInstancesObserved='{pool.ClientInstancesObserved}', " +
                    $"PoolsOpened='{pool.PoolsOpened}', " +
                    $"PoolsClosed='{pool.PoolsClosed}', " +
                    $"ConnectionsOpened='{pool.ConnectionsOpened}', " +
                    $"ConnectionsClosed='{pool.ConnectionsClosed}', " +
                    $"ConnectionOpenFailures='{pool.ConnectionOpenFailures}', " +
                    $"Checkouts='{pool.Checkouts}', " +
                    $"CheckoutFailures='{pool.CheckoutFailures}'.");
            }

            output.WriteLine("[PERF2 MONGO ATTRIBUTION END]");
        }

        private void WriteMongoSummary(
            TimeSpan duration,
            MongoTrafficSnapshot? start,
            MongoTrafficSnapshot? end,
            string? error)
        {
            if (start is null || end is null)
            {
                output.WriteLine("Mongo.Available='false'");
                output.WriteLine($"Mongo.Error='{Escape(error)}'");
                return;
            }

            var inserts = Delta(start.OpInsert, end.OpInsert);
            var queries = Delta(start.OpQuery, end.OpQuery);
            var updates = Delta(start.OpUpdate, end.OpUpdate);
            var deletes = Delta(start.OpDelete, end.OpDelete);
            var getMores = Delta(start.OpGetMore, end.OpGetMore);
            var commands = Delta(start.OpCommand, end.OpCommand);
            var totalOperations = inserts + queries + updates + deletes + getMores + commands;
            var operationsPerSecond = ResolveRate(totalOperations, duration);
            var counterResetDetected = HasCounterReset(start, end);

            output.WriteLine("Mongo.Available='true'");
            output.WriteLine($"Mongo.StartCapturedAtUtc='{start.CapturedAtUtc:O}'");
            output.WriteLine($"Mongo.EndCapturedAtUtc='{end.CapturedAtUtc:O}'");
            output.WriteLine($"Mongo.CounterResetDetected='{counterResetDetected.ToString().ToLowerInvariant()}'");
            output.WriteLine($"Mongo.TotalOperations='{totalOperations}'");
            output.WriteLine("Mongo.TotalOperationsDefinition='Sum of serverStatus opcounters insert, query, update, delete, getmore, and command deltas.'");
            output.WriteLine($"Mongo.OperationsPerSecond='{operationsPerSecond.ToString("F2", CultureInfo.InvariantCulture)}'");
            output.WriteLine($"Mongo.OpCounters.Insert='{inserts}'");
            output.WriteLine($"Mongo.OpCounters.Query='{queries}'");
            output.WriteLine($"Mongo.OpCounters.Update='{updates}'");
            output.WriteLine($"Mongo.OpCounters.Delete='{deletes}'");
            output.WriteLine($"Mongo.OpCounters.GetMore='{getMores}'");
            output.WriteLine($"Mongo.OpCounters.Command='{commands}'");
            output.WriteLine($"Mongo.NetworkRequests='{Delta(start.NetworkRequests, end.NetworkRequests)}'");
            output.WriteLine($"Mongo.NetworkInputBytes='{Delta(start.NetworkBytesIn, end.NetworkBytesIn)}'");
            output.WriteLine($"Mongo.NetworkOutputBytes='{Delta(start.NetworkBytesOut, end.NetworkBytesOut)}'");
            output.WriteLine($"Mongo.NewConnections='{Delta(start.ConnectionsCreated, end.ConnectionsCreated)}'");
            output.WriteLine($"Mongo.RejectedConnections='{Delta(start.ConnectionsRejected, end.ConnectionsRejected)}'");
            output.WriteLine($"Mongo.ConnectionsCurrent.Start='{start.ConnectionsCurrent}'");
            output.WriteLine($"Mongo.ConnectionsCurrent.End='{end.ConnectionsCurrent}'");
            output.WriteLine($"Mongo.ConnectionsAvailable.Start='{start.ConnectionsAvailable}'");
            output.WriteLine($"Mongo.ConnectionsAvailable.End='{end.ConnectionsAvailable}'");
            output.WriteLine($"Mongo.ConnectionsActive.Start='{start.ConnectionsActive}'");
            output.WriteLine($"Mongo.ConnectionsActive.End='{end.ConnectionsActive}'");
            output.WriteLine($"Mongo.QueryExecutor.ScannedKeys='{Delta(start.QueryExecutorScanned, end.QueryExecutorScanned)}'");
            output.WriteLine($"Mongo.QueryExecutor.ScannedDocuments='{Delta(start.QueryExecutorScannedObjects, end.QueryExecutorScannedObjects)}'");
            output.WriteLine($"Mongo.CursorsOpen.Start='{start.CursorsOpen}'");
            output.WriteLine($"Mongo.CursorsOpen.End='{end.CursorsOpen}'");
            output.WriteLine($"Mongo.CursorsTimedOut='{Delta(start.CursorsTimedOut, end.CursorsTimedOut)}'");
            output.WriteLine($"Mongo.WiredTiger.CacheBytesReadIntoCache='{Delta(start.WiredTigerCacheBytesReadIntoCache, end.WiredTigerCacheBytesReadIntoCache)}'");
            output.WriteLine($"Mongo.WiredTiger.CacheBytesWrittenFromCache='{Delta(start.WiredTigerCacheBytesWrittenFromCache, end.WiredTigerCacheBytesWrittenFromCache)}'");
            output.WriteLine($"Mongo.WriteConflicts='{Delta(start.WriteConflicts, end.WriteConflicts)}'");
            output.WriteLine($"Mongo.DocumentsInserted='{Delta(start.DocumentsInserted, end.DocumentsInserted)}'");
            output.WriteLine($"Mongo.DocumentsReturned='{Delta(start.DocumentsReturned, end.DocumentsReturned)}'");
            output.WriteLine($"Mongo.DocumentsUpdated='{Delta(start.DocumentsUpdated, end.DocumentsUpdated)}'");
            output.WriteLine($"Mongo.DocumentsDeleted='{Delta(start.DocumentsDeleted, end.DocumentsDeleted)}'");
            output.WriteLine($"Mongo.ReadOperations='{Delta(start.ReadOperations, end.ReadOperations)}'");
            output.WriteLine($"Mongo.ReadLatencyMicros='{Delta(start.ReadLatencyMicros, end.ReadLatencyMicros)}'");
            output.WriteLine($"Mongo.WriteOperations='{Delta(start.WriteOperations, end.WriteOperations)}'");
            output.WriteLine($"Mongo.WriteLatencyMicros='{Delta(start.WriteLatencyMicros, end.WriteLatencyMicros)}'");
            output.WriteLine($"Mongo.CommandOperations='{Delta(start.CommandOperations, end.CommandOperations)}'");
            output.WriteLine($"Mongo.CommandLatencyMicros='{Delta(start.CommandLatencyMicros, end.CommandLatencyMicros)}'");
            output.WriteLine($"Mongo.TransactionOperations='{Delta(start.TransactionOperations, end.TransactionOperations)}'");
            output.WriteLine($"Mongo.TransactionLatencyMicros='{Delta(start.TransactionLatencyMicros, end.TransactionLatencyMicros)}'");

            var commandDeltas = CreateDictionaryDelta(
                start.CommandCalls,
                end.CommandCalls);

            var nonZeroCommandDeltas = commandDeltas
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();

            output.WriteLine($"Mongo.DistinctCommands='{nonZeroCommandDeltas.Length}'");
            output.WriteLine($"Mongo.CommandMetricsTotal='{nonZeroCommandDeltas.Sum(pair => pair.Value)}'");

            foreach (var command in nonZeroCommandDeltas)
            {
                output.WriteLine($"Mongo.Command.{NormalizeMetricName(command.Key)}='{command.Value}'");
            }
        }

        private static async Task<RedisTrafficSnapshot> CaptureRedisSnapshotAsync(
            ConnectionMultiplexer connection,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connection);
            cancellationToken.ThrowIfCancellationRequested();

            var totalCommandsProcessed = 0L;
            var totalConnectionsReceived = 0L;
            var totalNetInputBytes = 0L;
            var totalNetOutputBytes = 0L;
            var totalErrorReplies = 0L;
            var keyspaceHits = 0L;
            var keyspaceMisses = 0L;
            var expiredKeys = 0L;
            var evictedKeys = 0L;
            var commandCalls = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var serverCount = 0;

            foreach (var endpoint in connection.GetEndPoints())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var server = connection.GetServer(endpoint);

                if (!server.IsConnected)
                {
                    continue;
                }

                var stats = await server
                    .InfoAsync("stats")
                    .ConfigureAwait(false);

                var commandStats = await server
                    .InfoAsync("commandstats")
                    .ConfigureAwait(false);

                var statsValues = FlattenRedisInfo(stats);
                var commandStatsValues = FlattenRedisInfo(commandStats);

                totalCommandsProcessed += ReadLong(statsValues, "total_commands_processed");
                totalConnectionsReceived += ReadLong(statsValues, "total_connections_received");
                totalNetInputBytes += ReadLong(statsValues, "total_net_input_bytes");
                totalNetOutputBytes += ReadLong(statsValues, "total_net_output_bytes");
                totalErrorReplies += ReadLong(statsValues, "total_error_replies");
                keyspaceHits += ReadLong(statsValues, "keyspace_hits");
                keyspaceMisses += ReadLong(statsValues, "keyspace_misses");
                expiredKeys += ReadLong(statsValues, "expired_keys");
                evictedKeys += ReadLong(statsValues, "evicted_keys");

                foreach (var pair in commandStatsValues)
                {
                    if (!pair.Key.StartsWith("cmdstat_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var commandName = pair.Key["cmdstat_".Length..];
                    var calls = ParseRedisCommandCalls(pair.Value);

                    commandCalls[commandName] =
                        commandCalls.GetValueOrDefault(commandName) + calls;
                }

                serverCount++;
            }

            if (serverCount == 0)
            {
                throw new InvalidOperationException(
                    "No connected Redis server endpoint was available for the traffic snapshot.");
            }

            return new RedisTrafficSnapshot(
                DateTimeOffset.UtcNow,
                serverCount,
                totalCommandsProcessed,
                totalConnectionsReceived,
                totalNetInputBytes,
                totalNetOutputBytes,
                totalErrorReplies,
                keyspaceHits,
                keyspaceMisses,
                expiredKeys,
                evictedKeys,
                new ReadOnlyDictionary<string, long>(commandCalls));
        }

        private static async Task<MongoTrafficSnapshot> CaptureMongoSnapshotAsync(
            MongoClient client,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(client);
            cancellationToken.ThrowIfCancellationRequested();

            var adminDatabase = client.GetDatabase("admin");
            var status = await adminDatabase
                .RunCommandAsync<BsonDocument>(
                    new BsonDocument("serverStatus", 1),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new MongoTrafficSnapshot(
                DateTimeOffset.UtcNow,
                ReadLong(status, "opcounters", "insert"),
                ReadLong(status, "opcounters", "query"),
                ReadLong(status, "opcounters", "update"),
                ReadLong(status, "opcounters", "delete"),
                ReadLong(status, "opcounters", "getmore"),
                ReadLong(status, "opcounters", "command"),
                ReadLong(status, "network", "bytesIn"),
                ReadLong(status, "network", "bytesOut"),
                ReadLong(status, "network", "numRequests"),
                ReadLong(status, "connections", "totalCreated"),
                ReadLong(status, "connections", "rejected"),
                ReadLong(status, "metrics", "document", "inserted"),
                ReadLong(status, "metrics", "document", "returned"),
                ReadLong(status, "metrics", "document", "updated"),
                ReadLong(status, "metrics", "document", "deleted"),
                ReadLong(status, "opLatencies", "reads", "ops"),
                ReadLong(status, "opLatencies", "reads", "latency"),
                ReadLong(status, "opLatencies", "writes", "ops"),
                ReadLong(status, "opLatencies", "writes", "latency"),
                ReadLong(status, "opLatencies", "commands", "ops"),
                ReadLong(status, "opLatencies", "commands", "latency"),
                ReadLong(status, "opLatencies", "transactions", "ops"),
                ReadLong(status, "opLatencies", "transactions", "latency"),
                ReadLong(status, "connections", "current"),
                ReadLong(status, "connections", "available"),
                ReadLong(status, "connections", "active"),
                ReadLong(status, "metrics", "queryExecutor", "scanned"),
                ReadLong(status, "metrics", "queryExecutor", "scannedObjects"),
                ReadLong(status, "metrics", "cursor", "open", "total"),
                ReadLong(status, "metrics", "cursor", "timedOut"),
                ReadLong(status, "wiredTiger", "cache", "bytes read into cache"),
                ReadLong(status, "wiredTiger", "cache", "bytes written from cache"),
                ReadLong(status, "metrics", "operation", "writeConflicts"),
                new ReadOnlyDictionary<string, long>(ReadMongoCommandCalls(status)));
        }

        private static Dictionary<string, string> FlattenRedisInfo(
            IGrouping<string, KeyValuePair<string, string>>[] sections)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in sections)
            {
                foreach (var pair in section)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            return values;
        }

        private static Dictionary<string, long> ReadMongoCommandCalls(BsonDocument status)
        {
            var calls = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            if (!TryNavigate(status, out var commandsValue, "metrics", "commands") ||
                !commandsValue.IsBsonDocument)
            {
                return calls;
            }

            foreach (var command in commandsValue.AsBsonDocument.Elements)
            {
                if (!command.Value.IsBsonDocument)
                {
                    continue;
                }

                var total = ReadLong(command.Value.AsBsonDocument, "total");

                if (total > 0)
                {
                    calls[command.Name] = total;
                }
            }

            return calls;
        }

        private static IReadOnlyDictionary<string, long> CreateDictionaryDelta(
            IReadOnlyDictionary<string, long> start,
            IReadOnlyDictionary<string, long> end)
        {
            var keys = new HashSet<string>(start.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(end.Keys);

            var delta = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                delta[key] = Delta(
                    start.GetValueOrDefault(key),
                    end.GetValueOrDefault(key));
            }

            return delta;
        }

        private static long ParseRedisCommandCalls(string value)
        {
            foreach (var component in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = component.Trim();

                if (!trimmed.StartsWith("calls=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return long.TryParse(
                    trimmed["calls=".Length..],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var calls)
                    ? calls
                    : 0L;
            }

            return 0L;
        }

        private static long ReadLong(
            IReadOnlyDictionary<string, string> values,
            string key)
        {
            return values.TryGetValue(key, out var value) &&
                   long.TryParse(
                       value,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out var parsed)
                ? parsed
                : 0L;
        }

        private static long ReadLong(
            BsonDocument document,
            params string[] path)
        {
            if (!TryNavigate(document, out var value, path))
            {
                return 0L;
            }

            return value.BsonType switch
            {
                BsonType.Int32 => value.AsInt32,
                BsonType.Int64 => value.AsInt64,
                BsonType.Double => Convert.ToInt64(value.AsDouble),
                _ => 0L
            };
        }

        private static bool TryNavigate(
            BsonDocument document,
            out BsonValue value,
            params string[] path)
        {
            BsonValue current = document;

            foreach (var segment in path)
            {
                if (!current.IsBsonDocument ||
                    !current.AsBsonDocument.TryGetValue(segment, out var next))
                {
                    value = BsonNull.Value;
                    return false;
                }

                current = next;
            }

            value = current;
            return true;
        }

        private static bool HasCounterReset(
            RedisTrafficSnapshot start,
            RedisTrafficSnapshot end)
        {
            return end.TotalCommandsProcessed < start.TotalCommandsProcessed ||
                   end.TotalConnectionsReceived < start.TotalConnectionsReceived ||
                   end.TotalNetInputBytes < start.TotalNetInputBytes ||
                   end.TotalNetOutputBytes < start.TotalNetOutputBytes;
        }

        private static bool HasCounterReset(
            MongoTrafficSnapshot start,
            MongoTrafficSnapshot end)
        {
            return end.OpInsert < start.OpInsert ||
                   end.OpQuery < start.OpQuery ||
                   end.OpUpdate < start.OpUpdate ||
                   end.OpDelete < start.OpDelete ||
                   end.OpGetMore < start.OpGetMore ||
                   end.OpCommand < start.OpCommand ||
                   end.NetworkRequests < start.NetworkRequests;
        }

        private static long Delta(long start, long end)
        {
            return end >= start
                ? end - start
                : end;
        }

        private static double ResolveRate(long count, TimeSpan duration)
        {
            return duration.TotalSeconds > 0
                ? count / duration.TotalSeconds
                : 0D;
        }

        private static string ResolveConnectionString(
            string preferredEnvironmentVariable,
            string standardEnvironmentVariable,
            string fallback)
        {
            var preferred = Environment.GetEnvironmentVariable(preferredEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            var standard = Environment.GetEnvironmentVariable(standardEnvironmentVariable);

            return !string.IsNullOrWhiteSpace(standard)
                ? standard
                : fallback;
        }

        private static string NormalizeMetricName(string name)
        {
            return string.Concat(
                name.Select(character =>
                    char.IsLetterOrDigit(character)
                        ? char.ToUpperInvariant(character)
                        : '_'));
        }

        private static string FormatException(Exception exception)
        {
            return $"{exception.GetType().FullName}: {exception.Message}";
        }

        private static string Escape(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value
                    .Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal)
                    .Replace("'", "''", StringComparison.Ordinal);
        }

        private sealed record RedisTrafficSnapshot(
            DateTimeOffset CapturedAtUtc,
            int ServerCount,
            long TotalCommandsProcessed,
            long TotalConnectionsReceived,
            long TotalNetInputBytes,
            long TotalNetOutputBytes,
            long TotalErrorReplies,
            long KeyspaceHits,
            long KeyspaceMisses,
            long ExpiredKeys,
            long EvictedKeys,
            IReadOnlyDictionary<string, long> CommandCalls);

        private sealed record MongoTrafficSnapshot(
            DateTimeOffset CapturedAtUtc,
            long OpInsert,
            long OpQuery,
            long OpUpdate,
            long OpDelete,
            long OpGetMore,
            long OpCommand,
            long NetworkBytesIn,
            long NetworkBytesOut,
            long NetworkRequests,
            long ConnectionsCreated,
            long ConnectionsRejected,
            long DocumentsInserted,
            long DocumentsReturned,
            long DocumentsUpdated,
            long DocumentsDeleted,
            long ReadOperations,
            long ReadLatencyMicros,
            long WriteOperations,
            long WriteLatencyMicros,
            long CommandOperations,
            long CommandLatencyMicros,
            long TransactionOperations,
            long TransactionLatencyMicros,
            long ConnectionsCurrent,
            long ConnectionsAvailable,
            long ConnectionsActive,
            long QueryExecutorScanned,
            long QueryExecutorScannedObjects,
            long CursorsOpen,
            long CursorsTimedOut,
            long WiredTigerCacheBytesReadIntoCache,
            long WiredTigerCacheBytesWrittenFromCache,
            long WriteConflicts,
            IReadOnlyDictionary<string, long> CommandCalls);
    }
}
