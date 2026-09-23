from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any

from matrix_process import run_scenario_process

ROOT = Path(__file__).resolve().parents[2]
MATRIX_ROOT = Path(__file__).resolve().parent
EVIDENCE_ROOT = MATRIX_ROOT / "evidence" / "watch"
DEFAULT_MANIFEST = MATRIX_ROOT / ".state" / "runtime-manifest.json"

WATCH_SCENARIOS: tuple[dict[str, str], ...] = (
    {
        "id": "watch-dotnet-client-dotnet-worker",
        "clientLanguage": "dotnet",
        "workerLanguage": "dotnet",
    },
    {
        "id": "watch-typescript-client-typescript-worker",
        "clientLanguage": "typescript",
        "workerLanguage": "typescript",
    },
    {
        "id": "watch-python-client-python-worker",
        "clientLanguage": "python",
        "workerLanguage": "python",
    },
)

REQUIRED_EVIDENCE = {
    "publish",
    "submit",
    "sdk-execution-watch",
    "mcp-http-public-boundary",
    "initial-snapshot",
    "ordered-incremental-event",
    "terminal-watch-convergence",
    "terminal-result",
    "public-execution-id",
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
    sample_project = (
        ROOT
        / "implementations"
        / "sdk"
        / "samples"
        / "published-functions"
        / "dotnet"
        / "Multiplexed.AI.Samples.PublishedFunctions"
        / "Multiplexed.AI.Samples.PublishedFunctions.csproj"
    )
    sample_root = Path(os.environ.get("MATRIX_SAMPLE_ROOT", str(MATRIX_ROOT / ".state" / "samples"))).resolve()
    sample_dotnet = sample_root / "dotnet"
    sample_typescript = sample_root / "typescript"
    sample_python = sample_root / "python"
    sample_dotnet.mkdir(parents=True, exist_ok=True)
    sample_typescript.mkdir(parents=True, exist_ok=True)
    sample_python.mkdir(parents=True, exist_ok=True)

    _run(["dotnet", "publish", str(sample_project), "-c", "Release", "-o", str(sample_dotnet)])
    shutil.copy2(
        ROOT / "implementations" / "sdk" / "samples" / "published-functions" / "typescript" / "functions.ts",
        sample_typescript / "functions.ts",
    )
    shutil.copy2(
        ROOT / "implementations" / "sdk" / "samples" / "published-functions" / "python" / "functions.py",
        sample_python / "functions.py",
    )
    os.environ["MATRIX_SAMPLE_ROOT"] = str(sample_root)

    _run(
        [
            "dotnet",
            "build",
            str(
                MATRIX_ROOT
                / "clients"
                / "dotnet"
                / "Multiplexed.AI.Matrix.DotNetClient"
                / "Multiplexed.AI.Matrix.DotNetClient.csproj"
            ),
            "-c",
            "Release",
        ]
    )
    _run([_resolve_tool("npm"), "run", "build"], cwd=ROOT / "implementations" / "node" / "sdk")


def _command_for(scenario: dict[str, str], manifest: Path) -> list[str]:
    worker = scenario["workerLanguage"]
    client = scenario["clientLanguage"]
    scenario_id = scenario["id"]
    evidence = EVIDENCE_ROOT / f"{scenario_id}.json"
    common = [
        "--feature",
        "watch",
        "--manifest",
        str(manifest),
        "--worker",
        worker,
        "--scenario-id",
        scenario_id,
        "--evidence",
        str(evidence),
    ]

    if client == "dotnet":
        project = (
            MATRIX_ROOT
            / "clients"
            / "dotnet"
            / "Multiplexed.AI.Matrix.DotNetClient"
            / "Multiplexed.AI.Matrix.DotNetClient.csproj"
        )
        return [
            os.environ.get("MATRIX_DOTNET_EXECUTABLE", "dotnet"),
            "run",
            "--project",
            str(project),
            "-c",
            "Release",
            "--no-build",
            "--",
            *common,
        ]
    if client == "typescript":
        return [
            os.environ.get("MATRIX_NODE_EXECUTABLE", "node"),
            str(MATRIX_ROOT / "clients" / "typescript" / "run.mjs"),
            *common,
        ]
    if client == "python":
        return [sys.executable, str(MATRIX_ROOT / "clients" / "python" / "run.py"), *common]
    raise RuntimeError(f"Unsupported Watch client language: {client}")


def _validate_evidence_document(document: dict[str, Any], scenario: dict[str, str]) -> str | None:
    if document.get("status") != "passed":
        return "status is not passed"
    if document.get("coverageTarget") != "execution-watch-e2e":
        return "wrong coverage target"
    if document.get("clientLanguage") != scenario["clientLanguage"]:
        return "wrong client language"
    if document.get("workerLanguage") != scenario["workerLanguage"]:
        return "wrong worker language"
    if document.get("initialSnapshotObserved") is not True:
        return "initial snapshot was not observed"
    if document.get("incrementalEventObserved") is not True:
        return "incremental event was not observed"
    if document.get("resyncObserved") is not False:
        return "nominal Watch unexpectedly resynchronized"
    if document.get("terminalStatus") != "Completed":
        return "Watch did not converge to Completed"

    sequences = document.get("publicSequences")
    if not isinstance(sequences, list) or len(sequences) < 2 or not all(isinstance(value, int) for value in sequences):
        return "public sequence evidence is missing or incomplete"
    if any(right <= left for left, right in zip(sequences, sequences[1:])):
        return "public sequence evidence is not strictly increasing"

    event_types = document.get("publicEventTypes")
    if not isinstance(event_types, list) or not event_types or not all(isinstance(value, str) and value for value in event_types):
        return "public event-type evidence is missing"

    evidence = document.get("evidence")
    if not isinstance(evidence, list) or not REQUIRED_EVIDENCE.issubset(set(evidence)):
        return "required Watch E2E evidence is missing"
    return None


def _run_scenario(scenario: dict[str, str], manifest: Path) -> None:
    if not manifest.exists():
        raise RuntimeError(
            f"Runtime manifest was not found at '{manifest}'. Start the local runtime stack first "
            "or use the local Watch-only runner."
        )

    EVIDENCE_ROOT.mkdir(parents=True, exist_ok=True)
    evidence = EVIDENCE_ROOT / f"{scenario['id']}.json"
    evidence.unlink(missing_ok=True)
    diagnostic_root = Path(
        os.environ.get("MATRIX_CLIENT_LOG_DIR", str(manifest.parent / "diagnostics" / "clients"))
    )
    run_scenario_process(
        _command_for(scenario, manifest),
        cwd=ROOT,
        log_path=diagnostic_root / f"{scenario['id']}.log",
    )


def _summary(scenarios: list[dict[str, str]] | None = None) -> int:
    selected = list(WATCH_SCENARIOS if scenarios is None else scenarios)
    passed = True
    for scenario in selected:
        path = EVIDENCE_ROOT / f"{scenario['id']}.json"
        if not path.exists():
            print(f"{scenario['id']}: NOT RUN")
            passed = False
            continue
        try:
            document = json.loads(path.read_text(encoding="utf-8-sig"))
            error = _validate_evidence_document(document, scenario)
        except Exception as exception:
            error = f"invalid evidence: {exception}"
        if error:
            print(f"{scenario['id']}: FAILED ({error})")
            passed = False
        else:
            print(f"{scenario['id']}: PASSED")
    return 0 if passed and selected else 1


def main() -> int:
    parser = argparse.ArgumentParser(description="Execute real MCP/HTTP execution.watch() E2E scenarios for all three external SDKs.")
    sub = parser.add_subparsers(dest="command", required=True)

    run = sub.add_parser("run", help="Run one Watch E2E scenario or all three against a running runtime manifest.")
    run.add_argument("--scenario", default="all")
    run.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    run.add_argument("--no-build", action="store_true")
    sub.add_parser("summary", help="Validate and summarize recorded Watch E2E evidence.")

    args = parser.parse_args()
    if args.command == "summary":
        return _summary()

    scenarios = list(WATCH_SCENARIOS)
    if args.scenario != "all":
        scenarios = [scenario for scenario in scenarios if scenario["id"] == args.scenario]
        if not scenarios:
            raise SystemExit(f"Unknown Watch scenario: {args.scenario}")

    if not args.no_build:
        _build_prerequisites()
    manifest = args.manifest.resolve()
    for scenario in scenarios:
        _run_scenario(scenario, manifest)
    return _summary(scenarios)


if __name__ == "__main__":
    raise SystemExit(main())
