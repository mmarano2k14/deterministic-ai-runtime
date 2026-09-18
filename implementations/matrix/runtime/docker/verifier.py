from __future__ import annotations

import json
import sys
import time
from pathlib import Path

ROOT = Path("/matrix/evidence")
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
EXPECTED = [*CORE_EXPECTED, *FEATURE_EXPECTED, *CUSTOM_POLICY_EXPECTED]


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

    if failures:
        for failure in failures:
            print(failure, file=sys.stderr)
        return 1

    print("9/9 production-like Docker ProcessHostPool scenarios passed.")
    print("6/6 publication-pinning/dependency-package ProcessHostPool feature scenarios passed.")
    print("3/3 hosted custom-policy-family ProcessHostPool scenarios passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
