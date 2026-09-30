from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path
from urllib.parse import urljoin

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
ATTEST = HERE / "attest_processhostpool.py"
RUNNER = HERE / "run_e2e.py"


def _run(command: list[str], *, env: dict[str, str] | None = None) -> None:
    completed = subprocess.run(command, cwd=ROOT, env=env, check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"Command failed ({completed.returncode}): {' '.join(command)}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Run the canonical interactive-agent matrix against an attested ProcessHostPool.")
    parser.add_argument("--runtime-transport", choices=("HTTP", "gRPC"), required=True)
    parser.add_argument("--runtime-endpoint", required=True, help="Public MCP endpoint for the ProcessHostPool control plane.")
    parser.add_argument("--inventory", type=Path, required=True, help="Machine-generated ProcessHostPool inventory from the topology-specific launcher/diagnostics.")
    parser.add_argument("--evidence-dir", type=Path, default=HERE / "evidence")
    parser.add_argument("--attestation-dir", type=Path, default=HERE / "attestations")
    parser.add_argument("--auth-mode", choices=("auto", "token", "local-jwt"), default="auto")
    parser.add_argument("--skip-prepare", action="store_true")
    args = parser.parse_args()

    transport_slug = args.runtime_transport.lower().replace("grpc", "grpc")
    attestation = args.attestation_dir / f"processhostpool-{transport_slug}.json"

    env = os.environ.copy()
    env["AI_RUNTIME_ENDPOINT"] = args.runtime_endpoint
    env["AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT"] = urljoin(
        args.runtime_endpoint,
        "/auth/access-context",
    )
    # ProcessHostPool validation must bootstrap RBAC against the same public
    # control-plane boundary used by MCP. Never inherit a stale Docker/demo
    # access-context endpoint from the parent shell.
    env.pop("AI_RUNTIME_ACCESS_CONTEXT", None)

    try:
        print(f"[processhost-matrix] attesting ProcessHostPool/{args.runtime_transport}")
        _run([
            sys.executable,
            str(ATTEST),
            "--inventory", str(args.inventory),
            "--runtime-transport", args.runtime_transport,
            "--output", str(attestation),
        ], env=env)

        print(f"[processhost-matrix] running 3 SDK clients against ProcessHostPool/{args.runtime_transport}")
        command = [
            sys.executable,
            str(RUNNER),
            "--runtime-provider", "ProcessHostPool",
            "--runtime-transport", args.runtime_transport,
            "--topology-attestation", str(attestation),
            "--evidence-dir", str(args.evidence_dir),
            "--sdk", "all",
            "--auth-mode", args.auth_mode,
        ]
        if args.skip_prepare:
            command.append("--skip-prepare")
        _run(command, env=env)
    except RuntimeError as exc:
        print(f"[processhost-matrix] FAILED: {exc}", file=sys.stderr)
        return 1

    print(f"[processhost-matrix] PASS: ProcessHostPool/{args.runtime_transport} 3/3")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
