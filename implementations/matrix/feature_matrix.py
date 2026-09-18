from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any

from matrix_plan import assert_valid_plan, load_plan

ROOT = Path(__file__).resolve().parents[2]
MATRIX_ROOT = Path(__file__).resolve().parent
EVIDENCE_ROOT = MATRIX_ROOT / "evidence" / "features"
DEFAULT_MANIFEST = MATRIX_ROOT / ".state" / "runtime-manifest.json"
FEATURE_TARGETS = {
    "publication-pinning",
    "deterministic-dependency-packaging",
    "custom-policy-family",
}


def _run(command: list[str], cwd: Path | None = None) -> None:
    print("> " + " ".join(command), flush=True)
    subprocess.run(command, cwd=cwd or ROOT, check=True)


def _resolve_tool(name: str, windows: bool | None = None) -> str:
    is_windows = os.name == "nt" if windows is None else windows
    candidates = [f"{name}.cmd", f"{name}.exe", name] if is_windows else [name]
    for candidate in candidates:
        resolved = shutil.which(candidate)
        if resolved:
            return resolved
    raise FileNotFoundError(f"Required command was not found on PATH: {name}")


def _build_prerequisites() -> None:
    dotnet = _resolve_tool("dotnet")
    core_project = MATRIX_ROOT / "fixtures" / "dotnet-worker" / "Multiplexed.AI.Matrix.Worker" / "Multiplexed.AI.Matrix.Worker.csproj"
    packaged_project = MATRIX_ROOT / "fixtures" / "dotnet-packaged-worker" / "Multiplexed.AI.Matrix.PackagedWorker" / "Multiplexed.AI.Matrix.PackagedWorker.csproj"
    _run([dotnet, "build", str(core_project), "-c", "Release"])
    _run([dotnet, "build", str(packaged_project), "-c", "Release"])

    fixture_root = Path(
        os.environ.get("MATRIX_FIXTURE_ROOT", str(MATRIX_ROOT / ".state" / "fixtures"))
    ).resolve()
    core_target = fixture_root / "dotnet-worker"
    packaged_target = fixture_root / "dotnet-packaged-worker"
    core_target.mkdir(parents=True, exist_ok=True)
    packaged_target.mkdir(parents=True, exist_ok=True)

    shutil.copy2(
        core_project.parent / "bin" / "Release" / "net10.0" / "Multiplexed.AI.Matrix.Worker.dll",
        core_target / "Multiplexed.AI.Matrix.Worker.dll",
    )
    packaged_bin = packaged_project.parent / "bin" / "Release" / "net10.0"
    for name in ("Multiplexed.AI.Matrix.PackagedWorker.dll", "Multiplexed.AI.Matrix.Dependency.dll"):
        shutil.copy2(packaged_bin / name, packaged_target / name)

    os.environ["MATRIX_FIXTURE_ROOT"] = str(fixture_root)


def _command_for(scenario: dict[str, Any], manifest: Path) -> list[str]:
    evidence = EVIDENCE_ROOT / f"{scenario['id']}.json"
    command = [
        sys.executable,
        str(MATRIX_ROOT / "clients" / "python" / "feature.py"),
        "--manifest",
        str(manifest),
        "--feature",
        scenario["coverageTarget"],
        "--worker",
        scenario["workerLanguage"],
        "--scenario-id",
        scenario["id"],
        "--evidence",
        str(evidence),
    ]
    if scenario["coverageTarget"] == "custom-policy-family":
        command.extend(["--policy-family", scenario["coverageValues"][0]])
    return command


def _run_scenario(scenario: dict[str, Any], manifest: Path) -> None:
    if not manifest.exists():
        raise RuntimeError(
            f"Runtime manifest was not found at '{manifest}'. Start the local runtime stack first "
            "or use the Docker Compose topology."
        )
    EVIDENCE_ROOT.mkdir(parents=True, exist_ok=True)
    _run(_command_for(scenario, manifest))


def _summary(plan: dict[str, Any]) -> int:
    scenarios = [
        scenario for scenario in plan["featureScenarios"]
        if scenario["coverageTarget"] in FEATURE_TARGETS
    ]
    rows: list[tuple[str, str]] = []
    for scenario in scenarios:
        path = EVIDENCE_ROOT / f"{scenario['id']}.json"
        if not path.exists():
            rows.append((scenario["id"], "NOT RUN"))
            continue
        try:
            document = json.loads(path.read_text(encoding="utf-8-sig"))
            rows.append((scenario["id"], str(document.get("status", "INVALID")).upper()))
        except Exception:
            rows.append((scenario["id"], "INVALID"))
    for scenario_id, status in rows:
        print(f"{scenario_id}: {status}")
    return 0 if rows and all(status == "PASSED" for _, status in rows) else 1


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Execute bound production-like feature scenarios through the external Python SDK client."
    )
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run")
    run.add_argument("--scenario", default="all")
    run.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    run.add_argument("--no-build", action="store_true")
    sub.add_parser("summary")
    args = parser.parse_args()

    plan = load_plan()
    assert_valid_plan(plan)
    scenarios = [
        scenario for scenario in plan["featureScenarios"]
        if scenario["coverageTarget"] in FEATURE_TARGETS
    ]

    if args.command == "summary":
        return _summary(plan)

    if args.scenario != "all":
        scenarios = [scenario for scenario in scenarios if scenario["id"] == args.scenario]
        if not scenarios:
            raise SystemExit(f"Unknown feature scenario: {args.scenario}")

    if not args.no_build:
        _build_prerequisites()
    for scenario in scenarios:
        _run_scenario(scenario, args.manifest.resolve())
    return _summary(plan)


if __name__ == "__main__":
    raise SystemExit(main())
