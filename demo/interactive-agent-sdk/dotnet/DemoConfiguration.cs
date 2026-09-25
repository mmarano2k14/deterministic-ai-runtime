namespace Multiplexed.AI.Demo.InteractiveAgent.DotNet;

internal sealed record DemoConfiguration(
    string Endpoint,
    string? Token,
    string? AccessContext,
    string AccessContextHeader,
    string AccessContextEndpoint,
    string OpenAiModel,
    bool Verbose)
{
    internal static DemoConfiguration Load()
    {
        var endpoint = Required("AI_RUNTIME_ENDPOINT");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp &&
             endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "AI_RUNTIME_ENDPOINT must be an absolute HTTP or HTTPS URI.");
        }

        var accessContextEndpoint =
            Optional("AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT")
            ?? new Uri(endpointUri, "/auth/access-context").AbsoluteUri;

        if (!Uri.TryCreate(
                accessContextEndpoint,
                UriKind.Absolute,
                out var accessContextUri) ||
            (accessContextUri.Scheme != Uri.UriSchemeHttp &&
             accessContextUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT must be an absolute HTTP or HTTPS URI.");
        }

        return new DemoConfiguration(
            endpointUri.AbsoluteUri,
            Optional("AI_RUNTIME_TOKEN"),
            Optional("AI_RUNTIME_ACCESS_CONTEXT"),
            Optional("AI_RUNTIME_ACCESS_CONTEXT_HEADER") ?? "X-Access-Context",
            accessContextUri.AbsoluteUri,
            Required("OPENAI_MODEL"),
            Flag("AI_DEMO_VERBOSE"));
    }

    private static string Required(string name)
    {
        var value = Optional(name);

        return value
            ?? throw new InvalidOperationException(
                $"Missing required demo configuration '{name}'.");
    }

    private static bool Flag(string name)
    {
        var value = Optional(name);

        return value is not null &&
            (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value
            : null;
}
