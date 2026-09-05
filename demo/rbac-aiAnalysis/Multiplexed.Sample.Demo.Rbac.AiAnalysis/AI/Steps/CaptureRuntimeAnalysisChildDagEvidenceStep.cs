using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Steps
{
    /// <summary>
    /// Captures one deterministic evidence receipt inside each recursive child
    /// execution. It intentionally performs no orchestration itself; recursive
    /// composition remains owned by the runtime's native ExecuteChildDagStep.
    /// </summary>
    [AiStep(RuntimeAnalysisStepKeys.CaptureChildDagEvidence)]
    public sealed class CaptureRuntimeAnalysisChildDagEvidenceStep : IAiStep
    {
        public string Name =>
            RuntimeAnalysisStepKeys.CaptureChildDagEvidence;

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            var helper = context.GetHelper();

            var rootExecutionId =
                await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.RootExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var analysisJson =
                await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.AnalysisResultJson,
                        cancellationToken)
                    .ConfigureAwait(false);

            var policyJson =
                await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.PolicyValidationJson,
                        cancellationToken)
                    .ConfigureAwait(false);

            var scenarioExecutionJson =
                await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.ScenarioExecutionJson,
                        cancellationToken)
                    .ConfigureAwait(false);

            var depth = await helper.GetRequiredConfigAsync<int>(
                    RuntimeAnalysisStepConfigKeys.ChildDagDepth,
                    cancellationToken)
                .ConfigureAwait(false);

            var analysis = Deserialize<RuntimeAnalysisResult>(
                analysisJson,
                "AI analysis");

            var policy =
                Deserialize<RuntimeAnalysisScenarioPolicyValidationResult>(
                    policyJson,
                    "scenario policy validation");

            var scenarioExecution =
                Deserialize<RuntimeAnalysisScenarioExecutionResult>(
                    scenarioExecutionJson,
                    "scenario execution");

            var observation = scenarioExecution.Observation;

            var evidence =
                new RuntimeAnalysisChildDagNodeEvidence
                {
                    Depth = depth,
                    RootExecutionId = rootExecutionId,
                    CurrentExecutionId = helper.ExecutionId,
                    ScenarioName = scenarioExecution.Scenario.Name,
                    ScenarioExecutionStatus = scenarioExecution.Status,
                    ObservedCompleted = observation?.Completed ?? 0,
                    ObservedInFlight = observation?.InFlight ?? 0,
                    ObservedErrors = observation?.Errors ?? 0,
                    AiSeverity = analysis.Severity,
                    PolicyAllowed = policy.Allowed
                };

            return AiStepResult.Ok(
                output: JsonSerializer.Serialize(
                    evidence),
                data: new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    ["runtimeStepKey"] = Name,
                    ["rootExecutionId"] = rootExecutionId,
                    ["childDepth"] = depth,
                    ["scenarioExecutionStatus"] =
                        scenarioExecution.Status,
                    ["policyAllowed"] = policy.Allowed
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
    }
}
