from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Mapping, Sequence

DEMO_PACKAGE_VERSION = "0.0.0-local"


def require_tool(name: str) -> str:
    path = shutil.which(name)
    if path is None:
        raise RuntimeError(f"Required tool '{name}' was not found on PATH.")
    return path


def run(
    command: Sequence[str],
    *,
    cwd: Path | None = None,
    env: Mapping[str, str] | None = None,
) -> None:
    rendered = " ".join(f'"{part}"' if " " in part else part for part in command)
    print(f"[demo-bootstrap] > {rendered}")
    completed = subprocess.run(
        list(command),
        cwd=None if cwd is None else str(cwd),
        env=None if env is None else dict(env),
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(
            f"Command failed with exit code {completed.returncode}: {rendered}"
        )


def clean_directory(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True, exist_ok=True)


def verify_single(path_glob: list[Path], description: str) -> Path:
    if len(path_glob) != 1:
        rendered = ", ".join(str(p) for p in path_glob) or "(none)"
        raise RuntimeError(
            f"Expected exactly one {description}; found {len(path_glob)}: {rendered}"
        )
    return path_glob[0]


def smoke_environment() -> dict[str, str]:
    env = os.environ.copy()
    env.update(
        {
            "AI_RUNTIME_ENDPOINT": "http://127.0.0.1:1/mcp",
            "AI_RUNTIME_DOTNET_ENVIRONMENT_REF": "demo-smoke-dotnet",
            "AI_RUNTIME_TYPESCRIPT_ENVIRONMENT_REF": "demo-smoke-typescript",
            "AI_RUNTIME_PYTHON_ENVIRONMENT_REF": "demo-smoke-python",
            "OPENAI_MODEL": "gpt-smoke-not-used",
            "AI_DEMO_SMOKE": "1",
        }
    )
    env.pop("AI_RUNTIME_TOKEN", None)
    env.pop("AI_RUNTIME_ACCESS_CONTEXT", None)
    return env


def main() -> int:
    script_path = Path(__file__).resolve()
    demo_root = script_path.parents[1]
    repo_root = demo_root.parents[1]

    dotnet = require_tool("dotnet")
    npm = require_tool("npm")
    node = require_tool("node")
    host_python = sys.executable

    dotnet_sdk_project = (
        repo_root
        / "implementations"
        / "dotnet"
        / "src"
        / "Multiplexed.AI.Sdk"
        / "Multiplexed.AI.Sdk.csproj"
    )
    dotnet_contracts_project = (
        repo_root
        / "implementations"
        / "dotnet"
        / "src"
        / "Multiplexed.AI.Sdk.Contracts"
        / "Multiplexed.AI.Sdk.Contracts.csproj"
    )
    node_sdk = repo_root / "implementations" / "node" / "sdk"
    python_sdk = repo_root / "implementations" / "python" / "sdk"

    for required in (
        dotnet_sdk_project,
        dotnet_contracts_project,
        node_sdk / "package.json",
        python_sdk / "pyproject.toml",
    ):
        if not required.exists():
            raise RuntimeError(f"Required repository source was not found: {required}")

    package_root = demo_root / ".packages"
    clean_directory(package_root)

    dotnet_packages = package_root / "dotnet"
    typescript_packages = package_root / "typescript"
    python_packages = package_root / "python"
    nuget_cache = package_root / "nuget-cache"

    for path in (dotnet_packages, typescript_packages, python_packages, nuget_cache):
        path.mkdir(parents=True, exist_ok=True)

    # Remove consumer build/install outputs so no stale package can be reused.
    for path in (
        demo_root / "dotnet" / "bin",
        demo_root / "dotnet" / "obj",
        demo_root / "typescript" / "node_modules",
        demo_root / "typescript" / "dist",
        demo_root / "python" / ".venv",
    ):
        if path.exists():
            shutil.rmtree(path)

    print("[demo-bootstrap] LOCAL PACKAGE MODE")
    print("[demo-bootstrap] No NuGet, npm, or Python package is published by this script.")
    print()

    print("[demo-bootstrap] Packing local .NET SDK packages...")
    package_property = f"-p:PackageVersion={DEMO_PACKAGE_VERSION}"

    run(
        [
            dotnet,
            "pack",
            str(dotnet_contracts_project),
            "-c",
            "Release",
            f"-p:PackageOutputPath={dotnet_packages}",
            package_property,
            "--nologo",
        ]
    )
    run(
        [
            dotnet,
            "pack",
            str(dotnet_sdk_project),
            "-c",
            "Release",
            f"-p:PackageOutputPath={dotnet_packages}",
            package_property,
            "--nologo",
        ]
    )

    contracts_nupkg = verify_single(
        list(dotnet_packages.glob(f"Multiplexed.AI.Sdk.Contracts.{DEMO_PACKAGE_VERSION}.nupkg")),
        "local SDK contracts NuGet package",
    )
    sdk_nupkg = verify_single(
        list(dotnet_packages.glob(f"Multiplexed.AI.Sdk.{DEMO_PACKAGE_VERSION}.nupkg")),
        "local SDK NuGet package",
    )
    print(f"[demo-bootstrap] Local NuGet: {contracts_nupkg.name}")
    print(f"[demo-bootstrap] Local NuGet: {sdk_nupkg.name}")

    print()
    print("[demo-bootstrap] Packing local TypeScript SDK...")
    if (node_sdk / "package-lock.json").exists():
        run([npm, "ci"], cwd=node_sdk)
    else:
        run([npm, "install"], cwd=node_sdk)
    run([npm, "run", "build"], cwd=node_sdk)
    run(
        [npm, "pack", "--pack-destination", str(typescript_packages)],
        cwd=node_sdk,
    )
    typescript_tgz = verify_single(
        list(typescript_packages.glob("multiplexed-ai-sdk-*.tgz")),
        "local TypeScript SDK tarball",
    )
    print(f"[demo-bootstrap] Local npm tarball: {typescript_tgz.name}")

    print()
    print("[demo-bootstrap] Building local Python SDK wheel...")
    run(
        [
            host_python,
            "-m",
            "pip",
            "wheel",
            str(python_sdk),
            "--no-deps",
            "-w",
            str(python_packages),
        ]
    )
    python_wheel = verify_single(
        list(python_packages.glob("multiplexed_ai_sdk-*.whl")),
        "local Python SDK wheel",
    )
    print(f"[demo-bootstrap] Local Python wheel: {python_wheel.name}")

    print()
    print("[demo-bootstrap] Restoring/building .NET consumer from local SDK package...")
    dotnet_demo = demo_root / "dotnet" / "InteractiveAgentSdkDemo.csproj"
    nuget_config = demo_root / "NuGet.Config"
    dotnet_env = os.environ.copy()
    dotnet_env["NUGET_PACKAGES"] = str(nuget_cache)

    run(
        [
            dotnet,
            "restore",
            str(dotnet_demo),
            "--configfile",
            str(nuget_config),
            "--packages",
            str(nuget_cache),
            "--nologo",
        ],
        env=dotnet_env,
    )

    assets_path = demo_root / "dotnet" / "obj" / "project.assets.json"
    if not assets_path.exists():
        raise RuntimeError(f".NET restore did not produce {assets_path}")
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    libraries = assets.get("libraries", {})
    expected_sdk_key = f"Multiplexed.AI.Sdk/{DEMO_PACKAGE_VERSION}"
    expected_contracts_key = f"Multiplexed.AI.Sdk.Contracts/{DEMO_PACKAGE_VERSION}"
    if expected_sdk_key not in libraries or expected_contracts_key not in libraries:
        raise RuntimeError(
            "The .NET consumer did not resolve the expected local SDK packages. "
            f"Expected '{expected_sdk_key}' and '{expected_contracts_key}'."
        )

    run(
        [
            dotnet,
            "build",
            str(dotnet_demo),
            "-c",
            "Release",
            "--no-restore",
            "--nologo",
        ],
        env=dotnet_env,
    )

    print()
    print("[demo-bootstrap] Installing/building TypeScript consumer...")
    typescript_demo = demo_root / "typescript"
    run([npm, "install"], cwd=typescript_demo)
    run([npm, "run", "build"], cwd=typescript_demo)

    print()
    print("[demo-bootstrap] Creating Python consumer environment...")
    python_demo = demo_root / "python"
    venv = python_demo / ".venv"
    run([host_python, "-m", "venv", str(venv)])

    if os.name == "nt":
        venv_python = venv / "Scripts" / "python.exe"
    else:
        venv_python = venv / "bin" / "python"

    run([str(venv_python), "-m", "pip", "install", str(python_wheel)])
    run(
        [
            str(venv_python),
            "-m",
            "pip",
            "install",
            "-e",
            str(python_demo),
            "--no-deps",
        ]
    )

    print()
    print("[demo-bootstrap] Running consumer smoke checks (no runtime request is sent)...")
    smoke_env = smoke_environment()

    run(
        [
            dotnet,
            "run",
            "--project",
            str(dotnet_demo),
            "-c",
            "Release",
            "--no-build",
        ],
        env={**smoke_env, "NUGET_PACKAGES": str(nuget_cache)},
    )
    run(
        [node, str(typescript_demo / "dist" / "index.js")],
        cwd=typescript_demo,
        env=smoke_env,
    )
    run(
        [str(venv_python), "-m", "interactive_agent_sdk_demo"],
        cwd=python_demo,
        env=smoke_env,
    )

    print()
    print("[demo-bootstrap] READY")
    print("[demo-bootstrap] Local package build: PASS")
    print("[demo-bootstrap] .NET consumer build/smoke: PASS")
    print("[demo-bootstrap] TypeScript consumer build/smoke: PASS")
    print("[demo-bootstrap] Python consumer install/smoke: PASS")
    print()
    print("Run the launcher with:")
    print(r"  .\demo\interactive-agent-sdk\run.cmd")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\n[demo-bootstrap] CANCELLED", file=sys.stderr)
        raise SystemExit(130)
    except Exception as exc:
        print(f"\n[demo-bootstrap] FAILED: {exc}", file=sys.stderr)
        raise SystemExit(1)
