from __future__ import annotations

import argparse
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


def verify(plan_path: Path, evidence_path: Path) -> None:
    plan = _load(plan_path)
    evidence = _load(evidence_path)
    scenario = plan.get("scenario")
    _require(isinstance(scenario, dict), "Plan scenario must be an object.")

    _require(plan.get("schemaVersion") == 1, "Plan schemaVersion must be 1.")
    _require(plan.get("matrixId") == "kubernetes-pool-runtime-recovery-v1", "Unexpected KubernetesPool recovery matrix id.")
    _require(plan.get("baselineExecutedCoverage") == 37, "KubernetesPool recovery matrix must preserve the validated 37/37 baseline.")

    _require(evidence.get("schemaVersion") == 1, "Evidence schemaVersion must be 1.")
    _require(evidence.get("scenarioId") == scenario.get("id"), "Recovery evidence scenario id does not match the plan.")
    _require(str(evidence.get("status", "")).lower() == "passed", "KubernetesPool recovery evidence is not passed.")
    _require(evidence.get("topology") == "kubernetes", "Recovery evidence topology must be kubernetes.")
    _require(evidence.get("runtimeProvider") == "KubernetesPool", "Recovery evidence runtimeProvider must be KubernetesPool.")
    _require(evidence.get("transport") == scenario.get("transport"), "Recovery evidence transport does not match the plan.")

    recovery = evidence.get("recovery")
    _require(isinstance(recovery, dict), "Evidence recovery section must be an object.")
    _require(recovery.get("observationMode") == scenario.get("requiredObservationMode"), "Recovery observation mode does not match the plan.")
    _require(int(recovery.get("executionCycleCount", 0)) >= int(scenario["minimumExecutionCycleCount"]), "Execution-cycle evidence is below the plan minimum.")
    _require(int(recovery.get("childDepth", 0)) >= int(scenario["minimumChildDepth"]), "Recursive Child DAG depth evidence is below the plan minimum.")
    _require(int(recovery.get("runtimeProcessFailureCount", 0)) >= int(scenario["minimumRuntimeProcessFailureCount"]), "No in-Pod runtime-process failure was proven.")
    _require(int(recovery.get("podFailureCount", 0)) >= int(scenario["minimumPodFailureCount"]), "No busy-Pod failure was proven.")

    recovered = int(recovery.get("recoveredSharedRunCount", 0))
    forensics = int(recovery.get("recoveryForensicsProofCount", 0))
    ownership = int(recovery.get("runtimeOwnershipTransitionCount", 0))
    _require(recovered > 0, "No recovered shared runs were recorded.")
    _require(forensics == recovered, "Recovery-forensics proof count must exactly match recovered shared-run count.")
    _require(ownership == recovered, "Runtime ownership transition count must exactly match recovered shared-run count.")
    _require(int(recovery.get("runtimeOwnershipTransitionViolationCount", -1)) == 0, "Runtime ownership transition violations were detected.")

    replay_expected = int(recovery.get("parentReplayExpectedExecutionCount", 0))
    replay_proven = int(recovery.get("parentReplayProvenExecutionCount", 0))
    _require(replay_expected > 0, "Parent replay expectation must be positive.")
    _require(replay_proven == replay_expected, "Parent replay proof did not cover every expected execution.")

    _require(int(recovery.get("missingRecursiveChildLogicalStepCount", -1)) == 0, "Recursive Child DAG logical steps are missing.")
    _require(int(recovery.get("unexpectedDuplicateRecursiveChildLogicalStepCount", -1)) == 0, "Unexpected duplicate recursive Child DAG logical steps were detected.")
    _require(int(recovery.get("lostRunCount", -1)) == 0, "Lost runs were detected.")
    _require(int(recovery.get("duplicateDurableDispatchCount", -1)) == 0, "Duplicate durable dispatches were detected.")
    _require(recovery.get("warmReuseProven") is True, "Warm KubernetesPool reuse was not proven.")

    kinds = evidence.get("evidenceKinds")
    _require(isinstance(kinds, list), "evidenceKinds must be an array.")
    missing = sorted(set(scenario.get("requiredEvidenceKinds", [])) - set(kinds))
    _require(not missing, f"KubernetesPool recovery evidence is missing required kinds: {missing}")

    print(f"{scenario['id']}: PASSED")
    print(f"runtimeProvider=KubernetesPool; topology=kubernetes; transport={evidence['transport']}; observationMode={recovery['observationMode']}")
    print(
        "runtimeProcessFailures="
        f"{recovery['runtimeProcessFailureCount']}; "
        f"podFailures={recovery['podFailureCount']}; "
        f"recoveredSharedRuns={recovered}; "
        f"ownershipTransitions={ownership}; violations=0"
    )
    print(
        f"parentReplay={replay_proven}/{replay_expected}; "
        "lostRuns=0; duplicateDurableDispatches=0; warmReuse=PASS"
    )
    print("GREEN - KUBERNETESPOOL RECOVERY PASSED")


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify KubernetesPool lifecycle/recovery evidence emitted by the existing full-failure production harness.")
    matrix_root = Path(__file__).resolve().parents[2]
    parser.add_argument(
        "--plan",
        type=Path,
        default=Path(__file__).with_name("kubernetes-pool-recovery-v1.json"),
    )
    parser.add_argument(
        "--evidence",
        type=Path,
        default=matrix_root / "evidence" / "kubernetes" / "feature-runtime-provider-kubernetes-pool-hierarchical-recovery.json",
    )
    args = parser.parse_args()

    try:
        verify(args.plan.resolve(), args.evidence.resolve())
        return 0
    except Exception as exception:
        print(f"RED - KUBERNETESPOOL RECOVERY FAILED: {exception}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
