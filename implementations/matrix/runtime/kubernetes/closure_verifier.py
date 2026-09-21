from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
from typing import Any


def _load(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8-sig") as handle:
        value = json.load(handle)
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path}")
    return value


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def _load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load verifier module: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def verify(
    closure_plan_path: Path,
    baseline_plan_path: Path,
    execution_evidence_path: Path,
    recovery_evidence_path: Path,
    sdk_profile_path: Path,
    sdk_evidence_path: Path,
    sdk_kubernetes_evidence_path: Path,
    require_immutable_image: bool,
) -> None:
    root = Path(__file__).resolve().parent
    closure = _load(closure_plan_path)
    baseline = _load(baseline_plan_path)

    _require(closure.get("schemaVersion") == 1, "Closure schemaVersion must be 1.")
    _require(
        closure.get("matrixId") == "kubernetes-pool-cross-topology-closure-v1",
        "Unexpected KubernetesPool closure matrix id.",
    )

    baseline_section = closure.get("baseline")
    kubernetes_section = closure.get("kubernetes")
    _require(isinstance(baseline_section, dict), "Closure baseline section must be an object.")
    _require(isinstance(kubernetes_section, dict), "Closure kubernetes section must be an object.")

    baseline_scenarios = list(baseline.get("coreScenarios", [])) + list(baseline.get("featureScenarios", []))
    baseline_ids = [scenario.get("id") for scenario in baseline_scenarios if isinstance(scenario, dict)]
    _require(baseline.get("matrixId") == baseline_section.get("matrixId"), "Closure baseline matrix id does not match the canonical plan.")
    _require(len(baseline_ids) == 37, "Canonical Docker baseline must contain exactly 37 scenarios.")
    _require(len(set(baseline_ids)) == 37, "Canonical Docker baseline scenario ids must be unique.")
    _require(baseline_section.get("executedScenarioCount") == 37, "Closure must preserve the 37/37 Docker baseline.")
    _require(baseline_section.get("topology") == "docker", "Closure baseline topology must be docker.")
    _require(baseline_section.get("runtimeProvider") == "ProcessHostPool", "Closure baseline runtimeProvider must be ProcessHostPool.")
    _require(
        set(baseline_section.get("workerExecutionProviders", [])) == {"TrustedProcess", "ContainerIsolationProvider"},
        "Closure baseline worker-execution providers do not match the validated Docker closure.",
    )

    expected_kubernetes_ids = {
        "feature-runtime-provider-kubernetes-pool-http-command-routing",
        "feature-runtime-provider-kubernetes-pool-hierarchical-recovery",
        "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker",
    }
    _require(kubernetes_section.get("topology") == "kubernetes", "Kubernetes closure topology must be kubernetes.")
    _require(kubernetes_section.get("runtimeProvider") == "KubernetesPool", "Kubernetes closure runtimeProvider must be KubernetesPool.")
    _require(kubernetes_section.get("executedScenarioCount") == 3, "Kubernetes closure must contain exactly three executed sidecar scenarios.")
    _require(set(kubernetes_section.get("scenarioIds", [])) == expected_kubernetes_ids, "Kubernetes closure scenario ids do not match the expected executed set.")
    _require(closure.get("crossTopologyExecutedScenarioCount") == 40, "Cross-topology closure count must be 40 (37 Docker + 3 Kubernetes).")

    execution_verifier = _load_module("kubernetes_pool_execution_verifier", root / "verifier.py")
    recovery_verifier = _load_module("kubernetes_pool_recovery_verifier", root / "recovery_verifier.py")
    sdk_verifier = _load_module("kubernetes_pool_sdk_verifier", root / "sdk_verifier.py")

    execution_verifier.verify(
        root / "kubernetes-pool-execution-v1.json",
        execution_evidence_path,
        require_immutable_image=False,
    )
    recovery_verifier.verify(
        root / "kubernetes-pool-recovery-v1.json",
        recovery_evidence_path,
    )
    sdk_verifier.verify(
        root / "kubernetes-pool-external-sdk-v1.json",
        sdk_profile_path,
        sdk_evidence_path,
        sdk_kubernetes_evidence_path,
        require_immutable_image=require_immutable_image,
    )

    observed_ids = {
        _load(execution_evidence_path).get("scenarioId"),
        _load(recovery_evidence_path).get("scenarioId"),
        _load(sdk_evidence_path).get("scenarioId"),
    }
    _require(observed_ids == expected_kubernetes_ids, "Executed Kubernetes evidence does not exactly cover the closure scenario set.")

    print("3/3 KubernetesPool branch scenarios passed.")
    print("Preserved validated baseline: 37/37 Docker scenarios; runtimeProvider=ProcessHostPool.")
    print("KubernetesPool closure: live routing + hierarchical recovery + external SDK publication/execution.")
    print("Cross-topology validated coverage record: 40 scenarios (37 Docker + 3 Kubernetes).")
    print("This is not a homogeneous 40-scenario topology matrix.")
    print("GREEN - KUBERNETESPOOL MATRIX CLOSURE PASSED")


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify final KubernetesPool branch evidence against the preserved 37/37 Docker baseline.")
    matrix_root = Path(__file__).resolve().parents[2]
    evidence_root = matrix_root / "evidence" / "kubernetes"
    repo_root = Path(__file__).resolve().parents[4]
    parser.add_argument("--closure-plan", type=Path, default=Path(__file__).with_name("kubernetes-pool-closure-v1.json"))
    parser.add_argument("--baseline-plan", type=Path, default=matrix_root / "multilanguage-runtime-matrix-v1.json")
    parser.add_argument(
        "--execution-evidence",
        type=Path,
        default=evidence_root / "feature-runtime-provider-kubernetes-pool-http-command-routing.json",
    )
    parser.add_argument(
        "--recovery-evidence",
        type=Path,
        default=evidence_root / "feature-runtime-provider-kubernetes-pool-hierarchical-recovery.json",
    )
    parser.add_argument(
        "--sdk-profile",
        type=Path,
        default=repo_root / ".matrix-kubernetes-sdk" / "profile.json",
    )
    parser.add_argument(
        "--sdk-evidence",
        type=Path,
        default=evidence_root / "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker.json",
    )
    parser.add_argument(
        "--sdk-kubernetes-evidence",
        type=Path,
        default=evidence_root / "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker.kubernetes.json",
    )
    parser.add_argument("--require-immutable-image", action="store_true")
    args = parser.parse_args()

    try:
        verify(
            args.closure_plan.resolve(),
            args.baseline_plan.resolve(),
            args.execution_evidence.resolve(),
            args.recovery_evidence.resolve(),
            args.sdk_profile.resolve(),
            args.sdk_evidence.resolve(),
            args.sdk_kubernetes_evidence.resolve(),
            args.require_immutable_image,
        )
        return 0
    except Exception as exception:
        print(f"RED - KUBERNETESPOOL MATRIX CLOSURE FAILED: {exception}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
