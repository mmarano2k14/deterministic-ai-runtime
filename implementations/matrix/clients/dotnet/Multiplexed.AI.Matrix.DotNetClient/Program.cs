using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Sdk.Contracts.Replay;
using Multiplexed.AI.Sdk.Contracts.Watch;
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
        if (string.Equals(options.Feature, "watch", StringComparison.OrdinalIgnoreCase))
        {
            stage = "WATCH";
            await RunWatchAsync(client, options);
            return 0;
        }
        if (string.Equals(options.Feature, "control", StringComparison.OrdinalIgnoreCase))
        {
            stage = "CONTROL";
            await RunControlAsync(client, options);
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

static async Task RunWatchAsync(AiSdkClient client, Arguments options)
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
            ["matrix.feature"] = "watch"
        }
    });

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var sawSnapshot = false;
    var sawEvent = false;
    var sawResync = false;
    var eventTypes = new List<string>();
    var sequences = new List<long>();

    await foreach (var item in client.WatchExecutionAsync(
        new AiSdkExecutionWatchRequest
        {
            ExecutionId = submitted.ExecutionId,
            IncludeInitialSnapshot = true
        },
        timeout.Token))
    {
        if (item.ExecutionId != submitted.ExecutionId)
        {
            throw new InvalidOperationException("Watch returned an event for a different execution.");
        }

        if (item.Sequence is { } sequence)
        {
            sequences.Add(sequence);
        }

        if (item.Kind == AiSdkExecutionWatchEventKind.Snapshot)
        {
            sawSnapshot = true;
        }
        else if (item.Kind == AiSdkExecutionWatchEventKind.Event)
        {
            sawEvent = true;
            if (!string.IsNullOrWhiteSpace(item.EventType))
            {
                eventTypes.Add(item.EventType);
            }
        }
        else if (item.Kind == AiSdkExecutionWatchEventKind.ResyncRequired)
        {
            sawResync = true;
        }
    }

    if (!sawSnapshot)
    {
        throw new InvalidOperationException("Watch E2E did not receive the authoritative initial snapshot.");
    }
    if (!sawEvent)
    {
        throw new InvalidOperationException("Watch E2E did not receive any incremental public event after the snapshot.");
    }
    if (sawResync)
    {
        throw new InvalidOperationException("Nominal Watch E2E unexpectedly required resynchronization.");
    }
    if (sequences.Count < 2 || sequences.Zip(sequences.Skip(1), (left, right) => right > left).Any(increasing => !increasing))
    {
        throw new InvalidOperationException("Watch E2E did not observe a strictly increasing public sequence.");
    }

    var result = await client.GetExecutionResultAsync(submitted.ExecutionId);
    if (result.Status != AiSdkExecutionStatus.Completed)
    {
        throw new InvalidOperationException(
            $"Watch E2E execution '{submitted.ExecutionId}' ended as '{result.Status}', expected 'Completed'.");
    }

    await Evidence.WriteAsync(options.Evidence, new
    {
        schemaVersion = 1,
        scenarioId = options.ScenarioId,
        status = "passed",
        coverageTarget = "execution-watch-e2e",
        coverageValues = Array.Empty<string>(),
        clientLanguage = "dotnet",
        workerLanguage = options.Worker,
        endpoint = options.Endpoint,
        topology = options.Topology,
        provider = options.Provider,
        runtimeProvider = options.RuntimeProvider,
        workerExecutionProvider = options.WorkerExecutionProvider,
        publicationRef = publication.PublicationRef,
        executionId = submitted.ExecutionId,
        initialSnapshotObserved = sawSnapshot,
        incrementalEventObserved = sawEvent,
        resyncObserved = sawResync,
        publicSequences = sequences,
        publicEventTypes = eventTypes,
        terminalStatus = result.Status.ToString(),
        evidence = new[]
        {
            "publish", "submit", "sdk-execution-watch", "mcp-http-public-boundary",
            "initial-snapshot", "ordered-incremental-event", "terminal-watch-convergence",
            "terminal-result", "public-execution-id"
        },
        recordedAtUtc = DateTimeOffset.UtcNow
    });
}

static async Task RunControlAsync(AiSdkClient client, Arguments options)
{
    Console.WriteLine($"[matrix-dotnet-client] CONTROL START scenario={options.ScenarioId} worker={options.Worker}");

    // Pause/resume flow: one slow first step, then one dependent fast step.
    var pausePublication = await PublishControlPipelineAsync(client, options, "pause");
    var pauseExecution = await SubmitControlExecutionAsync(client, options, pausePublication.PublicationRef, "pause");
    using var pauseWatchCts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
    var pauseFirstStepActive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var pauseWatchTask = CollectControlWatchAsync(client, pauseExecution.ExecutionId, pauseWatchCts.Token, pauseFirstStepActive);
    await WaitForWatchActiveStepAsync(pauseFirstStepActive.Task, pauseExecution.ExecutionId, "first", TimeSpan.FromSeconds(30));

    var pause = await client.PauseExecutionAsync(
        pauseExecution.ExecutionId,
        new AiSdkExecutionControlRequest { Reason = "matrix-control-e2e-pause" });
    if (!pause.Accepted || pause.ExecutionId != pauseExecution.ExecutionId)
        throw new InvalidOperationException("Public pause operation was not accepted for the submitted execution.");
    if (pause.State is null ||
        (pause.State.Status != AiSdkExecutionControlStatus.Pausing &&
         pause.State.Status != AiSdkExecutionControlStatus.Paused))
    {
        throw new InvalidOperationException(
            $"Public pause did not expose a durable Pausing/Paused control state. Status='{pause.State?.Status.ToString() ?? "null"}'.");
    }

    var pauseGateVerified = await WaitForPauseGateAsync(client, pauseExecution.ExecutionId, TimeSpan.FromSeconds(20));
    if (!pauseGateVerified)
        throw new InvalidOperationException("Pause did not prevent the dependent second step from advancing after the first step drained.");

    var resume = await client.ResumeExecutionAsync(
        pauseExecution.ExecutionId,
        new AiSdkExecutionControlRequest { Reason = "matrix-control-e2e-resume" });
    if (!resume.Accepted || resume.ExecutionId != pauseExecution.ExecutionId)
        throw new InvalidOperationException("Public resume operation was not accepted for the paused execution.");

    var pauseResult = await WaitForCompletedResultAsync(client, pauseExecution.ExecutionId, TimeSpan.FromSeconds(45));
    var pauseWatch = await pauseWatchTask;
    if (pauseWatch.ResyncObserved)
        throw new InvalidOperationException("Pause/resume control flow unexpectedly forced Watch resynchronization.");

    // Human/external-input flow. The matrix-only setup endpoint creates the waiting precondition through
    // the production IAiExecutionControlService; the actual input submission is exclusively through the public SDK.
    var inputPublication = await PublishControlPipelineAsync(client, options, "input");
    var inputExecution = await SubmitControlExecutionAsync(client, options, inputPublication.PublicationRef, "input");
    using var inputWatchCts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
    var inputFirstStepActive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var inputWatchTask = CollectControlWatchAsync(client, inputExecution.ExecutionId, inputWatchCts.Token, inputFirstStepActive);
    await WaitForWatchActiveStepAsync(inputFirstStepActive.Task, inputExecution.ExecutionId, "first", TimeSpan.FromSeconds(30));

    var waitingKey = $"approval:{options.ScenarioId}:{Guid.NewGuid():N}";
    await SeedWaitingForInputAsync(options.Endpoint, inputExecution.ExecutionId, waitingKey, "second");
    var inputGateVerified = await WaitForPauseGateAsync(client, inputExecution.ExecutionId, TimeSpan.FromSeconds(20));
    if (!inputGateVerified)
        throw new InvalidOperationException("Waiting-for-input state did not prevent the dependent second step from advancing.");

    var input = await client.SubmitExecutionInputAsync(
        inputExecution.ExecutionId,
        new AiSdkExecutionInputSubmissionRequest
        {
            WaitingKey = waitingKey,
            WaitingStepName = "second",
            Reason = "matrix-control-e2e-approval",
            Input = JsonSerializer.SerializeToElement(new { approved = true, source = "matrix" })
        });
    if (!input.Accepted || input.ExecutionId != inputExecution.ExecutionId || input.State?.InputReceivedAtUtc is null)
        throw new InvalidOperationException("Public human-input submission was not durably acknowledged.");

    var inputResult = await WaitForCompletedResultAsync(client, inputExecution.ExecutionId, TimeSpan.FromSeconds(45));
    var inputWatch = await inputWatchTask;
    if (inputWatch.ResyncObserved)
        throw new InvalidOperationException("Human-input control flow unexpectedly forced Watch resynchronization.");

    // Replay flow uses a fresh normal completion so replay validation is not coupled to control interventions.
    var replaySource = WorkerSource.Load(options.Worker);
    var replayPublication = await client.PublishPipelineAsync(new AiSdkPipelinePublicationRequest
    {
        Definition = new AiSdkPipelineDefinition
        {
            Name = $"matrix-{options.ScenarioId}-replay",
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
                    Input = new Dictionary<string, JsonElement> { ["marker"] = JsonSerializer.SerializeToElement(options.ScenarioId + "-replay") }
                }
            ]
        },
        Functions =
        [
            UploadForStep(replaySource, options.EnvironmentRef, "work")
        ]
    });
    var replayExecution = await SubmitControlExecutionAsync(client, options, replayPublication.PublicationRef, "replay");
    await WaitForCompletedResultAsync(client, replayExecution.ExecutionId, TimeSpan.FromSeconds(45));
    var replay = await client.ReplayExecutionAsync(
        replayExecution.ExecutionId,
        new AiSdkExecutionReplayRequest { IncludeDiagnostics = true, Reason = "matrix-control-e2e-replay" });
    if (!replay.Succeeded || replay.Deterministic == false)
        throw new InvalidOperationException($"Public replay validation failed. Message='{replay.Message}', FailureReason='{replay.FailureReason}'.");

    await Evidence.WriteAsync(options.Evidence, new
    {
        schemaVersion = 1,
        scenarioId = options.ScenarioId,
        status = "passed",
        coverageTarget = "execution-control-replay-e2e",
        clientLanguage = "dotnet",
        workerLanguage = options.Worker,
        endpoint = options.Endpoint,
        topology = options.Topology,
        provider = options.Provider,
        runtimeProvider = options.RuntimeProvider,
        workerExecutionProvider = options.WorkerExecutionProvider,
        pauseExecutionId = pauseExecution.ExecutionId,
        pauseAccepted = pause.Accepted,
        pauseGateVerified,
        resumeAccepted = resume.Accepted,
        pauseResumeTerminalStatus = pauseResult.Status.ToString(),
        inputExecutionId = inputExecution.ExecutionId,
        inputWaitSeeded = true,
        inputAccepted = input.Accepted,
        inputGateVerified,
        inputTerminalStatus = inputResult.Status.ToString(),
        replayExecutionId = replayExecution.ExecutionId,
        replaySucceeded = replay.Succeeded,
        replayDeterministic = replay.Deterministic,
        watchResyncObserved = pauseWatch.ResyncObserved || inputWatch.ResyncObserved,
        watchSnapshotsObserved = pauseWatch.SnapshotObserved && inputWatch.SnapshotObserved,
        watchIncrementalEventsObserved = pauseWatch.EventObserved && inputWatch.EventObserved,
        evidence = new[]
        {
            "publish", "submit", "sdk-execution-watch", "sdk-execution-pause",
            "pause-gated-next-step", "sdk-execution-resume", "resume-terminal-convergence",
            "matrix-wait-input-production-authority", "sdk-execution-input-submit",
            "input-terminal-convergence", "sdk-execution-replay", "replay-validation-succeeded",
            "mcp-http-public-boundary"
        },
        recordedAtUtc = DateTimeOffset.UtcNow
    });
}

static async Task<AiSdkPipelinePublicationResponse> PublishControlPipelineAsync(
    AiSdkClient client,
    Arguments options,
    string suffix)
{
    var slow = WorkerSource.LoadControlSlow(options.Worker);
    var fast = WorkerSource.LoadControlFast(options.Worker);
    return await client.PublishPipelineAsync(new AiSdkPipelinePublicationRequest
    {
        Definition = new AiSdkPipelineDefinition
        {
            Name = $"matrix-{options.ScenarioId}-{suffix}",
            Version = "1",
            ExecutionLanguage = options.Worker,
            ExecutionMode = AiSdkExecutionMode.Dag,
            Steps =
            [
                new AiSdkPipelineStepDefinition
                {
                    Name = "first", StepKey = "custom", Order = 0, ExecutionLanguage = options.Worker,
                    Invocation = new AiSdkInvocationDefinition { Kind = AiSdkInvocationKind.Custom },
                    Input = new Dictionary<string, JsonElement> { ["marker"] = JsonSerializer.SerializeToElement(options.ScenarioId + "-first") }
                },
                new AiSdkPipelineStepDefinition
                {
                    Name = "second", StepKey = "custom", Order = 1, ExecutionLanguage = options.Worker,
                    DependsOn = ["first"],
                    Invocation = new AiSdkInvocationDefinition { Kind = AiSdkInvocationKind.Custom },
                    Input = new Dictionary<string, JsonElement> { ["marker"] = JsonSerializer.SerializeToElement(options.ScenarioId + "-second") }
                }
            ]
        },
        Functions =
        [
            UploadForStep(slow, options.EnvironmentRef, "first"),
            UploadForStep(fast, options.EnvironmentRef, "second")
        ]
    });
}

static AiSdkPublicationFunctionUpload UploadForStep(WorkerSource source, string environmentRef, string stepName) =>
    new()
    {
        Site = new AiSdkPublicationCallSite { Kind = AiSdkPublicationFunctionKind.Step, StepName = stepName },
        EnvironmentRef = environmentRef,
        EntryPointPath = source.EntryPointPath,
        EntryPointSymbol = source.EntryPointSymbol,
        Sources = [new AiSdkPublicationFileUpload { Path = source.EntryPointPath, ContentBase64 = Convert.ToBase64String(source.Bytes) }]
    };

static Task<AiSdkExecutionSubmissionResponse> SubmitControlExecutionAsync(
    AiSdkClient client,
    Arguments options,
    string publicationRef,
    string suffix) =>
    client.SubmitExecutionAsync(new AiSdkExecutionSubmissionRequest
    {
        PublicationRef = publicationRef,
        IdempotencyKey = $"{options.ScenarioId}-{suffix}-{Guid.NewGuid():N}",
        Input = JsonSerializer.SerializeToElement(new { scenario = options.ScenarioId, phase = suffix }),
        Metadata = new Dictionary<string, string>
        {
            ["matrix.scenario"] = options.ScenarioId,
            ["matrix.client"] = "dotnet",
            ["matrix.worker"] = options.Worker,
            ["matrix.feature"] = "control",
            ["matrix.phase"] = suffix
        }
    });

static async Task<bool> WaitForPauseGateAsync(AiSdkClient client, string executionId, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    AiSdkExecutionStatus? lastExecutionStatus = null;
    AiSdkExecutionStepStatus? lastFirstStatus = null;
    AiSdkExecutionStepStatus? lastSecondStatus = null;

    while (DateTimeOffset.UtcNow < deadline)
    {
        var observation = await client.ObserveExecutionAsync(executionId);
        var first = observation.Steps.FirstOrDefault(step => step.Name == "first");
        var second = observation.Steps.FirstOrDefault(step => step.Name == "second");

        lastExecutionStatus = observation.Status;
        lastFirstStatus = first?.Status;
        lastSecondStatus = second?.Status;

        if (second?.Status is AiSdkExecutionStepStatus.Running or AiSdkExecutionStepStatus.Completed or AiSdkExecutionStepStatus.Failed)
        {
            Console.WriteLine(
                $"[matrix-dotnet-client] PAUSE GATE FAILED second advanced. ExecutionStatus='{observation.Status}', FirstStatus='{first?.Status.ToString() ?? "missing"}', SecondStatus='{second.Status}'.");
            return false;
        }

        if (observation.Status is AiSdkExecutionStatus.Completed or AiSdkExecutionStatus.Failed or AiSdkExecutionStatus.Cancelled)
        {
            Console.WriteLine(
                $"[matrix-dotnet-client] PAUSE GATE FAILED execution became terminal. ExecutionStatus='{observation.Status}', FirstStatus='{first?.Status.ToString() ?? "missing"}', SecondStatus='{second?.Status.ToString() ?? "missing"}'.");
            return false;
        }

        // Published custom functions use the durable invocation adapter. The DAG step starts,
        // parks as WaitingForExternal, and the completed invocation continuation later makes
        // the same step Ready again. While paused, that Ready continuation must NOT be claimed.
        // Therefore Ready (not Completed) is the positive proof that the external work returned
        // and execution control successfully fenced the continuation and all downstream work.
        if (first?.Status == AiSdkExecutionStepStatus.Ready)
        {
            await Task.Delay(750);
            var confirm = await client.ObserveExecutionAsync(executionId);
            var confirmFirst = confirm.Steps.FirstOrDefault(step => step.Name == "first");
            var confirmSecond = confirm.Steps.FirstOrDefault(step => step.Name == "second");

            var gated =
                confirm.Status is not (AiSdkExecutionStatus.Completed or AiSdkExecutionStatus.Failed or AiSdkExecutionStatus.Cancelled) &&
                confirmFirst?.Status == AiSdkExecutionStepStatus.Ready &&
                confirmSecond?.Status is not (AiSdkExecutionStepStatus.Running or AiSdkExecutionStepStatus.Completed or AiSdkExecutionStepStatus.Failed);

            Console.WriteLine(
                $"[matrix-dotnet-client] PAUSE GATE PROOF executionStatus='{confirm.Status}' firstStatus='{confirmFirst?.Status.ToString() ?? "missing"}' secondStatus='{confirmSecond?.Status.ToString() ?? "missing"}' gated='{gated}'.");
            return gated;
        }

        if (first?.Status is AiSdkExecutionStepStatus.Completed or AiSdkExecutionStepStatus.Failed)
        {
            Console.WriteLine(
                $"[matrix-dotnet-client] PAUSE GATE FAILED first continuation advanced while paused. ExecutionStatus='{observation.Status}', FirstStatus='{first.Status}', SecondStatus='{second?.Status.ToString() ?? "missing"}'.");
            return false;
        }

        await Task.Delay(100);
    }

    Console.WriteLine(
        $"[matrix-dotnet-client] PAUSE GATE TIMEOUT executionId='{executionId}' ExecutionStatus='{lastExecutionStatus?.ToString() ?? "unknown"}' FirstStatus='{lastFirstStatus?.ToString() ?? "missing"}' SecondStatus='{lastSecondStatus?.ToString() ?? "missing"}'.");
    return false;
}

static async Task SeedWaitingForInputAsync(string publicEndpoint, string executionId, string waitingKey, string waitingStepName)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var endpoint = new Uri(new Uri(publicEndpoint), $"/matrix/execution-control/{Uri.EscapeDataString(executionId)}/wait-for-input");
    using var response = await http.PostAsJsonAsync(endpoint, new
    {
        waitingKey,
        waitingStepName,
        reason = "matrix-control-e2e-await-approval"
    });
    response.EnsureSuccessStatusCode();
}

static async Task<AiSdkExecutionResult> WaitForCompletedResultAsync(AiSdkClient client, string executionId, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var observation = await client.ObserveExecutionAsync(executionId);
        if (observation.Status == AiSdkExecutionStatus.Completed)
        {
            var result = await client.GetExecutionResultAsync(executionId);
            if (result.Status != AiSdkExecutionStatus.Completed)
                throw new InvalidOperationException($"Execution '{executionId}' observation completed but result was '{result.Status}'.");
            return result;
        }
        if (observation.Status is AiSdkExecutionStatus.Failed or AiSdkExecutionStatus.Cancelled)
            throw new InvalidOperationException($"Execution '{executionId}' ended as '{observation.Status}'.");
        await Task.Delay(150);
    }
    throw new TimeoutException($"Execution '{executionId}' did not complete within {timeout}.");
}

static async Task<ControlWatchEvidence> CollectControlWatchAsync(
    AiSdkClient client,
    string executionId,
    CancellationToken cancellationToken,
    TaskCompletionSource<bool>? firstStepActive = null)
{
    var snapshot = false;
    var evt = false;
    var resync = false;
    try
    {
        await foreach (var item in client.WatchExecutionAsync(
            new AiSdkExecutionWatchRequest { ExecutionId = executionId, IncludeInitialSnapshot = true },
            cancellationToken))
        {
            snapshot |= item.Kind == AiSdkExecutionWatchEventKind.Snapshot;
            evt |= item.Kind == AiSdkExecutionWatchEventKind.Event;
            resync |= item.Kind == AiSdkExecutionWatchEventKind.ResyncRequired;
            if (firstStepActive is not null && WatchItemShowsActiveStep(item, "first"))
            {
                firstStepActive.TrySetResult(true);
            }
        }
    }
    catch (Exception ex)
    {
        firstStepActive?.TrySetException(ex);
        throw;
    }
    finally
    {
        if (firstStepActive is not null && !firstStepActive.Task.IsCompleted)
        {
            firstStepActive.TrySetException(new InvalidOperationException(
                $"Execution '{executionId}' Watch ended before step 'first' became active."));
        }
    }
    return new ControlWatchEvidence(snapshot, evt, resync);
}

static bool WatchItemShowsActiveStep(AiSdkExecutionWatchEvent item, string stepName)
{
    if (item.Snapshot?.Steps.Any(step =>
            string.Equals(step.Name, stepName, StringComparison.Ordinal) &&
            step.Status is AiSdkExecutionStepStatus.Running or AiSdkExecutionStepStatus.WaitingForExternal) == true)
    {
        return true;
    }

    if (item.Kind != AiSdkExecutionWatchEventKind.Event ||
        item.Channel != AiSdkExecutionWatchChannel.Steps ||
        item.Payload is not JsonElement payload ||
        payload.ValueKind != JsonValueKind.Object ||
        !payload.TryGetProperty("name", out var name) ||
        !string.Equals(name.GetString(), stepName, StringComparison.Ordinal) ||
        !payload.TryGetProperty("status", out var status) ||
        status.ValueKind != JsonValueKind.String)
    {
        return false;
    }

    return status.GetString() is nameof(AiSdkExecutionStepStatus.Running)
        or nameof(AiSdkExecutionStepStatus.WaitingForExternal);
}

static async Task WaitForWatchActiveStepAsync(
    Task<bool> activeStep,
    string executionId,
    string stepName,
    TimeSpan timeout)
{
    using var timeoutCts = new CancellationTokenSource(timeout);
    try
    {
        await activeStep.WaitAsync(timeoutCts.Token);
    }
    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
    {
        throw new TimeoutException(
            $"Execution '{executionId}' Watch did not expose active step '{stepName}' within {timeout}.");
    }
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

internal sealed record ControlWatchEvidence(bool SnapshotObserved, bool EventObserved, bool ResyncObserved);

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

    internal static WorkerSource LoadControlSlow(string worker)
    {
        var root = Environment.GetEnvironmentVariable("MATRIX_SAMPLE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) root = FindRepoRoot();
        return worker switch
        {
            "dotnet" => FromBytes(root, SamplePath("dotnet/Multiplexed.AI.Samples.PublishedFunctions.dll"), "control-slow.dll", "Multiplexed.AI.Samples.PublishedFunctions.Functions::PinStable"),
            "typescript" => new("control-slow.ts", "run", Encoding.UTF8.GetBytes(
                "export async function run(inputs: unknown, context: unknown) { await new Promise(resolve => setTimeout(resolve, 8000)); return { success: true, payload: { marker: (inputs as any)?.marker ?? null, phase: 'slow' } }; }\n")),
            "python" => new("control_slow.py", "run", Encoding.UTF8.GetBytes(
                "import time\ndef run(inputs, context):\n    time.sleep(8)\n    return {'success': True, 'payload': {'marker': inputs.get('marker'), 'phase': 'slow'}}\n")),
            _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, "Unsupported worker language.")
        };
    }

    internal static WorkerSource LoadControlFast(string worker)
    {
        var root = Environment.GetEnvironmentVariable("MATRIX_SAMPLE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) root = FindRepoRoot();
        return worker switch
        {
            "dotnet" => FromBytes(root, SamplePath("dotnet/Multiplexed.AI.Samples.PublishedFunctions.dll"), "control-fast.dll", "Multiplexed.AI.Samples.PublishedFunctions.Functions::Run"),
            "typescript" => new("control-fast.ts", "run", Encoding.UTF8.GetBytes(
                "export function run(inputs: unknown, context: unknown) { return { success: true, payload: { marker: (inputs as any)?.marker ?? null, phase: 'fast' } }; }\n")),
            "python" => new("control_fast.py", "run", Encoding.UTF8.GetBytes(
                "def run(inputs, context):\n    return {'success': True, 'payload': {'marker': inputs.get('marker'), 'phase': 'fast'}}\n")),
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
