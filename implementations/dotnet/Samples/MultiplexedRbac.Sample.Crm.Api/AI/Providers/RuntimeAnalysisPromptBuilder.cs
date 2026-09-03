using System.Text.Json;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Providers
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
            - Prefer one of the existing scenario types when it can test the hypothesis.
            - Do not claim that runtime, policy, recovery, DAG or child-DAG behavior exists unless the supplied evidence supports it.
            """;

        public static string BuildInput(
            RuntimeAnalysisProviderRequest request)
        {
            var payload = new
            {
                question = request.Question,
                snapshot = request.Snapshot
            };

            return
                "Analyze this runtime snapshot and answer the question. "
                + "Use evidenceIndexes to reference zero-based indexes in snapshot.evidence.\n"
                + JsonSerializer.Serialize(
                    payload);
        }
    }
}
