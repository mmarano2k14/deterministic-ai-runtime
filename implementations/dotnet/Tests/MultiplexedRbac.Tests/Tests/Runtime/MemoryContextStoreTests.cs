using Microsoft.Extensions.Caching.Memory;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Stores.Memory;

namespace MultiplexedRbac.Tests.Runtime
{
    public sealed class MemoryContextStoreTests
    {
        [Fact]
        public async Task AcquireInFlightAsync_ClassifiesMissingContext()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = new MemoryContextStore(cache, TimeSpan.FromMinutes(20));

            var result = await store.AcquireInFlightAsync("missing", maxInFlight: 10);

            Assert.Equal(InFlightAcquireResult.ContextNotFound, result);
        }

        [Fact]
        public async Task AcquireInFlightAsync_ClassifiesLimitExceeded()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = new MemoryContextStore(cache, TimeSpan.FromMinutes(20));
            var key = await store.StoreAsync(CreateContext());

            Assert.Equal(
                InFlightAcquireResult.Acquired,
                await store.AcquireInFlightAsync(key, maxInFlight: 1));

            Assert.Equal(
                InFlightAcquireResult.LimitExceeded,
                await store.AcquireInFlightAsync(key, maxInFlight: 1));

            await store.ReleaseInFlightAsync(key);
        }

        [Fact]
        public async Task AcquireInFlightAsync_UnlimitedModeStillRejectsMissingContext()
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var store = new MemoryContextStore(cache, TimeSpan.FromMinutes(20));

            Assert.Equal(
                InFlightAcquireResult.ContextNotFound,
                await store.AcquireInFlightAsync("missing", maxInFlight: 0));

            var key = await store.StoreAsync(CreateContext());

            Assert.Equal(
                InFlightAcquireResult.Acquired,
                await store.AcquireInFlightAsync(key, maxInFlight: 0));
        }

        private static Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext CreateContext()
            => new()
            {
                ContextKey = string.Empty,
                Project = "test",
                UserId = "user-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "test",
                Namespaces = new List<NamespaceEntry>()
            };
    }
}
