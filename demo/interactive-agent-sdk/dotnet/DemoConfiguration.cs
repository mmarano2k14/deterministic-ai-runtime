namespace Multiplexed.AI.Demo.InteractiveAgent.DotNet;

internal sealed record DemoConfiguration(
    string Endpoint,
    string? Token,
    string? AccessContext,
    string AccessContextHeader,
    string OpenAiModel)
{
    internal static DemoConfiguration Load()
    {
        var endpoint = Required("AI_RUNTIME_ENDPOINT");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "AI_RUNTIME_ENDPOINT must be an absolute HTTP or HTTPS URI.");
        }

        return new DemoConfiguration(
            endpoint,
            Optional("AI_RUNTIME_TOKEN"),
            Optional("AI_RUNTIME_ACCESS_CONTEXT"),
            Optional("AI_RUNTIME_ACCESS_CONTEXT_HEADER") ?? "X-Access-Context",
            Required("OPENAI_MODEL"));
    }

    private static string Required(string name)
    {
        var value = Optional(name);

        return value
            ?? throw new InvalidOperationException(
                $"Missing required demo configuration '{name}'.");
    }

    private static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value
            : null;
}
