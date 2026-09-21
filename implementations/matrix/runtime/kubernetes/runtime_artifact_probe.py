"""Compare selected runtime file hashes without conflating image ID namespaces."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

# This is deliberately a named subset, not an assertion of whole-image equality.
ARTIFACT_PATHS = (
    "/app/Multiplexed.AI.McpServer.Host.dll",
    "/app/Multiplexed.AI.McpServer.dll",
    "/app/Multiplexed.AI.dll",
    "/app/Multiplexed.Abstractions.dll",
    "/app/workers/dotnet/Multiplexed.AI.HostedInvocation.DotNetWorker.dll",
    "/app/workers/typescript/worker.mjs",
    "/app/workers/python/worker.py",
    "/usr/local/bin/python3.12",
)
SCOPE = "selected-runtime-files-not-whole-image"


class ArtifactProbeError(RuntimeError):
    """One or more requested artifact hashes could not be established."""


def _command_text(arguments: list[str]) -> str:
    try:
        result = subprocess.run(
            arguments, shell=False, check=False, capture_output=True,
            text=True, encoding="utf-8", errors="replace", timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ArtifactProbeError(f"Artifact inspection could not run: {error}") from error
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()[-4000:]
        raise ArtifactProbeError(f"Artifact inspection failed (exit {result.returncode}): {detail}")
    return result.stdout


def parse_hashes(text: str) -> dict[str, str]:
    """Accept one exact SHA-256 record for each explicitly requested absolute path."""
    hashes: dict[str, str] = {}
    for line in text.splitlines():
        if not line.strip():
            continue
        match = re.fullmatch(r"([0-9a-f]{64}) [ *](/[^\r\n]+)", line)
        if match is None:
            raise ArtifactProbeError(f"Invalid sha256sum record: {line!r}")
        digest, path = match.groups()
        if path not in ARTIFACT_PATHS or path in hashes:
            raise ArtifactProbeError(f"Unexpected or duplicate artifact path: {path}")
        hashes[path] = digest
    if set(hashes) != set(ARTIFACT_PATHS):
        raise ArtifactProbeError("Artifact hash coverage is incomplete.")
    return hashes


def _validate_hashes(value: object) -> dict[str, str]:
    if not isinstance(value, dict) or set(value) != set(ARTIFACT_PATHS):
        raise ArtifactProbeError("Expected artifact evidence has incomplete file coverage.")
    if any(not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest)
           for digest in value.values()):
        raise ArtifactProbeError("Expected artifact evidence contains an invalid SHA-256.")
    return dict(value)


def probe_local(docker: str, image_id: str) -> dict[str, Any]:
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", image_id):
        raise ArtifactProbeError("Local inspection requires the already-probed Docker image ID, not a mutable tag.")
    hashes = parse_hashes(_command_text([
        docker, "run", "--rm", "--network", "none", "--entrypoint", "/usr/bin/sha256sum",
        image_id, *ARTIFACT_PATHS,
    ]))
    return {"schemaVersion": 1, "scope": SCOPE, "status": "captured",
            "localImageId": image_id, "hashes": hashes}


def compare_pod(kubectl: str, namespace: str, pod: str, container: str,
                expected: dict[str, Any]) -> dict[str, Any]:
    if expected.get("schemaVersion") != 1 or expected.get("scope") != SCOPE or expected.get("status") != "captured":
        raise ArtifactProbeError("Expected artifact evidence is not a successful local capture.")
    reference = _validate_hashes(expected.get("hashes"))
    for name, value in (("namespace", namespace), ("pod", pod), ("container", container)):
        if not re.fullmatch(r"[a-z0-9][a-z0-9.-]*", value):
            raise ArtifactProbeError(f"Invalid Kubernetes {name}.")
    actual = parse_hashes(_command_text([
        kubectl, "--request-timeout=10s", "exec", pod, "-n", namespace,
        "-c", container, "--", "/usr/bin/sha256sum", *ARTIFACT_PATHS,
    ]))
    differences = [{"path": path, "expected": reference[path], "actual": actual[path]}
                   for path in ARTIFACT_PATHS if reference[path] != actual[path]]
    return {"schemaVersion": 1, "scope": SCOPE,
            "status": "mismatch" if differences else "match",
            "localImageId": expected.get("localImageId"),
            "namespace": namespace, "pod": pod, "container": container,
            "hashes": actual, "differences": differences}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="mode", required=True)
    local = commands.add_parser("local")
    local.add_argument("--docker", required=True)
    local.add_argument("--image-id", required=True)
    local.add_argument("--output", required=True, type=Path)
    pod = commands.add_parser("pod")
    pod.add_argument("--kubectl", required=True)
    pod.add_argument("--namespace", required=True)
    pod.add_argument("--pod", required=True)
    pod.add_argument("--container", default="runtime-pool")
    pod.add_argument("--expected", required=True, type=Path)
    pod.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        if args.mode == "local":
            evidence = probe_local(args.docker, args.image_id)
        else:
            expected = json.loads(args.expected.read_text(encoding="utf-8-sig"))
            if not isinstance(expected, dict):
                raise ArtifactProbeError("Expected evidence must be a JSON object.")
            evidence = compare_pod(args.kubectl, args.namespace, args.pod, args.container, expected)
    except (ArtifactProbeError, OSError, ValueError) as error:
        evidence = {"schemaVersion": 1, "scope": SCOPE, "status": "unavailable", "error": str(error)}
    try:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    except OSError as error:
        print(f"[runtime-artifacts] Could not retain evidence: {error}", file=sys.stderr)
        return 2
    print(f"[runtime-artifacts] {evidence['status']}; selected files only; evidence={args.output}")
    # A diagnostic mismatch is recorded, not substituted for the SDK scenario outcome.
    return 2 if evidence["status"] == "unavailable" else 0


if __name__ == "__main__":
    raise SystemExit(main())
