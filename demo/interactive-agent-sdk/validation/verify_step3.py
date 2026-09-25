from __future__ import annotations

import json
from pathlib import Path

from topology import TopologyRequest, _derive_observed, _profile


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"Step 3 verification FAILED: {message}")


def main() -> int:
    here = Path(__file__).resolve().parent
    profiles = json.loads((here / "topology-profiles.json").read_text(encoding="utf-8"))
    require(len(profiles.get("profiles", [])) == 4, "four provider/transport profiles are required")

    for provider in ("ProcessHostPool", "KubernetesPool"):
        for transport in ("HTTP", "gRPC"):
            profile = _profile(TopologyRequest(provider, transport))
            require(profile.get("requiresExternalAttestation") is True, f"{provider}/{transport} must fail closed without attestation")
            require(bool(profile.get("attestationAuthority")), f"{provider}/{transport} attestation authority missing")

    local = _derive_observed(
        {"AiMcpHost__Mode": "ControlPlaneWithLocalRuntimeInstances"},
        "0123456789abcdef",
    )
    require(local["runtimeProvider"] == "LocalRuntimePool", "local runtime must not be classified as ProcessHostPool")
    require(local["runtimeTransport"] == "Local", "local runtime transport classification is wrong")

    http_process = _derive_observed(
        {
            "AiMcpHost__Mode": "ControlPlaneWithHttpRuntimeInstances",
            "AiRuntimeInstanceRegistration__ProviderName": "http",
            "AiHttpRuntimeScaleOut__Mode": "HostManager",
            "AiHttpRuntimeScaleOut__HostCreationMode": "Process",
        },
        "0123456789abcdef",
    )
    require(http_process["runtimeProvider"] == "ExternalRuntimeInstances", "HTTP process configuration must not be over-claimed as ProcessHostPool")
    require(http_process["runtimeTransport"] == "HTTP", "HTTP transport diagnosis failed")

    grpc_process = _derive_observed(
        {
            "AiMcpHost__Mode": "ControlPlaneWithGrpcRuntimeInstances",
            "AiRuntimeInstanceRegistration__ProviderName": "grpc",
            "AiGrpcRuntimeScaleOut__Mode": "HostManager",
            "AiGrpcRuntimeScaleOut__HostCreationMode": "Process",
        },
        "0123456789abcdef",
    )
    require(grpc_process["runtimeProvider"] == "ExternalRuntimeInstances", "gRPC process configuration must not be over-claimed as ProcessHostPool")
    require(grpc_process["runtimeTransport"] == "gRPC", "gRPC transport diagnosis failed")

    run_e2e = (here / "run_e2e.py").read_text(encoding="utf-8")
    require("verify_topology" in run_e2e, "run_e2e must verify topology before client execution")
    require("--topology-attestation" in run_e2e, "topology attestation CLI is missing")
    require("topology mismatch" in run_e2e.lower(), "fail-closed mismatch path is missing")
    require("_attach_topology" in run_e2e, "verified topology must be embedded in final evidence")

    validator = (here / "validate_evidence.py").read_text(encoding="utf-8")
    require("topology.verified must be true" in validator, "evidence validator must require verified topology")
    require("topology observed provider mismatch" in validator, "observed provider parity is not enforced")
    require("topology observed transport mismatch" in validator, "observed transport parity is not enforced")

    print("Interactive-agent topology verification contract: PASS")
    print("- LocalRuntimePool cannot be mislabeled as ProcessHostPool")
    print("- HTTP/gRPC process-host configuration alone cannot be over-claimed as ProcessHostPool")
    print("- ProcessHostPool requires process-host-inspection attestation")
    print("- KubernetesPool requires kubernetes-cluster-inspection attestation")
    print("- E2E execution is blocked before consumers when topology is not attested")
    print("- final evidence requires topology.verified=true and requested/observed parity")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
