using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Services;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Providers
{
    public sealed class OpenAiRuntimeAnalysisProvider : IAiRuntimeAnalysisProvider
    {
        private readonly HttpClient _httpClient;
        private readonly OpenAiRuntimeAnalysisOptions _options;
        private readonly RuntimeAnalysisResultValidator _resultValidator;

        public OpenAiRuntimeAnalysisProvider(
            HttpClient httpClient,
            IOptions<OpenAiRuntimeAnalysisOptions> options,
            RuntimeAnalysisResultValidator resultValidator)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _resultValidator = resultValidator;
        }

        public RuntimeAnalysisProviderStatus Status =>
            new()
            {
                Provider = "OpenAI",
                Model = _options.Model,
                Configured = !string.IsNullOrWhiteSpace(
                    _options.ApiKey)
            };

        public async Task<RuntimeAnalysisResult> AnalyzeAsync(
            RuntimeAnalysisProviderRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            EnsureConfigured();

            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                _options.Endpoint);

            httpRequest.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    _options.ApiKey);

            var payload = new
            {
                model = _options.Model,
                instructions = RuntimeAnalysisPromptBuilder.Instructions,
                input = RuntimeAnalysisPromptBuilder.BuildInput(
                    request),
                store = false,
                max_output_tokens = _options.MaxOutputTokens,
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "runtime_analysis_result",
                        strict = true,
                        schema = OpenAiRuntimeAnalysisSchema.Create()
                    }
                }
            };

            httpRequest.Content = new StringContent(
                JsonSerializer.Serialize(
                    payload),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var responseJson = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new RuntimeAnalysisProviderException(
                    BuildProviderErrorMessage(
                        responseJson,
                        (int)response.StatusCode));
            }

            var result = OpenAiRuntimeAnalysisResponseParser.Parse(
                responseJson);

            _resultValidator.Validate(
                result,
                request.Snapshot);

            return result;
        }

        private void EnsureConfigured()
        {
            if (string.IsNullOrWhiteSpace(
                    _options.ApiKey))
            {
                throw new InvalidOperationException(
                    $"OpenAI runtime analysis is not configured. Set {OpenAiRuntimeAnalysisOptions.ApiKeyEnvironmentVariable} on the sample API process.");
            }

            if (string.IsNullOrWhiteSpace(
                    _options.Model))
            {
                throw new InvalidOperationException(
                    "OpenAI runtime analysis model is not configured.");
            }

            if (!Uri.TryCreate(
                    _options.Endpoint,
                    UriKind.Absolute,
                    out _))
            {
                throw new InvalidOperationException(
                    "OpenAI runtime analysis endpoint is invalid.");
            }

            if (_options.MaxOutputTokens < 1)
            {
                throw new InvalidOperationException(
                    "OpenAI runtime analysis MaxOutputTokens must be greater than zero.");
            }
        }

        private static string BuildProviderErrorMessage(
            string responseJson,
            int statusCode)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    responseJson);

                if (document.RootElement.TryGetProperty(
                        "error",
                        out var errorElement)
                    && errorElement.ValueKind == JsonValueKind.Object
                    && errorElement.TryGetProperty(
                        "message",
                        out var messageElement)
                    && messageElement.ValueKind == JsonValueKind.String)
                {
                    var message = messageElement.GetString();

                    if (!string.IsNullOrWhiteSpace(
                            message))
                    {
                        return $"OpenAI request failed with HTTP {statusCode}: {message}";
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to the safe generic error below.
            }

            return $"OpenAI request failed with HTTP {statusCode}.";
        }
    }
}
