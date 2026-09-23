from __future__ import annotations

import sys
from pathlib import Path

MATRIX_ROOT = Path(__file__).resolve().parents[1]
if str(MATRIX_ROOT) not in sys.path:
    sys.path.insert(0, str(MATRIX_ROOT))

import watch_matrix


def test_watch_matrix_has_exactly_one_scenario_per_external_sdk() -> None:
    scenarios = watch_matrix.WATCH_SCENARIOS
    assert len(scenarios) == 3
    assert {scenario["clientLanguage"] for scenario in scenarios} == {"dotnet", "typescript", "python"}
    assert {scenario["workerLanguage"] for scenario in scenarios} == {"dotnet", "typescript", "python"}
    assert len({scenario["id"] for scenario in scenarios}) == 3
    assert all(scenario["clientLanguage"] == scenario["workerLanguage"] for scenario in scenarios)


def test_watch_commands_use_real_client_entrypoints_and_watch_feature(tmp_path: Path) -> None:
    manifest = tmp_path / "runtime-manifest.json"
    for scenario in watch_matrix.WATCH_SCENARIOS:
        command = watch_matrix._command_for(scenario, manifest)
        assert "--feature" in command
        assert command[command.index("--feature") + 1] == "watch"
        assert "--manifest" in command
        assert command[command.index("--manifest") + 1] == str(manifest)
        if scenario["clientLanguage"] == "dotnet":
            assert "dotnet" in Path(command[0]).name.lower()
            assert "Multiplexed.AI.Matrix.DotNetClient.csproj" in " ".join(command)
        elif scenario["clientLanguage"] == "typescript":
            assert command[1].endswith("clients/typescript/run.mjs") or command[1].endswith("clients\\typescript\\run.mjs")
        else:
            assert command[1].endswith("clients/python/run.py") or command[1].endswith("clients\\python\\run.py")


def test_watch_evidence_requires_snapshot_incremental_event_terminal_result_and_order() -> None:
    scenario = watch_matrix.WATCH_SCENARIOS[0]
    document = {
        "status": "passed",
        "coverageTarget": "execution-watch-e2e",
        "clientLanguage": scenario["clientLanguage"],
        "workerLanguage": scenario["workerLanguage"],
        "initialSnapshotObserved": True,
        "incrementalEventObserved": True,
        "resyncObserved": False,
        "terminalStatus": "Completed",
        "publicSequences": [3, 4, 5],
        "publicEventTypes": ["public.event.one", "public.event.two"],
        "evidence": sorted(watch_matrix.REQUIRED_EVIDENCE),
    }
    assert watch_matrix._validate_evidence_document(document, scenario) is None

    document["publicSequences"] = [3, 5, 4]
    assert watch_matrix._validate_evidence_document(document, scenario) == "public sequence evidence is not strictly increasing"

    document["publicSequences"] = [3, 4, 5]
    document["incrementalEventObserved"] = False
    assert watch_matrix._validate_evidence_document(document, scenario) == "incremental event was not observed"
