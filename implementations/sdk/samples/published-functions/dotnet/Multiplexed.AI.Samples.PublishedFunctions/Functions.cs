using System.Text.Json;

namespace Multiplexed.AI.Samples.PublishedFunctions;

/// <summary>Reusable functions demonstrating externally published .NET invocation contracts.</summary>
public static class Functions
{
    public static object Run(JsonElement inputs, JsonElement context)
    {
        var marker = inputs.TryGetProperty("marker", out var value) ? value.GetString() : null;
        return Result(new { workerLanguage = "dotnet", marker });
    }

    public static object PinStable(JsonElement inputs, JsonElement context)
    {
        Thread.Sleep(TimeSpan.FromSeconds(8));
        return Result(new { workerLanguage = "dotnet", revision = 1 });
    }

    public static object PinPoison(JsonElement inputs, JsonElement context) =>
        throw new InvalidOperationException("Replacement publication code must never execute for an already pinned run.");

    public static object DelegationDeny(JsonElement inputs, JsonElement context)
    {
        var requestId = inputs.GetProperty("requestId").GetString()
            ?? throw new InvalidOperationException("Policy requestId is required.");
        return Result(new
        {
            schemaVersion = 1,
            requestId,
            policyKind = "delegation",
            decision = "deny",
            reason = "sample-delegation-deny"
        });
    }

    private static object Result(object payload) => new { success = true, payload };
}
