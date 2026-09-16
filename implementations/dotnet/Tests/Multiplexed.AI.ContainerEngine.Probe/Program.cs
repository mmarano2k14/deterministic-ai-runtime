using System.Text.Json;

// Docker-compatible argument probe for transport tests. It does not create containers,
// access a registry, load tenant code or reference runtime assemblies.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) return 64;
        if (args[0] == "rm")
        {
            var marker = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_CLEANUP_MARKER");
            if (!string.IsNullOrWhiteSpace(marker)) await File.WriteAllTextAsync(marker, string.Join(Environment.NewLine, args));
            return Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_CLEANUP_FAIL") == "1" ? 70 : 0;
        }
        if (args[0] != "run") return 65;

        var line = await Console.In.ReadLineAsync();
        using var requestDocument = JsonDocument.Parse(line ?? throw new InvalidOperationException("Missing invocation request."));
        var request = requestDocument.RootElement;

        object Frame(string type, bool? success = null, object? payload = null)
        {
            var frame = new Dictionary<string, object?>
            {
                ["protocolVersion"] = 1,
                ["type"] = type,
                ["requestId"] = request.GetProperty("requestId").GetString(),
                ["operationId"] = request.GetProperty("operationId").GetString(),
                ["workerId"] = request.GetProperty("workerId").GetString(),
                ["epoch"] = request.GetProperty("epoch").GetInt64()
            };
            if (type == "result") { frame["success"] = success; frame["payload"] = payload; }
            return frame;
        }

        async Task Send(object frame)
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(frame));
            await Console.Out.FlushAsync();
        }

        await Send(Frame("ready"));
        if (Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_MODE") == "hang-after-ready")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }

        await Send(Frame("result", true, new
        {
            arguments = args,
            image = args[^1],
            explicitValue = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_VALUE"),
            inheritedPath = Environment.GetEnvironmentVariable("PATH")
        }));
        return 0;
    }
}
