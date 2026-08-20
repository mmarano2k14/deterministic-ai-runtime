using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Prompt;
using Multiplexed.Abstractions.AI.Prompt.Models;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.AI.Prompt.Models;
using Multiplexed.AI.Runtime.Execution.Context;

namespace Multiplexed.AI.Runtime.Pipeline.Steps.Prompt
{
    /// <summary>
    /// Pipeline step that executes a provider-agnostic AI prompt.
    ///
    /// PURPOSE:
    /// - Acts as the declarative runtime entry point for AI prompt execution
    /// - Reads the current step configuration from pipeline state
    /// - Builds a normalized <see cref="AiPromptRequest"/>
    /// - Delegates prompt execution to <see cref="IAiPromptExecutor"/>
    ///
    /// DESIGN:
    /// - This step is intentionally orchestration-facing, not provider-facing
    /// - Provider selection, template rendering, response parsing, and normalization
    ///   are delegated to reusable AI runtime services
    /// - Declared input resolution is delegated to <see cref="AiStepExecutionContext"/>
    /// - The step returns a serializable result suitable for persistence and replay
    ///
    /// IMPORTANT:
    /// - This step must not contain provider-specific SDK logic
    /// - Replay safety comes from persisting the exact step result after execution
    /// - The provider must never be re-called during replay restoration
    /// </summary>
    [AiStep("ai.prompt")]
    public sealed class AiPromptStep : IAiStep
    {
        private readonly IAiPromptExecutor _promptExecutor;

        public AiPromptStep(IAiPromptExecutor promptExecutor)
        {
            _promptExecutor = promptExecutor ?? throw new ArgumentNullException(nameof(promptExecutor));
        }

        public string Name => "ai.prompt";

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var helper = context.GetHelper();

            var configuration = await BuildConfigurationAsync(helper, cancellationToken);

            var resolvedInputs = await helper.GetResolvedInputsAsync(
                  includeReservedVariables: true,
                  cancellationToken).ConfigureAwait(false);

            var variables = new Dictionary<string, object?>(
                resolvedInputs,
                StringComparer.Ordinal);

            var request = new AiPromptRequest
            {
                ProviderKey = configuration.Provider,
                Model = configuration.Model,
                PromptTemplate = configuration.Template,
                Variables = variables,
                Temperature = configuration.Temperature,
                MaxTokens = configuration.MaxTokens,
                ResponseFormat = configuration.ResponseFormat,
                PromptVersion = configuration.PromptVersion,
                Metadata = BuildRequestMetadata(helper)
            };

            var result = await _promptExecutor.ExecuteAsync(
                request,
                cancellationToken);

            return AiStepResult.Ok(
                output: result.RawText,
                data: helper.ToDictionary(new
                {
                    value = result.RawText,
                    providerKey = result.ProviderKey,
                    model = result.Model,
                    rawText = result.RawText,
                    parsedResult = result.ParsedResult,
                    inputTokens = result.InputTokens,
                    outputTokens = result.OutputTokens,
                    totalTokens = result.TotalTokens,
                    finishReason = result.FinishReason,
                    promptVersion = result.PromptVersion,
                    renderedPromptHash = result.RenderedPromptHash,
                    metadata = result.Metadata
                }, ignoreNull: false));
        }

        /// <summary>
        /// Builds the normalized prompt step configuration from the current step config.
        /// </summary>
        private static async Task<AiPromptStepConfiguration> BuildConfigurationAsync(
            IAiStepContextHelper helper,
            CancellationToken cancellationToken)
        {
            var provider = await helper.GetRequiredConfigAsync<string>("provider", cancellationToken);
            var model = await helper.GetRequiredConfigAsync<string>("model", cancellationToken);
            var template = await helper.GetRequiredConfigAsync<string>("template", cancellationToken);

            var configuration = new AiPromptStepConfiguration
            {
                Provider = provider,
                Model = model,
                Template = template
            };

            var temperature = await helper.GetConfigAsync<double?>("temperature", cancellationToken);
            if (temperature.HasValue)
            {
                configuration.Temperature = temperature.Value;
            }

            var maxTokens = await helper.GetConfigAsync<int?>("maxTokens", cancellationToken);
            if (maxTokens.HasValue)
            {
                configuration.MaxTokens = maxTokens.Value;
            }

            var responseFormat = await helper.GetConfigAsync<string>("responseFormat", cancellationToken);
            if (!string.IsNullOrWhiteSpace(responseFormat))
            {
                configuration.ResponseFormat = responseFormat;
            }

            var promptVersion = await helper.GetConfigAsync<string>("promptVersion", cancellationToken);
            if (!string.IsNullOrWhiteSpace(promptVersion))
            {
                configuration.PromptVersion = promptVersion;
            }

            return configuration;
        }

        /// <summary>
        /// Builds normalized metadata attached to the prompt request.
        ///
        /// This metadata is intended for diagnostics, audit trails, and future observability.
        /// It must remain serializable and provider-agnostic.
        /// </summary>
        private static Dictionary<string, object?> BuildRequestMetadata(IAiStepContextHelper helper)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AiExecutionMetadataKeys.CamelCaseExecutionId] = helper.ExecutionId,
                [AiStepMetadataKeys.CamelCaseStepName] = helper.StepName,
                [AiStepMetadataKeys.CamelCaseStepKey] = helper.StepKey
            };
        }
    }
}