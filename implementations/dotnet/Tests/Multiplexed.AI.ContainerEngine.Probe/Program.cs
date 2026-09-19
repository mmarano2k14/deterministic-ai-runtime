using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

// Docker-compatible argument/inspection probe for transport tests. It models the engine control
// surface only; it does not create a container, contact a registry or execute tenant code.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) return 64;
        return args[0] switch
        {
            "run" => await RunAsync(args),
            "inspect" => await InspectAsync(args),
            "ps" => await ListAsync(args),
            "rm" => await RemoveAsync(args),
            _ => 65
        };
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var name = Separate(args, "--name") ?? throw new InvalidOperationException("Container name is required.");
        var statePath = StatePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var state = new JsonObject
        {
            ["inspected"] = false,
            ["inspect"] = BuildInspection(args)
        };
        await File.WriteAllTextAsync(statePath, state.ToJsonString());

        var runMarker = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_RUN_MARKER");
        if (!string.IsNullOrWhiteSpace(runMarker)) await File.WriteAllTextAsync(runMarker, name);

        if (Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_REQUIRE_CLEANUP_BEFORE_RUN") == "1")
        {
            var cleanupMarker = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_CLEANUP_MARKER");
            if (string.IsNullOrWhiteSpace(cleanupMarker) || !File.Exists(cleanupMarker)) return 73;
        }

        var mode = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_MODE");
        try
        {
            if (mode == "exit-after-inspect")
            {
                while (true)
                {
                    var current = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
                        ?? throw new InvalidOperationException("Missing engine probe state.");
                    if (current["inspected"]?.GetValue<bool>() == true) return 74;
                    await Task.Delay(10);
                }
            }

            var line = await Console.In.ReadLineAsync();
            if (line is null) return 66;
            var requestMarker = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_REQUEST_MARKER");
            if (!string.IsNullOrWhiteSpace(requestMarker))
                await File.WriteAllTextAsync(requestMarker, "received");

            if (Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_REQUIRE_INSPECT_BEFORE_REQUEST") == "1")
            {
                var current = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
                    ?? throw new InvalidOperationException("Missing engine probe state.");
                if (current["inspected"]?.GetValue<bool>() != true)
                    throw new InvalidOperationException("Invocation request arrived before isolation inspection.");
            }

            using var requestDocument = JsonDocument.Parse(line);
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
            if (mode == "hang-after-ready")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            }

            var packageKinds = new List<string>();
            if (request.GetProperty("code").TryGetProperty("dependencies", out var dependencies) &&
                dependencies.ValueKind == JsonValueKind.Array)
            {
                foreach (var dependency in dependencies.EnumerateArray())
                {
                    if (dependency.TryGetProperty("package", out var package) &&
                        package.ValueKind == JsonValueKind.Object &&
                        package.TryGetProperty("kind", out var kind) &&
                        kind.ValueKind == JsonValueKind.String)
                    {
                        packageKinds.Add(kind.GetString()!);
                    }
                }
            }

            var imageIndex = ImageIndex(args);
            await Send(Frame("result", true, new
            {
                arguments = args,
                image = args[imageIndex],
                workerArguments = args[(imageIndex + 1)..],
                explicitValue = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_VALUE"),
                inheritedPath = Environment.GetEnvironmentVariable("PATH"),
                dependencyPackages = packageKinds
            }));
            return 0;
        }
        finally
        {
            if (mode is not ("hang-after-ready" or "exit-after-inspect"))
            {
                try { File.Delete(statePath); } catch { }
            }
        }
    }

    private static async Task<int> InspectAsync(string[] args)
    {
        if (args.Length != 4 || args[1] != "--type" || args[2] != "container") return 67;
        var delayText = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_INSPECT_DELAY_MS");
        if (int.TryParse(delayText, NumberStyles.None, CultureInfo.InvariantCulture, out var delay) && delay > 0)
            await Task.Delay(delay);

        var statePath = StatePath(args[3]);
        if (!File.Exists(statePath)) return 1;
        var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
            ?? throw new InvalidOperationException("Invalid engine probe state.");
        state["inspected"] = true;
        await File.WriteAllTextAsync(statePath, state.ToJsonString());

        var inspection = state["inspect"]?.DeepClone()?.AsObject()
            ?? throw new InvalidOperationException("Missing inspection state.");
        ApplyTamper(inspection, Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_ATTESTATION_TAMPER"));
        var array = new JsonArray(inspection);
        await Console.Out.WriteLineAsync(array.ToJsonString());
        return 0;
    }

    private static async Task<int> ListAsync(string[] args)
    {
        if (Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_PS_FAIL") == "1") return 75;
        var filters = SeparateAll(args, "--filter").ToArray();
        var expectedScope = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_EXPECT_OWNER_SCOPE");
        if (!filters.Contains("label=multiplexed.ai.hosted-worker=1", StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(expectedScope) ||
            !filters.Contains("label=multiplexed.ai.owner-scope=" + expectedScope, StringComparer.Ordinal))
        {
            return 76;
        }

        var format = Separate(args, "--format");
        if (format != "{{.Names}}") return 77;
        var names = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_PS_NAMES");
        if (!string.IsNullOrWhiteSpace(names))
        {
            foreach (var name in names.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await Console.Out.WriteLineAsync(name);
        }
        return 0;
    }

    private static async Task<int> RemoveAsync(string[] args)
    {
        var marker = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_CLEANUP_MARKER");
        if (!string.IsNullOrWhiteSpace(marker)) await File.WriteAllTextAsync(marker, string.Join(Environment.NewLine, args));
        if (Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_CLEANUP_FAIL") == "1") return 70;
        if (args.Length >= 3)
        {
            try { File.Delete(StatePath(args[^1])); } catch { }
        }
        return 0;
    }

    private static JsonObject BuildInspection(string[] args)
    {
        var tmpfs = Prefixed(args, "--tmpfs=") ?? throw new InvalidOperationException("tmpfs is required.");
        var split = tmpfs.IndexOf(':');
        if (split <= 0) throw new InvalidOperationException("Invalid tmpfs argument.");
        var tmpfsDestination = tmpfs[..split];
        var tmpfsOptions = tmpfs[(split + 1)..];
        var cpus = decimal.Parse(Prefixed(args, "--cpus=") ?? "0", CultureInfo.InvariantCulture);
        var labels = new JsonObject();
        foreach (var label in PrefixedAll(args, "--label="))
        {
            var separator = label.IndexOf('=');
            if (separator <= 0) throw new InvalidOperationException("Invalid label argument.");
            labels[label[..separator]] = label[(separator + 1)..];
        }

        return new JsonObject
        {
            ["Config"] = new JsonObject
            {
                ["Image"] = args[ImageIndex(args)],
                ["User"] = Separate(args, "--user") ?? string.Empty,
                ["Labels"] = labels
            },
            ["HostConfig"] = new JsonObject
            {
                ["ReadonlyRootfs"] = args.Contains("--read-only", StringComparer.Ordinal),
                ["Privileged"] = args.Contains("--privileged", StringComparer.Ordinal),
                ["AutoRemove"] = args.Contains("--rm", StringComparer.Ordinal),
                ["Init"] = args.Contains("--init", StringComparer.Ordinal),
                ["NetworkMode"] = Prefixed(args, "--network=") ?? string.Empty,
                ["Memory"] = long.Parse(Prefixed(args, "--memory=") ?? "0", CultureInfo.InvariantCulture),
                ["MemorySwap"] = long.Parse(Prefixed(args, "--memory-swap=") ?? "0", CultureInfo.InvariantCulture),
                ["NanoCpus"] = checked((long)(cpus * 1_000_000_000m)),
                ["PidsLimit"] = long.Parse(Prefixed(args, "--pids-limit=") ?? "0", CultureInfo.InvariantCulture),
                ["Binds"] = null,
                ["CapAdd"] = new JsonArray(),
                ["CapDrop"] = args.Contains("--cap-drop=ALL", StringComparer.Ordinal)
                    ? new JsonArray(JsonValue.Create("ALL")) : new JsonArray(),
                ["SecurityOpt"] = args.Contains("--security-opt=no-new-privileges", StringComparer.Ordinal)
                    ? new JsonArray(JsonValue.Create("no-new-privileges")) : new JsonArray(),
                ["Tmpfs"] = new JsonObject { [tmpfsDestination] = tmpfsOptions }
            },
            ["Mounts"] = new JsonArray(new JsonObject
            {
                ["Type"] = "tmpfs",
                ["Destination"] = tmpfsDestination
            })
        };
    }

    private static int ImageIndex(string[] args)
    {
        var logDriver = Array.IndexOf(args, "--log-driver=none");
        if (logDriver < 0 || logDriver + 1 >= args.Length)
            throw new InvalidOperationException("Container image argument is missing.");
        return logDriver + 1;
    }

    private static void ApplyTamper(JsonObject inspection, string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        var config = inspection["Config"]!.AsObject();
        var host = inspection["HostConfig"]!.AsObject();
        switch (mode)
        {
            case "network": host["NetworkMode"] = "bridge"; break;
            case "readonly": host["ReadonlyRootfs"] = false; break;
            case "memory": host["Memory"] = 1L; break;
            case "memory-swap": host["MemorySwap"] = 2L; break;
            case "cpu": host["NanoCpus"] = 1L; break;
            case "pids": host["PidsLimit"] = 1L; break;
            case "tmpfs": host["Tmpfs"] = new JsonObject { ["/tmp"] = "rw,size=1" }; break;
            case "user": config["User"] = "0:0"; break;
            case "privileged": host["Privileged"] = true; break;
            case "capdrop": host["CapDrop"] = new JsonArray(); break;
            case "security": host["SecurityOpt"] = new JsonArray(); break;
            case "image": config["Image"] = "registry.example.com/other@sha256:" + new string('c', 64); break;
            case "bind": host["Binds"] = new JsonArray(JsonValue.Create("/host:/guest")); break;
            case "autoremove": host["AutoRemove"] = false; break;
            case "init": host["Init"] = false; break;
            case "labels": config["Labels"] = new JsonObject
            {
                ["multiplexed.ai.hosted-worker"] = "1",
                ["multiplexed.ai.owner-scope"] = "other-host"
            }; break;
            default: throw new InvalidOperationException("Unknown attestation tamper mode.");
        }
    }

    private static string? Separate(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static IEnumerable<string> SeparateAll(string[] args, string option)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == option) yield return args[index + 1];
    }

    private static string? Prefixed(string[] args, string prefix) =>
        args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static IEnumerable<string> PrefixedAll(string[] args, string prefix) =>
        args.Where(argument => argument.StartsWith(prefix, StringComparison.Ordinal))
            .Select(argument => argument[prefix.Length..]);

    private static string StatePath(string containerName)
    {
        var directory = Environment.GetEnvironmentVariable("CONTAINER_ENGINE_PROBE_STATE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Probe state directory is required.");
        return Path.Combine(directory, containerName + ".json");
    }
}
