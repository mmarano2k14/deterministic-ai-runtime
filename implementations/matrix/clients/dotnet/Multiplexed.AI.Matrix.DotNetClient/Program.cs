using System.Text;
using System.Text.Json;
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Sdk.Transport;

var options = Arguments.Parse(args);
var transportOptions = new AiSdkTransportOptions
{
    CredentialProvider = string.IsNullOrWhiteSpace(options.Token)
        ? null
        : new AiSdkStaticCredentialProvider(new AiSdkCredential("Bearer", options.Token)),
    AdditionalHeaders = string.IsNullOrWhiteSpace(options.AccessContext)
        ? null
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [options.AccessContextHeader] = options.AccessContext
        }
};
var client = new AiSdkClient(new AiSdkMcpHttpTransport(new Uri(options.Endpoint), transportOptions));

var source = WorkerSource.Load(options.Worker);
var marker = options.ScenarioId + "-marker";
var publication = await client.PublishPipelineAsync(new AiSdkPipelinePublicationRequest
{
    Definition = new AiSdkPipelineDefinition
    {
        Name = "matrix-" + options.ScenarioId,
        Version = "1",
        ExecutionLanguage = options.Worker,
        ExecutionMode = AiSdkExecutionMode.Dag,
        Steps =
        [
            new AiSdkPipelineStepDefinition
            {
                Name = "work",
                StepKey = "custom",
                Order = 0,
                ExecutionLanguage = options.Worker,
                Invocation = new AiSdkInvocationDefinition { Kind = AiSdkInvocationKind.Custom },
                Input = new Dictionary<string, JsonElement>
                {
                    ["marker"] = JsonSerializer.SerializeToElement(marker)
                }
            }
        ]
    },
    Functions =
    [
        new AiSdkPublicationFunctionUpload
        {
            Site = new AiSdkPublicationCallSite
            {
                Kind = AiSdkPublicationFunctionKind.Step,
                StepName = "work"
            },
            EnvironmentRef = options.EnvironmentRef,
            EntryPointPath = source.EntryPointPath,
            EntryPointSymbol = source.EntryPointSymbol,
            Sources =
            [
                new AiSdkPublicationFileUpload
                {
                    Path = source.EntryPointPath,
                    ContentBase64 = Convert.ToBase64String(source.Bytes)
                }
            ]
        }
    ]
});

var submitted = await client.SubmitExecutionAsync(new AiSdkExecutionSubmissionRequest
{
    PublicationRef = publication.PublicationRef,
    IdempotencyKey = options.ScenarioId + "-" + Guid.NewGuid().ToString("N"),
    Input = JsonSerializer.SerializeToElement(new { marker }),
    Metadata = new Dictionary<string, string>
    {
        ["matrix.scenario"] = options.ScenarioId,
        ["matrix.client"] = "dotnet",
        ["matrix.worker"] = options.Worker
    }
});

var observed = await WaitForTerminalAsync(client, submitted.ExecutionId, TimeSpan.FromSeconds(90));
var result = await client.GetExecutionResultAsync(submitted.ExecutionId);
if (result.Status != AiSdkExecutionStatus.Completed)
{
    throw new InvalidOperationException($"Execution '{submitted.ExecutionId}' ended as '{result.Status}'.");
}

await Evidence.WriteAsync(options.Evidence, new
{
    schemaVersion = 1,
    scenarioId = options.ScenarioId,
    status = "passed",
    clientLanguage = "dotnet",
    workerLanguage = options.Worker,
    endpoint = options.Endpoint,
    topology = options.Topology,
    provider = options.Provider,
    publicationRef = publication.PublicationRef,
    executionId = submitted.ExecutionId,
    terminalStatus = result.Status.ToString(),
    evidence = new[] { "publish", "submit", "observe", "terminal-result", "public-execution-id" },
    recordedAtUtc = DateTimeOffset.UtcNow
});

static async Task<Multiplexed.AI.Sdk.Contracts.Observation.AiSdkExecutionObservation> WaitForTerminalAsync(
    AiSdkClient client,
    string executionId,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var observation = await client.ObserveExecutionAsync(executionId);
        if (observation.Status is AiSdkExecutionStatus.Completed or AiSdkExecutionStatus.Failed or AiSdkExecutionStatus.Cancelled)
        {
            return observation;
        }
        await Task.Delay(250);
    }
    throw new TimeoutException($"Execution '{executionId}' did not become terminal within {timeout}.");
}

internal sealed record Arguments(
    string Endpoint,
    string Worker,
    string EnvironmentRef,
    string ScenarioId,
    string Evidence,
    string? Token,
    string? AccessContext,
    string AccessContextHeader,
    string Topology,
    string Provider)
{
    internal static Arguments Parse(string[] values)
    {
        string? Read(string name)
        {
            var index = Array.IndexOf(values, name);
            return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
        }

        var worker = Read("--worker") ?? throw new ArgumentException("Missing --worker.");
        var scenario = Read("--scenario-id") ?? throw new ArgumentException("Missing --scenario-id.");
        var evidence = Read("--evidence") ?? throw new ArgumentException("Missing --evidence.");
        var manifestPath = Read("--manifest");
        if (manifestPath is not null)
        {
            var manifest = RuntimeManifest.Load(manifestPath);
            return new(
                Read("--endpoint") ?? manifest.Endpoint,
                worker,
                Read("--environment-ref") ?? manifest.EnvironmentRef(worker),
                scenario,
                evidence,
                Read("--token") ?? manifest.BearerToken,
                Read("--access-context") ?? manifest.AccessContext,
                manifest.AccessContextHeader,
                manifest.Topology,
                manifest.Provider);
        }

        return new(
            Read("--endpoint") ?? throw new ArgumentException("Missing --endpoint."),
            worker,
            Read("--environment-ref") ?? throw new ArgumentException("Missing --environment-ref."),
            scenario,
            evidence,
            Read("--token"),
            Read("--access-context"),
            Read("--access-context-header") ?? "X-Access-Context",
            Read("--topology") ?? "local",
            Read("--provider") ?? "ProcessHostPool");
    }
}

internal sealed record RuntimeManifest(
    string Endpoint,
    string BearerToken,
    string AccessContext,
    string AccessContextHeader,
    string Topology,
    string Provider,
    Dictionary<string, string> EnvironmentRefs)
{
    internal string EnvironmentRef(string language) =>
        EnvironmentRefs.TryGetValue(language, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Runtime manifest has no environment reference for '{language}'.");

    internal static RuntimeManifest Load(string path)
    {
        var document = JsonSerializer.Deserialize<RuntimeManifest>(
            File.ReadAllText(Path.GetFullPath(path)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return document ?? throw new InvalidOperationException("Runtime manifest is empty or invalid.");
    }
}

internal sealed record WorkerSource(string EntryPointPath, string EntryPointSymbol, byte[] Bytes)
{
    internal static WorkerSource Load(string worker)
    {
        var root = Environment.GetEnvironmentVariable("MATRIX_FIXTURE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) root = FindRepoRoot();
        return worker switch
        {
            "python" => FromText(root, FixturePath(root, "python-worker/main.py"), "main.py", "run"),
            "typescript" => FromText(root, FixturePath(root, "typescript-worker/main.ts"), "main.ts", "run"),
            "dotnet" => FromBytes(root, FixturePath(root, "dotnet-worker/Multiplexed.AI.Matrix.Worker.dll"), "functions.dll", "Multiplexed.AI.Matrix.Worker.Functions::Run"),
            _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, "Unsupported worker language.")
        };
    }

    private static string FixturePath(string root, string relative) =>
        Environment.GetEnvironmentVariable("MATRIX_FIXTURE_ROOT") is not null
            ? relative.Replace('/', Path.DirectorySeparatorChar)
            : Path.Combine("implementations", "matrix", "fixtures", relative.Replace('/', Path.DirectorySeparatorChar));

    private static WorkerSource FromText(string root, string relativePath, string logicalPath, string symbol) =>
        new(logicalPath, symbol, Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(root, relativePath))));

    private static WorkerSource FromBytes(string root, string relativePath, string logicalPath, string symbol) =>
        new(logicalPath, symbol, File.ReadAllBytes(Path.Combine(root, relativePath)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "implementations"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

internal static class Evidence
{
    internal static async Task WriteAsync(string path, object value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
