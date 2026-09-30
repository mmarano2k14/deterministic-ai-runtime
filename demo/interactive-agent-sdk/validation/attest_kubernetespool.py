from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def _now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _load_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Unable to read JSON: {path}") from exc
    if not isinstance(value, dict):
        raise RuntimeError(f"JSON root must be an object: {path}")
    return value


def _kubectl_json(kubectl: str, args: list[str]) -> dict[str, Any]:
    completed = subprocess.run(
        [kubectl, *args],
        text=True,
        capture_output=True,
        check=False,
    )
    if completed.returncode != 0:
        detail = (completed.stderr or completed.stdout).strip()
        raise RuntimeError(f"kubectl {' '.join(args)} failed: {detail}")
    try:
        value = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"kubectl {' '.join(args)} returned invalid JSON") from exc
    if not isinstance(value, dict):
        raise RuntimeError("kubectl JSON root must be an object")
    return value


def _items(document: dict[str, Any]) -> list[dict[str, Any]]:
    raw = document.get("items")
    if not isinstance(raw, list):
        return []
    return [item for item in raw if isinstance(item, dict)]


def _metadata(item: dict[str, Any]) -> dict[str, Any]:
    value = item.get("metadata")
    return value if isinstance(value, dict) else {}


def _annotations(item: dict[str, Any]) -> dict[str, str]:
    value = _metadata(item).get("annotations")
    if not isinstance(value, dict):
        return {}
    return {str(k): str(v) for k, v in value.items() if v is not None}


def _pool_items(document: dict[str, Any], pool_id: str) -> list[dict[str, Any]]:
    return [
        item
        for item in _items(document)
        if _annotations(item).get("multiplexed.ai/pool-id") == pool_id
    ]


def _pod_ready(pod: dict[str, Any]) -> bool:
    status = pod.get("status")
    if not isinstance(status, dict) or status.get("phase") != "Running":
        return False
    conditions = status.get("conditions")
    if not isinstance(conditions, list):
        return False
    return any(
        isinstance(item, dict)
        and item.get("type") == "Ready"
        and str(item.get("status")) == "True"
        for item in conditions
    )


def _runtime_count(pod: dict[str, Any]) -> int:
    raw = _annotations(pod).get("multiplexed.ai/runtime-instance-count", "")
    try:
        return int(raw)
    except ValueError:
        return 0


def _pod_images(pod: dict[str, Any]) -> list[str]:
    spec = pod.get("spec")
    if not isinstance(spec, dict):
        return []
    containers = spec.get("containers")
    if not isinstance(containers, list):
        return []
    return [
        str(container.get("image"))
        for container in containers
        if isinstance(container, dict) and container.get("image")
    ]


def _check(name: str, expected: Any, actual: Any, passed: bool | None = None) -> dict[str, Any]:
    return {
        "name": name,
        "expected": expected,
        "actual": actual,
        "passed": (actual == expected) if passed is None else bool(passed),
    }


def build_attestation_from_documents(
    profile: dict[str, Any],
    pods_document: dict[str, Any],
    services_document: dict[str, Any],
    transport: str,
) -> dict[str, Any]:
    if transport not in {"HTTP", "gRPC"}:
        raise RuntimeError(f"Unsupported KubernetesPool transport: {transport}")

    pool_id = str(profile.get("poolId") or "").strip()
    namespace = str(profile.get("namespaceName") or "").strip()
    runtime_image = str(profile.get("runtimeImage") or "").strip()
    if not pool_id or not namespace or not runtime_image:
        raise RuntimeError("KubernetesPool profile must expose poolId, namespaceName, and runtimeImage.")

    pods = _pool_items(pods_document, pool_id)
    services = _pool_items(services_document, pool_id)
    ready_pods = [pod for pod in pods if _pod_ready(pod)]
    runtime_counts = [_runtime_count(pod) for pod in pods]
    images = sorted({image for pod in pods for image in _pod_images(pod)})
    pod_names = [str(_metadata(pod).get("name") or "") for pod in pods]
    service_names = [str(_metadata(service).get("name") or "") for service in services]

    checks = [
        _check("runtimeProvider", "KubernetesPool", "KubernetesPool"),
        _check("runtimeTransport", transport, transport),
        _check("poolId-present", True, bool(pool_id)),
        _check("namespace-present", True, bool(namespace)),
        _check("pool-pod-count", ">=1", len(pods), len(pods) >= 1),
        _check("ready-pod-count", ">=1", len(ready_pods), len(ready_pods) >= 1),
        _check("pool-service-count", ">=1", len(services), len(services) >= 1),
        _check(
            "runtime-instance-count-annotation",
            ">=1",
            max(runtime_counts, default=0),
            max(runtime_counts, default=0) >= 1,
        ),
        _check(
            "runtime-image",
            runtime_image,
            images,
            runtime_image in images,
        ),
        _check(
            "pod-name-presence",
            True,
            all(bool(name) for name in pod_names) if pod_names else False,
        ),
        _check(
            "service-name-presence",
            True,
            all(bool(name) for name in service_names) if service_names else False,
        ),
    ]

    verified = all(check.get("passed") is True for check in checks)
    return {
        "schemaVersion": 1,
        "requested": {
            "runtimeProvider": "KubernetesPool",
            "runtimeTransport": transport,
        },
        "observed": {
            "source": "kubernetes-cluster-inspection",
            "runtimeProvider": "KubernetesPool" if verified else "Unknown",
            "runtimeTransport": transport if verified else "Unknown",
            "namespace": namespace,
            "poolId": pool_id,
            "podNames": pod_names,
            "readyPodNames": [str(_metadata(pod).get("name") or "") for pod in ready_pods],
            "serviceNames": service_names,
            "runtimeInstanceCount": max(runtime_counts, default=0),
            "runtimeContainerImages": images,
        },
        "verified": verified,
        "checks": checks,
        "verifiedAtUtc": _now(),
    }


def build_live_attestation(
    profile: dict[str, Any],
    transport: str,
    *,
    kubectl: str = "kubectl",
) -> dict[str, Any]:
    namespace = str(profile.get("namespaceName") or "").strip()
    if not namespace:
        raise RuntimeError("KubernetesPool profile does not expose namespaceName.")
    pods = _kubectl_json(
        kubectl,
        ["--request-timeout=5s", "get", "pods", "-n", namespace, "-l", "multiplexed.ai/runtime-pool=true", "-o", "json"],
    )
    services = _kubectl_json(
        kubectl,
        ["--request-timeout=5s", "get", "services", "-n", namespace, "-l", "multiplexed.ai/runtime-pool=true", "-o", "json"],
    )
    return build_attestation_from_documents(profile, pods, services, transport)


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate a fail-closed live KubernetesPool topology attestation.")
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--runtime-transport", choices=("HTTP", "gRPC"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--kubectl", default="kubectl")
    args = parser.parse_args()

    try:
        profile = _load_json(args.profile)
        attestation = build_live_attestation(profile, args.runtime_transport, kubectl=args.kubectl)
    except Exception as exc:
        print(f"KubernetesPool attestation FAILED: {exc}", file=sys.stderr)
        return 1

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(attestation, indent=2) + "\n", encoding="utf-8")
    if not attestation["verified"]:
        failed = [item["name"] for item in attestation["checks"] if not item.get("passed")]
        print("KubernetesPool attestation FAILED: " + ", ".join(failed), file=sys.stderr)
        return 1

    print(f"KubernetesPool attestation PASS: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
