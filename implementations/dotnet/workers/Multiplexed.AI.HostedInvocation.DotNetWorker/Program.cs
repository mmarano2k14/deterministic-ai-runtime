using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Multiplexed.AI.HostedInvocation.DotNetWorker;

internal static class Program
{
    private const int MaxRequestBytes = 50_331_648;
    private const int MaxInlineBytes = 262_144;
    private const int MaxFileBytes = 16_777_216;
    private const int MaxBundleBytes = 50_331_648;
    private const int MaxFiles = 512;
    private const int MaxDependencies = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--published-child")
                return await RunPublishedChildAsync(args).ConfigureAwait(false);
            return await RunParentAsync(args).ConfigureAwait(false);
        }
        catch
        {
            return 70;
        }
    }

    private static async Task<int> RunParentAsync(string[] args)
    {
        var options = ParseParentArgs(args);
        var raw = await ReadSingleRequestAsync(Console.OpenStandardInput()).ConfigureAwait(false);
        using var request = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 36 });
        var root = request.RootElement;
        ValidateRequest(root, options.Runtime);
        var workspace = Materialize(root.GetProperty("code"));
        try
        {
            var deadline = root.GetProperty("deadlineUtc").GetDateTimeOffset();
            if (deadline <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Invocation deadline has elapsed.");
            var context = BuildContext(root);
            var childInput = Path.Combine(workspace.Root, "child-input.json");
            var childOutput = Path.Combine(workspace.Root, "child-output.json");
            await File.WriteAllTextAsync(childInput, JsonSerializer.Serialize(new
            {
                entryAssembly = workspace.EntryAssembly,
                entryPointSymbol = workspace.EntryPointSymbol,
                inputs = root.GetProperty("inputs"),
                context
            }, Json), Utf8).ConfigureAwait(false);

            await EmitAsync(root, "ready").ConfigureAwait(false);
            using var child = StartChild(childInput, childOutput);
            var diagnostics = DrainDiagnosticsAsync(child);
            while (!child.HasExited)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    Kill(child);
                    throw new TimeoutException("Published .NET invocation deadline elapsed.");
                }
                var delay = TimeSpan.FromMilliseconds(Math.Min(options.HeartbeatMilliseconds, remaining.TotalMilliseconds));
                await Task.Delay(delay).ConfigureAwait(false);
                if (!child.HasExited) await EmitAsync(root, "heartbeat").ConfigureAwait(false);
            }
            await diagnostics.ConfigureAwait(false);
            if (child.ExitCode != 0 || !File.Exists(childOutput))
                throw new InvalidOperationException("Published .NET child failed without an authoritative result.");
            var resultBytes = await File.ReadAllBytesAsync(childOutput).ConfigureAwait(false);
            if (resultBytes.Length is < 2 or > MaxInlineBytes + 1024)
                throw new InvalidOperationException("Published .NET child result exceeds its bound.");
            using var result = JsonDocument.Parse(resultBytes, new JsonDocumentOptions { MaxDepth = 36 });
            ValidateBusinessResult(result.RootElement);
            await EmitAsync(root, "result", result.RootElement).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            try { Directory.Delete(workspace.Root, recursive: true); }
            catch { throw new IOException("Published .NET workspace cleanup failed."); }
        }
    }

    private static async Task<int> RunPublishedChildAsync(string[] args)
    {
        if (args.Length != 3) return 64;
        var inputPath = args[1]; var outputPath = args[2];
        using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false),
            new JsonDocumentOptions { MaxDepth = 36 });
        var root = input.RootElement;
        RequireExact(root, "entryAssembly", "entryPointSymbol", "inputs", "context");
        var assemblyPath = root.GetProperty("entryAssembly").GetString() ?? throw new InvalidOperationException();
        var symbol = root.GetProperty("entryPointSymbol").GetString() ?? throw new InvalidOperationException();
        var split = symbol.Split(new[] { "::" }, StringSplitOptions.None);
        if (split.Length != 2 || string.IsNullOrWhiteSpace(split[0]) || string.IsNullOrWhiteSpace(split[1]))
            throw new InvalidOperationException("The .NET entry point must use TypeName::MethodName.");
        var load = new PublishedLoadContext(Path.GetDirectoryName(assemblyPath)!);
        try
        {
            var assembly = load.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(split[0], throwOnError: true, ignoreCase: false)!;
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(m => m.Name == split[1])
                ?? throw new InvalidOperationException("The configured .NET entry point was not found exactly once.");
            var parameters = method.GetParameters();
            if (parameters.Length != 2 || parameters.Any(p => p.ParameterType != typeof(JsonElement)))
                throw new InvalidOperationException("The .NET entry point must accept exactly two JsonElement arguments.");
            var value = method.Invoke(null, new object[] { root.GetProperty("inputs"), root.GetProperty("context") });
            value = await AwaitResultAsync(value, method.ReturnType).ConfigureAwait(false);
            var result = JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), Json);
            ValidateBusinessResult(result);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
            if (bytes.Length > MaxInlineBytes + 1024) throw new InvalidOperationException("Published result exceeds its bound.");
            await File.WriteAllBytesAsync(outputPath, bytes).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            load.Unload();
        }
    }

    private static async Task<object?> AwaitResultAsync(object? value, Type returnType)
    {
        if (value is Task task)
        {
            await task.ConfigureAwait(false);
            return returnType.IsGenericType ? returnType.GetProperty("Result")!.GetValue(task) : null;
        }
        if (returnType == typeof(ValueTask) && value is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false); return null;
        }
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = returnType.GetMethod("AsTask")!.Invoke(value, null) as Task
                ?? throw new InvalidOperationException("Invalid ValueTask result.");
            await asTask.ConfigureAwait(false);
            return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
        }
        return value;
    }

    private static Process StartChild(string inputPath, string outputPath)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The .NET host executable is unavailable.");
        var workerAssembly = Assembly.GetExecutingAssembly().Location;
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Clear();
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } root)
            start.Environment["SystemRoot"] = root;
        start.ArgumentList.Add(workerAssembly);
        start.ArgumentList.Add("--published-child");
        start.ArgumentList.Add(inputPath);
        start.ArgumentList.Add(outputPath);
        var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("The published .NET child process did not start.");
        return process;
    }

    private static async Task DrainDiagnosticsAsync(Process process)
    {
        var stdout = Drain(process.StandardOutput.BaseStream);
        var stderr = Drain(process.StandardError.BaseStream);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        static async Task Drain(Stream stream)
        {
            var buffer = new byte[4096]; long total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) return;
                if ((total += read) > 65_536) throw new IOException("Published .NET diagnostics exceeded their bound.");
            }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static async Task<byte[]> ReadSingleRequestAsync(Stream input)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            memory.Write(buffer, 0, read);
            if (memory.Length > MaxRequestBytes + 1) throw new InvalidOperationException("Worker request exceeds its bound.");
        }
        var bytes = memory.ToArray();
        if (bytes.Length < 2 || bytes[^1] != (byte)'\n' || Array.IndexOf(bytes, (byte)'\n', 0, bytes.Length - 1) >= 0)
            throw new InvalidOperationException("Exactly one newline-terminated worker request is required.");
        return bytes[..^1];
    }

    private static ParentOptions ParseParentArgs(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in args)
        {
            var index = arg.IndexOf('=');
            if (!arg.StartsWith("--", StringComparison.Ordinal) || index < 3 || !values.TryAdd(arg[2..index], arg[(index + 1)..]))
                throw new InvalidOperationException("Invalid .NET worker argument.");
        }
        if (values.Count != 4 || !values.TryGetValue("runtime-reference", out var reference) ||
            !values.TryGetValue("runtime-version", out var version) || !values.TryGetValue("runtime-sha256", out var sha) ||
            !values.TryGetValue("heartbeat-ms", out var heartbeat) || !int.TryParse(heartbeat, out var heartbeatMs) ||
            heartbeatMs is < 50 or > 5000 || !IsHash(sha))
            throw new InvalidOperationException("Missing or invalid .NET worker arguments.");
        var actual = Environment.Version;
        if (!Version.TryParse(version, out var configured) || configured.Major != 10 || configured.Minor != 0 ||
            configured.Build < 0 || configured.ToString(3) != version || actual.Major != configured.Major || actual.Minor != configured.Minor || actual.Build != configured.Build)
            throw new InvalidOperationException("The installed .NET runtime differs from the configured profile.");
        return new(new(reference, "dotnet", version, sha), heartbeatMs);
    }

    private static void ValidateRequest(JsonElement request, RuntimeIdentity runtime)
    {
        RequireExact(request, "protocolVersion", "type", "requestId", "operationId", "effectIdempotencyKey", "workerId",
            "epoch", "tenantId", "executionId", "stepName", "generation", "deadlineUtc", "traceParent", "inputs", "code");
        if (request.GetProperty("protocolVersion").GetInt32() != 1 || request.GetProperty("type").GetString() != "invoke" ||
            request.GetProperty("epoch").GetInt64() < 1 || request.GetProperty("generation").GetInt32() < 0)
            throw new InvalidOperationException("Unsupported worker protocol.");
        foreach (var name in new[] { "requestId", "operationId", "effectIdempotencyKey", "workerId", "tenantId", "executionId", "stepName" })
            Text(request.GetProperty(name).GetString());
        if (request.GetProperty("inputs").ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Inputs must be an object.");
        if (Encoding.UTF8.GetByteCount(request.GetProperty("inputs").GetRawText()) > MaxInlineBytes) throw new InvalidOperationException("Inputs exceed their bound.");
        var code = request.GetProperty("code"); RequireExact(code, "target", "runtime", "entryPointPath", "entryPointSymbol", "sources", "dependencies");
        var runtimeElement = code.GetProperty("runtime"); RequireExact(runtimeElement, "reference", "executionLanguage", "runtimeVersion", "runtimeSha256");
        if (runtimeElement.GetProperty("reference").GetString() != runtime.Reference ||
            runtimeElement.GetProperty("executionLanguage").GetString() != "dotnet" ||
            runtimeElement.GetProperty("runtimeVersion").GetString() != runtime.RuntimeVersion ||
            runtimeElement.GetProperty("runtimeSha256").GetString() != runtime.RuntimeSha256)
            throw new InvalidOperationException("The exact configured .NET runtime is required.");
        var target = code.GetProperty("target");
        if (target.GetProperty("executionLanguage").GetString() != "dotnet") throw new InvalidOperationException("This worker executes .NET only.");
        foreach (var name in new[] { "definitionSha256", "publicationSha256", "implementationSha256", "environmentSha256" })
            if (!IsHash(target.GetProperty(name).GetString())) throw new InvalidOperationException("Invalid target digest.");
    }

    private static Workspace Materialize(JsonElement code)
    {
        var entry = PortablePath(code.GetProperty("entryPointPath").GetString(), requireDll: true);
        var symbol = Text(code.GetProperty("entryPointSymbol").GetString());
        if (!symbol.Contains("::", StringComparison.Ordinal)) throw new InvalidOperationException("A TypeName::MethodName symbol is required.");
        var root = Path.Combine(Path.GetTempPath(), "multiplexed-ai-dotnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            long total = 0; int count = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void WriteFiles(JsonElement files, string owner, string basePath)
            {
                if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 1 or > MaxFiles)
                    throw new InvalidOperationException("An explicit published file list is required.");
                foreach (var file in files.EnumerateArray())
                {
                    RequireExact(file, "path", "sha256", "sizeBytes", "base64Url");
                    var relative = PortablePath(file.GetProperty("path").GetString(), requireDll: false);
                    var hash = file.GetProperty("sha256").GetString(); if (!IsHash(hash)) throw new InvalidOperationException("Invalid file digest.");
                    var size = file.GetProperty("sizeBytes").GetInt64(); if (size is < 0 or > MaxFileBytes) throw new InvalidOperationException("Published file exceeds its bound.");
                    var bytes = DecodeBase64Url(file.GetProperty("base64Url").GetString() ?? string.Empty);
                    if (bytes.LongLength != size || Hash(bytes) != hash) throw new InvalidOperationException("Published file content integrity mismatch.");
                    total += size; count++; if (total > MaxBundleBytes || count > MaxFiles) throw new InvalidOperationException("Published .NET closure exceeds its bound.");
                    var key = owner + ":" + relative; if (!paths.Add(key)) throw new InvalidOperationException("Duplicate published path.");
                    var target = Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, bytes);
                }
            }
            WriteFiles(code.GetProperty("sources"), "source", root);
            var dependencies = code.GetProperty("dependencies");
            if (dependencies.ValueKind != JsonValueKind.Array || dependencies.GetArrayLength() > MaxDependencies)
                throw new InvalidOperationException("Invalid dependency list.");
            foreach (var dependency in dependencies.EnumerateArray())
            {
                RequireExact(dependency, "name", "version", "files");
                var name = SafeSegment(dependency.GetProperty("name").GetString());
                _ = Text(dependency.GetProperty("version").GetString());
                WriteFiles(dependency.GetProperty("files"), "dependency:" + name, Path.Combine(root, ".dependencies", name));
            }
            var assembly = Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(assembly)) throw new InvalidOperationException("The configured .NET entry assembly is absent.");
            return new(root, assembly, symbol);
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            throw;
        }
    }

    private static object BuildContext(JsonElement request) => new Dictionary<string, object?>
    {
        ["requestId"] = request.GetProperty("requestId").GetString(),
        ["operationId"] = request.GetProperty("operationId").GetString(),
        ["effectIdempotencyKey"] = request.GetProperty("effectIdempotencyKey").GetString(),
        ["workerId"] = request.GetProperty("workerId").GetString(),
        ["tenantId"] = request.GetProperty("tenantId").GetString(),
        ["executionId"] = request.GetProperty("executionId").GetString(),
        ["stepName"] = request.GetProperty("stepName").GetString(),
        ["epoch"] = request.GetProperty("epoch").GetInt64(),
        ["generation"] = request.GetProperty("generation").GetInt32(),
        ["deadlineUtc"] = request.GetProperty("deadlineUtc").GetDateTimeOffset(),
        ["traceParent"] = request.GetProperty("traceParent").ValueKind == JsonValueKind.Null ? null : request.GetProperty("traceParent").GetString(),
        ["target"] = JsonSerializer.Deserialize<object>(request.GetProperty("code").GetProperty("target").GetRawText(), Json)
    };

    private static async Task EmitAsync(JsonElement request, string type, JsonElement? result = null)
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
        if (type == "result")
        {
            if (result is null) throw new InvalidOperationException();
            frame["success"] = result.Value.GetProperty("success").GetBoolean();
            frame["payload"] = JsonSerializer.Deserialize<object>(result.Value.GetProperty("payload").GetRawText(), Json);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        if (bytes.Length > 1_048_576) throw new InvalidOperationException("Worker frame exceeds its bound.");
        var output = Console.OpenStandardOutput();
        await output.WriteAsync(bytes).ConfigureAwait(false);
        await output.WriteAsync(new byte[] { (byte)'\n' }).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static void ValidateBusinessResult(JsonElement result)
    {
        RequireExact(result, "success", "payload");
        if (result.GetProperty("success").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("Published success must be an explicit boolean.");
        if (Encoding.UTF8.GetByteCount(result.GetProperty("payload").GetRawText()) > MaxInlineBytes)
            throw new InvalidOperationException("Published payload exceeds its bound.");
    }

    private static void RequireExact(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Expected JSON object.");
        var expected = new HashSet<string>(names, StringComparer.Ordinal); var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++; if (!expected.Remove(property.Name)) throw new InvalidOperationException("Unexpected JSON field.");
        }
        if (count != names.Length || expected.Count != 0) throw new InvalidOperationException("Missing JSON field.");
    }

    private static string PortablePath(string? value, bool requireDll)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || Path.IsPathFullyQualified(value) || value.Contains('\\'))
            throw new InvalidOperationException("Published paths must be portable relative paths.");
        foreach (var part in value.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
                throw new InvalidOperationException("Invalid published path segment.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
                throw new InvalidOperationException("Device path is forbidden.");
        }
        if (requireDll && !value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The .NET entry point must be a published assembly.");
        return value;
    }

    private static string SafeSegment(string? value)
    {
        var text = Text(value);
        if (text.Length > 64 || text.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')) || text is "." or "..")
            throw new InvalidOperationException("Invalid dependency name.");
        return text;
    }

    private static string Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(c => char.IsControl(c)))
            throw new InvalidOperationException("Invalid text value.");
        return value;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] DecodeBase64Url(string value)
    {
        if (value.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')) || value.Length % 4 == 1)
            throw new InvalidOperationException("Invalid base64url content.");
        var padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
        var bytes = Convert.FromBase64String(padded);
        var canonical = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (canonical != value) throw new InvalidOperationException("Non-canonical base64url content.");
        return bytes;
    }

    private sealed record ParentOptions(RuntimeIdentity Runtime, int HeartbeatMilliseconds);
    private sealed record RuntimeIdentity(string Reference, string ExecutionLanguage, string RuntimeVersion, string RuntimeSha256);
    private sealed record Workspace(string Root, string EntryAssembly, string EntryPointSymbol);

    private sealed class PublishedLoadContext : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, string> _assemblies;
        public PublishedLoadContext(string root) : base(isCollectible: true)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    var name = AssemblyName.GetAssemblyName(file).Name;
                    if (!string.IsNullOrWhiteSpace(name) && !values.TryAdd(name, file))
                        throw new InvalidOperationException("Ambiguous published assembly identity.");
                }
                catch (BadImageFormatException) { }
            }
            _assemblies = values;
        }
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not null && _assemblies.TryGetValue(assemblyName.Name, out var path))
                return LoadFromAssemblyPath(path);
            return null;
        }
    }
}
