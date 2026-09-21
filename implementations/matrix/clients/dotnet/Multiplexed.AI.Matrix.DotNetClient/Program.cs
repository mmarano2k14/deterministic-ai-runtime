using System.Text;
using System.Text.Json;
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Sdk.Transport;

return await RunClientAsync(args);

static async Task<int> RunClientAsync(string[] args)
{
    var stage = "ARGUMENTS";
    try
    {
        var options = Arguments.Parse(args);
        if (string.Equals(options.Feature, "dependency-firewall", StringComparison.OrdinalIgnoreCase))
        {
            stage = "DEPENDENCY-FIREWALL";
            await RunDependencyFirewallAsync(options);
            return 0;
        }

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

        if (string.Equals(options.Feature, "cancellation", StringComparison.OrdinalIgnoreCase))
        {
            stage = "CANCELLATION";
            await RunCancellationAsync(client, options);
            return 0;
        }
        if (!string.IsNullOrWhiteSpace(options.Feature))
        {
            throw new ArgumentException($"Unsupported --feature '{options.Feature}'.");
        }

        Console.WriteLine($"[matrix-dotnet-client] START scenario={options.ScenarioId} worker={options.Worker} topology={options.Topology} runtimeProvider={options.RuntimeProvider}");
        stage = "SOURCE";
        var source = WorkerSource.Load(options.Worker);
        var marker = options.ScenarioId + "-marker";
        stage = "PUBLISH";
        Console.WriteLine("[matrix-dotnet-client] PUBLISH start");
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

        Console.WriteLine($"[matrix-dotnet-client] PUBLISH succeeded publicationRef={publication.PublicationRef}");
        stage = "SUBMIT";
        Console.WriteLine("[matrix-dotnet-client] SUBMIT start");
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

        Console.WriteLine($"[matrix-dotnet-client] SUBMIT succeeded executionId={submitted.ExecutionId} status={submitted.Status}");
        stage = "OBSERVE";
        Console.WriteLine($"[matrix-dotnet-client] OBSERVE waiting executionId={submitted.ExecutionId}");
        var observed = await WaitForTerminalAsync(client, submitted.ExecutionId, TimeSpan.FromSeconds(90));
        Console.WriteLine($"[matrix-dotnet-client] OBSERVE terminal executionId={submitted.ExecutionId} status={observed.Status}");
        stage = "RESULT";
        var result = await client.GetExecutionResultAsync(submitted.ExecutionId);
        if (result.Status != AiSdkExecutionStatus.Completed)
        {
            Console.Error.WriteLine("[matrix-dotnet-client] RESULT failure=" + JsonSerializer.Serialize(result.Failure));
            throw new InvalidOperationException($"Execution '{submitted.ExecutionId}' ended as '{result.Status}'.");
        }

        Console.WriteLine($"[matrix-dotnet-client] RESULT status={result.Status}");
        stage = "EVIDENCE";
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
            runtimeProvider = options.RuntimeProvider,
            workerExecutionProvider = options.WorkerExecutionProvider,
            publicationRef = publication.PublicationRef,
            executionId = submitted.ExecutionId,
            terminalStatus = result.Status.ToString(),
            evidence = new[] { "publish", "submit", "observe", "terminal-result", "public-execution-id" },
            recordedAtUtc = DateTimeOffset.UtcNow
        });
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"[matrix-dotnet-client] {stage} FAILED {exception.GetType().FullName}: {exception.Message}");
        if (exception is AiSdkException sdkException)
        {
            // Do not print credentials, request headers, the manifest or uploaded source.
            Console.Error.WriteLine("[matrix-dotnet-client] SDK error=" + JsonSerializer.Serialize(sdkException.Error));
        }
        Console.Error.WriteLine(exception.ToString());
        return 1;
    }
}

static async Task<AiSdkExecutionObservation> WaitForTerminalAsync(
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



static async Task RunDependencyFirewallAsync(Arguments options)
{
    var sdkAssembly = typeof(AiSdkClient).Assembly;
    var contractsAssembly = typeof(AiSdkPipelineDefinition).Assembly;

    static string[] RepositoryReferences(System.Reflection.Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name.StartsWith("Multiplexed.", StringComparison.Ordinal))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    var sdkRepositoryReferences = RepositoryReferences(sdkAssembly);
    var contractRepositoryReferences = RepositoryReferences(contractsAssembly);
    var publishedRepositoryAssemblies = Directory
        .EnumerateFiles(AppContext.BaseDirectory, "Multiplexed*.dll", SearchOption.TopDirectoryOnly)
        .Select(Path.GetFileNameWithoutExtension)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    var allowedSdkReferences = new HashSet<string>(StringComparer.Ordinal)
    {
        "Multiplexed.AI.Sdk.Contracts"
    };
    var allowedPublishedAssemblies = new HashSet<string>(StringComparer.Ordinal)
    {
        "Multiplexed.AI.Matrix.DotNetClient",
        "Multiplexed.AI.Sdk",
        "Multiplexed.AI.Sdk.Contracts"
    };

    var forbiddenRepositoryDependencies = sdkRepositoryReferences
        .Where(name => !allowedSdkReferences.Contains(name))
        .Concat(contractRepositoryReferences)
        .Concat(publishedRepositoryAssemblies.Where(name => !allowedPublishedAssemblies.Contains(name)))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    if (forbiddenRepositoryDependencies.Length != 0)
    {
        throw new InvalidOperationException(
            "External .NET SDK dependency firewall detected repository runtime/engine dependencies: " +
            string.Join(", ", forbiddenRepositoryDependencies));
    }

    await Evidence.WriteAsync(options.Evidence, new
    {
        schemaVersion = 1,
        scenarioId = options.ScenarioId,
        status = "passed",
        coverageTarget = "external-client-dependency-firewall",
        coverageValues = Array.Empty<string>(),
        clientLanguage = "dotnet",
        workerLanguage = (string?)null,
        topology = options.Topology,
        provider = options.Provider,
        runtimeProvider = options.RuntimeProvider,
        workerExecutionProvider = options.WorkerExecutionProvider,
        artifactKind = "published-dotnet-client",
        sdkRepositoryReferences,
        contractRepositoryReferences,
        publishedRepositoryAssemblies,
        forbiddenRepositoryDependencies,
        evidence = new[]
        {
            "sdk-assembly-reference-graph-inspected",
            "contracts-reference-graph-inspected",
            "published-client-bundle-inspected",
            "no-engine-runtime-dependency"
        },
        recordedAtUtc = DateTimeOffset.UtcNow
    });
}

static async Task RunCancellationAsync(AiSdkClient client, Arguments options)
{
    var source = WorkerSource.LoadCancellation(options.Worker);
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
            ["matrix.worker"] = options.Worker,
            ["matrix.feature"] = "cancellation"
        }
    });

    var active = await WaitForActiveStepAsync(client, submitted.ExecutionId, "work", TimeSpan.FromSeconds(30));
    var correlationId = "cancel-" + Guid.NewGuid().ToString("N");
    var cancellation = await client.CancelExecutionAsync(
        submitted.ExecutionId,
        new AiSdkExecutionCancellationRequest
        {
            Reason = "matrix-running-cancellation",
            CorrelationId = correlationId
        });

    if (!cancellation.CancellationRequested || cancellation.ExecutionId != submitted.ExecutionId)
    {
        throw new InvalidOperationException("Public cancellation operation did not acknowledge the submitted execution.");
    }
    if (!cancellation.RequestedAtUtc.HasValue || cancellation.CorrelationId != correlationId)
    {
        throw new InvalidOperationException("Public cancellation acknowledgement did not preserve durable request metadata.");
    }

    var terminal = await WaitForTerminalAsync(client, submitted.ExecutionId, TimeSpan.FromSeconds(45));
    var result = await client.GetExecutionResultAsync(submitted.ExecutionId);
    if (terminal.Status != AiSdkExecutionStatus.Cancelled || result.Status != AiSdkExecutionStatus.Cancelled)
    {
        throw new InvalidOperationException(
            $"Cancellation scenario ended as observation='{terminal.Status}', result='{result.Status}', expected 'Cancelled'.");
    }
    if (result.Output is not null || result.Failure is not null)
    {
        throw new InvalidOperationException("Cancelled public result unexpectedly exposed completed output or failure payload.");
    }

    var activeStep = active.Steps.FirstOrDefault(step => step.Name == "work");
    var terminalStep = terminal.Steps.FirstOrDefault(step => step.Name == "work");
    await Evidence.WriteAsync(options.Evidence, new
    {
        schemaVersion = 1,
        scenarioId = options.ScenarioId,
        status = "passed",
        coverageTarget = "cancellation",
        coverageValues = Array.Empty<string>(),
        cancellationMode = "running-cooperative",
        clientLanguage = "dotnet",
        workerLanguage = options.Worker,
        endpoint = options.Endpoint,
        topology = options.Topology,
        provider = options.Provider,
        runtimeProvider = options.RuntimeProvider,
        workerExecutionProvider = options.WorkerExecutionProvider,
        publicationRef = publication.PublicationRef,
        executionId = submitted.ExecutionId,
        activeStatusBeforeCancel = active.Status.ToString(),
        activeStepStatusBeforeCancel = activeStep?.Status.ToString(),
        cancellationRequested = cancellation.CancellationRequested,
        cancellationRequestedAtUtc = cancellation.RequestedAtUtc,
        cancellationCorrelationId = cancellation.CorrelationId,
        terminalStatus = result.Status.ToString(),
        terminalStepStatus = terminalStep?.Status.ToString(),
        evidence = new[]
        {
            "publish", "submit", "active-execution-observed", "sdk-execution-cancel",
            "durable-cancellation-acknowledged", "terminal-cancelled-observed", "terminal-result"
        },
        recordedAtUtc = terminal.UpdatedAtUtc
    });
}

static async Task<AiSdkExecutionObservation> WaitForActiveStepAsync(
    AiSdkClient client,
    string executionId,
    string stepName,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var observation = await client.ObserveExecutionAsync(executionId);
        if (observation.Status is AiSdkExecutionStatus.Completed or AiSdkExecutionStatus.Failed or AiSdkExecutionStatus.Cancelled)
        {
            throw new InvalidOperationException(
                $"Execution '{executionId}' became terminal as '{observation.Status}' before cancellation could be requested.");
        }
        var step = observation.Steps.FirstOrDefault(item => item.Name == stepName);
        if (step?.Status is AiSdkExecutionStepStatus.Running or AiSdkExecutionStepStatus.WaitingForExternal)
        {
            return observation;
        }
        await Task.Delay(100);
    }
    throw new TimeoutException($"Execution '{executionId}' did not expose active step '{stepName}' within {timeout}.");
}

internal sealed record Arguments(
    string Endpoint,
    string Worker,
    string EnvironmentRef,
    string? Feature,
    string ScenarioId,
    string Evidence,
    string? Token,
    string? AccessContext,
    string AccessContextHeader,
    string Topology,
    string Provider,
    string RuntimeProvider,
    string WorkerExecutionProvider)
{
    internal static Arguments Parse(string[] values)
    {
        string? Read(string name)
        {
            var index = Array.IndexOf(values, name);
            return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
        }

        var worker = Read("--worker") ?? throw new ArgumentException("Missing --worker.");
        var feature = Read("--feature");
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
                feature,
                scenario,
                evidence,
                Read("--token") ?? manifest.BearerToken,
                Read("--access-context") ?? manifest.AccessContext,
                manifest.AccessContextHeader,
                manifest.Topology,
                manifest.Provider,
                manifest.RuntimeProvider ?? manifest.Provider,
                manifest.WorkerExecutionProvider ?? "TrustedProcess");
        }

        return new(
            Read("--endpoint") ?? throw new ArgumentException("Missing --endpoint."),
            worker,
            Read("--environment-ref") ?? throw new ArgumentException("Missing --environment-ref."),
            feature,
            scenario,
            evidence,
            Read("--token"),
            Read("--access-context"),
            Read("--access-context-header") ?? "X-Access-Context",
            Read("--topology") ?? "local",
            Read("--provider") ?? "ProcessHostPool",
            Read("--runtime-provider") ?? "ProcessHostPool",
            Read("--worker-execution-provider") ?? "TrustedProcess");
    }
}

internal sealed record RuntimeManifest(
    string Endpoint,
    string BearerToken,
    string AccessContext,
    string AccessContextHeader,
    string Topology,
    string Provider,
    string? RuntimeProvider,
    string? WorkerExecutionProvider,
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
        var root = Environment.GetEnvironmentVariable("MATRIX_SAMPLE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) root = FindRepoRoot();
        return worker switch
        {
            "python" => FromText(root, SamplePath("python/functions.py"), "functions.py", "run"),
            "typescript" => FromText(root, SamplePath("typescript/functions.ts"), "functions.ts", "run"),
            "dotnet" => FromBytes(root, SamplePath("dotnet/Multiplexed.AI.Samples.PublishedFunctions.dll"), "functions.dll", "Multiplexed.AI.Samples.PublishedFunctions.Functions::Run"),
            _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, "Unsupported worker language.")
        };
    }

    internal static WorkerSource LoadCancellation(string worker)
    {
        var root = Environment.GetEnvironmentVariable("MATRIX_SAMPLE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) root = FindRepoRoot();
        return worker switch
        {
            "dotnet" => FromBytes(root, SamplePath("dotnet/Multiplexed.AI.Samples.PublishedFunctions.dll"), "functions.dll", "Multiplexed.AI.Samples.PublishedFunctions.Functions::PinStable"),
            "typescript" => new("main.ts", "run", Encoding.UTF8.GetBytes(
                "export async function run(inputs: unknown, context: unknown) { await new Promise(resolve => setTimeout(resolve, 8000)); return { success: true, payload: { cancellationSample: true } }; }\n")),
            "python" => new("main.py", "run", Encoding.UTF8.GetBytes(
                "import time\ndef run(inputs, context):\n    time.sleep(8)\n    return {'success': True, 'payload': {'cancellationSample': True}}\n")),
            _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, "Unsupported worker language.")
        };
    }

    private static string SamplePath(string relative) =>
        Environment.GetEnvironmentVariable("MATRIX_SAMPLE_ROOT") is not null
            ? relative.Replace('/', Path.DirectorySeparatorChar)
            : Path.Combine("implementations", "sdk", "samples", "published-functions", relative.Replace('/', Path.DirectorySeparatorChar));

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
        throw new DirectoryNotFoundException("Repository root could not be located.");
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
