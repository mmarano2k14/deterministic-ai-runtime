using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.Runtime;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Services;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Providers
{
    public sealed class OpenAiRuntimeAnalysisProvider : IAiRuntimeAnalysisProvider
    {
        private readonly HttpClient _httpClient;
        private readonly OpenAiRuntimeAnalysisOptions _options;
        private const string UiAiStartedCategory =
            "demo.ui.ai.started";

        private const string UiAiCompletedCategory =
            "demo.ui.ai.completed";

        private const string UiAiFailedCategory =
            "demo.ui.ai.failed";

        private readonly RuntimeAnalysisResultValidator _resultValidator;
        private readonly RuntimeAnalysisReanalysisResultValidator
            _reanalysisResultValidator;
        private readonly IRuntimeEventContext _realtimeEvents;

        public OpenAiRuntimeAnalysisProvider(
            HttpClient httpClient,
            IOptions<OpenAiRuntimeAnalysisOptions> options,
            RuntimeAnalysisResultValidator resultValidator,
            RuntimeAnalysisReanalysisResultValidator reanalysisResultValidator,
            IRuntimeEventContext realtimeEvents)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _resultValidator = resultValidator;
            _reanalysisResultValidator =
                reanalysisResultValidator
                ?? throw new ArgumentNullException(
                    nameof(reanalysisResultValidator));
            _realtimeEvents =
                realtimeEvents
                ?? throw new ArgumentNullException(
                    nameof(realtimeEvents));
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

            var activityId = BeginUiAiActivity(
                activityKind: "root-analysis",
                rootExecutionId: null,
                childExecutionId: null,
                depth: null);

            try
            {
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

                CompleteUiAiActivity(
                    activityId,
                    activityKind: "root-analysis",
                    rootExecutionId: null,
                    childExecutionId: null,
                    depth: null);

                return result;
            }
            catch (Exception exception)
            {
                FailUiAiActivity(
                    activityId,
                    activityKind: "root-analysis",
                    rootExecutionId: null,
                    childExecutionId: null,
                    depth: null,
                    exception);

                throw;
            }
        }

        public async Task<RuntimeAnalysisReanalysisResult> ReanalyzeAsync(
            RuntimeAnalysisReanalysisProviderRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            EnsureConfigured();

            var childExecutionId =
                request.CurrentChildEvidence.CurrentExecutionId;

            var activityId = BeginUiAiActivity(
                activityKind: "child-reanalysis",
                rootExecutionId: request.RootExecutionId,
                childExecutionId: childExecutionId,
                depth: request.Depth);

            try
            {
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
                    instructions = RuntimeAnalysisReanalysisPromptBuilder.Instructions,
                    input = RuntimeAnalysisReanalysisPromptBuilder.BuildInput(
                        request),
                    store = false,
                    max_output_tokens = _options.MaxOutputTokens,
                    text = new
                    {
                        format = new
                        {
                            type = "json_schema",
                            name = "runtime_reanalysis_result",
                            strict = true,
                            schema = OpenAiRuntimeReanalysisSchema.Create()
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

                var result = OpenAiRuntimeReanalysisResponseParser.Parse(
                    responseJson);

                _reanalysisResultValidator.Validate(
                    result,
                    request.OriginalRequest.Snapshot);

                CompleteUiAiActivity(
                    activityId,
                    activityKind: "child-reanalysis",
                    rootExecutionId: request.RootExecutionId,
                    childExecutionId: childExecutionId,
                    depth: request.Depth);

                return result;
            }
            catch (Exception exception)
            {
                FailUiAiActivity(
                    activityId,
                    activityKind: "child-reanalysis",
                    rootExecutionId: request.RootExecutionId,
                    childExecutionId: childExecutionId,
                    depth: request.Depth,
                    exception);

                throw;
            }
        }

        private string BeginUiAiActivity(
            string activityKind,
            string? rootExecutionId,
            string? childExecutionId,
            int? depth)
        {
            var activityId =
                Guid.NewGuid().ToString(
                    "N");

            TryEmitUiAiActivity(
                UiAiStartedCategory,
                "AI provider call started.",
                activityId,
                activityKind,
                rootExecutionId,
                childExecutionId,
                depth,
                errorType: null);

            return activityId;
        }

        private void CompleteUiAiActivity(
            string activityId,
            string activityKind,
            string? rootExecutionId,
            string? childExecutionId,
            int? depth)
        {
            TryEmitUiAiActivity(
                UiAiCompletedCategory,
                "AI provider call completed.",
                activityId,
                activityKind,
                rootExecutionId,
                childExecutionId,
                depth,
                errorType: null);
        }

        private void FailUiAiActivity(
            string activityId,
            string activityKind,
            string? rootExecutionId,
            string? childExecutionId,
            int? depth,
            Exception exception)
        {
            TryEmitUiAiActivity(
                UiAiFailedCategory,
                "AI provider call failed.",
                activityId,
                activityKind,
                rootExecutionId,
                childExecutionId,
                depth,
                exception.GetType().Name);
        }

        private void TryEmitUiAiActivity(
            string category,
            string message,
            string activityId,
            string activityKind,
            string? rootExecutionId,
            string? childExecutionId,
            int? depth,
            string? errorType)
        {
            try
            {
                var data = new
                {
                    activityId,
                    activityKind,
                    rootExecutionId,
                    childExecutionId,
                    depth,
                    provider = Status.Provider,
                    model = Status.Model,
                    errorType
                };

                if (string.Equals(
                        category,
                        UiAiFailedCategory,
                        StringComparison.Ordinal))
                {
                    _realtimeEvents.LogWarning(
                        message,
                        category,
                        data);

                    return;
                }

                _realtimeEvents.LogInfo(
                    message,
                    category,
                    data);
            }
            catch
            {
                // Demo UI telemetry is best-effort and must never affect the
                // AI provider call or deterministic runtime execution.
            }
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
