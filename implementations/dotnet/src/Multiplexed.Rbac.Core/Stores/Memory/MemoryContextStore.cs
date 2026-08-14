using Microsoft.Extensions.Caching.Memory;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using System.Collections.Concurrent;

namespace Multiplexed.Rbac.Core.Stores.Memory
{
    public sealed class MemoryContextStore : IContextStore
    {
        private readonly IMemoryCache _cache;
        private readonly TimeSpan _ttl;

        // Tracks current in-flight count per context key.
        private readonly ConcurrentDictionary<string, int> _inFlight = new(StringComparer.Ordinal);

        public MemoryContextStore(IMemoryCache cache, TimeSpan ttl)
        {
            ArgumentNullException.ThrowIfNull(cache);

            if (ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be greater than zero.");
            }

            _cache = cache;
            _ttl = ttl;
        }

        public Task<string> StoreAsync(Core.ExecutionContext.ExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var key = Guid.NewGuid().ToString("N");
            var stored = CloneWithKey(context, key);

            Set(key, stored);

            return Task.FromResult(key);
        }

        public Task<string> SeedAsync(Core.ExecutionContext.ExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var key = string.IsNullOrWhiteSpace(context.ContextKey)
                ? Guid.NewGuid().ToString("N")
                : context.ContextKey;

            var stored = CloneWithKey(context, key);

            Set(key, stored);

            return Task.FromResult(key);
        }

        public Task<Core.ExecutionContext.ExecutionContext?> GetAsync(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            return Task.FromResult(
                _cache.TryGetValue(key, out Core.ExecutionContext.ExecutionContext? ctx)
                    ? ctx
                    : null);
        }

        public async Task<bool> TryAcquireInFlightAsync(string key, int maxInFlight)
            => await AcquireInFlightAsync(key, maxInFlight).ConfigureAwait(false)
               == InFlightAcquireResult.Acquired;

        public Task<InFlightAcquireResult> AcquireInFlightAsync(
            string key,
            int maxInFlight)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!_cache.TryGetValue(
                    key,
                    out Core.ExecutionContext.ExecutionContext? _))
            {
                return Task.FromResult(InFlightAcquireResult.ContextNotFound);
            }

            if (maxInFlight <= 0)
            {
                return Task.FromResult(InFlightAcquireResult.Acquired);
            }

            while (true)
            {
                var current = _inFlight.GetOrAdd(key, 0);

                if (current >= maxInFlight)
                {
                    return Task.FromResult(InFlightAcquireResult.LimitExceeded);
                }

                if (_inFlight.TryUpdate(key, current + 1, current))
                {
                    return Task.FromResult(InFlightAcquireResult.Acquired);
                }

                if (current == 0 && _inFlight.TryAdd(key, 1))
                {
                    return Task.FromResult(InFlightAcquireResult.Acquired);
                }
            }
        }

        public Task ReleaseInFlightAsync(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            while (true)
            {
                if (!_inFlight.TryGetValue(key, out var current))
                {
                    return Task.CompletedTask;
                }

                if (current <= 1)
                {
                    _inFlight.TryRemove(key, out _);
                    return Task.CompletedTask;
                }

                if (_inFlight.TryUpdate(key, current - 1, current))
                {
                    return Task.CompletedTask;
                }
            }
        }

        public async Task<(string newKey, Core.ExecutionContext.ExecutionContext context)> RotateAsync(
            string key,
            TimeSpan overlapWindow)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (overlapWindow < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(overlapWindow), "overlapWindow cannot be negative.");
            }

            var existing = await GetAsync(key);

            if (existing is null)
            {
                throw new InvalidOperationException($"Execution context '{key}' was not found.");
            }

            var newKey = Guid.NewGuid().ToString("N");
            var rotated = CloneWithKey(existing, newKey);

            // Store new context with normal TTL.
            Set(newKey, rotated);

            // Keep old key alive during overlap window if requested.
            if (overlapWindow > TimeSpan.Zero)
            {
                _cache.Set(key, existing, overlapWindow);
            }
            else
            {
                _cache.Remove(key);
            }

            return (newKey, rotated);
        }

        public void Set(string key, Core.ExecutionContext.ExecutionContext ctx)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(ctx);

            _cache.Set(key, ctx, _ttl);
        }

        private static Core.ExecutionContext.ExecutionContext CloneWithKey(
            Core.ExecutionContext.ExecutionContext source,
            string key)
        {
            return new Core.ExecutionContext.ExecutionContext
            {
                ContextKey = key,
                TenantId = source.TenantId,
                TenantGroupId = source.TenantGroupId,
                UserId = source.UserId,
                Project = source.Project,
                CurrentNamespace = source.CurrentNamespace,
                Namespaces = source.Namespaces?.ToList() ?? new List<NamespaceEntry>(),
                CreatedAtUtc = source.CreatedAtUtc == default
                    ? DateTime.UtcNow
                    : source.CreatedAtUtc
            };
        }
    }
}