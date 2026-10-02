from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
from datetime import datetime, timezone, timedelta
from pathlib import Path
from urllib.parse import urlparse
from typing import Any


def _now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _load_json(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Unable to read ProcessHostPool inventory: {path}") from exc
    if not isinstance(data, dict):
        raise RuntimeError("ProcessHostPool inventory root must be an object.")
    return data


def _process_tree() -> dict[int, int]:
    if os.name == "nt":
        command = [
            "powershell.exe",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId | ConvertTo-Json -Compress",
        ]
        completed = subprocess.run(command, text=True, capture_output=True, check=False)
        if completed.returncode != 0 or not completed.stdout.strip():
            raise RuntimeError("Unable to inspect the Windows process tree.")
        raw = json.loads(completed.stdout)
        rows = raw if isinstance(raw, list) else [raw]
        return {int(x["ProcessId"]): int(x["ParentProcessId"]) for x in rows if isinstance(x, dict)}

    tree: dict[int, int] = {}
    proc = Path("/proc")
    if not proc.is_dir():
        raise RuntimeError("Live process-tree inspection is unsupported on this platform.")
    for child in proc.iterdir():
        if not child.name.isdigit():
            continue
        try:
            stat = (child / "stat").read_text(encoding="utf-8")
            right = stat.rfind(")")
            fields = stat[right + 2 :].split()
            tree[int(child.name)] = int(fields[1])
        except (OSError, ValueError, IndexError):
            continue
    return tree


def _is_descendant(tree: dict[int, int], child_pid: int, ancestor_pid: int) -> bool:
    current = child_pid
    visited: set[int] = set()
    while current > 0 and current not in visited:
        if current == ancestor_pid:
            return True
        visited.add(current)
        current = tree.get(current, 0)
    return False


def _endpoint_reachable(value: str, timeout: float = 2.0) -> bool:
    try:
        parsed = urlparse(value)
        host = parsed.hostname
        port = parsed.port
        if not host or not port:
            return False
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except (OSError, ValueError):
        return False


def _normalize_endpoint(value: str) -> str:
    return value.strip().rstrip("/").lower()


def _check(name: str, expected: Any, actual: Any, passed: bool | None = None) -> dict[str, Any]:
    return {
        "name": name,
        "expected": expected,
        "actual": actual,
        "passed": (actual == expected) if passed is None else bool(passed),
    }


def build_attestation(inventory: dict[str, Any], transport: str, *, live_process_check: bool = True) -> dict[str, Any]:
    checks: list[dict[str, Any]] = []
    checks.append(_check("runtimeProvider", "ProcessHostPool", inventory.get("runtimeProvider")))
    checks.append(_check("runtimeTransport", transport, inventory.get("runtimeTransport")))
    checks.append(_check("schemaVersion", 1, inventory.get("schemaVersion")))
    checks.append(_check("inventory-source", "runtime-pool-diagnostics", inventory.get("source")))
    checks.append(_check("captureId-present", True, isinstance(inventory.get("captureId"), str) and bool(inventory.get("captureId", "").strip())))
    checks.append(_check("live-process-check-enabled", True, live_process_check))

    captured_at = inventory.get("capturedAtUtc")
    fresh = False
    age_seconds = None
    if isinstance(captured_at, str) and captured_at.strip():
        try:
            parsed = datetime.fromisoformat(captured_at.replace("Z", "+00:00"))
            age_seconds = max(0.0, (datetime.now(timezone.utc) - parsed.astimezone(timezone.utc)).total_seconds())
            fresh = age_seconds <= 300
        except ValueError:
            pass
    checks.append(_check("inventory-freshness-seconds", "<=300", age_seconds, fresh))

    pool_id = inventory.get("poolId")
    checks.append(_check("poolId-present", True, isinstance(pool_id, str) and bool(pool_id.strip())))

    hosts = inventory.get("hosts")
    hosts_list = hosts if isinstance(hosts, list) else []
    checks.append(_check("host-count", ">=2", len(hosts_list), len(hosts_list) >= 2))

    tree = _process_tree() if live_process_check else {}
    runtime_ids: list[str] = []
    route_ids: list[str] = []
    child_pids: list[int] = []
    host_ids: list[str] = []
    host_pids: list[int] = []
    stable_endpoints: list[str] = []
    expected_provider = "http" if transport == "HTTP" else "grpc"

    for index, host in enumerate(hosts_list):
        if not isinstance(host, dict):
            checks.append(_check(f"host[{index}]-shape", "object", type(host).__name__, False))
            continue
        host_id = str(host.get("hostId") or "")
        host_pid = int(host.get("processId") or 0)
        endpoint = str(host.get("stableEndpoint") or "")
        instances = host.get("runtimeInstances") if isinstance(host.get("runtimeInstances"), list) else []
        host_ids.append(host_id)
        host_pids.append(host_pid)
        stable_endpoints.append(endpoint)

        checks.append(_check(f"host[{index}]-hostId", True, bool(host_id)))
        checks.append(_check(f"host[{index}]-runtime-count", ">=2", len(instances), len(instances) >= 2))
        checks.append(_check(f"host[{index}]-stable-endpoint", True, _endpoint_reachable(endpoint)))
        if live_process_check:
            checks.append(_check(f"host[{index}]-process-alive", True, host_pid in tree))

        for child_index, item in enumerate(instances):
            if not isinstance(item, dict):
                checks.append(_check(f"host[{index}].runtime[{child_index}]-shape", "object", type(item).__name__, False))
                continue
            runtime_id = str(item.get("runtimeInstanceId") or "")
            route_id = str(item.get("routeId") or "")
            raw_pid = item.get("processId")
            pid = int(raw_pid) if isinstance(raw_pid, int) and raw_pid > 0 else 0
            endpoint_value = str(item.get("endpoint") or "")
            runtime_ids.append(runtime_id)
            if route_id:
                route_ids.append(route_id)
            child_pids.append(pid)
            checks.extend([
                _check(f"host[{index}].runtime[{child_index}]-runtimeInstanceId", True, bool(runtime_id)),
                _check(f"host[{index}].runtime[{child_index}]-processId", True, pid > 0),
                _check(f"host[{index}].runtime[{child_index}]-status", "Ready", item.get("status")),
                _check(f"host[{index}].runtime[{child_index}]-canAcceptRun", True, item.get("canAcceptRun")),
                _check(f"host[{index}].runtime[{child_index}]-provider", expected_provider, str(item.get("providerName") or "").lower()),
                _check(f"host[{index}].runtime[{child_index}]-transport", expected_provider, str(item.get("transportName") or "").lower()),
                _check(
                    f"host[{index}].runtime[{child_index}]-stable-endpoint-parity",
                    _normalize_endpoint(endpoint),
                    _normalize_endpoint(endpoint_value) if endpoint_value else "",
                ),
            ])
            if live_process_check:
                checks.append(_check(f"host[{index}].runtime[{child_index}]-process-alive", True, pid in tree))
                checks.append(_check(
                    f"host[{index}].runtime[{child_index}]-child-of-host",
                    True,
                    _is_descendant(tree, pid, host_pid),
                ))

    checks.extend([
        _check("hostId-uniqueness", len(host_ids), len(set(host_ids))),
        _check("runtimeInstanceId-uniqueness", len(runtime_ids), len(set(runtime_ids))),
        _check("runtime-processId-uniqueness", len(child_pids), len(set(child_pids))),
        _check("host-processId-uniqueness", len(host_pids), len(set(host_pids))),
        _check("stable-endpoint-uniqueness", len(stable_endpoints), len(set(stable_endpoints))),
    ])
    if route_ids:
        checks.append(_check("routeId-uniqueness", len(route_ids), len(set(route_ids))))

    all_passed = bool(checks) and all(x.get("passed") is True for x in checks)
    observed = {
        "source": "process-host-inspection",
        "runtimeProvider": "ProcessHostPool" if all_passed else str(inventory.get("runtimeProvider") or "Unknown"),
        "runtimeTransport": transport if all_passed else str(inventory.get("runtimeTransport") or "Unknown"),
        "poolId": pool_id,
        "hostCount": len(hosts_list),
        "hostIds": host_ids,
        "runtimeInstanceCount": len(runtime_ids),
        "runtimeInstanceIds": runtime_ids,
        "routeIds": route_ids,
        "routeIdEvidenceCount": len(route_ids),
        "childProcessEvidenceCount": len(child_pids),
        "stableEndpoints": stable_endpoints,
        "liveProcessCheck": live_process_check,
        "inventorySource": inventory.get("source"),
        "captureId": inventory.get("captureId"),
        "inventoryCapturedAtUtc": inventory.get("capturedAtUtc"),
        "inventoryAgeSeconds": age_seconds,
    }
    return {
        "schemaVersion": 1,
        "requested": {"runtimeProvider": "ProcessHostPool", "runtimeTransport": transport},
        "observed": observed,
        "verified": all_passed,
        "checks": checks,
        "verifiedAtUtc": _now(),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate a fail-closed ProcessHostPool topology attestation from runtime inventory.")
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--runtime-transport", choices=("HTTP", "gRPC"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--skip-live-process-check", action="store_true", help="Schema/testing only. A skipped live process check must not be used as final topology evidence.")
    args = parser.parse_args()

    try:
        inventory = _load_json(args.inventory)
        attestation = build_attestation(
            inventory,
            args.runtime_transport,
            live_process_check=not args.skip_live_process_check,
        )
    except Exception as exc:
        print(f"ProcessHostPool attestation FAILED: {exc}", file=sys.stderr)
        return 1

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(attestation, indent=2) + "\n", encoding="utf-8")
    if not attestation["verified"]:
        failed = [x["name"] for x in attestation["checks"] if not x.get("passed")]
        print("ProcessHostPool attestation FAILED: " + ", ".join(failed), file=sys.stderr)
        return 1

    print(f"ProcessHostPool attestation PASS: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
