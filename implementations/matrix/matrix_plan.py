from __future__ import annotations

import json
from itertools import product
from pathlib import Path
from typing import Any

CLIENT_LANGUAGES = ("dotnet", "typescript", "python")
WORKER_LANGUAGES = ("dotnet", "typescript", "python")
CUSTOM_POLICY_FAMILIES = ("concurrency", "retry", "delegation")
DEPENDENCY_PACKAGE_BY_WORKER = {
    "dotnet": "DotNetAssemblyClosure",
    "typescript": "NodeLockedBundle",
    "python": "PythonWheelBundle",
}
REQUIRED_COVERAGE_TARGETS = {
    "publication-pinning",
    "deterministic-dependency-packaging",
    "custom-policy-family",
    "nested-child-dag",
    "cancellation",
    "recovery",
    "mcp-effect-evidence",
    "journal-result-acceptance",
    "worker-isolation-provider",
    "isolation-artifact-selection",
    "external-client-dependency-firewall",
}
REQUIRED_INVARIANTS = {
    "same-public-server-boundary",
    "existing-publication-authority",
    "existing-queue-and-scheduler-authority",
    "existing-recovery-authority",
    "existing-journal-result-acceptance-authority",
    "immutable-publication-run-pinning",
    "no-engine-dependency-in-external-clients",
    "no-worker-authority-over-dag-transitions",
    "no-runtime-package-installation",
    "no-silent-custom-to-native-fallback",
}
ALLOWED_EXECUTOR_KINDS = {"dotnet", "typescript", "python", "command"}


class MatrixPlanError(ValueError):
    pass


def default_plan_path() -> Path:
    return Path(__file__).with_name("multilanguage-runtime-matrix-v1.json")


def load_plan(path: Path | None = None) -> dict[str, Any]:
    resolved = path or default_plan_path()
    with resolved.open("r", encoding="utf-8-sig") as handle:
        data = json.load(handle)
    if not isinstance(data, dict):
        raise MatrixPlanError("Matrix plan root must be a JSON object.")
    return data


def validate_plan(plan: dict[str, Any]) -> list[str]:
    errors: list[str] = []

    if plan.get("schemaVersion") != 1:
        errors.append("schemaVersion must be 1.")
    if plan.get("matrixId") != "multilanguage-runtime-v1":
        errors.append("matrixId must be 'multilanguage-runtime-v1'.")

    languages = plan.get("languages")
    if not isinstance(languages, dict):
        errors.append("languages must be an object.")
    else:
        _require_exact_sequence(errors, "languages.clients", languages.get("clients"), CLIENT_LANGUAGES)
        _require_exact_sequence(errors, "languages.workers", languages.get("workers"), WORKER_LANGUAGES)

    if plan.get("dependencyPackageByWorker") != DEPENDENCY_PACKAGE_BY_WORKER:
        errors.append("dependencyPackageByWorker must exactly match the supported worker/package mapping.")

    _require_exact_sequence(
        errors,
        "customPolicyFamilies",
        plan.get("customPolicyFamilies"),
        CUSTOM_POLICY_FAMILIES,
    )

    invariants = plan.get("invariants")
    if (
        not isinstance(invariants, list)
        or len(invariants) != len(REQUIRED_INVARIANTS)
        or set(invariants) != REQUIRED_INVARIANTS
    ):
        errors.append("invariants must contain exactly the required runtime-authority and SDK-boundary invariants.")

    _validate_core_scenarios(errors, plan.get("coreScenarios"))
    _validate_coverage_targets(errors, plan.get("coverageTargets"))
    _validate_feature_scenarios(errors, plan.get("featureScenarios"))

    return errors


def assert_valid_plan(plan: dict[str, Any]) -> None:
    errors = validate_plan(plan)
    if errors:
        raise MatrixPlanError("\n".join(errors))


def _validate_core_scenarios(errors: list[str], value: Any) -> None:
    if not isinstance(value, list):
        errors.append("coreScenarios must be an array.")
        return

    if len(value) != len(CLIENT_LANGUAGES) * len(WORKER_LANGUAGES):
        errors.append("coreScenarios must contain exactly the 3 x 3 client/worker cross-product.")

    expected_pairs = set(product(CLIENT_LANGUAGES, WORKER_LANGUAGES))
    observed_pairs: set[tuple[str, str]] = set()
    ids: set[str] = set()

    for index, scenario in enumerate(value):
        prefix = f"coreScenarios[{index}]"
        if not isinstance(scenario, dict):
            errors.append(f"{prefix} must be an object.")
            continue

        scenario_id = scenario.get("id")
        if not isinstance(scenario_id, str) or not scenario_id.strip():
            errors.append(f"{prefix}.id must be non-empty.")
        elif scenario_id in ids:
            errors.append(f"Duplicate scenario id '{scenario_id}'.")
        else:
            ids.add(scenario_id)

        client = scenario.get("clientLanguage")
        worker = scenario.get("workerLanguage")
        if client not in CLIENT_LANGUAGES:
            errors.append(f"{prefix}.clientLanguage is unsupported: {client!r}.")
        if worker not in WORKER_LANGUAGES:
            errors.append(f"{prefix}.workerLanguage is unsupported: {worker!r}.")
        if client in CLIENT_LANGUAGES and worker in WORKER_LANGUAGES:
            observed_pairs.add((client, worker))
            expected_id = f"core-{client}-client-{worker}-worker"
            if scenario_id != expected_id:
                errors.append(f"{prefix}.id must be '{expected_id}' for its client/worker pair.")

        _validate_executor(errors, prefix, scenario.get("executor"))

        evidence = scenario.get("requiredEvidence")
        if not isinstance(evidence, list) or not evidence:
            errors.append(f"{prefix}.requiredEvidence must be a non-empty array.")

        if any(key in scenario for key in ("status", "passed", "executed", "skipped")):
            errors.append(f"{prefix} must describe the plan only; execution outcomes belong in evidence results.")

    if observed_pairs != expected_pairs:
        missing = sorted(expected_pairs - observed_pairs)
        extra = sorted(observed_pairs - expected_pairs)
        if missing:
            errors.append(f"coreScenarios is missing client/worker pairs: {missing!r}.")
        if extra:
            errors.append(f"coreScenarios contains unsupported client/worker pairs: {extra!r}.")


def _validate_coverage_targets(errors: list[str], value: Any) -> None:
    if not isinstance(value, list):
        errors.append("coverageTargets must be an array.")
        return

    ids: list[str] = []
    for index, target in enumerate(value):
        prefix = f"coverageTargets[{index}]"
        if not isinstance(target, dict):
            errors.append(f"{prefix} must be an object.")
            continue

        target_id = target.get("id")
        if not isinstance(target_id, str) or not target_id.strip():
            errors.append(f"{prefix}.id must be non-empty.")
            continue
        ids.append(target_id)

        minimum = target.get("minimumExecutedScenarios")
        if not isinstance(minimum, int) or isinstance(minimum, bool) or minimum < 1:
            errors.append(f"{prefix}.minimumExecutedScenarios must be an integer >= 1.")

        _validate_language_subset(errors, f"{prefix}.requiredClientLanguages", target.get("requiredClientLanguages", []), CLIENT_LANGUAGES)
        _validate_language_subset(errors, f"{prefix}.requiredWorkerLanguages", target.get("requiredWorkerLanguages", []), WORKER_LANGUAGES)

        required_values = target.get("requiredValues")
        if not isinstance(required_values, list):
            errors.append(f"{prefix}.requiredValues must be an array.")

        notes = target.get("notes")
        if not isinstance(notes, str) or not notes.strip():
            errors.append(f"{prefix}.notes must explain the bounded coverage requirement.")

    if len(ids) != len(set(ids)):
        errors.append("coverageTargets contains duplicate ids.")
    if set(ids) != REQUIRED_COVERAGE_TARGETS:
        errors.append("coverageTargets must contain exactly the required final-roadmap coverage targets.")

    by_id = {target.get("id"): target for target in value if isinstance(target, dict)}
    dependency = by_id.get("deterministic-dependency-packaging", {})
    if set(dependency.get("requiredValues", [])) != set(DEPENDENCY_PACKAGE_BY_WORKER.values()):
        errors.append("deterministic-dependency-packaging must require all three supported package kinds.")

    policy = by_id.get("custom-policy-family", {})
    if tuple(policy.get("requiredValues", [])) != CUSTOM_POLICY_FAMILIES:
        errors.append("custom-policy-family must require only concurrency, retry and delegation.")


def _validate_feature_scenarios(errors: list[str], value: Any) -> None:
    if not isinstance(value, list):
        errors.append("featureScenarios must be an array.")
        return

    ids: set[str] = set()
    for index, scenario in enumerate(value):
        prefix = f"featureScenarios[{index}]"
        if not isinstance(scenario, dict):
            errors.append(f"{prefix} must be an object.")
            continue

        scenario_id = scenario.get("id")
        if not isinstance(scenario_id, str) or not scenario_id.strip():
            errors.append(f"{prefix}.id must be non-empty.")
        elif scenario_id in ids:
            errors.append(f"Duplicate feature scenario id '{scenario_id}'.")
        else:
            ids.add(scenario_id)

        target_id = scenario.get("coverageTarget")
        if target_id not in REQUIRED_COVERAGE_TARGETS:
            errors.append(f"{prefix}.coverageTarget is unsupported: {target_id!r}.")

        client = scenario.get("clientLanguage")
        if client is not None and client not in CLIENT_LANGUAGES:
            errors.append(f"{prefix}.clientLanguage is unsupported: {client!r}.")

        worker = scenario.get("workerLanguage")
        if worker is not None and worker not in WORKER_LANGUAGES:
            errors.append(f"{prefix}.workerLanguage is unsupported: {worker!r}.")

        _validate_executor(errors, prefix, scenario.get("executor"))

        values = scenario.get("coverageValues", [])
        if not isinstance(values, list):
            errors.append(f"{prefix}.coverageValues must be an array.")

        if any(key in scenario for key in ("status", "passed", "executed", "skipped")):
            errors.append(f"{prefix} must not claim an execution outcome inside the plan.")


def _validate_executor(errors: list[str], prefix: str, executor: Any) -> None:
    if executor is None:
        return
    if not isinstance(executor, dict):
        errors.append(f"{prefix}.executor must be null or an object.")
        return

    kind = executor.get("kind")
    if kind not in ALLOWED_EXECUTOR_KINDS:
        errors.append(f"{prefix}.executor.kind is unsupported: {kind!r}.")
    command = executor.get("command")
    if not isinstance(command, list) or not command or not all(isinstance(item, str) and item for item in command):
        errors.append(f"{prefix}.executor.command must be a non-empty string array.")


def _require_exact_sequence(errors: list[str], name: str, actual: Any, expected: tuple[str, ...]) -> None:
    if not isinstance(actual, list) or tuple(actual) != expected:
        errors.append(f"{name} must exactly equal {list(expected)!r}.")


def _validate_language_subset(errors: list[str], name: str, actual: Any, allowed: tuple[str, ...]) -> None:
    if not isinstance(actual, list):
        errors.append(f"{name} must be an array.")
        return
    if len(actual) != len(set(actual)):
        errors.append(f"{name} must not contain duplicates.")
    unknown = [value for value in actual if value not in allowed]
    if unknown:
        errors.append(f"{name} contains unsupported languages: {unknown!r}.")
