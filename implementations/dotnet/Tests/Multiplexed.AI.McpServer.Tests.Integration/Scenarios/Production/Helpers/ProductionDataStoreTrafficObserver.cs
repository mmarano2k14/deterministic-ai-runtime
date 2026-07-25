using MongoDB.Bson;
using MongoDB.Driver;
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
            Stopwatch stopwatch)
        {
            this.output = output;
            this.redisConnection = redisConnection;
            this.mongoClient = mongoClient;
            this.redisStartSnapshot = redisStartSnapshot;
            this.mongoStartSnapshot = mongoStartSnapshot;
            this.redisStartError = redisStartError;
            this.mongoStartError = mongoStartError;
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
            output.WriteLine("Scope='Server-wide counters for every process sharing the observed Redis and MongoDB instances.'");
            output.WriteLine("ObserverOverhead='The final delta includes approximately two Redis INFO commands and one MongoDB serverStatus command.'");
            output.WriteLine("[DATA STORE TRAFFIC OBSERVER START END]");

            return new ProductionDataStoreTrafficObserver(
                output,
                redisConnection,
                mongoClient,
                redisStartSnapshot,
                mongoStartSnapshot,
                redisStartError,
                mongoStartError,
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
            }
            catch (Exception exception)
            {
                output.WriteLine(string.Empty);
                output.WriteLine("[DATA STORE TRAFFIC OBSERVER SUMMARY FAILURE]");
                output.WriteLine($"ExceptionType='{exception.GetType().FullName}'");
                output.WriteLine($"Message='{Escape(exception.Message)}'");
                output.WriteLine("[DATA STORE TRAFFIC OBSERVER SUMMARY FAILURE END]");
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
            output.WriteLine("ObserverOverhead='Approximately two Redis INFO commands and one MongoDB serverStatus command are included in the raw deltas.'");

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
            IReadOnlyDictionary<string, long> CommandCalls);
    }
}
