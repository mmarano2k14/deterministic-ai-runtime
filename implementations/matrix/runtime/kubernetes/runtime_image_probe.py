"""Inspect installed runtime identities without changing the Kubernetes bootstrap path."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import PurePosixPath
from typing import Any


class RuntimeImageProbeError(RuntimeError):
    """The local image cannot provide an exact, usable runtime identity."""


def _docker_text(docker: str, arguments: list[str]) -> str:
    # An argument vector avoids nested shell quoting on Windows PowerShell 5.1.
    try:
        result = subprocess.run(
            [docker, *arguments],
            shell=False,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=60,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise RuntimeImageProbeError(f"Runtime image probe could not run: {error}") from error
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()[-4000:]
        raise RuntimeImageProbeError(
            f"Runtime image probe failed (exit {result.returncode}): {detail}"
        )
    return result.stdout.strip()


def _image_text(docker: str, image_id: str, executable: str, *arguments: str) -> str:
    return _docker_text(
        docker,
        ["run", "--rm", "--network", "none", "--entrypoint", executable, image_id, *arguments],
    )


def _canonical_executable(docker: str, image_id: str, candidate: str) -> str:
    # Resolve inside the image, not on the Windows host. -e rejects missing targets.
    path = _image_text(docker, image_id, "/usr/bin/readlink", "-e", candidate)
    if (
        not path.startswith("/")
        or path == "/"
        or "\n" in path
        or "\r" in path
        or "\x00" in path
        or str(PurePosixPath(path)) != path
        or ".." in PurePosixPath(path).parts
    ):
        raise RuntimeImageProbeError(f"Invalid canonical executable for {candidate}: {path!r}")
    return path


def probe_runtime_image(docker: str, image: str) -> dict[str, Any]:
    """Probe one local Linux image; return canonical paths, versions and the Python hash."""
    if not docker.strip() or not image.strip():
        raise RuntimeImageProbeError("A Docker executable and local runtime image are required.")
    try:
        inspected = json.loads(_docker_text(docker, ["image", "inspect", image]))
    except json.JSONDecodeError as error:
        raise RuntimeImageProbeError("Docker image inspection did not return valid JSON.") from error
    if not isinstance(inspected, list) or len(inspected) != 1 or not isinstance(inspected[0], dict):
        raise RuntimeImageProbeError("Docker image inspection must identify exactly one local image.")
    metadata = inspected[0]
    image_id = metadata.get("Id")
    architecture = metadata.get("Architecture")
    if not isinstance(image_id, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", image_id):
        raise RuntimeImageProbeError("The local runtime image ID is missing or invalid.")
    if metadata.get("Os") != "linux" or not isinstance(architecture, str) or not architecture.strip():
        raise RuntimeImageProbeError("The runtime image must declare Linux and its architecture.")

    # Keep all probes on the same local image, even when the supplied name is a mutable tag.
    # A Docker image ID is not claimed to be a Kubernetes repository manifest digest.
    paths = {
        language: _canonical_executable(docker, image_id, candidate)
        for language, candidate in (
            ("dotnet", "/usr/share/dotnet/dotnet"),
            ("typescript", "/usr/local/bin/node"),
            ("python", "/usr/local/bin/python3"),
        )
    }
    dotnet_output = _image_text(docker, image_id, paths["dotnet"], "--list-runtimes")
    dotnet_versions = re.findall(r"(?m)^Microsoft\.NETCore\.App\s+(10\.0\.\d+)\s", dotnet_output)
    if not dotnet_versions:
        raise RuntimeImageProbeError("The image does not expose a stable .NET 10 runtime.")
    dotnet_version = max(dotnet_versions, key=lambda version: tuple(map(int, version.split("."))))

    node_output = _image_text(docker, image_id, paths["typescript"], "--version")
    node_version = re.fullmatch(r"v(\d+\.\d+\.\d+)", node_output)
    if node_version is None:
        raise RuntimeImageProbeError(f"Invalid exact Node runtime version: {node_output!r}")
    python_output = _image_text(docker, image_id, paths["python"], "--version")
    python_version = re.fullmatch(r"Python (\d+\.\d+\.\d+)", python_output)
    if python_version is None:
        raise RuntimeImageProbeError(f"Invalid exact Python runtime version: {python_output!r}")
    hash_output = _image_text(docker, image_id, "/usr/bin/sha256sum", paths["python"])
    python_hash = hash_output.split()[0] if hash_output else ""
    if not re.fullmatch(r"[0-9a-f]{64}", python_hash):
        raise RuntimeImageProbeError("The exact Python executable SHA-256 is missing or invalid.")

    return {
        "schemaVersion": 1,
        "runtimeImage": image,
        "localImageId": image_id,
        "operatingSystem": "linux",
        "architecture": architecture,
        "dotnet": {"executablePath": paths["dotnet"], "version": dotnet_version},
        "typescript": {"executablePath": paths["typescript"], "version": node_version.group(1)},
        "python": {
            "executablePath": paths["python"],
            "version": python_version.group(1),
            "sha256": python_hash,
        },
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--docker", required=True)
    parser.add_argument("--image", required=True)
    args = parser.parse_args(argv)
    try:
        evidence = probe_runtime_image(args.docker, args.image)
    except RuntimeImageProbeError as error:
        print(f"[kubernetes-runtime-image] {error}", file=sys.stderr)
        return 1
    print(json.dumps(evidence, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
