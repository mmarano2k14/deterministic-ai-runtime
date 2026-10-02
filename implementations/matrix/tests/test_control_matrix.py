from __future__ import annotations

import sys
from pathlib import Path

MATRIX_ROOT = Path(__file__).resolve().parents[1]
if str(MATRIX_ROOT) not in sys.path:
    sys.path.insert(0, str(MATRIX_ROOT))

import control_matrix


def test_control_matrix_has_exactly_one_scenario_per_external_sdk() -> None:
    scenarios = control_matrix.CONTROL_SCENARIOS
    assert len(scenarios) == 3
    assert {scenario["clientLanguage"] for scenario in scenarios} == {"dotnet", "typescript", "python"}
    assert all(scenario["clientLanguage"] == scenario["workerLanguage"] for scenario in scenarios)


def test_control_commands_use_real_client_entrypoints_and_control_feature(tmp_path: Path) -> None:
    manifest = tmp_path / "runtime-manifest.json"
    for scenario in control_matrix.CONTROL_SCENARIOS:
        command = control_matrix._command_for(scenario, manifest)
        assert command[command.index("--feature") + 1] == "control"
        assert command[command.index("--manifest") + 1] == str(manifest)


def test_control_evidence_requires_all_public_commands_and_terminal_convergence() -> None:
    scenario = control_matrix.CONTROL_SCENARIOS[0]
    document = {
        "status": "passed",
        "coverageTarget": "execution-control-replay-e2e",
        "clientLanguage": scenario["clientLanguage"],
        "workerLanguage": scenario["workerLanguage"],
        "pauseAccepted": True,
        "pauseGateVerified": True,
        "resumeAccepted": True,
        "pauseResumeTerminalStatus": "Completed",
        "inputWaitSeeded": True,
        "inputAccepted": True,
        "inputTerminalStatus": "Completed",
        "replaySucceeded": True,
        "replayDeterministic": True,
        "watchResyncObserved": False,
        "evidence": sorted(control_matrix.REQUIRED_EVIDENCE),
    }
    assert control_matrix._validate_evidence_document(document, scenario) is None
    document["pauseGateVerified"] = False
    assert control_matrix._validate_evidence_document(document, scenario) == "pause did not gate the next step"


def test_control_wait_setup_is_harness_only_and_uses_production_execution_control_service() -> None:
    source = (MATRIX_ROOT.parent / "dotnet/src/Multiplexed.AI.McpServer.Host/Bootstrap/ApplicationConfiguration.cs").read_text(encoding="utf-8-sig")
    assert '"/matrix/execution-control/{executionId}/wait-for-input"' in source
    assert "IAiExecutionControlService controlService" in source
    assert ".MarkWaitingForInputAsync(" in source
    assert "matrix.UserId" in source


def test_control_runners_synchronize_commands_on_public_watch_step_activity() -> None:
    dotnet = (MATRIX_ROOT / "clients/dotnet/Multiplexed.AI.Matrix.DotNetClient/Program.cs").read_text(encoding="utf-8-sig")
    typescript = (MATRIX_ROOT / "clients/typescript/run.mjs").read_text(encoding="utf-8")
    python = (MATRIX_ROOT / "clients/python/run.py").read_text(encoding="utf-8")

    assert "WaitForWatchActiveStepAsync" in dotnet
    assert "WatchItemShowsActiveStep" in dotnet
    assert "startControlWatch" in typescript
    assert "waitForWatchActiveStep" in typescript
    assert "_start_control_watch" in python
    assert "_wait_for_watch_active_step" in python
