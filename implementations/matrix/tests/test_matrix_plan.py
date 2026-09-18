from __future__ import annotations

import copy
import sys
import unittest
from itertools import product
from pathlib import Path

MATRIX_ROOT = Path(__file__).resolve().parents[1]
if str(MATRIX_ROOT) not in sys.path:
    sys.path.insert(0, str(MATRIX_ROOT))

from matrix_plan import (  # noqa: E402
    CLIENT_LANGUAGES,
    CUSTOM_POLICY_FAMILIES,
    DEPENDENCY_PACKAGE_BY_WORKER,
    MCP_EFFECT_VALUES,
    REQUIRED_COVERAGE_TARGETS,
    WORKER_LANGUAGES,
    load_plan,
    validate_plan,
)


class MatrixPlanTests(unittest.TestCase):
    def setUp(self) -> None:
        self.plan = load_plan()

    def test_plan_is_valid(self) -> None:
        self.assertEqual([], validate_plan(self.plan))

    def test_core_matrix_is_exact_three_by_three_cross_product(self) -> None:
        expected = set(product(CLIENT_LANGUAGES, WORKER_LANGUAGES))
        actual = {
            (scenario["clientLanguage"], scenario["workerLanguage"])
            for scenario in self.plan["coreScenarios"]
        }
        self.assertEqual(expected, actual)
        self.assertEqual(9, len(self.plan["coreScenarios"]))

    def test_dependency_package_mapping_is_language_specific_and_complete(self) -> None:
        self.assertEqual(DEPENDENCY_PACKAGE_BY_WORKER, self.plan["dependencyPackageByWorker"])

    def test_only_supported_hosted_policy_families_are_required(self) -> None:
        self.assertEqual(list(CUSTOM_POLICY_FAMILIES), self.plan["customPolicyFamilies"])
        target = next(item for item in self.plan["coverageTargets"] if item["id"] == "custom-policy-family")
        self.assertEqual(list(CUSTOM_POLICY_FAMILIES), target["requiredValues"])

    def test_all_final_roadmap_coverage_targets_are_explicit(self) -> None:
        self.assertEqual(REQUIRED_COVERAGE_TARGETS, {item["id"] for item in self.plan["coverageTargets"]})

    def test_plan_does_not_claim_live_execution_results(self) -> None:
        for scenario in [*self.plan["coreScenarios"], *self.plan["featureScenarios"]]:
            self.assertFalse({"status", "passed", "executed", "skipped"}.intersection(scenario))


    def test_initial_feature_bindings_cover_all_hosted_languages(self) -> None:
        for target_id in ("publication-pinning", "deterministic-dependency-packaging"):
            scenarios = [
                item for item in self.plan["featureScenarios"]
                if item["coverageTarget"] == target_id
            ]
            self.assertEqual(set(WORKER_LANGUAGES), {item["workerLanguage"] for item in scenarios})
            self.assertTrue(all(item["executor"] is not None for item in scenarios))

    def test_dependency_feature_binding_matches_language_package_kind(self) -> None:
        scenarios = [
            item for item in self.plan["featureScenarios"]
            if item["coverageTarget"] == "deterministic-dependency-packaging"
        ]
        self.assertEqual(3, len(scenarios))
        for scenario in scenarios:
            self.assertEqual(
                [DEPENDENCY_PACKAGE_BY_WORKER[scenario["workerLanguage"]]],
                scenario["coverageValues"],
            )


    def test_custom_policy_feature_bindings_cover_exact_supported_families(self) -> None:
        scenarios = [
            item for item in self.plan["featureScenarios"]
            if item["coverageTarget"] == "custom-policy-family"
        ]
        self.assertEqual(3, len(scenarios))
        self.assertEqual(
            set(CUSTOM_POLICY_FAMILIES),
            {item["coverageValues"][0] for item in scenarios},
        )
        self.assertTrue(all(item["clientLanguage"] == "python" for item in scenarios))
        self.assertTrue(all(item["executor"] is not None for item in scenarios))

    def test_missing_custom_policy_family_binding_fails_closed(self) -> None:
        invalid = copy.deepcopy(self.plan)
        invalid["featureScenarios"] = [
            item for item in invalid["featureScenarios"]
            if item["id"] != "feature-custom-policy-delegation-dotnet-worker"
        ]
        errors = validate_plan(invalid)
        self.assertTrue(any("custom-policy-family feature bindings" in error for error in errors))

    def test_hosted_invocation_registration_installs_all_supported_policy_transports(self) -> None:
        host = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "HostedInvocationHostRegistration.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("services.AddAiHostedConcurrencyPolicyExecution();", host)
        self.assertIn("services.AddAiHostedRetryPolicyExecution();", host)
        self.assertIn("services.AddAiHostedDelegationPolicyExecution();", host)
        self.assertLess(host.index("services.AddAiHostedInvocationWorkers("), host.index("services.AddAiHostedConcurrencyPolicyExecution();"))

    def test_missing_initial_feature_worker_fails_closed(self) -> None:
        invalid = copy.deepcopy(self.plan)
        invalid["featureScenarios"] = [
            item for item in invalid["featureScenarios"]
            if not (
                item["coverageTarget"] == "publication-pinning"
                and item["workerLanguage"] == "python"
            )
        ]
        errors = validate_plan(invalid)
        self.assertTrue(any("publication-pinning feature bindings" in error for error in errors))

    def test_missing_core_pair_fails_closed(self) -> None:
        invalid = copy.deepcopy(self.plan)
        invalid["coreScenarios"].pop()
        errors = validate_plan(invalid)
        self.assertTrue(any("3 x 3" in error or "missing client/worker pairs" in error for error in errors))

    def test_unknown_policy_family_fails_closed(self) -> None:
        invalid = copy.deepcopy(self.plan)
        invalid["customPolicyFamilies"].append("routing")
        errors = validate_plan(invalid)
        self.assertTrue(any("customPolicyFamilies" in error for error in errors))

    def test_unknown_feature_binding_fails_closed(self) -> None:
        invalid = copy.deepcopy(self.plan)
        invalid["featureScenarios"].append(
            {
                "id": "invalid",
                "coverageTarget": "not-real",
                "clientLanguage": "dotnet",
                "workerLanguage": "python",
                "coverageValues": [],
                "executor": None,
            }
        )
        errors = validate_plan(invalid)
        self.assertTrue(any("coverageTarget is unsupported" in error for error in errors))

    def test_current_process_feature_matrix_binds_fourteen_scenarios(self):
        plan = self.plan
        self.assertEqual(14, len(plan["featureScenarios"]))
        counts = {}
        for scenario in plan["featureScenarios"]:
            counts[scenario["coverageTarget"]] = counts.get(scenario["coverageTarget"], 0) + 1
        self.assertEqual(3, counts.get("publication-pinning"))
        self.assertEqual(3, counts.get("deterministic-dependency-packaging"))
        self.assertEqual(3, counts.get("custom-policy-family"))
        self.assertEqual(3, counts.get("nested-child-dag"))
        self.assertEqual(2, counts.get("mcp-effect-evidence"))

    def test_mcp_effect_feature_bindings_cover_exact_durable_cases(self) -> None:
        scenarios = [
            item for item in self.plan["featureScenarios"]
            if item["coverageTarget"] == "mcp-effect-evidence"
        ]
        self.assertEqual(2, len(scenarios))
        self.assertEqual(set(MCP_EFFECT_VALUES), {item["coverageValues"][0] for item in scenarios})
        self.assertTrue(all(item["clientLanguage"] == "python" for item in scenarios))
        self.assertTrue(all(item["workerLanguage"] is None for item in scenarios))
        self.assertTrue(all(item["executor"] is not None for item in scenarios))

    def test_missing_mcp_effect_case_fails_closed(self) -> None:
        invalid = copy.deepcopy(self.plan)
        invalid["featureScenarios"] = [
            item for item in invalid["featureScenarios"]
            if item["id"] != "feature-mcp-effect-uncertain-blocks-blind-resend-python-client"
        ]
        errors = validate_plan(invalid)
        self.assertTrue(any("mcp-effect-evidence feature bindings" in error for error in errors))

    def test_nested_child_dag_feature_bindings_cover_all_hosted_languages(self) -> None:
        scenarios = [
            item for item in self.plan["featureScenarios"]
            if item["coverageTarget"] == "nested-child-dag"
        ]
        self.assertEqual(3, len(scenarios))
        self.assertEqual(set(WORKER_LANGUAGES), {item["workerLanguage"] for item in scenarios})
        self.assertTrue(all(item["clientLanguage"] == "python" for item in scenarios))
        self.assertTrue(all(item["coverageValues"] == [] for item in scenarios))
        self.assertTrue(all(item["executor"] is not None for item in scenarios))

    def test_missing_nested_child_dag_worker_fails_closed(self) -> None:
        invalid = copy.deepcopy(self.plan)
        invalid["featureScenarios"] = [
            item for item in invalid["featureScenarios"]
            if item["id"] != "feature-nested-child-dag-python-client-python-worker"
        ]
        errors = validate_plan(invalid)
        self.assertTrue(any("nested-child-dag feature bindings" in error for error in errors))

    def test_matrix_readme_records_mcp_effect_evidence_as_current_coverage(self):
        readme = (MATRIX_ROOT / "README.md").read_text(encoding="utf-8-sig")
        self.assertIn("fourteen currently bound feature scenarios", readme)
        self.assertIn("three hosted custom-policy scenarios", readme)
        self.assertIn("three nested Child DAG scenarios", readme)
        self.assertIn("two durable MCP effect evidence scenarios", readme)


if __name__ == "__main__":
    unittest.main()
