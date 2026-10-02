using System.Text;
using System.Text.Json;
using Multiplexed.AI.HostedInvocation.TestDependency;

namespace Multiplexed.AI.HostedInvocation.TestFunctions;

public static class Functions
{
    private static int _counter;

    public static object Run(JsonElement inputs, JsonElement context) => Result(true, new { value = inputs.GetProperty("amount").GetInt32() * 2 });
    public static async Task<object> RunAsync(JsonElement inputs, JsonElement context)
    {
        await Task.Delay(20).ConfigureAwait(false);
        return Result(true, inputs.GetProperty("amount").GetInt32() + 1);
    }
    public static object BusinessFail(JsonElement inputs, JsonElement context) => Result(false, new { reason = "decision" });
    public static object UseDependency(JsonElement inputs, JsonElement context) => Result(true, ScaleRule.Scale(inputs.GetProperty("amount").GetInt32()));
    public static object Context(JsonElement inputs, JsonElement context) => Result(true, context);
    public static object Counter(JsonElement inputs, JsonElement context) => Result(true, Interlocked.Increment(ref _counter));
    public static object Revision1(JsonElement inputs, JsonElement context) => Result(true, new { revision = 1, amount = inputs.GetProperty("amount").GetInt32() });
    public static object Revision2(JsonElement inputs, JsonElement context) => Result(true, new { revision = 2, amount = inputs.GetProperty("amount").GetInt32() });
    public static object PolicyAllow(JsonElement inputs, JsonElement context) => Result(true, new
    {
        schemaVersion = 1,
        requestId = inputs.GetProperty("requestId").GetString(),
        policyKind = "concurrency",
        decision = "allow",
        reason = (string?)null
    });
    public static object PolicyFamily(JsonElement inputs, JsonElement context)
    {
        var requestId = inputs.GetProperty("requestId").GetString();
        return inputs.GetProperty("policyKind").GetString() switch
        {
            "concurrency" => Result(true, new
            {
                schemaVersion = 1,
                requestId,
                policyKind = "concurrency",
                decision = "allow",
                reason = (string?)null
            }),
            "retry" => Result(true, new
            {
                schemaVersion = 1,
                requestId,
                policyKind = "retry",
                decision = "retry",
                reason = "transient",
                suggestedDelayMs = 250
            }),
            "delegation" => Result(true, new
            {
                schemaVersion = 1,
                requestId,
                policyKind = "delegation",
                decision = "approve",
                reason = (string?)null
            }),
            _ => throw new InvalidOperationException("unsupported policy family")
        };
    }
    public static object Log(JsonElement inputs, JsonElement context)
    {
        Console.WriteLine("published console output");
        Console.OpenStandardOutput().Write(Encoding.UTF8.GetBytes("published raw stdout\n"));
        return Result(true, 42);
    }
    public static object Throw(JsonElement inputs, JsonElement context) => throw new InvalidOperationException("private technical data");
    public static object Invalid(JsonElement inputs, JsonElement context) => new { value = 42 };
    public static object ExtraField(JsonElement inputs, JsonElement context) => new { success = true, payload = 42, park = true };
    public static object RunWrongSignature(JsonElement inputs) => Result(true, 1);
    public static object NullAndUnicode(JsonElement inputs, JsonElement context) => Result(true, inputs);
    public static async Task<object> Slow(JsonElement inputs, JsonElement context)
    {
        await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        return Result(true, 1);
    }
    private static object Result(bool success, object? payload) => new { success, payload };
}
