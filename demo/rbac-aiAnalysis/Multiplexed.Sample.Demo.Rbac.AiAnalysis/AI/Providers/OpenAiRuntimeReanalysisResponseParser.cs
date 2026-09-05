using System.Text.Json;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Providers
{
    internal static class OpenAiRuntimeReanalysisResponseParser
    {
        private static readonly JsonSerializerOptions SerializerOptions =
            new()
            {
                PropertyNameCaseInsensitive = true
            };

        public static RuntimeAnalysisReanalysisResult Parse(
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
                        $"OpenAI refused the runtime re-analysis request: {refusal}");
                }

                var outputText = FindOutputText(
                    root);

                if (string.IsNullOrWhiteSpace(
                        outputText))
                {
                    throw new RuntimeAnalysisProviderException(
                        "OpenAI returned no structured runtime re-analysis output text.");
                }

                return JsonSerializer.Deserialize<RuntimeAnalysisReanalysisResult>(
                           outputText,
                           SerializerOptions)
                       ?? throw new RuntimeAnalysisProviderException(
                           "OpenAI returned an empty runtime re-analysis result.");
            }
            catch (RuntimeAnalysisProviderException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new RuntimeAnalysisProviderException(
                    "OpenAI returned an invalid structured runtime re-analysis response.",
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

            throw new RuntimeAnalysisProviderException(
                $"OpenAI runtime re-analysis response status was '{status ?? "unknown"}'.");
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
    }
}
