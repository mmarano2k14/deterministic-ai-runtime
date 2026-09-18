using System.Text.Json;
using Multiplexed.AI.Matrix.Dependency;

namespace Multiplexed.AI.Matrix.PackagedWorker;

public static class Functions
{
    public static object Run(JsonElement inputs, JsonElement context)
    {
        return new
        {
            success = true,
            payload = new
            {
                workerLanguage = "dotnet",
                dependencyValue = Rules.Scale(21)
            }
        };
    }
}
