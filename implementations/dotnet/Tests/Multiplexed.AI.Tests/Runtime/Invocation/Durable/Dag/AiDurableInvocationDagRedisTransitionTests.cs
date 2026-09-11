using System.Text.Json;
using Multiplexed.AI.Stores.Cache.Redis.Lua;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag
{
    /// <summary>Opt-in proof against a real Redis instance; does not replace existing production harnesses.</summary>
    public sealed class AiDurableInvocationDagRedisTransitionTests
    {
        private const string ConnectionVariable = "MULTIPLEXED_TEST_REDIS_INVOCATION_CONNECTION_STRING";

        [InvocationRedisFact]
        public Task Custom_Failure_Atomically_Persists_Receipt_With_Terminal_Transition() => VerifyAsync(true, 0, false);

        [InvocationRedisFact]
        public Task Native_Failure_Keeps_Its_Historical_Result_Representation() => VerifyAsync(false, 0, false);

        [InvocationRedisFact]
        public Task Custom_Failure_Preserves_Existing_Retry_Budget() => VerifyAsync(true, 1, false);

        [InvocationRedisFact]
        public Task Stale_Claim_Cannot_Write_Failure_Receipt() => VerifyAsync(true, 0, true);

        private static async Task VerifyAsync(bool custom, int maxRetries, bool stale)
        {
            var options = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable(ConnectionVariable)!);
            options.AbortOnConnectFail = true;
            using var connection = await ConnectionMultiplexer.ConnectAsync(options);
            var database = connection.GetDatabase();
            RedisKey key = "test:durable-invocation:failure:" + Guid.NewGuid().ToString("N");
            var original = JsonSerializer.Serialize(new
            {
                StepName = "analyze", Status = "Running", ClaimToken = "current", ClaimedBy = "runtime-a",
                RetryState = new { RetryCount = 0 }, Retry = new { MaxRetries = maxRetries, BaseDelayMs = 1 },
                Version = 7, DependsOn = Array.Empty<string>(), Result = (object?)null
            });
            var resultJson = custom ? JsonSerializer.Serialize(new
            {
                Success = false, Outcome = "Fail", Error = "custom failure",
                Value = new { reason = "declined" },
                InvocationReceipt = new { OperationId = "operation-a", ResultSha256 = new string('a', 64) }
            }) : string.Empty;
            try
            {
                await database.StringSetAsync(key, original);
                var changed = (int)await database.ScriptEvaluateAsync(RedisDagLuaScripts.FailPreparedScript, new
                {
                    stepKey = key, claimToken = (RedisValue)(stale ? "stale" : "current"),
                    nowUnix = (RedisValue)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    error = (RedisValue)"custom failure", resultJson = (RedisValue)resultJson
                });
                var stored = (string)(await database.StringGetAsync(key))!;
                if (stale)
                { Assert.Equal(0, changed); Assert.Equal(original, stored); return; }
                Assert.Equal(1, changed);
                using var document = JsonDocument.Parse(stored);
                var step = document.RootElement;
                Assert.Equal(maxRetries == 0 ? "Failed" : "WaitingForRetry", step.GetProperty("Status").GetString());
                Assert.Equal(JsonValueKind.Null, step.GetProperty("ClaimToken").ValueKind);
                Assert.Equal(8, step.GetProperty("Version").GetInt32());
                Assert.Equal(maxRetries == 0 ? 0 : 1, step.GetProperty("RetryState").GetProperty("RetryCount").GetInt32());
                if (custom) Assert.Equal("operation-a", step.GetProperty("Result").GetProperty("InvocationReceipt").GetProperty("OperationId").GetString());
                else Assert.Equal(JsonValueKind.Null, step.GetProperty("Result").ValueKind);
            }
            finally { await database.KeyDeleteAsync(key); }
        }

        public sealed class InvocationRedisFactAttribute : FactAttribute
        {
            public InvocationRedisFactAttribute()
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                    Skip = "Set " + ConnectionVariable + " to run this real Redis transition test.";
            }
        }
    }
}
