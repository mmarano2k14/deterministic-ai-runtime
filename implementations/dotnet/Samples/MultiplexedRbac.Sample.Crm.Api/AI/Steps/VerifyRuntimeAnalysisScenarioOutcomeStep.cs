using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Runtime;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Steps
{
    [AiStep(RuntimeAnalysisStepKeys.VerifyScenarioOutcome)]
    public sealed class VerifyRuntimeAnalysisScenarioOutcomeStep : IAiStep
    {
        public string Name =>
            RuntimeAnalysisStepKeys.VerifyScenarioOutcome;

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            var helper = context.GetHelper();

            var scenarioExecutionJson =
                await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.ScenarioExecutionJson,
                        cancellationToken)
                    .ConfigureAwait(false);

            var providerRequestJson =
                await helper.GetConfigAsync<string>(
                        RuntimeAnalysisStepConfigKeys.ProviderRequestJson,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(
                    providerRequestJson))
            {
                throw new InvalidOperationException(
                    "Verification step is missing the original provider request snapshot.");
            }

            var scenarioExecution =
                Deserialize<RuntimeAnalysisScenarioExecutionResult>(
                    scenarioExecutionJson,
                    "scenario execution");

            var providerRequest =
                Deserialize<RuntimeAnalysisProviderRequest>(
                    providerRequestJson,
                    "provider request");

            if (!string.Equals(
                    scenarioExecution.Status,
                    RuntimeAnalysisScenarioExecutionStatuses.Completed,
                    StringComparison.Ordinal)
                || scenarioExecution.Observation is null)
            {
                return Complete(
                    new RuntimeAnalysisVerificationResult
                    {
                        Status =
                            RuntimeAnalysisVerificationStatuses.Skipped,
                        Executed = false,
                        ExpectedRequests =
                            scenarioExecution.Scenario.TotalRequests,
                        Summary =
                            "Outcome verification was skipped because the proposed scenario was not executed."
                    });
            }

            var observed =
                scenarioExecution.Observation;

            var expectedRequests =
                scenarioExecution.Scenario.TotalRequests;

            var httpNonOk =
                observed.Unauthorized
                + observed.Forbidden
                + observed.TooManyRequests
                + observed.OtherHttp;

            var outcomeCount =
                observed.Ok
                + httpNonOk
                + observed.Errors;

            var verification =
                new RuntimeAnalysisVerificationResult
                {
                    Status =
                        RuntimeAnalysisVerificationStatuses.Verified,
                    Executed = true,
                    CompletedMatchesPlan =
                        observed.Completed == expectedRequests,
                    NoResidualInFlight =
                        observed.InFlight == 0,
                    OutcomeCountConsistent =
                        outcomeCount == observed.Completed,
                    ExpectedRequests =
                        expectedRequests,
                    ObservedCompleted =
                        observed.Completed,
                    ObservedOk =
                        observed.Ok,
                    ObservedHttpNonOk =
                        httpNonOk,
                    ObservedErrors =
                        observed.Errors,
                    BaselineP50Ms =
                        providerRequest.Snapshot.Metrics.P50Ms,
                    ObservedP50Ms =
                        observed.P50Ms,
                    P50DeltaMs =
                        Delta(
                            providerRequest.Snapshot.Metrics.P50Ms,
                            observed.P50Ms),
                    BaselineP95Ms =
                        providerRequest.Snapshot.Metrics.P95Ms,
                    ObservedP95Ms =
                        observed.P95Ms,
                    P95DeltaMs =
                        Delta(
                            providerRequest.Snapshot.Metrics.P95Ms,
                            observed.P95Ms),
                    Summary =
                        BuildSummary(
                            scenarioExecution,
                            observed,
                            expectedRequests,
                            httpNonOk)
                };

            return Complete(
                verification);
        }

        private static AiStepResult Complete(
            RuntimeAnalysisVerificationResult result)
        {
            return AiStepResult.Ok(
                output: JsonSerializer.Serialize(
                    result),
                data: new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    ["verification.status"] =
                        result.Status,
                    ["verification.executed"] =
                        result.Executed,
                    ["verification.completedMatchesPlan"] =
                        result.CompletedMatchesPlan,
                    ["verification.noResidualInFlight"] =
                        result.NoResidualInFlight
                });
        }

        private static double? Delta(
            double? baseline,
            double? observed)
        {
            if (!baseline.HasValue
                || !observed.HasValue)
            {
                return null;
            }

            return observed.Value - baseline.Value;
        }

        private static string BuildSummary(
            RuntimeAnalysisScenarioExecutionResult execution,
            RuntimeAnalysisScenarioExecutionObservation observed,
            int expectedRequests,
            int httpNonOk)
        {
            var completion =
                observed.Completed == expectedRequests
                    ? $"{observed.Completed}/{expectedRequests} requests completed as planned"
                    : $"{observed.Completed}/{expectedRequests} requests completed";

            var residual =
                observed.InFlight == 0
                    ? "no residual in-flight work"
                    : $"{observed.InFlight} requests remain in flight";

            return
                $"{completion}; {observed.Ok} OK, {httpNonOk} HTTP non-OK, {observed.Errors} client/runtime errors; {residual}. "
                + $"Observed p50={FormatMs(observed.P50Ms)}, p95={FormatMs(observed.P95Ms)} for scenario '{execution.Scenario.Name}'.";
        }

        private static string FormatMs(
            double? value)
        {
            return value.HasValue
                ? $"{value.Value:0.0} ms"
                : "n/a";
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
