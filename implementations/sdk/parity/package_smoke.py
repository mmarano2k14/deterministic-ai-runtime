from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

PARITY_ROOT = Path(__file__).resolve().parent
REPO_ROOT = PARITY_ROOT.parents[2]
DOTNET_CONTRACTS = REPO_ROOT / "implementations" / "dotnet" / "src" / "Multiplexed.AI.Sdk.Contracts" / "Multiplexed.AI.Sdk.Contracts.csproj"
DOTNET_SDK = REPO_ROOT / "implementations" / "dotnet" / "src" / "Multiplexed.AI.Sdk" / "Multiplexed.AI.Sdk.csproj"
NODE_SDK = REPO_ROOT / "implementations" / "node" / "sdk"
PYTHON_SDK = REPO_ROOT / "implementations" / "python" / "sdk"


def _run(args: list[str], cwd: Path | None = None) -> None:
    print(">", " ".join(args))
    subprocess.run(args, cwd=cwd, check=True)


def _require(command: str) -> str:
    resolved = shutil.which(command)
    if resolved is None:
        raise RuntimeError(f"Required command '{command}' was not found on PATH.")
    return resolved


def _nuget_version(package_path: Path) -> str:
    with zipfile.ZipFile(package_path) as archive:
        nuspec = next(name for name in archive.namelist() if name.endswith(".nuspec"))
        package_root = ET.fromstring(archive.read(nuspec))
    metadata = next(child for child in package_root if child.tag.rsplit("}", 1)[-1] == "metadata")
    version = next(child for child in metadata if child.tag.rsplit("}", 1)[-1] == "version")
    if not version.text:
        raise RuntimeError(f"Package '{package_path.name}' does not declare a version.")
    return version.text


def _dotnet_smoke(temp_root: Path) -> None:
    dotnet = _require("dotnet")
    package_dir = temp_root / "nuget"
    package_dir.mkdir()
    _run([dotnet, "pack", str(DOTNET_CONTRACTS), "-c", "Release", "-o", str(package_dir)])
    _run([dotnet, "pack", str(DOTNET_SDK), "-c", "Release", "-o", str(package_dir)])

    sdk_packages = [
        package
        for package in package_dir.glob("Multiplexed.AI.Sdk.*.nupkg")
        if ".Contracts." not in package.name
    ]
    if not sdk_packages:
        raise RuntimeError("The .NET SDK package was not produced.")
    sdk_package = max(sdk_packages, key=lambda item: item.stat().st_mtime)
    version = _nuget_version(sdk_package)

    consumer = temp_root / "dotnet-consumer"
    consumer.mkdir()
    (consumer / "Smoke.csproj").write_text(
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
        "  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n"
        f"  <ItemGroup><PackageReference Include=\"Multiplexed.AI.Sdk\" Version=\"{version}\" /></ItemGroup>\n"
        "</Project>\n",
        encoding="utf-8",
    )
    (consumer / "Program.cs").write_text(
        "using System;\nusing Multiplexed.AI.Sdk;\nConsole.WriteLine(typeof(AiSdkClient).FullName);\n",
        encoding="utf-8",
    )
    source = package_dir.as_posix()
    (consumer / "NuGet.Config").write_text(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        "<configuration><packageSources><clear />"
        f"<add key=\"local\" value=\"{source}\" />"
        "<add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />"
        "</packageSources></configuration>\n",
        encoding="utf-8",
    )
    _run([dotnet, "restore", "Smoke.csproj", "--configfile", "NuGet.Config"], cwd=consumer)
    _run([dotnet, "run", "--project", "Smoke.csproj", "--no-restore"], cwd=consumer)


def _node_smoke(temp_root: Path) -> None:
    npm = _require("npm")
    node = _require("node")
    package_dir = temp_root / "npm"
    package_dir.mkdir()
    _run([npm, "run", "build"], cwd=NODE_SDK)
    _run([npm, "pack", "--pack-destination", str(package_dir)], cwd=NODE_SDK)
    tarballs = list(package_dir.glob("*.tgz"))
    if not tarballs:
        raise RuntimeError("The TypeScript SDK package was not produced.")
    tarball = max(tarballs, key=lambda item: item.stat().st_mtime)

    consumer = temp_root / "node-consumer"
    consumer.mkdir()
    (consumer / "package.json").write_text(
        json.dumps({"name": "multiplexed-ai-sdk-smoke", "private": True, "type": "module"}),
        encoding="utf-8",
    )
    _run([npm, "install", str(tarball), "--ignore-scripts", "--no-audit", "--no-fund"], cwd=consumer)
    (consumer / "smoke.mjs").write_text(
        "import { AiSdkClient, AI_SDK_PROTOCOL_VERSION } from \"@multiplexed/ai-sdk\";\n"
        "if (AI_SDK_PROTOCOL_VERSION !== 1) throw new Error(\"Unexpected protocol version\");\n"
        "console.log(AiSdkClient.name);\n",
        encoding="utf-8",
    )
    _run([node, "smoke.mjs"], cwd=consumer)


def _python_smoke(temp_root: Path) -> None:
    python = sys.executable
    wheel_dir = temp_root / "python-wheels"
    wheel_dir.mkdir()
    _run([python, "-m", "pip", "wheel", str(PYTHON_SDK), "--no-deps", "-w", str(wheel_dir)])
    wheels = list(wheel_dir.glob("*.whl"))
    if not wheels:
        raise RuntimeError("The Python SDK wheel was not produced.")
    wheel = max(wheels, key=lambda item: item.stat().st_mtime)

    target = temp_root / "python-target"
    target.mkdir()
    _run([python, "-m", "pip", "install", str(wheel), "--no-deps", "--target", str(target)])
    env = os.environ.copy()
    env["PYTHONPATH"] = str(target)
    print(">", python, "-c", "import multiplexed_ai_sdk")
    subprocess.run(
        [python, "-c", "import multiplexed_ai_sdk; print(multiplexed_ai_sdk.AI_SDK_PROTOCOL_VERSION)"],
        check=True,
        env=env,
    )


def main() -> int:
    parser = argparse.ArgumentParser(description="Build and consume local external SDK packages.")
    parser.add_argument(
        "--language",
        choices=("all", "dotnet", "typescript", "python"),
        default="all",
        help="Package smoke scope.",
    )
    args = parser.parse_args()

    with tempfile.TemporaryDirectory(prefix="multiplexed-ai-sdk-smoke-") as temp:
        temp_root = Path(temp)
        if args.language in ("all", "dotnet"):
            _dotnet_smoke(temp_root)
        if args.language in ("all", "typescript"):
            _node_smoke(temp_root)
        if args.language in ("all", "python"):
            _python_smoke(temp_root)

    print("External SDK package smoke validation completed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
