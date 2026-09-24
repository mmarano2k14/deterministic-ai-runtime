using System.Text.Json;
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Authentication;
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
            Console.WriteLine("SDK command: sdk.publish_pipeline");

            var publication = await client.PublishPipelineAsync(
                InteractiveAgentPipeline.CreatePublication(config.OpenAiModel));

            Console.WriteLine($"PublicationRef: {publication.PublicationRef}");
            Console.WriteLine($"Pipeline: {publication.PipelineName}@{publication.PipelineVersion}");
            Console.WriteLine();

            Console.WriteLine("SDK command: sdk.execution.submit");

            var submission = await client.SubmitExecutionAsync(
                InteractiveAgentPipeline.CreateSubmission(
                    publication.PublicationRef,
                    userPrompt));

            Console.WriteLine($"ExecutionId: {submission.ExecutionId}");
            Console.WriteLine($"Initial status: {submission.Status}");
            Console.WriteLine();

            var console = new InteractiveExecutionConsole(
                client,
                submission.ExecutionId,
                InteractiveAgentPipeline.WaitingKey,
                InteractiveAgentPipeline.WaitingStepName);

            var result = await console.RunAsync();

            if (result is null)
            {
                Console.WriteLine();
                Console.WriteLine(
                    "Local console detached. The durable execution was not cancelled.");
                return 0;
            }

            PrintTerminalResult(result);

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
            Console.WriteLine("Authentication bootstrap:");
            Console.WriteLine($"  POST {config.AccessContextEndpoint}");
            Console.WriteLine("  Authorization: Bearer <redacted>");

            var bootstrap = await AiSdkAccessContextBootstrapper.CreateAsync(
                new AiSdkAccessContextBootstrapOptions
                {
                    Endpoint = new Uri(config.AccessContextEndpoint),
                    CredentialProvider = credentials,
                    AccessContextHeaderName = config.AccessContextHeader
                });

            accessContext = bootstrap.AccessContext;

            Console.WriteLine(
                $"  Access context created via '{bootstrap.HeaderName}'. Handle not displayed.");
            Console.WriteLine(
                "  Subsequent handle rotation is managed by the SDK transport.");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine(
                "Using the pre-provisioned AI_RUNTIME_ACCESS_CONTEXT. " +
                "Subsequent rotation is managed by the SDK transport.");
            Console.WriteLine();
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
        Console.WriteLine();
        Console.WriteLine(
            "Runtime authentication uses a Bearer JWT plus a server-created RBAC access context.");
        Console.WriteLine(
            "OpenAI authentication stays on the runtime host. " +
            "The external SDK does not send OPENAI_API_KEY.");
        Console.WriteLine();
    }

    private static void PrintTerminalResult(
        Multiplexed.AI.Sdk.Contracts.Executions.AiSdkExecutionResult result)
    {
        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine(" Terminal execution result");
        Console.WriteLine("==================================================");
        Console.WriteLine($"ExecutionId: {result.ExecutionId}");
        Console.WriteLine($"Status: {result.Status}");
        Console.WriteLine($"CompletedAtUtc: {result.CompletedAtUtc:O}");

        if (result.Output is JsonElement output)
        {
            Console.WriteLine();
            Console.WriteLine("Agent response:");

            if (output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("result", out var finalResult))
            {
                Console.WriteLine(InteractiveExecutionConsole.FormatJson(finalResult));
            }
            else
            {
                Console.WriteLine(InteractiveExecutionConsole.FormatJson(output));
            }
        }

        if (result.Failure is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Failure code: {result.Failure.Code}");
            Console.WriteLine($"Failure message: {result.Failure.Message}");
        }
    }

    private static bool IsSmokeMode() =>
        string.Equals(
            Environment.GetEnvironmentVariable("AI_DEMO_SMOKE"),
            "1",
            StringComparison.Ordinal);
}
