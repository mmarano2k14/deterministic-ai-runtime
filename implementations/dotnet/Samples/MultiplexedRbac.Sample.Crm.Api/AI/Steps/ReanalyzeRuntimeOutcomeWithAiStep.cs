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
    [AiStep(RuntimeAnalysisStepKeys.ReanalyzeVerifiedOutcome)]
    public sealed class ReanalyzeRuntimeOutcomeWithAiStep : IAiStep
    {
        public string Name =>
            RuntimeAnalysisStepKeys.ReanalyzeVerifiedOutcome;

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            var helper = context.GetHelper();

            var originalRequest =
                Deserialize<RuntimeAnalysisProviderRequest>(
                    await helper.GetRequiredInputAsync<string>(
                            RuntimeAnalysisStepInputKeys.ProviderRequestJson,
                            cancellationToken)
                        .ConfigureAwait(false),
                    "original provider request");

            var depth = await helper.GetRequiredConfigAsync<int>(
                    RuntimeAnalysisStepConfigKeys.ChildDagDepth,
                    cancellationToken)
                .ConfigureAwait(false);

            var request = new RuntimeAnalysisReanalysisProviderRequest
            {
                RootExecutionId = await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.RootExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false),
                Depth = depth,
                InvestigationMode =
                    RuntimeAnalysisInvestigationModes.IsSupported(
                        originalRequest.InvestigationMode)
                        ? originalRequest.InvestigationMode
                        : RuntimeAnalysisInvestigationModes.StopWhenConclusive,
                MaximumApprovedChildDepth =
                    RuntimeAnalysisChildDagDefinitionFactory
                        .MaximumApprovedChildDepth,
                CanCreateAnotherChild =
                    depth
                    < RuntimeAnalysisChildDagDefinitionFactory
                        .MaximumApprovedChildDepth,
                OriginalRequest = originalRequest,
                RootAnalysis = Deserialize<RuntimeAnalysisResult>(
                    await helper.GetRequiredInputAsync<string>(
                            RuntimeAnalysisStepInputKeys.RootAnalysisResultJson,
                            cancellationToken)
                        .ConfigureAwait(false),
                    "root analysis"),
                PreviousReanalysis = DeserializeOptional<RuntimeAnalysisReanalysisResult>(
                    await helper.GetRequiredInputAsync<string>(
                            RuntimeAnalysisStepInputKeys.PreviousReanalysisJson,
                            cancellationToken)
                        .ConfigureAwait(false),
                    "previous re-analysis"),
                CurrentChildEvidence = Deserialize<RuntimeAnalysisChildDagNodeEvidence>(
                    await helper.GetRequiredInputAsync<string>(
                            RuntimeAnalysisStepInputKeys.ChildDagEvidenceJson,
                            cancellationToken)
                        .ConfigureAwait(false),
                    "current child DAG evidence"),
                PreviousScenarioExecution = Deserialize<RuntimeAnalysisScenarioExecutionResult>(
                    await helper.GetRequiredInputAsync<string>(
                            RuntimeAnalysisStepInputKeys.ScenarioExecutionJson,
                            cancellationToken)
                        .ConfigureAwait(false),
                    "previous scenario execution"),
                PreviousVerification = Deserialize<RuntimeAnalysisVerificationResult>(
                    await helper.GetRequiredInputAsync<string>(
                            RuntimeAnalysisStepInputKeys.VerificationJson,
                            cancellationToken)
                        .ConfigureAwait(false),
                    "previous verification")
            };

            var provider = context.Services
                .GetRequiredService<IAiRuntimeAnalysisProvider>();

            var result = await provider.ReanalyzeAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);

            return AiStepResult.Ok(
                output: JsonSerializer.Serialize(
                    result),
                data: new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    ["runtimeStepKey"] = Name,
                    ["reanalysis.depth"] = request.Depth,
                    ["reanalysis.investigationMode"] =
                        request.InvestigationMode,
                    ["reanalysis.conclusion"] = result.Conclusion,
                    ["reanalysis.shouldContinue"] = result.ShouldContinue,
                    ["reanalysis.confidence"] = result.Confidence
                });
        }

        private static T Deserialize<T>(
            string json,
            string label)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(
                           json)
                       ?? throw new InvalidOperationException(
                           $"{label} deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"{label} is invalid JSON.",
                    exception);
            }
        }

        private static T? DeserializeOptional<T>(
            string json,
            string label)
            where T : class
        {
            if (string.IsNullOrWhiteSpace(
                    json)
                || string.Equals(
                    json.Trim(),
                    "null",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Deserialize<T>(
                json,
                label);
        }
    }
}
