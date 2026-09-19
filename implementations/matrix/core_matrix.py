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
EVIDENCE_ROOT = MATRIX_ROOT / "evidence" / "core"
DEFAULT_MANIFEST = MATRIX_ROOT / ".state" / "runtime-manifest.json"


def _run(command: list[str], cwd: Path | None = None, env: dict[str, str] | None = None) -> None:
    print("> " + " ".join(command), flush=True)
    subprocess.run(command, cwd=cwd or ROOT, env=env, check=True)


def _resolve_tool(name: str, windows: bool | None = None) -> str:
    is_windows = os.name == "nt" if windows is None else windows
    candidates = [f"{name}.cmd", f"{name}.exe", name] if is_windows else [name]
    for candidate in candidates:
        resolved = shutil.which(candidate)
        if resolved:
            return resolved
    raise FileNotFoundError(f"Required command was not found on PATH: {name}")


def _build_prerequisites(client_language: str) -> None:
    sample_project = ROOT / "implementations" / "sdk" / "samples" / "published-functions" / "dotnet" / "Multiplexed.AI.Samples.PublishedFunctions" / "Multiplexed.AI.Samples.PublishedFunctions.csproj"
    sample_root = Path(os.environ.get("MATRIX_SAMPLE_ROOT", str(MATRIX_ROOT / ".state" / "samples"))).resolve()
    sample_dotnet = sample_root / "dotnet"
    sample_typescript = sample_root / "typescript"
    sample_python = sample_root / "python"
    sample_dotnet.mkdir(parents=True, exist_ok=True)
    sample_typescript.mkdir(parents=True, exist_ok=True)
    sample_python.mkdir(parents=True, exist_ok=True)
    _run(["dotnet", "publish", str(sample_project), "-c", "Release", "-o", str(sample_dotnet)])
    shutil.copy2(ROOT / "implementations" / "sdk" / "samples" / "published-functions" / "typescript" / "functions.ts", sample_typescript / "functions.ts")
    shutil.copy2(ROOT / "implementations" / "sdk" / "samples" / "published-functions" / "python" / "functions.py", sample_python / "functions.py")
    os.environ["MATRIX_SAMPLE_ROOT"] = str(sample_root)
    if client_language == "dotnet":
        _run([
            "dotnet", "build",
            str(MATRIX_ROOT / "clients" / "dotnet" / "Multiplexed.AI.Matrix.DotNetClient" / "Multiplexed.AI.Matrix.DotNetClient.csproj"),
            "-c", "Release",
        ])
    elif client_language == "typescript":
        _run([_resolve_tool("npm"), "run", "build"], cwd=ROOT / "implementations" / "node" / "sdk")


def _command_for(scenario: dict[str, Any], manifest: Path) -> list[str]:
    worker = scenario["workerLanguage"]
    client = scenario["clientLanguage"]
    scenario_id = scenario["id"]
    evidence = EVIDENCE_ROOT / f"{scenario_id}.json"
    common = [
        "--manifest", str(manifest),
        "--worker", worker,
        "--scenario-id", scenario_id,
        "--evidence", str(evidence),
    ]
    if client == "dotnet":
        project = MATRIX_ROOT / "clients" / "dotnet" / "Multiplexed.AI.Matrix.DotNetClient" / "Multiplexed.AI.Matrix.DotNetClient.csproj"
        return ["dotnet", "run", "--project", str(project), "-c", "Release", "--no-build", "--", *common]
    if client == "typescript":
        return ["node", str(MATRIX_ROOT / "clients" / "typescript" / "run.mjs"), *common]
    if client == "python":
        return [sys.executable, str(MATRIX_ROOT / "clients" / "python" / "run.py"), *common]
    raise RuntimeError(f"Unsupported client language: {client}")


def _run_scenario(scenario: dict[str, Any], manifest: Path, no_build: bool) -> None:
    if not no_build:
        _build_prerequisites(scenario["clientLanguage"])
    if not manifest.exists():
        raise RuntimeError(
            f"Runtime manifest was not found at '{manifest}'. Start the local runtime stack first "
            "or use the Docker Compose topology."
        )
    EVIDENCE_ROOT.mkdir(parents=True, exist_ok=True)
    _run(_command_for(scenario, manifest))


def _summary(plan: dict[str, Any]) -> int:
    rows: list[tuple[str, str]] = []
    for scenario in plan["coreScenarios"]:
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
    parser = argparse.ArgumentParser(description="Execute the live 3 x 3 external SDK / hosted worker matrix.")
    sub = parser.add_subparsers(dest="command", required=True)

    run = sub.add_parser("run", help="Run one core scenario or all nine scenarios against an already running local topology.")
    run.add_argument("--scenario", default="all")
    run.add_argument("--topology", choices=("local",), default="local")
    run.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    run.add_argument("--no-build", action="store_true")
    sub.add_parser("summary", help="Summarize recorded live core evidence.")

    args = parser.parse_args()
    plan = load_plan()
    assert_valid_plan(plan)
    if args.command == "summary":
        return _summary(plan)

    scenarios = plan["coreScenarios"]
    if args.scenario != "all":
        scenarios = [item for item in scenarios if item["id"] == args.scenario]
        if not scenarios:
            raise SystemExit(f"Unknown core scenario: {args.scenario}")

    if args.scenario == "all" and not args.no_build:
        _build_prerequisites("dotnet")
        _build_prerequisites("typescript")
    for scenario in scenarios:
        _run_scenario(scenario, args.manifest.resolve(), no_build=args.no_build or args.scenario == "all")
    return _summary(plan)


if __name__ == "__main__":
    raise SystemExit(main())
