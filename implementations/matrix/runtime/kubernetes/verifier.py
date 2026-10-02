from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any

EXACT_IMAGE = re.compile(r"^.+@sha256:[0-9a-fA-F]{64}$")


def _load(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8-sig") as handle:
        value = json.load(handle)
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path}")
    return value


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def verify(plan_path: Path, evidence_path: Path, require_immutable_image: bool) -> None:
    plan = _load(plan_path)
    evidence = _load(evidence_path)
    scenario = plan.get("scenario")
    _require(isinstance(scenario, dict), "Plan scenario must be an object.")

    _require(plan.get("schemaVersion") == 1, "Plan schemaVersion must be 1.")
    _require(plan.get("matrixId") == "kubernetes-pool-runtime-execution-v1", "Unexpected KubernetesPool matrix id.")
    _require(plan.get("baselineExecutedCoverage") == 37, "KubernetesPool matrix must preserve the validated 37/37 baseline.")

    _require(evidence.get("schemaVersion") == 1, "Evidence schemaVersion must be 1.")
    _require(evidence.get("scenarioId") == scenario.get("id"), "Evidence scenario id does not match the plan.")
    _require(str(evidence.get("status", "")).lower() == "passed", "KubernetesPool execution evidence is not passed.")
    _require(evidence.get("topology") == "kubernetes", "Evidence topology must be kubernetes.")
    _require(evidence.get("runtimeProvider") == "KubernetesPool", "Evidence runtimeProvider must be KubernetesPool.")
    _require(evidence.get("transport") == scenario.get("transport"), "Evidence transport does not match the plan.")

    runtime_image = evidence.get("runtimeImage")
    _require(isinstance(runtime_image, str) and runtime_image.strip(), "Evidence runtimeImage must be non-empty.")
    if require_immutable_image:
        _require(bool(EXACT_IMAGE.fullmatch(runtime_image.strip())), "Immutable Kubernetes matrix execution requires repository@sha256:<64-hex>.")
        _require(evidence.get("immutableRuntimeImage") is True, "Evidence must mark the runtime image immutable.")

    kubernetes = evidence.get("kubernetes")
    _require(isinstance(kubernetes, dict), "Evidence kubernetes section must be an object.")
    _require(isinstance(kubernetes.get("namespaceName"), str) and kubernetes["namespaceName"].strip(), "Kubernetes namespace evidence is missing.")
    _require(isinstance(kubernetes.get("podName"), str) and kubernetes["podName"].strip(), "Kubernetes Pod evidence is missing.")
    _require(isinstance(kubernetes.get("serviceName"), str) and kubernetes["serviceName"].strip(), "Kubernetes Service evidence is missing.")

    runtime_ids = kubernetes.get("runtimeInstanceIds")
    _require(isinstance(runtime_ids, list), "runtimeInstanceIds must be an array.")
    _require(len(runtime_ids) >= int(scenario["minimumRuntimeInstanceCount"]), "Runtime-instance evidence is below the plan minimum.")
    _require(len(runtime_ids) == len(set(runtime_ids)), "Runtime-instance identities must be unique.")
    _require(kubernetes.get("runtimeInstanceCount") == len(runtime_ids), "runtimeInstanceCount does not match runtimeInstanceIds.")
    _require(int(kubernetes.get("commandCount", 0)) >= int(scenario["minimumSuccessfulCommandCount"]), "Successful routed command evidence is below the plan minimum.")
    _require(kubernetes.get("cleanupSucceeded") is True, "KubernetesPool cleanup evidence must be true.")

    kinds = evidence.get("evidenceKinds")
    _require(isinstance(kinds, list), "evidenceKinds must be an array.")
    missing = sorted(set(scenario.get("requiredEvidenceKinds", [])) - set(kinds))
    _require(not missing, f"KubernetesPool evidence is missing required kinds: {missing}")

    print(f"{scenario['id']}: PASSED")
    print(f"runtimeProvider=KubernetesPool; topology=kubernetes; transport={evidence['transport']}")
    print(f"runtimeImage={runtime_image}")
    print(f"runtimeInstances={len(runtime_ids)}; routedCommands={kubernetes['commandCount']}; delete=ACCEPTED")
    print("GREEN - KUBERNETESPOOL EXECUTION PASSED")


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify live KubernetesPool execution evidence produced by the existing engine integration scenario.")
    matrix_root = Path(__file__).resolve().parents[2]
    parser.add_argument(
        "--plan",
        type=Path,
        default=Path(__file__).with_name("kubernetes-pool-execution-v1.json"),
    )
    parser.add_argument(
        "--evidence",
        type=Path,
        default=matrix_root / "evidence" / "kubernetes" / "feature-runtime-provider-kubernetes-pool-http-command-routing.json",
    )
    parser.add_argument("--require-immutable-image", action="store_true")
    args = parser.parse_args()

    try:
        verify(args.plan.resolve(), args.evidence.resolve(), args.require_immutable_image)
        return 0
    except Exception as exception:
        print(f"RED - KUBERNETESPOOL EXECUTION FAILED: {exception}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
