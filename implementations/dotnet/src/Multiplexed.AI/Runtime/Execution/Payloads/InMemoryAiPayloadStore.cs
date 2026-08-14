using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;

namespace Multiplexed.AI.Runtime.Execution.Payloads
{
    /// <summary>
    /// In-memory payload store used for development and testing.
    ///
    /// PURPOSE:
    /// - Provides a safe, deterministic storage backend for payloads
    /// - Avoids external dependencies (Redis, S3, etc.)
    ///
    /// IMPORTANT:
    /// - Data is lost when process restarts
    /// - Not suitable for production usage
    /// </summary>
    public sealed class InMemoryAiPayloadStore : IAiImmutablePayloadStore
    {
        private readonly ConcurrentDictionary<string, string> _store = new();

        /// <summary>
        /// Stores serialized payload content in memory.
        /// </summary>
        public Task<string> SaveAsync(
            string content,
            CancellationToken cancellationToken = default)
        {
            var key = Guid.NewGuid().ToString("N");
            _store[key] = content;
            return Task.FromResult(key);
        }

        /// <inheritdoc />
        public Task<string> SaveImmutableAsync(
            string key,
            string content,
            AiPayloadMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(content);
            ArgumentNullException.ThrowIfNull(metadata);

            if (_store.TryAdd(key, content))
            {
                return Task.FromResult(key);
            }

            if (_store.TryGetValue(key, out var existing) &&
                string.Equals(existing, content, StringComparison.Ordinal))
            {
                return Task.FromResult(key);
            }

            throw new InvalidOperationException(
                $"Immutable payload key '{key}' already exists with different content.");
        }

        /// <summary>
        /// Retrieves payload content from memory.
        /// </summary>
        public Task<string?> LoadAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            _store.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        /// <summary>
        /// Deletes payload content from memory.
        /// </summary>
        public Task DeleteAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
}