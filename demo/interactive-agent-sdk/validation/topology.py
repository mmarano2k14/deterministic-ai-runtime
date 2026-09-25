from __future__ import annotations

import json
import subprocess
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
PROFILES = Path(__file__).with_name("topology-profiles.json")


class TopologyVerificationError(RuntimeError):
    pass


@dataclass(frozen=True)
class TopologyRequest:
    runtime_provider: str
    runtime_transport: str


def _now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _load_profiles() -> dict[str, Any]:
    return json.loads(PROFILES.read_text(encoding="utf-8"))


def _profile(request: TopologyRequest) -> dict[str, Any]:
    payload = _load_profiles()
    for item in payload.get("profiles", []):
        if (
            item.get("runtimeProvider") == request.runtime_provider
            and item.get("runtimeTransport") == request.runtime_transport
        ):
            return item
    raise TopologyVerificationError(
        f"No topology profile exists for {request.runtime_provider}/{request.runtime_transport}."
    )


def _parse_env(entries: list[str]) -> dict[str, str]:
    env: dict[str, str] = {}
    for entry in entries:
        if isinstance(entry, str) and "=" in entry:
            key, value = entry.split("=", 1)
            env[key] = value
    return env


def inspect_local_docker_runtime() -> tuple[dict[str, str], str]:
    compose_file = ROOT / "compose.yaml"
    try:
        ps = subprocess.run(
            ["docker", "compose", "-f", str(compose_file), "ps", "-q", "runtime"],
            cwd=ROOT,
            text=True,
            capture_output=True,
            check=False,
        )
    except (OSError, FileNotFoundError) as exc:
        raise TopologyVerificationError("Docker CLI is not available for topology inspection.") from exc

    container_id = ps.stdout.strip() if ps.returncode == 0 else ""
    if not container_id:
        raise TopologyVerificationError(
            "No running interactive-agent-sdk Docker runtime container was found."
        )

    inspect = subprocess.run(
        ["docker", "inspect", container_id, "--format", "{{json .Config.Env}}"],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )
    if inspect.returncode != 0 or not inspect.stdout.strip():
        raise TopologyVerificationError("Unable to inspect the running runtime container environment.")

    try:
        entries = json.loads(inspect.stdout)
    except json.JSONDecodeError as exc:
        raise TopologyVerificationError("Docker runtime environment inspection returned invalid JSON.") from exc

    if not isinstance(entries, list):
        raise TopologyVerificationError("Docker runtime environment inspection returned an invalid shape.")
    return _parse_env(entries), container_id


def _derive_observed(env: dict[str, str], container_id: str) -> dict[str, Any]:
    host_mode = env.get("AiMcpHost__Mode")
    provider_name = env.get("AiRuntimeInstanceRegistration__ProviderName")

    if host_mode == "ControlPlaneWithLocalRuntimeInstances":
        runtime_provider = "LocalRuntimePool"
        runtime_transport = "Local"
        scale_out_mode = None
        host_creation_mode = None
    elif host_mode == "ControlPlaneWithHttpRuntimeInstances":
        # Provider/transport configuration alone does not prove ProcessHostPool.
        # Historical one-runtime-per-process modes use the same control-plane family.
        runtime_provider = "ExternalRuntimeInstances"
        runtime_transport = "HTTP"
        scale_out_mode = env.get("AiHttpRuntimeScaleOut__Mode")
        host_creation_mode = env.get("AiHttpRuntimeScaleOut__HostCreationMode")
    elif host_mode == "ControlPlaneWithGrpcRuntimeInstances":
        # Provider/transport configuration alone does not prove ProcessHostPool.
        runtime_provider = "ExternalRuntimeInstances"
        runtime_transport = "gRPC"
        scale_out_mode = env.get("AiGrpcRuntimeScaleOut__Mode")
        host_creation_mode = env.get("AiGrpcRuntimeScaleOut__HostCreationMode")
    else:
        runtime_provider = "Unknown"
        runtime_transport = provider_name or "Unknown"
        scale_out_mode = None
        host_creation_mode = None

    return {
        "source": "docker-runtime-inspection",
        "runtimeProvider": runtime_provider,
        "runtimeTransport": runtime_transport,
        "hostMode": host_mode,
        "providerName": provider_name,
        "scaleOutMode": scale_out_mode,
        "hostCreationMode": host_creation_mode,
        "service": "runtime",
        "containerId": container_id[:12],
    }


def _check(name: str, expected: Any, actual: Any) -> dict[str, Any]:
    return {
        "name": name,
        "expected": expected,
        "actual": actual,
        "passed": actual == expected,
    }


def diagnose_local_docker(request: TopologyRequest) -> dict[str, Any]:
    env, container_id = inspect_local_docker_runtime()
    observed = _derive_observed(env, container_id)
    checks = [
        _check("runtimeProvider", request.runtime_provider, observed["runtimeProvider"]),
        _check("runtimeTransport", request.runtime_transport, observed["runtimeTransport"]),
    ]
    return {
        "schemaVersion": 1,
        "requested": {
            "runtimeProvider": request.runtime_provider,
            "runtimeTransport": request.runtime_transport,
        },
        "observed": observed,
        "verified": False,
        "checks": checks,
        "verifiedAtUtc": _now(),
    }


def load_external_attestation(path: Path, request: TopologyRequest) -> dict[str, Any]:
    if not path.is_file():
        raise TopologyVerificationError(f"Topology attestation not found: {path}")
    data = json.loads(path.read_text(encoding="utf-8"))
    requested = data.get("requested", {})
    observed = data.get("observed", {})
    checks = data.get("checks", [])

    errors: list[str] = []
    if data.get("schemaVersion") != 1:
        errors.append("schemaVersion must be 1")
    if requested.get("runtimeProvider") != request.runtime_provider:
        errors.append("attested requested.runtimeProvider does not match CLI request")
    if requested.get("runtimeTransport") != request.runtime_transport:
        errors.append("attested requested.runtimeTransport does not match CLI request")
    if observed.get("runtimeProvider") != request.runtime_provider:
        errors.append("observed.runtimeProvider does not match CLI request")
    if observed.get("runtimeTransport") != request.runtime_transport:
        errors.append("observed.runtimeTransport does not match CLI request")
    if data.get("verified") is not True:
        errors.append("topology attestation is not verified")
    if not isinstance(checks, list) or not checks or not all(isinstance(x, dict) and x.get("passed") is True for x in checks):
        errors.append("all topology attestation checks must pass")

    profile = _profile(request)
    authority = profile.get("attestationAuthority")
    if authority and observed.get("source") != authority:
        errors.append(f"observed.source must be {authority}")

    if errors:
        raise TopologyVerificationError("; ".join(errors))
    return data


def verify_topology(
    request: TopologyRequest,
    *,
    local_endpoint: bool,
    attestation_path: Path | None,
) -> dict[str, Any]:
    _profile(request)

    if attestation_path is not None:
        return load_external_attestation(attestation_path, request)

    if local_endpoint:
        try:
            diagnostic = diagnose_local_docker(request)
            observed = diagnostic.get("observed", {})
            raise TopologyVerificationError(
                "Requested topology is not attested. "
                f"requested={request.runtime_provider}/{request.runtime_transport}; "
                f"observed-control-plane={observed.get('runtimeProvider')}/{observed.get('runtimeTransport')} "
                f"hostMode={observed.get('hostMode')}. "
                "Provider/transport configuration alone is not sufficient proof of Runtime Pool topology. "
                "Supply the machine-generated --topology-attestation produced by the topology-specific launcher."
            )
        except TopologyVerificationError as exc:
            if str(exc).startswith("Requested topology is not attested"):
                raise
            # No inspectable demo container: still fail closed rather than trusting labels.

    raise TopologyVerificationError(
        f"{request.runtime_provider}/{request.runtime_transport} requires a machine-generated topology attestation. "
        "The runner will not infer runtime topology from CLI labels."
    )
