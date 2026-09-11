using System.Collections.Concurrent;
using System.Threading;
using MongoDB.Driver;
using Multiplexed.AI.Runtime.Observability.Performance;

namespace Multiplexed.AI.Stores.Mongo
{
    /// <summary>
    /// Reuses one MongoDB client for exactly equivalent process-local connection configurations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MongoDB.Driver can share an underlying cluster and pool between equivalent clients, but
    /// constructing store-local MongoClient objects still duplicates client ownership and makes
    /// connection lifecycle harder to reason about. This registry makes the reuse contract
    /// explicit: one MongoClient instance per process for the same trimmed connection string.
    /// </para>
    /// <para>
    /// The key is deliberately conservative. Credentials, TLS, read preferences, write concerns,
    /// retry settings, timeouts, application names, and every other connection-string option are
    /// left untouched. Any textual configuration difference therefore remains isolated in a
    /// distinct MongoClient.
    /// </para>
    /// </remarks>
    public static class AiMongoClientFactory
    {
        private static readonly ConcurrentDictionary<string, Lazy<MongoClient>> Clients =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Returns the process-local MongoClient for the exact supplied configuration.
        /// </summary>
        /// <param name="connectionString">The MongoDB connection string.</param>
        /// <returns>A shared process-local MongoClient.</returns>
        public static MongoClient GetOrCreate(string connectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

            var normalizedConnectionString = connectionString.Trim();
            var lazyClient = Clients.GetOrAdd(
                normalizedConnectionString,
                static key => new Lazy<MongoClient>(
                    () => AiMongoAttributionDiagnostics.CreateMongoClient(
                        key,
                        AiMongoAttributionClientRoles.SharedEquivalentClient),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return lazyClient.Value;
        }
    }
}
