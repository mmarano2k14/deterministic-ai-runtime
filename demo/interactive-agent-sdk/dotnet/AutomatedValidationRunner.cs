using System.Text.Json;
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Replay;

namespace Multiplexed.AI.Demo.InteractiveAgent.DotNet;

internal static class AutomatedValidationRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);
    private static readonly string[] RootStepNames =
    {
        "plan", "delegate-analysis", "await-review", "final-answer", "publish-result"
    };

    internal static bool IsEnabled() => Flag("AI_DEMO_AUTOMATED_VALIDATION");

    internal static async Task<int> RunAsync(
        IAiSdkClient client,
        DemoConfiguration config)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var runtimeProvider = Required("AI_VALIDATION_RUNTIME_PROVIDER");
        var runtimeTransport = Required("AI_VALIDATION_RUNTIME_TRANSPORT");
        var evidencePath = Required("AI_DEMO_EVIDENCE_PATH");
        var prompt = Optional("AI_DEMO_VALIDATION_PROMPT")
            ?? "Validate the interactive agent topology through the public SDK boundary.";
        var feedback = Optional("AI_DEMO_VALIDATION_FEEDBACK") ?? "topology-validation";
        var timeoutSeconds = int.TryParse(
            Optional("AI_DEMO_VALIDATION_TIMEOUT_SECONDS"),
            out var parsedTimeout)
            ? parsedTimeout
            : 20 * 60;

        var publication = await client.PublishPipelineAsync(
            InteractiveAgentPipeline.CreatePublication(config.OpenAiModel));
        var submission = await client.SubmitExecutionAsync(
            InteractiveAgentPipeline.CreateSubmission(publication.PublicationRef, prompt));
        var executionId = submission.ExecutionId;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(timeoutSeconds, 1));

        var waitingBoundaryReached = false;
        string? executionIdAfterHumanInput = null;
        bool inputAccepted = false;
        AiSdkExecutionObservation? finalObservation = null;

        while (true)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Automated validation timed out after {timeoutSeconds}s for {executionId}.");
            }

            var observation = await client.ObserveExecutionAsync(executionId);
            finalObservation = observation;
            var waitingStep = observation.Steps.FirstOrDefault(
                step => string.Equals(
                    step.Name,
                    InteractiveAgentPipeline.WaitingStepName,
                    StringComparison.Ordinal));

            if (!inputAccepted &&
                waitingStep is not null &&
                string.Equals(
                    waitingStep.Status.ToString(),
                    "WaitingForExternal",
                    StringComparison.Ordinal))
            {
                waitingBoundaryReached = true;
                var inputResponse = await client.SubmitExecutionInputAsync(
                    executionId,
                    new AiSdkExecutionInputSubmissionRequest
                    {
                        WaitingKey = InteractiveAgentPipeline.WaitingKey,
                        WaitingStepName = InteractiveAgentPipeline.WaitingStepName,
                        Reason = "interactive-agent-topology-validation",
                        CorrelationId = $"interactive-agent-validation-input-{Guid.NewGuid():N}",
                        Input = JsonSerializer.SerializeToElement(
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["approved"] = true,
                                ["feedback"] = feedback
                            })
                    });

                inputAccepted = inputResponse.Accepted;
                executionIdAfterHumanInput = inputResponse.ExecutionId;
                if (!inputAccepted)
                {
                    throw new InvalidOperationException("Automated human input was not accepted.");
                }
                if (!string.Equals(
                    executionIdAfterHumanInput,
                    executionId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Human input changed the durable ExecutionId.");
                }
            }

            if (IsTerminal(observation.Status.ToString()))
            {
                break;
            }

            await Task.Delay(PollInterval);
        }

        if (finalObservation is null || !waitingBoundaryReached || !inputAccepted)
        {
            throw new InvalidOperationException(
                "Human-review waiting boundary was not reached.");
        }

        var result = await client.GetExecutionResultAsync(executionId);
        var replay = await client.ReplayExecutionAsync(
            executionId,
            new AiSdkExecutionReplayRequest
            {
                StrictDeterminism = true,
                IncludeDiagnostics = true,
                Reason = "interactive-agent-topology-validation",
                CorrelationId = $"interactive-agent-validation-replay-{Guid.NewGuid():N}"
            });

        JsonElement? published = null;
        if (result.Output is JsonElement output)
        {
            published = PublishedResult(output);
        }

        var provider = published is JsonElement p
            ? ReadString(p, "providerKey") ?? "openai"
            : "openai";
        var model = published is JsonElement m
            ? ReadString(m, "model") ?? config.OpenAiModel
            : config.OpenAiModel;
        var requestId = published is JsonElement r ? FindRequestId(r) : null;
        var rootSteps = finalObservation.Steps
            .Where(step => RootStepNames.Contains(step.Name, StringComparer.Ordinal))
            .ToDictionary(
                step => step.Name,
                step => step.Status.ToString(),
                StringComparer.Ordinal);

        var evidence = new
        {
            contractVersion = 1,
            scenarioId = "interactive-agent-sdk-canonical-v1",
            sdkClient = "dotnet",
            runtimeProvider,
            runtimeTransport,
            publication = new
            {
                accepted = true,
                publicationRef = publication.PublicationRef,
                pipelineName = publication.PipelineName,
                pipelineVersion = publication.PipelineVersion
            },
            execution = new
            {
                executionId,
                executionIdAfterHumanInput,
                sameExecutionIdAfterHumanInput = string.Equals(
                    executionIdAfterHumanInput,
                    executionId,
                    StringComparison.Ordinal),
                status = result.Status.ToString()
            },
            humanReview = new
            {
                waitingBoundaryReached,
                waitingStepName = InteractiveAgentPipeline.WaitingStepName,
                waitingKey = InteractiveAgentPipeline.WaitingKey,
                accepted = inputAccepted,
                approved = true,
                feedback
            },
            provider = new
            {
                name = provider,
                model,
                responsePayloadPresent = published is not null
            },
            rootSteps,
            result = new
            {
                available = result.Output is not null,
                requestId
            },
            replay = new
            {
                succeeded = replay.Succeeded,
                deterministic = replay.Deterministic == true,
                executionIdUnchanged = string.Equals(
                    result.ExecutionId,
                    executionId,
                    StringComparison.Ordinal)
            },
            timing = new
            {
                startedAtUtc = startedAtUtc.ToString("O"),
                completedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            },
            diagnostics = new
            {
                replayMessage = replay.Message,
                replayFailureReason = replay.FailureReason,
                terminalCompletedAtUtc = result.CompletedAtUtc.ToString("O")
            }
        };

        var directory = Path.GetDirectoryName(evidencePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(
            evidencePath,
            JsonSerializer.Serialize(
                evidence,
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        var passed = result.Status.ToString() == "Completed" &&
            inputAccepted &&
            string.Equals(executionIdAfterHumanInput, executionId, StringComparison.Ordinal) &&
            RootStepNames.All(name =>
                rootSteps.TryGetValue(name, out var status) && status == "Completed") &&
            provider == "openai" &&
            !string.IsNullOrWhiteSpace(model) &&
            published is not null &&
            result.Output is not null &&
            replay.Succeeded &&
            replay.Deterministic == true &&
            string.Equals(result.ExecutionId, executionId, StringComparison.Ordinal);

        return passed ? 0 : 1;
    }

    private static bool IsTerminal(string status) =>
        status is "Completed" or "Failed" or "Cancelled";

    private static JsonElement PublishedResult(JsonElement output) =>
        output.ValueKind == JsonValueKind.Object &&
        output.TryGetProperty("result", out var result)
            ? result
            : output;

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? FindRequestId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("requestId", out var requestId) &&
                requestId.ValueKind == JsonValueKind.String)
            {
                return requestId.GetString();
            }
            foreach (var property in value.EnumerateObject())
            {
                var found = FindRequestId(property.Value);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var found = FindRequestId(item);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static bool Flag(string name)
    {
        var value = Optional(name);
        return value is not null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static string Required(string name) =>
        Optional(name) ?? throw new InvalidOperationException(
            $"Missing required automated-validation configuration '{name}'.");

    private static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value
            : null;
}
