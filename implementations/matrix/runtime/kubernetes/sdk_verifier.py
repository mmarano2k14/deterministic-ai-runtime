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


def verify(
    plan_path: Path,
    profile_path: Path,
    sdk_evidence_path: Path,
    kubernetes_evidence_path: Path,
    require_immutable_image: bool,
) -> None:
    plan = _load(plan_path)
    profile = _load(profile_path)
    sdk = _load(sdk_evidence_path)
    kubernetes = _load(kubernetes_evidence_path)

    scenario = plan.get("scenario")
    _require(isinstance(scenario, dict), "Plan scenario must be an object.")
    _require(plan.get("schemaVersion") == 1, "Plan schemaVersion must be 1.")
    _require(
        plan.get("matrixId") == "kubernetes-pool-external-sdk-execution-v1",
        "Unexpected external-SDK KubernetesPool matrix id.",
    )
    _require(
        plan.get("baselineExecutedCoverage") == 37,
        "External-SDK KubernetesPool matrix must preserve the validated 37/37 Docker baseline.",
    )

    _require(profile.get("schemaVersion") == 1, "Profile schemaVersion must be 1.")
    _require(
        profile.get("submitMode") == "QueueFirst",
        "External-SDK KubernetesPool profile must use the proven QueueFirst scale-out path.",
    )
    _require(
        profile.get("runtimeImage") == kubernetes.get("runtimeImage"),
        "Profile and live Kubernetes evidence disagree on the Runtime Pool image.",
    )
    _require(
        profile.get("poolId") == kubernetes.get("poolId"),
        "Profile and live Kubernetes evidence disagree on the Runtime Pool id.",
    )
    _require(
        profile.get("namespaceName") == kubernetes.get("namespaceName"),
        "Profile and live Kubernetes evidence disagree on the namespace.",
    )

    publication_runtime = profile.get("publicationRuntime")
    _require(isinstance(publication_runtime, dict), "Profile publicationRuntime must be an object.")
    publication_ref = publication_runtime.get("reference")
    _require(isinstance(publication_ref, str) and publication_ref.strip(), "Publication-only runtime reference is missing.")
    _require(publication_runtime.get("language") == "python", "Publication-only runtime must be Python.")
    runtime_sha = publication_runtime.get("sha256")
    _require(
        isinstance(runtime_sha, str)
        and len(runtime_sha) == 64
        and runtime_sha == runtime_sha.lower()
        and all(char in "0123456789abcdef" for char in runtime_sha),
        "Publication-only runtime requires a lower-case SHA-256 runtime identity.",
    )

    authority = profile.get("authority")
    _require(isinstance(authority, dict), "Profile authority must be an object.")
    _require(authority.get("controlPlaneWorkerPolling") is False, "Control-plane worker polling must be disabled.")
    _require(authority.get("controlPlaneDagReconciliation") is True, "Control-plane DAG reconciliation must remain enabled.")
    _require(authority.get("runtimeWorkerPolling") is True, "Runtime child worker polling must be enabled.")
    _require(authority.get("runtimeDagReconciliation") is False, "Runtime child DAG reconciliation must be disabled.")

    _require(sdk.get("schemaVersion") == 1, "External SDK evidence schemaVersion must be 1.")
    _require(sdk.get("scenarioId") == scenario.get("id"), "External SDK evidence scenario id does not match the plan.")
    _require(str(sdk.get("status", "")).lower() == "passed", "External SDK evidence is not passed.")
    _require(sdk.get("clientLanguage") == scenario.get("clientLanguage"), "Unexpected SDK client language.")
    _require(sdk.get("workerLanguage") == scenario.get("workerLanguage"), "Unexpected hosted worker language.")
    _require(sdk.get("environmentRef") == publication_ref, "SDK publication did not use the publication-only Kubernetes runtime identity.")
    _require(sdk.get("topology") == "kubernetes", "SDK evidence topology must be kubernetes.")
    _require(sdk.get("runtimeProvider") == "KubernetesPool", "SDK evidence runtimeProvider must be KubernetesPool.")
    _require(
        sdk.get("workerExecutionProvider") == "TrustedProcess",
        "SDK evidence workerExecutionProvider must be TrustedProcess inside the Kubernetes Runtime Pool.",
    )
    _require(
        sdk.get("terminalStatus") == scenario.get("requiredTerminalStatus"),
        "Published external function did not reach the required terminal status.",
    )
    _require(
        sdk.get("outputMarkerVerified") is True,
        "Terminal result did not prove execution of the externally published function payload.",
    )
    sdk_kinds = sdk.get("evidence")
    _require(isinstance(sdk_kinds, list), "SDK evidence list must be an array.")
    _require(
        {"publish", "submit", "observe", "terminal-result", "public-execution-id"}.issubset(set(sdk_kinds)),
        "External SDK evidence is missing publish/submit/observe/result proof.",
    )

    _require(kubernetes.get("schemaVersion") == 1, "Kubernetes evidence schemaVersion must be 1.")
    _require(kubernetes.get("scenarioId") == scenario.get("id"), "Kubernetes evidence scenario id does not match the plan.")
    _require(str(kubernetes.get("status", "")).lower() == "passed", "Kubernetes evidence is not passed.")
    _require(kubernetes.get("topology") == "kubernetes", "Kubernetes evidence topology must be kubernetes.")
    _require(kubernetes.get("runtimeProvider") == "KubernetesPool", "Kubernetes evidence runtimeProvider must be KubernetesPool.")
    _require(kubernetes.get("scaleOutWatcherReady") is True, "Kubernetes evidence must prove scale-out watcher readiness before submission.")

    runtime_image = kubernetes.get("runtimeImage")
    _require(isinstance(runtime_image, str) and runtime_image.strip(), "Kubernetes runtime image must be non-empty.")
    if require_immutable_image:
        _require(bool(EXACT_IMAGE.fullmatch(runtime_image.strip())), "Immutable run requires repository@sha256:<64-hex>.")
        _require(profile.get("immutableRuntimeImage") is True, "Profile must mark the runtime image immutable.")

    pod_names = kubernetes.get("podNames")
    ready_pods = kubernetes.get("readyPodNames")
    service_names = kubernetes.get("serviceNames")
    container_images = kubernetes.get("runtimeContainerImages")
    _require(isinstance(pod_names, list) and pod_names, "Live Kubernetes evidence must contain a Runtime Pool Pod.")
    _require(isinstance(ready_pods, list) and ready_pods, "At least one Runtime Pool Pod must be Ready.")
    _require(set(ready_pods).issubset(set(pod_names)), "Ready Pod evidence must belong to the observed Runtime Pool Pods.")
    _require(isinstance(service_names, list) and service_names, "Live Kubernetes evidence must contain a Runtime Pool Service.")
    _require(isinstance(container_images, list) and runtime_image in container_images, "Observed Kubernetes container image does not match the profile.")

    kinds = kubernetes.get("evidenceKinds")
    _require(isinstance(kinds, list), "Kubernetes evidenceKinds must be an array.")
    missing = sorted(set(scenario.get("requiredEvidenceKinds", [])) - set(kinds))
    _require(not missing, f"Kubernetes external-SDK evidence is missing required kinds: {missing}")

    print(f"{scenario['id']}: PASSED")
    print("client=python; worker=python; runtimeProvider=KubernetesPool; workerExecutionProvider=TrustedProcess")
    print(f"publicationEnvironment={publication_ref}; terminalStatus={sdk['terminalStatus']}; uploadedFunctionMarker=VERIFIED")
    print(f"runtimeImage={runtime_image}; pods={len(pod_names)}; readyPods={len(ready_pods)}; services={len(service_names)}")
    print("authority=control-plane-reconciliation/runtime-worker-polling")
    print("GREEN - KUBERNETESPOOL EXTERNAL SDK EXECUTION PASSED")


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify external SDK publication/execution through the existing KubernetesPool provider.")
    matrix_root = Path(__file__).resolve().parents[2]
    parser.add_argument("--plan", type=Path, default=Path(__file__).with_name("kubernetes-pool-external-sdk-v1.json"))
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument(
        "--sdk-evidence",
        type=Path,
        default=matrix_root / "evidence" / "kubernetes" / "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker.json",
    )
    parser.add_argument(
        "--kubernetes-evidence",
        type=Path,
        default=matrix_root / "evidence" / "kubernetes" / "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker.kubernetes.json",
    )
    parser.add_argument("--require-immutable-image", action="store_true")
    args = parser.parse_args()

    try:
        verify(
            args.plan.resolve(),
            args.profile.resolve(),
            args.sdk_evidence.resolve(),
            args.kubernetes_evidence.resolve(),
            args.require_immutable_image,
        )
        return 0
    except Exception as exception:
        print(f"RED - KUBERNETESPOOL EXTERNAL SDK EXECUTION FAILED: {exception}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
