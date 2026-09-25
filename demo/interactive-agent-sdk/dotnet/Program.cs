using System.Text.Json;
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Transport;

namespace Multiplexed.AI.Demo.InteractiveAgent.DotNet;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            var config = DemoConfiguration.Load();

            if (IsSmokeMode())
            {
                Console.WriteLine("Interactive Agent SDK Demo");
                Console.WriteLine("SDK: .NET");
                Console.WriteLine($"Runtime endpoint: {config.Endpoint}");
                Console.WriteLine($"OpenAI model configured: {!string.IsNullOrWhiteSpace(config.OpenAiModel)}");
                Console.WriteLine("External SDK consumer initialized.");
                Console.WriteLine("Smoke mode: no authentication/bootstrap/runtime request was sent.");
                return 0;
            }

            PrintHeader(config);

            IAiSdkClient client = await CreateClientAsync(config);

            Console.Write("User request: ");
            var userPrompt = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                Console.Error.WriteLine("A non-empty user request is required.");
                return 2;
            }

            Console.WriteLine();
            if (config.Verbose)
            {
                Console.WriteLine("SDK command: sdk.publish_pipeline");
            }
            else
            {
                Console.WriteLine("[>] Publishing immutable pipeline");
            }

            var publication = await client.PublishPipelineAsync(
                InteractiveAgentPipeline.CreatePublication(config.OpenAiModel));

            if (config.Verbose)
            {
                Console.WriteLine($"PublicationRef: {publication.PublicationRef}");
                Console.WriteLine($"Pipeline: {publication.PipelineName}@{publication.PipelineVersion}");
            }
            else
            {
                Console.WriteLine($"[OK] Published {publication.PipelineName}@{publication.PipelineVersion}");
            }

            Console.WriteLine();
            if (config.Verbose)
            {
                Console.WriteLine("SDK command: sdk.execution.submit");
            }
            else
            {
                Console.WriteLine("[>] Submitting durable execution");
            }

            var submission = await client.SubmitExecutionAsync(
                InteractiveAgentPipeline.CreateSubmission(
                    publication.PublicationRef,
                    userPrompt));

            Console.WriteLine($"ExecutionId: {submission.ExecutionId}");
            if (config.Verbose)
            {
                Console.WriteLine($"Initial status: {submission.Status}");
            }
            Console.WriteLine();

            var console = new InteractiveExecutionConsole(
                client,
                submission.ExecutionId,
                InteractiveAgentPipeline.WaitingKey,
                InteractiveAgentPipeline.WaitingStepName,
                config.Verbose);

            var result = await console.RunAsync();

            if (result is null)
            {
                Console.WriteLine();
                Console.WriteLine(
                    "Local console detached. The durable execution was not cancelled.");
                return 0;
            }

            PrintTerminalResult(
                result,
                console.LastObservation,
                config.OpenAiModel,
                console.ReviewApproved,
                console.ReviewFeedback);

            await console.RunPostTerminalCommandsAsync(result.Status);

            return result.Status == Multiplexed.AI.Sdk.Contracts.Executions.AiSdkExecutionStatus.Completed
                ? 0
                : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Demo failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<IAiSdkClient> CreateClientAsync(
        DemoConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.Token))
        {
            throw new InvalidOperationException(
                "AI_RUNTIME_TOKEN is required for the standalone authenticated .NET demo.");
        }

        var credentials = new AiSdkStaticCredentialProvider(
            new AiSdkCredential("Bearer", config.Token));

        var accessContext = config.AccessContext;

        if (string.IsNullOrWhiteSpace(accessContext))
        {
            if (config.Verbose)
            {
                Console.WriteLine("Authentication bootstrap:");
                Console.WriteLine($"  POST {config.AccessContextEndpoint}");
                Console.WriteLine("  Authorization: Bearer <redacted>");
            }
            else
            {
                Console.WriteLine("[>] Creating RBAC access context from JWT claims");
            }

            var bootstrap = await AiSdkAccessContextBootstrapper.CreateAsync(
                new AiSdkAccessContextBootstrapOptions
                {
                    Endpoint = new Uri(config.AccessContextEndpoint),
                    CredentialProvider = credentials,
                    AccessContextHeaderName = config.AccessContextHeader
                });

            accessContext = bootstrap.AccessContext;

            if (config.Verbose)
            {
                Console.WriteLine(
                    $"  Access context created via '{bootstrap.HeaderName}'. Handle not displayed.");
                Console.WriteLine(
                    "  Subsequent handle rotation is managed by the SDK transport.");
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine(
                    $"[OK] RBAC access context created; {bootstrap.HeaderName} rotation enabled");
                Console.WriteLine();
            }
        }
        else if (config.Verbose)
        {
            Console.WriteLine(
                "Using the pre-provisioned AI_RUNTIME_ACCESS_CONTEXT. " +
                "Subsequent rotation is managed by the SDK transport.");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine(
                "[OK] Using pre-provisioned RBAC access context; rotation enabled");
            Console.WriteLine();
        }

        if (string.IsNullOrWhiteSpace(accessContext))
        {
            throw new InvalidOperationException(
                "The access-context bootstrap did not produce a usable handle.");
        }

        var transportOptions = new AiSdkTransportOptions
        {
            CredentialProvider = credentials,
            AccessContextHeaderName = config.AccessContextHeader,
            AdditionalHeaders =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [config.AccessContextHeader] = accessContext
                }
        };

        return new AiSdkClient(
            new AiSdkMcpHttpTransport(
                new Uri(config.Endpoint),
                transportOptions));
    }

    private static void PrintHeader(DemoConfiguration config)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine(" Deterministic AI Runtime - Interactive SDK Agent");
        Console.WriteLine("==================================================");
        Console.WriteLine();
        Console.WriteLine("SDK: .NET");
        Console.WriteLine($"Runtime endpoint: {config.Endpoint}");
        Console.WriteLine($"OpenAI model: {config.OpenAiModel}");
        Console.WriteLine($"Console mode: {(config.Verbose ? "verbose" : "presentation")}");
        Console.WriteLine();
        Console.WriteLine(
            "Runtime authentication uses a Bearer JWT plus a server-created RBAC access context.");
        Console.WriteLine(
            "OpenAI authentication stays on the runtime host. " +
            "The external SDK does not send OPENAI_API_KEY.");
        Console.WriteLine();
    }

    private static void PrintTerminalResult(
        Multiplexed.AI.Sdk.Contracts.Executions.AiSdkExecutionResult result,
        AiSdkExecutionObservation? observation,
        string configuredModel,
        bool? reviewApproved,
        string? reviewFeedback)
    {
        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine(" Agent result");
        Console.WriteLine("==================================================");

        if (result.Output is JsonElement output)
        {
            var published = PublishedResult(output);
            var answer = ReadString(published, "value")
                ?? ReadString(published, "rawText")
                ?? (published.ValueKind == JsonValueKind.String
                    ? published.GetString()
                    : null);

            Console.WriteLine();
            Console.WriteLine("OpenAI response:");
            Console.WriteLine();
            Console.WriteLine(
                string.IsNullOrWhiteSpace(answer)
                    ? InteractiveExecutionConsole.FormatJson(published)
                    : answer);

            if (published.ValueKind == JsonValueKind.Object)
            {
                var model = ReadString(published, "model") ?? configuredModel;
                var provider = ReadString(published, "providerKey") ?? "openai";
                var inputTokens = ReadNumber(published, "inputTokens");
                var outputTokens = ReadNumber(published, "outputTokens");
                var totalTokens = ReadNumber(published, "totalTokens");

                Console.WriteLine();
                Console.WriteLine("Model response metadata:");
                Console.WriteLine($"  Provider: {provider}");
                Console.WriteLine($"  Model:    {model}");

                if (inputTokens is not null || outputTokens is not null || totalTokens is not null)
                {
                    Console.WriteLine(
                        $"  Tokens:   input={inputTokens?.ToString() ?? "-"}, " +
                        $"output={outputTokens?.ToString() ?? "-"}, " +
                        $"total={totalTokens?.ToString() ?? "-"}");
                }
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("OpenAI response: (none)");
        }

        Console.WriteLine();
        Console.WriteLine("Execution:");
        Console.WriteLine($"  ID:        {result.ExecutionId}");
        Console.WriteLine($"  Status:    {result.Status}");
        Console.WriteLine($"  Completed: {result.CompletedAtUtc:O}");

        if (observation is not null)
        {
            Console.WriteLine();
            Console.WriteLine("Pipeline:");
            foreach (var step in observation.Steps)
            {
                Console.WriteLine($"  {step.Name,-18} {step.Status}");
            }
        }

        if (reviewApproved is not null)
        {
            Console.WriteLine();
            Console.WriteLine("Human review:");
            Console.WriteLine($"  Approved: {reviewApproved.Value}");
            if (!string.IsNullOrWhiteSpace(reviewFeedback))
            {
                Console.WriteLine($"  Feedback: {reviewFeedback}");
            }
        }

        if (result.Failure is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Failure code: {result.Failure.Code}");
            Console.WriteLine($"Failure message: {result.Failure.Message}");
        }
    }

    private static JsonElement PublishedResult(JsonElement output)
    {
        if (output.ValueKind == JsonValueKind.Object &&
            output.TryGetProperty("result", out var result))
        {
            return result;
        }

        return output;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static long? ReadNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var value))
        {
            return null;
        }

        return value;
    }

    private static bool IsSmokeMode() =>
        string.Equals(
            Environment.GetEnvironmentVariable("AI_DEMO_SMOKE"),
            "1",
            StringComparison.Ordinal);
}
