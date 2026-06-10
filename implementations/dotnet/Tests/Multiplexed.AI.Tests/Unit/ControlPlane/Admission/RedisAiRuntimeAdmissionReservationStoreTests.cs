using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Admission
{
    /// <summary>
    /// Integration tests for Redis-backed runtime admission reservations.
    /// </summary>
    public sealed class RedisAiRuntimeAdmissionReservationStoreTests :
        IAsyncLifetime
    {
        private readonly string keyPrefix =
            $"multiplexed:ai:test:admission-reservations:{Guid.NewGuid():N}";

        private IConnectionMultiplexer? redis;
        private RedisAiRuntimeAdmissionReservationStore? store;

        public async Task InitializeAsync()
        {
            var connectionString =
                Environment.GetEnvironmentVariable("MULTIPLEXED_TEST_REDIS") ??
                "localhost:6379";

            redis =
                await ConnectionMultiplexer
                    .ConnectAsync(connectionString)
                    .ConfigureAwait(false);

            store =
                new RedisAiRuntimeAdmissionReservationStore(
                    redis,
                    Options.Create(
                        new AiRuntimeAdmissionReservationRedisOptions
                        {
                            KeyPrefix = keyPrefix,
                            ReservationTtl = TimeSpan.FromMilliseconds(500),
                            KeyTtl = TimeSpan.FromSeconds(5)
                        }));
        }

        public async Task DisposeAsync()
        {
            if (redis is not null)
            {
                await DeleteTestKeysAsync(redis)
                    .ConfigureAwait(false);

                await redis
                    .CloseAsync()
                    .ConfigureAwait(false);

                redis.Dispose();
            }
        }

        [Fact]
        public async Task ReserveAsync_ShouldIncreaseReservedRunCount()
        {
            var runtimeInstanceId =
                $"runtime-test-{Guid.NewGuid():N}";

            await store!
                .ReserveAsync(runtimeInstanceId, runCount: 1)
                .ConfigureAwait(false);

            var reservedRunCount =
                await store
                    .GetReservedRunCountAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.Equal(1, reservedRunCount);
        }

        [Fact]
        public async Task ReserveAsync_WithMultipleRuns_ShouldIncreaseReservedRunCount()
        {
            var runtimeInstanceId =
                $"runtime-test-{Guid.NewGuid():N}";

            await store!
                .ReserveAsync(runtimeInstanceId, runCount: 3)
                .ConfigureAwait(false);

            var reservedRunCount =
                await store
                    .GetReservedRunCountAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.Equal(3, reservedRunCount);
        }

        [Fact]
        public async Task ReleaseAsync_ShouldDecreaseReservedRunCount()
        {
            var runtimeInstanceId =
                $"runtime-test-{Guid.NewGuid():N}";

            await store!
                .ReserveAsync(runtimeInstanceId, runCount: 3)
                .ConfigureAwait(false);

            await store
                .ReleaseAsync(runtimeInstanceId, runCount: 1)
                .ConfigureAwait(false);

            var reservedRunCount =
                await store
                    .GetReservedRunCountAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.Equal(2, reservedRunCount);
        }

        [Fact]
        public async Task ReleaseAsync_WhenReleaseExceedsReservedCount_ShouldReturnZero()
        {
            var runtimeInstanceId =
                $"runtime-test-{Guid.NewGuid():N}";

            await store!
                .ReserveAsync(runtimeInstanceId, runCount: 1)
                .ConfigureAwait(false);

            await store
                .ReleaseAsync(runtimeInstanceId, runCount: 10)
                .ConfigureAwait(false);

            var reservedRunCount =
                await store
                    .GetReservedRunCountAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.Equal(0, reservedRunCount);
        }

        [Fact]
        public async Task GetReservedRunCountAsync_WhenReservationExpired_ShouldReturnZero()
        {
            var runtimeInstanceId =
                $"runtime-test-{Guid.NewGuid():N}";

            await store!
                .ReserveAsync(runtimeInstanceId, runCount: 2)
                .ConfigureAwait(false);

            await Task
                .Delay(TimeSpan.FromMilliseconds(800))
                .ConfigureAwait(false);

            var reservedRunCount =
                await store
                    .GetReservedRunCountAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.Equal(0, reservedRunCount);
        }

        [Fact]
        public async Task Reservations_ShouldBeIsolatedPerRuntimeInstance()
        {
            var runtimeInstanceId1 =
                $"runtime-test-{Guid.NewGuid():N}";

            var runtimeInstanceId2 =
                $"runtime-test-{Guid.NewGuid():N}";

            await store!
                .ReserveAsync(runtimeInstanceId1, runCount: 2)
                .ConfigureAwait(false);

            await store
                .ReserveAsync(runtimeInstanceId2, runCount: 1)
                .ConfigureAwait(false);

            var count1 =
                await store
                    .GetReservedRunCountAsync(runtimeInstanceId1)
                    .ConfigureAwait(false);

            var count2 =
                await store
                    .GetReservedRunCountAsync(runtimeInstanceId2)
                    .ConfigureAwait(false);

            Assert.Equal(2, count1);
            Assert.Equal(1, count2);
        }

        [Fact]
        public async Task Store_ShouldRecoverAfterNoScriptResponse()
        {
            var runtimeInstanceId =
                $"runtime-test-{Guid.NewGuid():N}";

            await store!
                .ReserveAsync(runtimeInstanceId, runCount: 1)
                .ConfigureAwait(false);

            SetPrivateShaField(
                store,
                "reserveScriptSha",
                CreateInvalidSha());

            await store
                .ReserveAsync(runtimeInstanceId, runCount: 1)
                .ConfigureAwait(false);

            var reservedRunCount =
                await store
                    .GetReservedRunCountAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.Equal(2, reservedRunCount);
        }

        private static void SetPrivateShaField(
    RedisAiRuntimeAdmissionReservationStore target,
    string fieldName,
    byte[] sha)
        {
            var field =
                typeof(RedisAiRuntimeAdmissionReservationStore)
                    .GetField(
                        fieldName,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic);

            Assert.NotNull(field);

            field.SetValue(
                target,
                sha);
        }

        private static byte[] CreateInvalidSha()
        {
            return Convert.FromHexString(
                "0000000000000000000000000000000000000000");
        }

        private async Task DeleteTestKeysAsync(
            IConnectionMultiplexer connection)
        {
            var database =
                connection.GetDatabase();

            foreach (var endpoint in connection.GetEndPoints())
            {
                var server =
                    connection.GetServer(endpoint);

                if (!server.IsConnected)
                {
                    continue;
                }

                var keys =
                    server.Keys(
                        pattern: $"{keyPrefix}:*")
                    .ToArray();

                if (keys.Length > 0)
                {
                    await database
                        .KeyDeleteAsync(keys)
                        .ConfigureAwait(false);
                }
            }
        }

        private static async Task FlushScriptsAsync(
            IConnectionMultiplexer connection)
        {
            foreach (var endpoint in connection.GetEndPoints())
            {
                var server =
                    connection.GetServer(endpoint);

                if (!server.IsConnected)
                {
                    continue;
                }

                await server
                    .ScriptFlushAsync()
                    .ConfigureAwait(false);
            }
        }
    }
}