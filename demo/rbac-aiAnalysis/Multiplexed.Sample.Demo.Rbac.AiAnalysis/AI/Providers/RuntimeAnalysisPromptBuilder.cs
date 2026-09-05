using System.Text.Json;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Providers
{
    internal static class RuntimeAnalysisPromptBuilder
    {
        public const string Instructions =
            """
            You are a runtime-analysis assistant for a deterministic execution and observability demo.

            Analyze only the supplied runtime snapshot and the user's question.

            Important interpretation rules:
            - Metrics are the authoritative aggregate measurements for the scenario.
            - Evidence is a bounded, selected and possibly compacted observational subset.
            - Evidence is not guaranteed to contain one item for every request or lifecycle event.
            - When evidence metadata contains an "occurrences" value, it represents the number of equivalent observations compacted into that item.
            - Never claim that an event is missing merely because an aggregate metric is larger than the number of physical evidence items.
            - Distinguish correlation from causation. State uncertainty when the evidence does not prove causality.
            - Prefer concrete runtime evidence over generic advice.
            - A suggested scenario is only a proposal. It has not been authorized, policy-validated or executed.
            - The input may contain scenarioPolicyDefinition. When present, it is the exact deterministic policy envelope that the same runtime-analysis DAG executes after this AI step.
            - Treat scenarioPolicyDefinition as a HARD proposal-generation boundary: do not suggest a scenario that would be denied by the configured policies.
            - Use only scenario types allowed by the configured safety policy.
            - Keep every numeric field inside the configured limits.
            - MaxInFlight must be one of the configured allowedMaxInFlightValues, not merely below the maximum.
            - Respect scenario-specific requirements: maintained-concurrency requires positive concurrency; wave modes require positive batchSize.
            - Never use a custom scenario type unless the configured safety policy explicitly allows it.
            - Policy awareness does not grant execution authority. The deterministic runtime policy step still validates the proposal independently and the human must still approve execution.
            - Do not claim that runtime, policy, recovery, DAG or child-DAG behavior exists unless the supplied evidence supports it.
            """;

        public static string BuildInput(
            RuntimeAnalysisProviderRequest request)
        {
            var payload = new
            {
                question = request.Question,
                snapshot = request.Snapshot,
                scenarioPolicyDefinition =
                    request.ScenarioPolicyDefinition
            };

            return
                "Analyze this runtime snapshot and answer the question. "
                + "Use evidenceIndexes to reference zero-based indexes in snapshot.evidence.\n"
                + JsonSerializer.Serialize(
                    payload);
        }
    }
}
