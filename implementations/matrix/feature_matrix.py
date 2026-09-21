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
from matrix_process import run_scenario_process

ROOT = Path(__file__).resolve().parents[2]
MATRIX_ROOT = Path(__file__).resolve().parent
EVIDENCE_ROOT = MATRIX_ROOT / "evidence" / "features"
DEFAULT_MANIFEST = MATRIX_ROOT / ".state" / "runtime-manifest.json"
FEATURE_TARGETS = {
    "publication-pinning",
    "deterministic-dependency-packaging",
    "custom-policy-family",
    "nested-child-dag",
    "mcp-effect-evidence",
    "recovery",
    "journal-result-acceptance",
    "worker-isolation-provider",
    "isolation-artifact-selection",
    "external-client-dependency-firewall",
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
    sample_base = ROOT / "implementations" / "sdk" / "samples" / "published-functions" / "dotnet"
    core_project = sample_base / "Multiplexed.AI.Samples.PublishedFunctions" / "Multiplexed.AI.Samples.PublishedFunctions.csproj"
    packaged_project = sample_base / "Multiplexed.AI.Samples.PublishedPackagedFunctions" / "Multiplexed.AI.Samples.PublishedPackagedFunctions.csproj"
    sample_root = Path(os.environ.get("MATRIX_SAMPLE_ROOT", str(MATRIX_ROOT / ".state" / "samples"))).resolve()
    dotnet_target = sample_root / "dotnet"
    typescript_target = sample_root / "typescript"
    python_target = sample_root / "python"
    dotnet_target.mkdir(parents=True, exist_ok=True)
    typescript_target.mkdir(parents=True, exist_ok=True)
    python_target.mkdir(parents=True, exist_ok=True)
    _run([dotnet, "publish", str(core_project), "-c", "Release", "-o", str(dotnet_target)])
    _run([dotnet, "publish", str(packaged_project), "-c", "Release", "-o", str(dotnet_target)])
    shutil.copy2(ROOT / "implementations" / "sdk" / "samples" / "published-functions" / "typescript" / "functions.ts", typescript_target / "functions.ts")
    shutil.copy2(ROOT / "implementations" / "sdk" / "samples" / "published-functions" / "python" / "functions.py", python_target / "functions.py")
    dotnet_client = MATRIX_ROOT / "clients" / "dotnet" / "Multiplexed.AI.Matrix.DotNetClient" / "Multiplexed.AI.Matrix.DotNetClient.csproj"
    _run([dotnet, "build", str(dotnet_client), "-c", "Release"])
    _run([_resolve_tool("npm"), "run", "build"], cwd=ROOT / "implementations" / "node" / "sdk")
    os.environ["MATRIX_SAMPLE_ROOT"] = str(sample_root)


def _command_for(scenario: dict[str, Any], manifest: Path) -> list[str]:
    evidence = EVIDENCE_ROOT / f"{scenario['id']}.json"
    if scenario["coverageTarget"] == "external-client-dependency-firewall":
        client = scenario["clientLanguage"]
        common = [
            "--manifest", str(manifest),
            "--feature", "dependency-firewall",
            "--worker", client,
            "--scenario-id", scenario["id"],
            "--evidence", str(evidence),
        ]
        if client == "dotnet":
            project = MATRIX_ROOT / "clients" / "dotnet" / "Multiplexed.AI.Matrix.DotNetClient" / "Multiplexed.AI.Matrix.DotNetClient.csproj"
            return [os.environ.get("MATRIX_DOTNET_EXECUTABLE", "dotnet"), "run", "--project", str(project), "-c", "Release", "--no-build", "--", *common]
        if client == "typescript":
            return [os.environ.get("MATRIX_NODE_EXECUTABLE", "node"), str(MATRIX_ROOT / "clients" / "typescript" / "run.mjs"), *common]
        if client == "python":
            return [sys.executable, str(MATRIX_ROOT / "clients" / "python" / "run.py"), *common]
        raise RuntimeError(f"Unsupported dependency-firewall client language: {client}")

    command = [
        sys.executable,
        str(MATRIX_ROOT / "clients" / "python" / "feature.py"),
        "--manifest",
        str(manifest),
        "--feature",
        scenario["coverageTarget"],
        "--scenario-id",
        scenario["id"],
        "--evidence",
        str(evidence),
    ]
    worker = scenario.get("workerLanguage")
    if worker:
        command.extend(["--worker", worker])
    if scenario["coverageTarget"] == "custom-policy-family":
        command.extend(["--policy-family", scenario["coverageValues"][0]])
    if scenario["coverageTarget"] == "mcp-effect-evidence":
        command.extend(["--effect-case", scenario["coverageValues"][0]])
    if scenario["coverageTarget"] == "recovery":
        command.extend(["--recovery-case", scenario["coverageValues"][0]])
    if scenario["coverageTarget"] == "journal-result-acceptance":
        command.extend(["--journal-case", scenario["coverageValues"][0]])
    if scenario["coverageTarget"] in {"worker-isolation-provider", "isolation-artifact-selection"}:
        value = scenario["coverageValues"][0]
        profile = "container" if value in {"sandboxed-container", "OciImage"} else "process"
        command.extend(["--coverage-value", value, "--environment-profile", profile])
    return command


def _run_scenario(scenario: dict[str, Any], manifest: Path) -> None:
    if not manifest.exists():
        raise RuntimeError(
            f"Runtime manifest was not found at '{manifest}'. Start the local runtime stack first "
            "or use the Docker Compose topology."
        )
    EVIDENCE_ROOT.mkdir(parents=True, exist_ok=True)
    # A failed rerun must not leave an earlier PASSED document for this scenario.
    (EVIDENCE_ROOT / f"{scenario['id']}.json").unlink(missing_ok=True)
    diagnostic_root = Path(os.environ.get(
        "MATRIX_CLIENT_LOG_DIR", str(manifest.parent / "diagnostics" / "clients")
    ))
    run_scenario_process(
        _command_for(scenario, manifest), cwd=ROOT,
        log_path=diagnostic_root / f"{scenario['id']}.log",
    )


def _summary(plan: dict[str, Any], scenarios: list[dict[str, Any]] | None = None) -> int:
    if scenarios is None:
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


def _manifest_has_container_environments(path: Path) -> bool:
    if not path.exists():
        return False
    try:
        document = json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception:
        return False
    refs = document.get("containerEnvironmentRefs")
    return isinstance(refs, dict) and bool(refs)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Execute bound production-like feature scenarios through their declared external SDK client."
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

    manifest = args.manifest.resolve()
    if not _manifest_has_container_environments(manifest):
        scenarios = [
            scenario for scenario in scenarios
            if scenario["coverageTarget"] not in {"worker-isolation-provider", "isolation-artifact-selection"}
        ]

    if args.scenario != "all":
        scenarios = [scenario for scenario in scenarios if scenario["id"] == args.scenario]
        if not scenarios:
            raise SystemExit(f"Unknown feature scenario: {args.scenario}")

    if not args.no_build:
        _build_prerequisites()
    for scenario in scenarios:
        _run_scenario(scenario, manifest)
    return _summary(plan, scenarios)


if __name__ == "__main__":
    raise SystemExit(main())
