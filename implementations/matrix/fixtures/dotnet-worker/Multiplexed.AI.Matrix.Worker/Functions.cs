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
}
