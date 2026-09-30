from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import run_e2e as common
from attest_kubernetespool import build_live_attestation
from topology import TopologyRequest, verify_topology

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
LAUNCHER = ROOT / "launcher" / "launcher.py"
VALIDATOR = HERE / "validate_evidence.py"
SDKS = ("dotnet", "typescript", "python")


def _load_profile(path: Path) -> dict[str, object]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise RuntimeError("KubernetesPool profile root must be an object.")
    return value


def _write_attestation(path: Path, value: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def _wait_for_live_attestation(
    profile: dict[str, object],
    transport: str,
    output: Path,
    process: subprocess.Popen[bytes] | None,
    timeout_seconds: int,
) -> dict[str, object]:
    deadline = time.monotonic() + timeout_seconds
    last_failed: list[str] = []
    while time.monotonic() < deadline:
        try:
            value = build_live_attestation(profile, transport)
            if value.get("verified") is True:
                _write_attestation(output, value)
                return value
            last_failed = [
                str(item.get("name"))
                for item in value.get("checks", [])
                if isinstance(item, dict) and item.get("passed") is not True
            ]
        except Exception as exc:
            last_failed = [str(exc)]

        if process is not None and process.poll() is not None:
            # One final cluster inspection after the consumer exits can still prove
            # the pool that executed it because the owning control plane remains alive.
            try:
                value = build_live_attestation(profile, transport)
                if value.get("verified") is True:
                    _write_attestation(output, value)
                    return value
            except Exception:
                pass
            raise RuntimeError(
                "External SDK consumer exited before KubernetesPool became live-attestable. "
                f"returncode={process.returncode}; last failed checks={last_failed}"
            )
        time.sleep(1.0)

    raise RuntimeError(
        "KubernetesPool did not become live-attestable within the validation window. "
        f"Last failed checks={last_failed}"
    )


def _validate_evidence(evidence: Path, attestation: dict[str, object], consumer_returncode: int) -> bool:
    if not evidence.is_file():
        print(f"[kubernetes-interactive] evidence missing: {evidence}", file=sys.stderr)
        return False
    common._attach_topology(evidence, attestation)
    check = subprocess.run([sys.executable, str(VALIDATOR), str(evidence)], cwd=ROOT, check=False)
    if check.returncode != 0:
        return False
    if consumer_returncode != 0:
        print(
            f"[kubernetes-interactive] evidence passed but consumer returned {consumer_returncode}",
            file=sys.stderr,
        )
        return False
    return True


def _consumer_env(token: str, evidence: Path, transport: str, prompt: str) -> dict[str, str]:
    env = common._consumer_environment(os.environ, token)
    env.update(
        {
            "AI_DEMO_AUTOMATED_VALIDATION": "1",
            "AI_DEMO_EVIDENCE_PATH": str(evidence),
            "AI_VALIDATION_RUNTIME_PROVIDER": "KubernetesPool",
            "AI_VALIDATION_RUNTIME_TRANSPORT": transport,
            "AI_DEMO_VALIDATION_PROMPT": prompt,
            "AI_DEMO_VALIDATION_TIMEOUT_SECONDS": os.environ.get(
                "AI_DEMO_VALIDATION_TIMEOUT_SECONDS", "300"
            ),
        }
    )
    return env


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Run the three interactive external SDK consumers through an already-started KubernetesPool control plane."
    )
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--attestation", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    parser.add_argument("--runtime-transport", choices=("HTTP", "gRPC"), default="HTTP")
    parser.add_argument("--prompt", default="Validate the interactive agent topology through the KubernetesPool public SDK boundary.")
    parser.add_argument("--attestation-timeout-seconds", type=int, default=300)
    parser.add_argument("--skip-prepare", action="store_true")
    args = parser.parse_args()

    try:
        profile = _load_profile(args.profile)
        args.evidence_dir.mkdir(parents=True, exist_ok=True)
        if not args.skip_prepare:
            common._prepare_consumers()
        token, token_source = common._resolve_token("local-jwt", ROOT / ".env")
        print(f"[kubernetes-interactive] authentication: {token_source}; token not displayed")
        print("[kubernetes-interactive] server OpenAI credential remains runtime-owned; not forwarded to SDK consumers")

        failures = 0
        attestation: dict[str, object] | None = None
        for index, sdk in enumerate(SDKS):
            evidence = args.evidence_dir / f"kubernetespool-{args.runtime_transport.lower()}-{sdk}.json"
            evidence.unlink(missing_ok=True)
            env = _consumer_env(token, evidence, args.runtime_transport, args.prompt)
            print(f"[kubernetes-interactive] {sdk}: starting KubernetesPool/{args.runtime_transport}")
            process = subprocess.Popen([sys.executable, str(LAUNCHER), "--sdk", sdk], cwd=ROOT, env=env)

            if index == 0:
                attestation = _wait_for_live_attestation(
                    profile,
                    args.runtime_transport,
                    args.attestation,
                    process,
                    args.attestation_timeout_seconds,
                )
                observed = attestation.get("observed", {})
                print(
                    "[kubernetes-interactive] topology attested: "
                    f"poolId={observed.get('poolId')} "
                    f"readyPods={observed.get('readyPodNames')} "
                    f"runtimeInstances={observed.get('runtimeInstanceCount')}"
                )
            else:
                # Refresh the live proof before every subsequent SDK so the 3/3 result
                # is tied to the still-running pool rather than a stale first-client snapshot.
                refreshed = build_live_attestation(profile, args.runtime_transport)
                if refreshed.get("verified") is not True:
                    raise RuntimeError(f"KubernetesPool topology lost readiness before {sdk} execution.")
                attestation = refreshed
                _write_attestation(args.attestation, attestation)

            returncode = process.wait()
            assert attestation is not None
            if _validate_evidence(evidence, attestation, returncode):
                print(f"[kubernetes-interactive] {sdk}: PASS -> {evidence}")
            else:
                failures += 1

        # Final fail-closed topology verification uses the same generic consumer-side contract.
        verify_topology(
            TopologyRequest("KubernetesPool", args.runtime_transport),
            local_endpoint=True,
            attestation_path=args.attestation,
        )
        return 0 if failures == 0 else 1
    except (RuntimeError, subprocess.CalledProcessError, OSError, json.JSONDecodeError) as exc:
        print(f"[kubernetes-interactive] FAILED: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
