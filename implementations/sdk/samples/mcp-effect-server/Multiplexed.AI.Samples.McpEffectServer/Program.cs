using System.Collections.Concurrent;
using System.ComponentModel;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<SampleEffectTools>();
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { ready = true }));
app.MapGet("/state/{scenario}", (string scenario) => Results.Ok(SampleEffectTools.State(scenario)));
app.MapMcp("/mcp");
await app.RunAsync().ConfigureAwait(false);

/// <summary>Standalone MCP sample for demonstrating deterministic outbound effect behavior.</summary>
[McpServerToolType]
internal sealed class SampleEffectTools
{
    private static readonly ConcurrentDictionary<string, int> Calls = new(StringComparer.Ordinal);

    public static object State(string scenario) => new
    {
        scenario,
        physicalCallCount = Calls.TryGetValue(scenario, out var count) ? count : 0
    };

    [McpServerTool(Name = "probe.fail-count")]
    [Description("Counts one tools/call and returns an explicit MCP tool error.")]
    public static CallToolResult FailCount(string scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        var count = Calls.AddOrUpdate(scenario, 1, static (_, current) => checked(current + 1));
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"attempt:{count}" }]
        };
    }

    [McpServerTool(Name = "probe.slow-count")]
    [Description("Counts one tools/call, then waits long enough for the caller-owned deadline to expire.")]
    public static async Task<string> SlowCount(string scenario, int milliseconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        if (milliseconds is < 1 or > 30000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        _ = Calls.AddOrUpdate(scenario, 1, static (_, current) => checked(current + 1));
        await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        return "completed";
    }
}
