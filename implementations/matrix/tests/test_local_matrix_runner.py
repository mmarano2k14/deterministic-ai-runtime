from __future__ import annotations

import contextlib
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

MATRIX_ROOT = Path(__file__).resolve().parents[1]
if str(MATRIX_ROOT) not in sys.path:
    sys.path.insert(0, str(MATRIX_ROOT))

import core_matrix
import feature_matrix
from matrix_plan import load_plan
from matrix_process import run_scenario_process


class NativeClientCaptureTests(unittest.TestCase):
    def run_child(self, script: str, root: Path) -> Path:
        log = root / "nested" / "client.log"
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            run_scenario_process([sys.executable, "-c", script], cwd=root, log_path=log)
        return log

    def test_success_retains_stdout_and_stderr_from_a_real_subprocess(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            log = self.run_child("import sys; print('publish'); print('stderr-detail', file=sys.stderr)", Path(directory))
            text = log.read_text(encoding="utf-8")
            self.assertIn("publish", text)
            self.assertIn("stderr-detail", text)
            self.assertIn("EXIT code=0", text)

    def test_failure_preserves_exception_and_exit_code(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(subprocess.CalledProcessError) as caught:
                self.run_child("import sys; print('initial exception', file=sys.stderr); sys.exit(23)", root)
            self.assertEqual(23, caught.exception.returncode)
            text = (root / "nested" / "client.log").read_text()
            self.assertIn("initial exception", text)
            self.assertIn("EXIT code=23 hex=0x00000017", text)

    def test_large_simultaneous_output_does_not_deadlock(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            script = ("import sys, threading; "
                "t=threading.Thread(target=lambda: sys.stderr.write('E'*262144+'\\n')); "
                "t.start(); sys.stdout.write('O'*262144+'\\n'); t.join()")
            log = self.run_child(script, Path(directory))
            text = log.read_text()
            self.assertIn("E" * 1000, text)
            self.assertIn("O" * 1000, text)
            self.assertIn("EXIT code=0", text)

    def test_native_start_failure_is_saved_without_synthesizing_success(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            log = root / "client.log"
            with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(OSError):
                run_scenario_process([str(root / "does-not-exist")], cwd=root, log_path=log)
            self.assertIn("START FAILED", log.read_text())
            self.assertNotIn("EXIT code=0", log.read_text())

    def test_existing_log_is_replaced_not_mixed_with_an_earlier_attempt(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.run_child("print('old-attempt')", root)
            log = self.run_child("print('new-attempt')", root)
            self.assertNotIn("old-attempt", log.read_text())
            self.assertIn("new-attempt", log.read_text())

    def test_invalid_utf8_does_not_hide_the_native_failure(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(subprocess.CalledProcessError) as caught:
                self.run_child("import os,sys; os.write(2,b'bad-encoding: \\xff\\n'); sys.exit(5)", root)
            self.assertEqual(5, caught.exception.returncode)
            self.assertIn("bad-encoding:", (root / "nested" / "client.log").read_text())


class LocalScenarioIsolationTests(unittest.TestCase):
    def test_single_core_summary_does_not_require_unselected_evidence(self) -> None:
        plan = load_plan()
        scenario = plan["coreScenarios"][0]
        with tempfile.TemporaryDirectory() as directory:
            evidence = Path(directory)
            (evidence / (scenario["id"] + ".json")).write_text('{"status":"passed"}')
            with mock.patch.object(core_matrix, "EVIDENCE_ROOT", evidence), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, core_matrix._summary(plan, [scenario]))
                self.assertEqual(1, core_matrix._summary(plan))

    def test_core_failure_removes_only_its_stale_passed_evidence(self) -> None:
        scenario = load_plan()["coreScenarios"][0]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "runtime-manifest.json"
            manifest.write_text("{}")
            evidence = root / (scenario["id"] + ".json")
            evidence.write_text('{"status":"passed"}')
            other = root / "other-scenario.json"
            other.write_text('{"status":"passed"}')
            with mock.patch.object(core_matrix, "EVIDENCE_ROOT", root), mock.patch.object(
                core_matrix, "run_scenario_process", side_effect=subprocess.CalledProcessError(1, ["dotnet"])
            ), self.assertRaises(subprocess.CalledProcessError):
                core_matrix._run_scenario(scenario, manifest, no_build=True)
            self.assertFalse(evidence.exists())
            self.assertTrue(other.exists())

    def test_feature_failure_removes_its_stale_passed_evidence(self) -> None:
        scenario = next(item for item in load_plan()["featureScenarios"]
                        if item["coverageTarget"] == "publication-pinning")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "runtime-manifest.json"
            manifest.write_text("{}")
            evidence = root / (scenario["id"] + ".json")
            evidence.write_text('{"status":"passed"}')
            with mock.patch.object(feature_matrix, "EVIDENCE_ROOT", root), mock.patch.object(
                feature_matrix, "run_scenario_process", side_effect=subprocess.CalledProcessError(1, ["python"])
            ), self.assertRaises(subprocess.CalledProcessError):
                feature_matrix._run_scenario(scenario, manifest)
            self.assertFalse(evidence.exists())

    def test_selected_core_cli_passes_its_selection_to_summary(self) -> None:
        scenario = load_plan()["coreScenarios"][0]
        with mock.patch.object(sys, "argv", ["core_matrix.py", "run", "--no-build", "--scenario", scenario["id"]]), mock.patch.object(
            core_matrix, "_run_scenario"
        ) as run, mock.patch.object(core_matrix, "_summary", return_value=0) as summary:
            self.assertEqual(0, core_matrix.main())
            self.assertEqual(1, run.call_count)
            self.assertEqual([scenario], summary.call_args.args[1])

    def test_client_command_uses_selected_native_executables(self) -> None:
        with mock.patch.dict(os.environ, {"MATRIX_DOTNET_EXECUTABLE": "/exact/dotnet", "MATRIX_NODE_EXECUTABLE": "/exact/node"}):
            for scenario in load_plan()["coreScenarios"]:
                command = core_matrix._command_for(scenario, Path("manifest.json"))
                if scenario["clientLanguage"] == "dotnet":
                    self.assertEqual("/exact/dotnet", command[0])
                elif scenario["clientLanguage"] == "typescript":
                    self.assertEqual("/exact/node", command[0])


class LocalRunnerSourceGuards(unittest.TestCase):
    def setUp(self) -> None:
        self.runner = (MATRIX_ROOT / "runtime/local/run.ps1").read_text()
        self.support = (MATRIX_ROOT / "runtime/local/process-support.ps1").read_text()
        self.client = (MATRIX_ROOT / "clients/dotnet/Multiplexed.AI.Matrix.DotNetClient/Program.cs").read_text()

    def test_every_local_build_and_publish_uses_checked_invocation(self) -> None:
        self.assertEqual(5, self.runner.count('Invoke-LocalChecked -Executable $dotnet -Arguments @("publish",'))
        self.assertEqual(1, self.runner.count('Invoke-LocalChecked -Executable $dotnet -Arguments @("build",'))
        self.assertNotIn('& $dotnet publish', self.runner)
        self.assertIn('$exitCode = $LASTEXITCODE', self.support)
        self.assertIn('if ($exitCode -ne 0)', self.support)

    def test_powershell_51_uses_native_npm_and_compatible_process_apis(self) -> None:
        self.assertIn('$env:OS -eq "Windows_NT"', self.runner)
        self.assertNotIn('$IsWindows', self.runner)
        self.assertIn('-CommandType Application', self.runner)
        self.assertNotIn('ArgumentList.Add', self.support)
        self.assertNotIn('Environment[', self.support)
        self.assertIn('ConvertTo-LocalNativeArgument', self.support)

    def test_both_host_pipes_are_drained_before_readiness(self) -> None:
        self.assertIn('StandardOutput.BaseStream.CopyToAsync', self.support)
        self.assertIn('StandardError.BaseStream.CopyToAsync', self.support)
        self.assertLess(self.runner.index('$hostCapture = Start-LocalLoggedProcess'),
                        self.runner.index('Wait-LocalHttpReady -Url "http://127.0.0.1:8081/health"'))

    def test_readiness_times_out_instead_of_falling_through(self) -> None:
        self.assertIn('throw "Matrix service did not become ready', self.support)
        self.assertIn('-TimeoutSeconds 30 -Process $probeCapture.Process', self.runner)
        self.assertIn('-TimeoutSeconds 120', self.runner)

    def test_local_roles_are_explicit_without_changing_kubernetes(self) -> None:
        for setting in ("EnableLocalWorkerProfiles", "EnableWorkerPolling", "EnableDagReconciliation"):
            self.assertIn(f'$env:AiHostedInvocation__{setting} = "true"', self.runner)
        self.assertIn('--Multiplexed.Rbac.Core:Project=matrix', self.runner)

    def test_watch_and_control_only_use_one_shared_mongo_decision_ledger_across_processes(self) -> None:
        self.assertIn('if ($WatchOnly -or $ControlOnly)', self.runner)
        self.assertIn('$env:AiDecisionLedger__Provider = "mongo"', self.runner)
        self.assertIn('$env:AiDecisionLedger__Provider = "inmemory"', self.runner)
        self.assertIn('$ledgerReason = if ($WatchOnly) { "Watch" } else { "Control/Replay" }', self.runner)
        self.assertIn('Decision Ledger provider=mongo', self.runner)
        self.assertIn(r'.\implementations\matrix\control_matrix.py', self.runner)
        self.assertLess(
            self.runner.index('$env:AiDecisionLedger__Provider = "mongo"'),
            self.runner.index('$hostCapture = Start-LocalLoggedProcess'),
        )

    def test_failure_archive_is_built_after_process_capture_completes(self) -> None:
        self.assertLess(self.runner.index('foreach ($capture in $captures) { Complete-LocalCapture'),
                        self.runner.index('Compress-Archive'))
        self.assertIn('[local-sdk-matrix] Failure bundle:', self.runner)
        self.assertNotIn('Copy-Item $manifest', self.runner)
        self.assertNotIn('-Name dotnet', self.support)
        self.assertIn('/PID $Process.Id /T /F', self.support)

    def test_selected_core_cannot_be_reported_as_full_matrix(self) -> None:
        self.assertIn('$CoreScenario -ne "all" -and -not $CoreOnly', self.runner)
        self.assertIn('Feature matrix was not run.', self.runner)
        self.assertIn('if ($CoreOnly)', self.runner)

    def test_dotnet_client_reports_stages_and_preserves_sdk_error_detail(self) -> None:
        for stage in ("SOURCE", "PUBLISH", "SUBMIT", "OBSERVE", "RESULT", "EVIDENCE"):
            self.assertIn(f'stage = "{stage}"', self.client)
        self.assertIn('JsonSerializer.Serialize(sdkException.Error)', self.client)
        self.assertIn('JsonSerializer.Serialize(result.Failure)', self.client)
        self.assertIn('return 1;', self.client)
        self.assertIn('TimeSpan.FromSeconds(90)', self.client)

    def test_remote_error_normalizer_uses_existing_details_without_new_dependencies(self) -> None:
        sdk = MATRIX_ROOT.parent / "dotnet/src/Multiplexed.AI.Sdk"
        transport = (sdk / "Transport/AiSdkMcpHttpTransport.cs").read_text()
        self.assertIn('return CreateRemoteToolFailure(request.Operation, result);', transport)
        self.assertIn('details["remoteContent"]', transport)
        self.assertIn('details["remoteStructuredContent"] = structured.Clone();', transport)
        self.assertIn('IsRetryable = false', transport)
        self.assertEqual(1, transport.count('client.CallToolAsync('))
        self.assertNotIn('Multiplexed.AI.Runtime', transport)


if __name__ == "__main__":
    unittest.main()
