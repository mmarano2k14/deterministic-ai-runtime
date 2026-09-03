using System.Text.Json;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Providers
{
    internal static class OpenAiRuntimeAnalysisResponseParser
    {
        private static readonly JsonSerializerOptions SerializerOptions =
            new()
            {
                PropertyNameCaseInsensitive = true
            };

        public static RuntimeAnalysisResult Parse(
            string responseJson)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    responseJson);

                var root = document.RootElement;

                EnsureCompleted(
                    root);

                var refusal = FindRefusal(
                    root);

                if (!string.IsNullOrWhiteSpace(
                        refusal))
                {
                    throw new RuntimeAnalysisProviderException(
                        $"OpenAI refused the runtime analysis request: {refusal}");
                }

                var outputText = FindOutputText(
                    root);

                if (string.IsNullOrWhiteSpace(
                        outputText))
                {
                    throw new RuntimeAnalysisProviderException(
                        "OpenAI returned no structured output text.");
                }

                var result = JsonSerializer.Deserialize<RuntimeAnalysisResult>(
                    outputText,
                    SerializerOptions);

                return result
                    ?? throw new RuntimeAnalysisProviderException(
                        "OpenAI returned an empty runtime analysis result.");
            }
            catch (RuntimeAnalysisProviderException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new RuntimeAnalysisProviderException(
                    "OpenAI returned an invalid structured runtime analysis response.",
                    exception);
            }
        }

        private static void EnsureCompleted(
            JsonElement root)
        {
            if (!root.TryGetProperty(
                    "status",
                    out var statusElement))
            {
                return;
            }

            var status = statusElement.GetString();

            if (string.Equals(
                    status,
                    "completed",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var errorMessage = TryReadErrorMessage(
                root);

            throw new RuntimeAnalysisProviderException(
                string.IsNullOrWhiteSpace(
                    errorMessage)
                    ? $"OpenAI response status was '{status ?? "unknown"}'."
                    : $"OpenAI response failed: {errorMessage}");
        }

        private static string? FindOutputText(
            JsonElement root)
        {
            if (root.TryGetProperty(
                    "output_text",
                    out var outputTextElement)
                && outputTextElement.ValueKind == JsonValueKind.String)
            {
                var outputText = outputTextElement.GetString();

                if (!string.IsNullOrWhiteSpace(
                        outputText))
                {
                    return outputText;
                }
            }

            if (!root.TryGetProperty(
                    "output",
                    out var outputElement)
                || outputElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var outputItem in outputElement.EnumerateArray())
            {
                if (!outputItem.TryGetProperty(
                        "content",
                        out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in contentElement.EnumerateArray())
                {
                    if (!contentItem.TryGetProperty(
                            "type",
                            out var typeElement)
                        || !string.Equals(
                            typeElement.GetString(),
                            "output_text",
                            StringComparison.OrdinalIgnoreCase)
                        || !contentItem.TryGetProperty(
                            "text",
                            out var textElement)
                        || textElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var text = textElement.GetString();

                    if (!string.IsNullOrWhiteSpace(
                            text))
                    {
                        return text;
                    }
                }
            }

            return null;
        }

        private static string? FindRefusal(
            JsonElement root)
        {
            if (!root.TryGetProperty(
                    "output",
                    out var outputElement)
                || outputElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var outputItem in outputElement.EnumerateArray())
            {
                if (!outputItem.TryGetProperty(
                        "content",
                        out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in contentElement.EnumerateArray())
                {
                    if (!contentItem.TryGetProperty(
                            "type",
                            out var typeElement)
                        || !string.Equals(
                            typeElement.GetString(),
                            "refusal",
                            StringComparison.OrdinalIgnoreCase)
                        || !contentItem.TryGetProperty(
                            "refusal",
                            out var refusalElement)
                        || refusalElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    return refusalElement.GetString();
                }
            }

            return null;
        }

        private static string? TryReadErrorMessage(
            JsonElement root)
        {
            if (!root.TryGetProperty(
                    "error",
                    out var errorElement)
                || errorElement.ValueKind != JsonValueKind.Object
                || !errorElement.TryGetProperty(
                    "message",
                    out var messageElement)
                || messageElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return messageElement.GetString();
        }
    }
}
