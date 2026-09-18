using System.Text.Json;

namespace Multiplexed.AI.Matrix.Worker;

public static class Functions
{
    public static object Run(JsonElement inputs, JsonElement context)
    {
        var marker = inputs.TryGetProperty("marker", out var value)
            ? value.GetString()
            : null;
        return new
        {
            success = true,
            payload = new
            {
                workerLanguage = "dotnet",
                marker
            }
        };
    }

    public static object PinStable(JsonElement inputs, JsonElement context)
    {
        Thread.Sleep(TimeSpan.FromSeconds(8));
        return new
        {
            success = true,
            payload = new
            {
                workerLanguage = "dotnet",
                revision = 1
            }
        };
    }

    public static object PinPoison(JsonElement inputs, JsonElement context) =>
        throw new InvalidOperationException("Replacement publication code must never execute for the already pinned run.");

    public static object DelegationDeny(JsonElement inputs, JsonElement context)
    {
        var requestId = inputs.GetProperty("requestId").GetString()
            ?? throw new InvalidOperationException("Policy requestId is required.");
        return new
        {
            success = true,
            payload = new
            {
                schemaVersion = 1,
                requestId,
                policyKind = "delegation",
                decision = "deny",
                reason = "matrix-delegation-deny"
            }
        };
    }
}
