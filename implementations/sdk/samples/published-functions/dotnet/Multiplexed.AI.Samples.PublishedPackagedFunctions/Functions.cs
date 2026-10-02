using System.Text.Json;
using Multiplexed.AI.Samples.PublishedDependency;

namespace Multiplexed.AI.Samples.PublishedPackagedFunctions;

/// <summary>Published function sample consuming an explicitly packaged dependency.</summary>
public static class Functions
{
    public static object Run(JsonElement inputs, JsonElement context) => new
    {
        success = true,
        payload = new
        {
            workerLanguage = "dotnet",
            dependencyValue = ScaleRule.Scale(21)
        }
    };
}
