using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Transport;

var config = DemoConfiguration.Load(
    "AI_RUNTIME_DOTNET_ENVIRONMENT_REF",
    ".NET");

var transportOptions = new AiSdkTransportOptions
{
    CredentialProvider = string.IsNullOrWhiteSpace(config.Token)
        ? null
        : new AiSdkStaticCredentialProvider(
            new AiSdkCredential("Bearer", config.Token)),
    AdditionalHeaders = string.IsNullOrWhiteSpace(config.AccessContext)
        ? null
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [config.AccessContextHeader] = config.AccessContext
        }
};

IAiSdkClient client = new AiSdkClient(
    new AiSdkMcpHttpTransport(new Uri(config.Endpoint), transportOptions));

Console.WriteLine("Interactive Agent SDK Demo");
Console.WriteLine("SDK: .NET");
Console.WriteLine($"Runtime endpoint: {config.Endpoint}");
Console.WriteLine($"Environment ref: {config.EnvironmentRef}");
Console.WriteLine($"Bearer token configured: {!string.IsNullOrWhiteSpace(config.Token)}");
Console.WriteLine($"Access context configured: {!string.IsNullOrWhiteSpace(config.AccessContext)}");
Console.WriteLine($"OpenAI key configured: {config.OpenAiKeyConfigured}");
Console.WriteLine($"OpenAI model configured: {config.OpenAiModelConfigured}");
Console.WriteLine();
Console.WriteLine("External SDK consumer initialized.");
Console.WriteLine("No runtime request is sent by the scaffold increment.");

return 0;

internal sealed record DemoConfiguration(
    string Endpoint,
    string EnvironmentRef,
    string? Token,
    string? AccessContext,
    string AccessContextHeader,
    bool OpenAiKeyConfigured,
    bool OpenAiModelConfigured)
{
    internal static DemoConfiguration Load(
        string environmentRefVariable,
        string sdkName)
    {
        var endpoint = Required("AI_RUNTIME_ENDPOINT");
        _ = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : throw new InvalidOperationException(
                "AI_RUNTIME_ENDPOINT must be an absolute HTTP or HTTPS URI.");

        return new DemoConfiguration(
            endpoint,
            Required(environmentRefVariable),
            Optional("AI_RUNTIME_TOKEN"),
            Optional("AI_RUNTIME_ACCESS_CONTEXT"),
            Optional("AI_RUNTIME_ACCESS_CONTEXT_HEADER") ?? "X-Access-Context",
            !string.IsNullOrWhiteSpace(Optional("OPENAI_API_KEY")),
            !string.IsNullOrWhiteSpace(Optional("OPENAI_MODEL")));

        string Required(string name)
        {
            var value = Optional(name);
            return !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException(
                    $"Missing required {sdkName} demo configuration '{name}'.");
        }

        static string? Optional(string name) =>
            Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
                ? value
                : null;
    }
}
