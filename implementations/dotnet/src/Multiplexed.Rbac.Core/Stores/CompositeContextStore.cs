using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Stores.Cache;
using Multiplexed.Rbac.Core.Stores.Memory;

namespace Multiplexed.Rbac.Core.Stores
{
    public sealed class CompositeContextStore : IContextStore
    {
        private readonly RedisContextStore _primary;
        private readonly MemoryContextStore _fallback;

        private readonly bool _denyRotationIfPrimaryUnavailable = true;

        public CompositeContextStore(RedisContextStore primary, MemoryContextStore fallback)
        {
            _primary = primary;
            _fallback = fallback;
        }

        public Task<string> StoreAsync(Core.ExecutionContext.ExecutionContext context)
            => _primary.StoreAsync(context);

        public Task<string> SeedAsync(Core.ExecutionContext.ExecutionContext context)
            => _primary.SeedAsync(context);

        public async Task<Core.ExecutionContext.ExecutionContext?> GetAsync(string key)
        {
            try
            {
                var ctx = await _primary.GetAsync(key);

                if (ctx is not null)
                {
                    _fallback.Set(key, ctx);
                }

                return ctx;
            }
            catch
            {
                return await _fallback.GetAsync(key);
            }
        }

        public async Task<bool> TryAcquireInFlightAsync(string key, int maxInFlight)
            => await AcquireInFlightAsync(key, maxInFlight).ConfigureAwait(false)
               == InFlightAcquireResult.Acquired;

        public async Task<InFlightAcquireResult> AcquireInFlightAsync(
            string key,
            int maxInFlight)
        {
            try
            {
                return await _primary
                    .AcquireInFlightAsync(key, maxInFlight)
                    .ConfigureAwait(false);
            }
            catch
            {
                return await _fallback
                    .AcquireInFlightAsync(key, maxInFlight)
                    .ConfigureAwait(false);
            }
        }

        public async Task ReleaseInFlightAsync(string key)
        {
            try
            {
                await _primary.ReleaseInFlightAsync(key);
            }
            catch
            {
                await _fallback.ReleaseInFlightAsync(key);
            }
        }

        public async Task<(string newKey, Core.ExecutionContext.ExecutionContext context)> RotateAsync(
            string key,
            TimeSpan overlapWindow)
        {
            try
            {
                var rotated = await _primary.RotateAsync(key, overlapWindow);

                // Keep fallback cache aligned
                _fallback.Set(key, rotated.context);
                _fallback.Set(rotated.newKey, rotated.context);

                return rotated;
            }
            catch
            {
                if (_denyRotationIfPrimaryUnavailable)
                    throw;

                var ctx = await _fallback.GetAsync(key);
                if (ctx is null)
                    throw;

                return (key, ctx);
            }
        }
    }
}