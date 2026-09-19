from __future__ import annotations

import json
import sys
import time
from pathlib import Path

ROOT = Path("/matrix/evidence")
PLAN_PATH = Path("/app/matrix-plan.json")
CLIENTS = ("dotnet", "typescript", "python")
WORKERS = ("dotnet", "typescript", "python")
CORE_EXPECTED = [f"core-{client}-client-{worker}-worker" for client in CLIENTS for worker in WORKERS]
PACKAGE_BY_WORKER = {
    "dotnet": "DotNetAssemblyClosure",
    "typescript": "NodeLockedBundle",
    "python": "PythonWheelBundle",
}
FEATURE_EXPECTED = {
    **{
        f"feature-publication-pinning-python-client-{worker}-worker": (
            "publication-pinning",
            worker,
            None,
        )
        for worker in WORKERS
    },
    **{
        f"feature-dependency-package-python-client-{worker}-worker": (
            "deterministic-dependency-packaging",
            worker,
            PACKAGE_BY_WORKER[worker],
        )
        for worker in WORKERS
    },
}
CUSTOM_POLICY_EXPECTED = {
    "feature-custom-policy-concurrency-python-worker": ("concurrency", "python", "Completed", "allow"),
    "feature-custom-policy-retry-typescript-worker": ("retry", "typescript", "Failed", "stop"),
    "feature-custom-policy-delegation-dotnet-worker": ("delegation", "dotnet", "Failed", "deny"),
}
NESTED_CHILD_DAG_EXPECTED = {
    f"feature-nested-child-dag-python-client-{worker}-worker": worker
    for worker in WORKERS
}
CANCELLATION_EXPECTED = {
    f"feature-cancellation-{language}-client-{language}-worker": (language, language)
    for language in CLIENTS
}
MCP_EFFECT_EXPECTED = {
    "feature-mcp-effect-completed-local-replay-python-client": ("completed-local-replay", "Completed"),
    "feature-mcp-effect-uncertain-blocks-blind-resend-python-client": ("uncertain-blocks-blind-resend", "Uncertain"),
}
RECOVERY_EXPECTED = {
    "feature-recovery-in-flight-resume-python-client": "in-flight-resume",
    "feature-recovery-local-queued-redispatch-python-client": "local-queued-redispatch",
}
FIREWALL_EXPECTED = {
    f"feature-external-client-dependency-firewall-{language}-client": language
    for language in CLIENTS
}
JOURNAL_RESULT_ACCEPTANCE_EXPECTED = {
    "feature-journal-result-accepted-replay-python-client": "accepted-result-replay",
    "feature-journal-duplicate-delivery-convergence-python-client": "duplicate-delivery-convergence",
}
ISOLATION_EXPECTED = {
    "feature-worker-isolation-provider-trusted-process-python-worker": (
        "worker-isolation-provider", "trusted-process", "trusted-process", "HostRuntime",
        "process", "matrix-python", "ProcessHostPool",
    ),
    "feature-worker-isolation-provider-sandboxed-container-python-worker": (
        "worker-isolation-provider", "sandboxed-container", "sandboxed-container", "OciImage",
        "container", "matrix-python-oci", "ContainerIsolationProvider",
    ),
    "feature-isolation-artifact-selection-host-runtime-python-worker": (
        "isolation-artifact-selection", "HostRuntime", "trusted-process", "HostRuntime",
        "process", "matrix-python", "ProcessHostPool",
    ),
    "feature-isolation-artifact-selection-oci-image-python-worker": (
        "isolation-artifact-selection", "OciImage", "sandboxed-container", "OciImage",
        "container", "matrix-python-oci", "ContainerIsolationProvider",
    ),
}
EXPECTED = [
    *CORE_EXPECTED,
    *FEATURE_EXPECTED,
    *CUSTOM_POLICY_EXPECTED,
    *NESTED_CHILD_DAG_EXPECTED,
    *MCP_EFFECT_EXPECTED,
    *CANCELLATION_EXPECTED,
    *RECOVERY_EXPECTED,
    *JOURNAL_RESULT_ACCEPTANCE_EXPECTED,
    *ISOLATION_EXPECTED,
    *FIREWALL_EXPECTED,
]


def _load(scenario: str) -> dict[str, object] | None:
    path = ROOT / f"{scenario}.json"
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8-sig"))


def _validate_common(scenario: str, document: dict[str, object]) -> str | None:
    if document.get("scenarioId") != scenario or document.get("status") != "passed":
        return "invalid status or identity"
    if document.get("topology") != "docker" or document.get("provider") != "ProcessHostPool":
        return "wrong topology/provider evidence"
    return None


def _validate_core(scenario: str, document: dict[str, object]) -> str | None:
    common = _validate_common(scenario, document)
    if common:
        return common
    required = {"publish", "submit", "observe", "terminal-result", "public-execution-id"}
    if not required.issubset(set(document.get("evidence", []))):
        return "missing required core evidence"
    return None


def _validate_feature(
    scenario: str,
    document: dict[str, object],
    target: str,
    worker: str,
    package_kind: str | None,
) -> str | None:
    common = _validate_common(scenario, document)
    if common:
        return common
    if document.get("coverageTarget") != target:
        return "wrong coverage target"
    if document.get("clientLanguage") != "python" or document.get("workerLanguage") != worker:
        return "wrong client/worker feature evidence"
    if document.get("terminalStatus") != "Completed":
        return "feature execution did not complete"

    evidence = set(document.get("evidence", []))
    if target == "publication-pinning":
        required = {
            "publish-original",
            "submit-original",
            "publish-replacement",
            "observe-original-pin-after-republication",
            "replacement-poison-not-executed",
            "terminal-result",
        }
        original = document.get("originalPublicationRef")
        replacement = document.get("replacementPublicationRef")
        if not isinstance(original, str) or not original:
            return "missing original publication reference"
        if not isinstance(replacement, str) or not replacement or replacement == original:
            return "replacement publication did not establish a distinct immutable reference"
        if document.get("submittedPublicationRef") != original:
            return "submitted execution was not pinned to the original publication"
        if document.get("postRepublishObservedPublicationRef") != original:
            return "post-republication observation did not retain the original publication"
        if document.get("terminalObservedPublicationRef") != original:
            return "terminal observation did not retain the original publication"
        if document.get("postRepublishObservedStatus") in {"Completed", "Failed", "Cancelled"}:
            return "post-republication observation did not occur while the run was nonterminal"
        if document.get("coverageValues") != []:
            return "publication pinning invented coverage values"
        if not required.issubset(evidence):
            return "missing publication-pinning evidence"
        return None

    expected_values = [package_kind]
    if document.get("coverageValues") != expected_values:
        return "wrong deterministic package coverage value"
    if document.get("packageKind") != package_kind:
        return "wrong deterministic package kind"
    required = {"publish", "submit", "packaged-dependency", "observe", "terminal-result"}
    if not required.issubset(evidence):
        return "missing deterministic dependency-package evidence"
    return None



def _validate_custom_policy(
    scenario: str,
    document: dict[str, object],
    family: str,
    worker: str,
    expected_terminal: str,
    decision: str,
) -> str | None:
    common = _validate_common(scenario, document)
    if common:
        return common
    if document.get("coverageTarget") != "custom-policy-family":
        return "wrong custom policy coverage target"
    if document.get("coverageValues") != [family] or document.get("policyFamily") != family:
        return "wrong custom policy family evidence"
    if document.get("clientLanguage") != "python" or document.get("workerLanguage") != worker:
        return "wrong custom policy client/worker evidence"
    if document.get("policyDecision") != decision:
        return "wrong custom policy decision evidence"
    if document.get("expectedTerminalStatus") != expected_terminal or document.get("terminalStatus") != expected_terminal:
        return "custom policy terminal outcome did not match the bounded behavioral proof"
    required = {"publish", "submit", "hosted-custom-policy", f"policy-{decision}-enforced", "observe", "terminal-result"}
    if not required.issubset(set(document.get("evidence", []))):
        return "missing hosted custom-policy evidence"
    return None


def _validate_nested_child_dag(
    scenario: str,
    document: dict[str, object],
    worker: str,
) -> str | None:
    common = _validate_common(scenario, document)
    if common:
        return common
    if document.get("coverageTarget") != "nested-child-dag":
        return "wrong nested Child DAG coverage target"
    if document.get("coverageValues") != []:
        return "nested Child DAG evidence invented coverage values"
    if document.get("clientLanguage") != "python" or document.get("workerLanguage") != worker:
        return "wrong nested Child DAG client/worker evidence"
    if document.get("terminalStatus") != "Completed":
        return "nested Child DAG execution did not complete"
    if document.get("nestedDepth") != 2:
        return "nested Child DAG depth proof is not exactly two"
    if document.get("definitionPath") != "/invoke-child/invoke-grandchild":
        return "nested custom declaration path is incorrect"
    if document.get("rootChildStep") != "invoke-child" or document.get("nestedChildStep") != "invoke-grandchild":
        return "nested Child DAG call-site evidence is incorrect"
    if document.get("leafStep") != "leaf":
        return "nested Child DAG leaf evidence is incorrect"
    if document.get("rootStepStatus") != "Completed":
        return "nested Child DAG parent continuation did not complete"
    required = {
        "publish-nested-definition",
        "submit-root",
        "nested-child-dispatch",
        "nested-grandchild-custom-declaration",
        "parent-continuation",
        "observe",
        "terminal-result",
    }
    if not required.issubset(set(document.get("evidence", []))):
        return "missing nested Child DAG evidence"
    return None


def _validate_mcp_effect(
    scenario: str,
    document: dict[str, object],
    effect_case: str,
    expected_evidence_status: str,
) -> str | None:
    common = _validate_common(scenario, document)
    if common:
        return common
    if document.get("coverageTarget") != "mcp-effect-evidence":
        return "wrong MCP effect evidence coverage target"
    if document.get("coverageValues") != [effect_case]:
        return "wrong MCP effect evidence value"
    if document.get("clientLanguage") != "python" or document.get("workerLanguage") is not None:
        return "MCP effect evidence incorrectly claims a hosted worker"
    if document.get("terminalStatus") != "Failed" or document.get("stepStatus") != "Failed":
        return "MCP effect evidence scenario did not fail after its bounded retry"
    if document.get("durableEvidenceStatus") != expected_evidence_status:
        return "wrong durable MCP effect evidence status"
    if document.get("retryCount") != 1:
        return "MCP effect evidence did not prove exactly one logical retry"
    if document.get("physicalCallCount") != 1:
        return "MCP effect evidence did not fence duplicate physical tools/call emission"
    effect_id = document.get("effectId")
    if not isinstance(effect_id, str) or not effect_id.startswith("mcp-effect-v1-"):
        return "missing durable MCP effect identity"

    evidence = set(document.get("evidence", []))
    common_required = {"publish", "submit", "logical-retry-observed", "single-physical-tools-call", "terminal-result"}
    if not common_required.issubset(evidence):
        return "missing common MCP effect evidence"

    if effect_case == "completed-local-replay":
        if document.get("durableResultIsError") is not True or document.get("uncertaintyReasonCode") is not None:
            return "completed replay did not preserve confirmed result evidence"
        if not {"durable-effect-completed", "completed-result-replayed-locally"}.issubset(evidence):
            return "missing completed local replay evidence"
        return None

    if document.get("durableResultIsError") is not None:
        return "uncertain effect unexpectedly contains a confirmed result"
    if document.get("uncertaintyReasonCode") != "transport-timeout":
        return "uncertain effect did not preserve the transport timeout reason"
    if not {"durable-effect-uncertain", "blind-resend-blocked"}.issubset(evidence):
        return "missing uncertain blind-resend fencing evidence"
    return None


def _validate_recovery(
    scenario: str,
    document: dict[str, object],
    recovery_case: str,
) -> str | None:
    common = _validate_common(scenario, document)
    if common:
        return common
    if document.get("coverageTarget") != "recovery":
        return "wrong recovery coverage target"
    if document.get("coverageValues") != [recovery_case]:
        return "wrong recovery coverage value"
    if document.get("clientLanguage") != "python" or document.get("workerLanguage") is not None:
        return "recovery evidence incorrectly claims a hosted worker"
    if document.get("runtimeIndexStatus") != "requeued-for-recovery":
        return "recovery did not durably mark the failed ownership as requeued"
    if document.get("recoveryAction") != "requeue-shared-run":
        return "recovery did not use the shared-run requeue transition"
    if document.get("terminalStatus") != "Completed":
        return "recovered execution did not converge to Completed"

    evidence = set(document.get("evidence", []))
    common_required = {"production-recovery-reconciler", "shared-run-requeued-for-recovery", "terminal-result"}
    if not common_required.issubset(evidence):
        return "missing common recovery evidence"

    if recovery_case == "in-flight-resume":
        if document.get("originalExecutionId") != document.get("recoveredExecutionId"):
            return "in-flight resume changed the durable execution identity"
        if document.get("activeStatusBeforeRecovery") in {"Completed", "Failed", "Cancelled"}:
            return "in-flight recovery was not triggered against an active execution"
        if document.get("activeStepStatusBeforeRecovery") not in {"Running", "WaitingForExternal"}:
            return "in-flight recovery was not triggered while the step was active"
        if not {"active-execution-observed", "same-execution-id-resumed"}.issubset(evidence):
            return "missing in-flight resume evidence"
        return None

    redispatched = document.get("redispatchedExecutionId")
    if document.get("preRecoveryExecutionId") is not None:
        return "local-queued recovery incorrectly carried an execution id before redispatch"
    if not isinstance(redispatched, str) or not redispatched:
        return "local-queued recovery did not create an execution after redispatch"
    if not document.get("replacementRuntimeInstanceId") or not document.get("replacementLocalRunId"):
        return "local-queued recovery did not prove replacement runtime ownership"
    if document.get("replacementRuntimeIndexStatus") != "completed":
        return "local-queued replacement runtime index did not converge to completed"
    if not {"public-sdk-definition-seed", "local-queued-no-execution-id", "local-queued-ownership-seeded", "healthy-runtime-redispatch", "new-execution-id-created"}.issubset(evidence):
        return "missing local-queued redispatch evidence"
    return None


def _validate_journal_result_acceptance(
    scenario: str,
    document: dict[str, object],
    acceptance_case: str,
) -> str | None:
    common = _validate_common(scenario, document)
    if common:
        return common
    if document.get("coverageTarget") != "journal-result-acceptance":
        return "wrong journal result-acceptance coverage target"
    if document.get("coverageValues") != [acceptance_case]:
        return "wrong journal result-acceptance coverage value"
    if document.get("clientLanguage") != "python" or document.get("workerLanguage") is not None:
        return "journal result acceptance incorrectly claims a hosted worker"
    if document.get("terminalStatus") != "Succeeded" or document.get("continuationStatus") != "Pending":
        return "journal terminal result or continuation intent is incorrect"
    if document.get("leaseEpoch") != 1:
        return "journal acceptance did not preserve epoch-one lease fencing"
    result_hash = document.get("resultSha256")
    if not isinstance(result_hash, str) or len(result_hash) != 64:
        return "journal result hash evidence is missing"

    evidence = set(document.get("evidence", []))
    if not {"mongo-backed-journal-prepare", "lease-epoch-acquired", "pending-continuation-preserved"}.issubset(evidence):
        return "missing common journal acceptance evidence"

    if acceptance_case == "accepted-result-replay":
        if document.get("firstCompletionStatus") != "Accepted" or document.get("replayCompletionStatus") != "AlreadyAccepted":
            return "accepted result replay did not converge idempotently"
        if not {"result-accepted", "fresh-journal-reload", "identical-result-replay-already-accepted"}.issubset(evidence):
            return "missing accepted-result replay evidence"
        return None

    if document.get("deliveryCount") != 8 or document.get("acceptedCount") != 1:
        return "duplicate delivery convergence did not produce exactly one accepted result"
    if document.get("alreadyAcceptedCount") != 7 or document.get("leaseRejectedCount") != 0:
        return "duplicate delivery convergence did not acknowledge all identical duplicates"
    if not {"concurrent-duplicate-deliveries", "single-result-accepted", "duplicates-converged-already-accepted"}.issubset(evidence):
        return "missing duplicate-delivery convergence evidence"
    return None


def _validate_cancellation(
    scenario: str,
    document: dict[str, object],
    client: str,
    worker: str,
) -> str | None:
    common = _validate_common(scenario, document)
    if common:
        return common
    if document.get("coverageTarget") != "cancellation":
        return "wrong cancellation coverage target"
    if document.get("coverageValues") != []:
        return "cancellation invented coverage values"
    if document.get("cancellationMode") != "running-cooperative":
        return "wrong cancellation mode"
    if document.get("clientLanguage") != client or document.get("workerLanguage") != worker:
        return "wrong cancellation client/worker evidence"
    if document.get("cancellationRequested") is not True:
        return "public cancellation was not acknowledged"
    if not document.get("cancellationRequestedAtUtc") or not document.get("cancellationCorrelationId"):
        return "durable cancellation request metadata is missing"
    if document.get("activeStatusBeforeCancel") in {"Completed", "Failed", "Cancelled"}:
        return "cancellation was not issued against an active execution"
    if document.get("activeStepStatusBeforeCancel") not in {"Running", "WaitingForExternal"}:
        return "cancellation was not issued while the hosted step was active"
    if document.get("terminalStatus") != "Cancelled":
        return "execution did not converge to Cancelled"
    required = {
        "publish", "submit", "active-execution-observed", "sdk-execution-cancel",
        "durable-cancellation-acknowledged", "terminal-cancelled-observed", "terminal-result",
    }
    if not required.issubset(set(document.get("evidence", []))):
        return "missing cancellation evidence"
    return None



def _validate_dependency_firewall(
    scenario: str,
    document: dict[str, object],
    client: str,
) -> str | None:
    common = _validate_common(scenario, document)
    if common:
        return common
    if document.get("coverageTarget") != "external-client-dependency-firewall":
        return "wrong external-client dependency-firewall coverage target"
    if document.get("coverageValues") != []:
        return "dependency firewall invented coverage values"
    if document.get("clientLanguage") != client or document.get("workerLanguage") is not None:
        return "wrong dependency-firewall client/worker evidence"
    evidence = set(document.get("evidence", []))
    if "no-engine-runtime-dependency" not in evidence:
        return "dependency firewall did not record the no-engine/runtime proof"

    if client == "dotnet":
        if document.get("sdkRepositoryReferences") != ["Multiplexed.AI.Sdk.Contracts"]:
            return "the .NET SDK repository reference graph is not limited to public contracts"
        if document.get("contractRepositoryReferences") != []:
            return "the .NET public contracts assembly has repository dependencies"
        if document.get("forbiddenRepositoryDependencies") != []:
            return "the .NET published client contains forbidden repository dependencies"
        return None

    if client == "typescript":
        if document.get("forbiddenDeclaredDependencies") != [] or document.get("forbiddenDistImports") != []:
            return "the TypeScript SDK contains forbidden repository dependencies"
        return None

    if document.get("forbiddenDeclaredDependencies") != [] or document.get("forbiddenAbsoluteImports") != []:
        return "the Python SDK contains forbidden repository dependencies"
    return None


def _validate_isolation_closure(
    scenario: str,
    document: dict[str, object],
    target: str,
    value: str,
    isolation_provider: str,
    artifact_kind: str,
    environment_profile: str,
    environment_ref: str,
    provider: str,
) -> str | None:
    if document.get("scenarioId") != scenario or document.get("status") != "passed":
        return "invalid status or identity"
    if document.get("topology") != "docker" or document.get("provider") != provider:
        return "wrong topology/provider evidence"
    if document.get("coverageTarget") != target or document.get("coverageValues") != [value]:
        return "wrong isolation coverage target/value"
    if document.get("clientLanguage") != "python" or document.get("workerLanguage") != "python":
        return "isolation closure must execute through the Python external SDK and Python hosted worker"
    if document.get("terminalStatus") != "Completed":
        return "isolation closure execution did not complete"
    if document.get("environmentProfile") != environment_profile:
        return "wrong environment profile evidence"
    if document.get("environmentRef") != environment_ref:
        return "wrong immutable environment reference"
    if document.get("isolationProvider") != isolation_provider:
        return "wrong isolation provider evidence"
    if document.get("artifactKind") != artifact_kind:
        return "wrong artifact-kind evidence"

    evidence = set(document.get("evidence", []))
    required = {
        "publish",
        "submit",
        "selected-environment-ref",
        "production-hosted-worker-executed",
        "terminal-result",
    }
    if isolation_provider == "sandboxed-container":
        required.update({
            "sandboxed-container-probe-passed",
            "non-root-worker",
            "read-only-rootfs",
            "network-none-loopback-only",
        })
    if not required.issubset(evidence):
        return "missing isolation execution evidence"
    return None


def _write_exact_coverage_closure() -> None:
    plan = json.loads(PLAN_PATH.read_text(encoding="utf-8-sig"))
    feature_targets = {scenario["coverageTarget"] for scenario in plan["featureScenarios"]}
    expected_executed_targets = {
        "publication-pinning",
        "deterministic-dependency-packaging",
        "custom-policy-family",
        "nested-child-dag",
        "mcp-effect-evidence",
        "cancellation",
        "recovery",
        "journal-result-acceptance",
        "worker-isolation-provider",
        "isolation-artifact-selection",
        "external-client-dependency-firewall",
    }
    if feature_targets != expected_executed_targets:
        raise RuntimeError(f"Exact coverage target set mismatch: {sorted(feature_targets)!r}.")
    plan_targets = {target["id"] for target in plan["coverageTargets"]}
    if plan_targets != feature_targets:
        raise RuntimeError("Every declared final-roadmap coverage target must have executed matrix bindings.")

    closure = {
        "schemaVersion": 1,
        "status": "passed",
        "topology": "docker",
        "provider": "mixed-hosted-worker-providers",
        "providers": ["ProcessHostPool", "ContainerIsolationProvider"],
        "executedScenarioCount": len(EXPECTED),
        "coreScenarioCount": len(CORE_EXPECTED),
        "featureScenarioCount": len(EXPECTED) - len(CORE_EXPECTED),
        "executedCoverageTargets": sorted(expected_executed_targets),
        "deferredCoverageTargets": [],
        "clientLanguages": list(CLIENTS),
        "workerLanguages": list(WORKERS),
        "dependencyPackageKinds": sorted(PACKAGE_BY_WORKER.values()),
        "customPolicyFamilies": ["concurrency", "retry", "delegation"],
        "recoveryPaths": ["in-flight-resume", "local-queued-redispatch"],
        "mcpEffectCases": ["completed-local-replay", "uncertain-blocks-blind-resend"],
        "journalAcceptanceCases": ["accepted-result-replay", "duplicate-delivery-convergence"],
        "workerIsolationProviders": ["trusted-process", "sandboxed-container"],
        "isolationArtifactKinds": ["HostRuntime", "OciImage"],
        "claimBoundary": "Only combinations represented by passed evidence documents are executed coverage; the OCI path is the local/CI Docker-socket profile, not production Kubernetes.",
    }
    (ROOT / "executed-coverage-closure.json").write_text(json.dumps(closure, indent=2) + "\n", encoding="utf-8")

def main() -> int:
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline and any(not (ROOT / f"{scenario}.json").exists() for scenario in EXPECTED):
        time.sleep(0.25)

    failures: list[str] = []
    for scenario in CORE_EXPECTED:
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_core(scenario, document)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    for scenario, (target, worker, package_kind) in FEATURE_EXPECTED.items():
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_feature(scenario, document, target, worker, package_kind)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    for scenario, (family, worker, expected_terminal, decision) in CUSTOM_POLICY_EXPECTED.items():
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_custom_policy(scenario, document, family, worker, expected_terminal, decision)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    for scenario, worker in NESTED_CHILD_DAG_EXPECTED.items():
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_nested_child_dag(scenario, document, worker)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    for scenario, (effect_case, expected_status) in MCP_EFFECT_EXPECTED.items():
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_mcp_effect(scenario, document, effect_case, expected_status)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    for scenario, (client, worker) in CANCELLATION_EXPECTED.items():
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_cancellation(scenario, document, client, worker)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    for scenario, recovery_case in RECOVERY_EXPECTED.items():
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_recovery(scenario, document, recovery_case)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    for scenario, acceptance_case in JOURNAL_RESULT_ACCEPTANCE_EXPECTED.items():
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_journal_result_acceptance(scenario, document, acceptance_case)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    for scenario, expected in ISOLATION_EXPECTED.items():
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_isolation_closure(scenario, document, *expected)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    for scenario, client in FIREWALL_EXPECTED.items():
        document = _load(scenario)
        if document is None:
            failures.append(f"{scenario}: missing evidence")
            continue
        error = _validate_dependency_firewall(scenario, document, client)
        if error:
            failures.append(f"{scenario}: {error}")
        else:
            print(f"{scenario}: PASSED")

    if failures:
        for failure in failures:
            print(failure, file=sys.stderr)
        return 1

    print("9/9 production-like Docker ProcessHostPool scenarios passed.")
    print("6/6 publication-pinning/dependency-package ProcessHostPool feature scenarios passed.")
    print("3/3 hosted custom-policy-family ProcessHostPool scenarios passed.")
    print("3/3 nested Child DAG ProcessHostPool scenarios passed.")
    print("2/2 durable MCP effect evidence ProcessHostPool scenarios passed.")
    print("3/3 durable cancellation SDK-client scenarios passed.")
    print("2/2 runtime recovery ProcessHostPool scenarios passed.")
    print("2/2 durable journal result-acceptance scenarios passed.")
    print("3/3 external client dependency-firewall scenarios passed.")
    print("2/2 worker-isolation-provider scenarios passed (trusted process + sandboxed container).")
    print("2/2 isolation-artifact-selection scenarios passed (HostRuntime + OciImage).")
    _write_exact_coverage_closure()
    print("Exact executed-coverage closure: 37/37 scenarios; topology=docker; providers=ProcessHostPool+ContainerIsolationProvider.")
    print("Fixture-free closure: public SDK samples + production hosted workers; matrix fixture tree not required.")
    print("OCI closure profile: sibling worker containers through the host Docker socket; no Docker-in-Docker and no Kubernetes claim.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
