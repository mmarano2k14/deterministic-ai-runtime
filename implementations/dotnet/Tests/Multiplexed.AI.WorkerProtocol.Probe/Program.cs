using System.Text.Json;

// Protocol-only test process. It does not load or execute any published tenant code,
// and has no project/package reference to the runtime, RBAC or SDK assemblies.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "success";
        var line = await Console.In.ReadLineAsync();
        using var json = JsonDocument.Parse(line ?? throw new InvalidOperationException("Missing request."));
        var request = json.RootElement;
        object Frame(string type, bool? success = null, object? payload = null)
        {
            var frame = new Dictionary<string, object?>
            {
                ["protocolVersion"] = mode == "wrong-version" ? 999 : 1, ["type"] = type,
                ["requestId"] = mode == "wrong-request" ? "foreign" : request.GetProperty("requestId").GetString(),
                ["operationId"] = request.GetProperty("operationId").GetString(),
                ["workerId"] = request.GetProperty("workerId").GetString(),
                ["epoch"] = request.GetProperty("epoch").GetInt64() + (mode == "wrong-epoch" ? 1 : 0)
            };
            if (type == "result") { frame["success"] = success; frame["payload"] = payload; }
            return frame;
        }
        async Task Send(object frame) { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(frame)); await Console.Out.FlushAsync(); }
        if (mode == "hang-start") { await Task.Delay(Timeout.Infinite); return 0; }
        if (mode == "invalid-json") { await Console.Out.WriteLineAsync("{"); return 0; }
        if (mode == "stdout-flood") { await Console.Out.WriteAsync(new string('x', 2097152)); return 0; }
        if (mode == "early-result") { await Send(Frame("result", true, 42)); return 0; }
        await Send(Frame("ready"));
        if (mode == "missing-result") return 0;
        if (mode == "duplicate-ready") await Send(Frame("ready"));
        if (mode == "hang-heartbeat") { await Task.Delay(Timeout.Infinite); return 0; }
        if (mode == "stderr-flood") { await Console.Error.WriteAsync(new string('x', 2097152)); await Console.Error.FlushAsync(); }
        if (mode == "heartbeats")
            for (var i = 0; i < 5; i++) { await Task.Delay(50); await Send(Frame("heartbeat")); }
        object payload = new { value = 42 };
        if (mode == "inspect") payload = new
        {
            processId = Environment.ProcessId,
            operationId = request.GetProperty("operationId").GetString(),
            publicationRef = request.GetProperty("code").GetProperty("target").GetProperty("publicationRef").GetString(),
            source = request.GetProperty("code").GetProperty("sources")[0].GetProperty("base64Url").GetString(),
            input = request.GetProperty("inputs"),
            explicitValue = Environment.GetEnvironmentVariable("WORKER_PROBE_VALUE"),
            unexpectedPath = Environment.GetEnvironmentVariable("PATH")
        };
        await Send(Frame("result", mode != "business-failure", payload));
        if (mode == "duplicate-result") await Send(Frame("result", true, new { value = 99 }));
        if (mode == "result-no-exit") await Task.Delay(Timeout.Infinite);
        return mode == "nonzero-exit" ? 13 : 0;
    }
}
