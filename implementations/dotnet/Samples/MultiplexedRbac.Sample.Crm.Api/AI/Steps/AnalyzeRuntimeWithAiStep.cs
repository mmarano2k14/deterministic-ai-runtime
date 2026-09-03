using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Providers;
using MultiplexedRbac.Sample.Crm.Api.AI.Runtime;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Steps
{
    [AiStep(RuntimeAnalysisStepKeys.AnalyzeWithOpenAi)]
    public sealed class AnalyzeRuntimeWithAiStep : IAiStep
    {
        public string Name =>
            RuntimeAnalysisStepKeys.AnalyzeWithOpenAi;

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            var helper = context.GetHelper();

            var providerRequestJson = await helper.GetConfigAsync<string>(
                    RuntimeAnalysisStepConfigKeys.ProviderRequestJson,
                    cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(
                    providerRequestJson))
            {
                throw new InvalidOperationException(
                    $"Runtime analysis step '{helper.StepName}' is missing '{RuntimeAnalysisStepConfigKeys.ProviderRequestJson}'.");
            }

            RuntimeAnalysisProviderRequest providerRequest;

            try
            {
                providerRequest =
                    JsonSerializer.Deserialize<RuntimeAnalysisProviderRequest>(
                        providerRequestJson)
                    ?? throw new InvalidOperationException(
                        "Runtime analysis provider request deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "Runtime analysis provider request is invalid JSON.",
                    exception);
            }

            var provider = context.Services
                .GetRequiredService<IAiRuntimeAnalysisProvider>();

            var result = await provider.AnalyzeAsync(
                    providerRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            var resultJson = JsonSerializer.Serialize(
                result);

            return AiStepResult.Ok(
                output: resultJson,
                data: new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    ["provider"] = provider.Status.Provider,
                    ["model"] = provider.Status.Model,
                    ["runtimeStepKey"] = Name,
                    ["executionId"] = helper.ExecutionId,
                    ["stepName"] = helper.StepName
                });
        }
    }
}
