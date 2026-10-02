from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
PROBE_PATH = ROOT / "runtime" / "kubernetes" / "runtime_artifact_probe.py"
SPEC = importlib.util.spec_from_file_location("kubernetes_runtime_artifact_probe", PROBE_PATH)
assert SPEC is not None and SPEC.loader is not None
PROBE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROBE)

IMAGE_ID = "sha256:" + "a" * 64
HASH = "b" * 64


def hashes() -> dict[str, str]:
    return {path: HASH for path in PROBE.ARTIFACT_PATHS}


def output(values: dict[str, str] | None = None) -> str:
    return "\n".join(f"{digest}  {path}" for path, digest in (values or hashes()).items())


def evidence() -> dict:
    return {"schemaVersion": 1, "scope": PROBE.SCOPE, "status": "captured",
            "localImageId": IMAGE_ID, "hashes": hashes()}


class RuntimeArtifactProbeTests(unittest.TestCase):
    def test_exact_file_coverage_is_parsed_independently_of_record_order(self) -> None:
        self.assertEqual(hashes(), PROBE.parse_hashes(output(dict(reversed(list(hashes().items()))))))

    def test_binary_marker_is_accepted(self) -> None:
        self.assertEqual(hashes(), PROBE.parse_hashes(output().replace("  /", " */")))

    def test_incomplete_duplicate_unexpected_and_invalid_records_fail_closed(self) -> None:
        for value in (
            "", output().splitlines()[0], output() + "\n" + output().splitlines()[0],
            output() + "\n" + HASH + "  /unrequested/file", output().replace(HASH, "not-a-hash", 1),
            output().replace(HASH, "c" * 63, 1),
        ):
            with self.subTest(value=value[:80]):
                with self.assertRaises(PROBE.ArtifactProbeError):
                    PROBE.parse_hashes(value)

    def test_local_probe_uses_pinned_image_with_no_network_or_shell(self) -> None:
        with patch.object(PROBE.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, output(), "")) as run:
            actual = PROBE.probe_local(r"C:\Program Files\Docker\docker.exe", IMAGE_ID)
        args, options = run.call_args
        self.assertEqual(r"C:\Program Files\Docker\docker.exe", args[0][0])
        self.assertEqual(["run", "--rm", "--network", "none", "--entrypoint", "/usr/bin/sha256sum", IMAGE_ID], args[0][1:8])
        self.assertEqual(list(PROBE.ARTIFACT_PATHS), args[0][8:])
        self.assertIs(False, options["shell"])
        self.assertEqual(30, options["timeout"])
        self.assertEqual(hashes(), actual["hashes"])
        self.assertEqual("captured", actual["status"])

    def test_mutable_tag_cannot_replace_the_probed_local_identity(self) -> None:
        for value in ("runtime:latest", "", "sha256:abcd"):
            with self.subTest(value=value), self.assertRaises(PROBE.ArtifactProbeError):
                PROBE.probe_local("docker", value)

    def test_pod_match_is_based_on_files_not_docker_or_cri_digest_strings(self) -> None:
        with patch.object(PROBE, "_command_text", return_value=output()) as command:
            actual = PROBE.compare_pod("kubectl", "ai-runtime", "current-pod", "runtime-pool", evidence())
        self.assertEqual("match", actual["status"])
        self.assertEqual([], actual["differences"])
        self.assertEqual(PROBE.SCOPE, actual["scope"])
        argv = command.call_args.args[0]
        self.assertEqual(["kubectl", "--request-timeout=10s", "exec", "current-pod", "-n", "ai-runtime", "-c", "runtime-pool", "--", "/usr/bin/sha256sum"], argv[:10])
        self.assertEqual(list(PROBE.ARTIFACT_PATHS), argv[10:])
        self.assertNotIn(IMAGE_ID, argv)

    def test_mismatch_reports_exact_changed_artifact(self) -> None:
        actual_hashes = hashes()
        path = PROBE.ARTIFACT_PATHS[2]
        actual_hashes[path] = "c" * 64
        with patch.object(PROBE, "_command_text", return_value=output(actual_hashes)):
            actual = PROBE.compare_pod("kubectl", "ai-runtime", "current-pod", "runtime-pool", evidence())
        self.assertEqual("mismatch", actual["status"])
        self.assertEqual([{"path": path, "expected": HASH, "actual": "c" * 64}], actual["differences"])

    def test_bad_reference_evidence_is_not_reported_as_a_match(self) -> None:
        for value in (
            {}, {**evidence(), "status": "unavailable"}, {**evidence(), "scope": "whole-image"},
            {**evidence(), "schemaVersion": 2}, {**evidence(), "hashes": {}},
            {**evidence(), "hashes": {**hashes(), PROBE.ARTIFACT_PATHS[0]: "bad"}},
        ):
            with self.subTest(value=value), self.assertRaises(PROBE.ArtifactProbeError):
                PROBE.compare_pod("kubectl", "ai-runtime", "current-pod", "runtime-pool", value)

    def test_kubernetes_names_cannot_be_interpreted_as_options(self) -> None:
        for fields in (("--all", "pod", "runtime-pool"), ("ai-runtime", "pod;bad", "runtime-pool"), ("ai-runtime", "pod", "")):
            with self.subTest(fields=fields), self.assertRaises(PROBE.ArtifactProbeError):
                PROBE.compare_pod("kubectl", *fields, evidence())

    def test_native_nonzero_exit_includes_error_detail(self) -> None:
        with patch.object(PROBE.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", "Forbidden: pods/exec")):
            with self.assertRaisesRegex(PROBE.ArtifactProbeError, "Forbidden"):
                PROBE._command_text(["kubectl"])

    def test_native_start_and_timeout_failures_are_bounded(self) -> None:
        for error in (OSError("missing"), subprocess.TimeoutExpired("kubectl", 30)):
            with self.subTest(error=str(error)), patch.object(PROBE.subprocess, "run", side_effect=error):
                with self.assertRaises(PROBE.ArtifactProbeError):
                    PROBE._command_text(["kubectl"])

    def test_cli_writes_failed_probe_evidence_instead_of_an_empty_match(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            target = Path(root, "failure.json")
            with patch.object(PROBE, "probe_local", side_effect=PROBE.ArtifactProbeError("missing file")), contextlib.redirect_stdout(io.StringIO()):
                status = PROBE.main(["local", "--docker", "docker", "--image-id", IMAGE_ID, "--output", str(target)])
            self.assertEqual(2, status)
            actual = json.loads(target.read_text())
            self.assertEqual("unavailable", actual["status"])
            self.assertIn("missing file", actual["error"])

    def test_cli_preserves_mismatch_as_diagnostic_not_execution_failure(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            reference, target = Path(root, "local.json"), Path(root, "pod.json")
            reference.write_text(json.dumps(evidence()), encoding="utf-8")
            different = {**hashes(), PROBE.ARTIFACT_PATHS[0]: "c" * 64}
            with patch.object(PROBE, "_command_text", return_value=output(different)), contextlib.redirect_stdout(io.StringIO()):
                status = PROBE.main(["pod", "--kubectl", "kubectl", "--namespace", "ai-runtime", "--pod", "current-pod", "--expected", str(reference), "--output", str(target)])
            self.assertEqual(0, status)
            self.assertEqual("mismatch", json.loads(target.read_text())["status"])

    def test_cli_missing_or_invalid_reference_is_retained_as_unavailable(self) -> None:
        with tempfile.TemporaryDirectory() as root:
            reference, target = Path(root, "local.json"), Path(root, "pod.json")
            for content in (None, "invalid-json", "[]"):
                if content is not None:
                    reference.write_text(content)
                with contextlib.redirect_stdout(io.StringIO()):
                    status = PROBE.main(["pod", "--kubectl", "kubectl", "--namespace", "ai-runtime", "--pod", "current-pod", "--expected", str(reference), "--output", str(target)])
                self.assertEqual(2, status)
                self.assertEqual("unavailable", json.loads(target.read_text())["status"])

    def test_real_local_sha256sum_records_use_the_same_parser(self) -> None:
        # Exercise a real hash process with temporary files; production paths remain fixed.
        import shutil
        if not shutil.which("sha256sum"):
            self.skipTest("sha256sum is not installed")
        with tempfile.TemporaryDirectory() as root:
            paths = []
            for number in range(3):
                path = Path(root, f"file {number}.bin")
                path.write_bytes(f"fixture {number}".encode())
                paths.append(str(path))
            text = subprocess.check_output(["sha256sum", *paths], text=True)
            with patch.object(PROBE, "ARTIFACT_PATHS", tuple(paths)):
                result = PROBE.parse_hashes(text)
            self.assertEqual(set(paths), set(result))
            self.assertEqual(3, len(set(result.values())))


class KubernetesDiagnosticCaptureStructureTests(unittest.TestCase):
    def setUp(self) -> None:
        self.runner = (ROOT / "runtime" / "kubernetes" / "run-kubernetes-pool-sdk-execution.ps1").read_text(encoding="utf-8-sig")

    def test_pod_logs_are_not_restricted_to_last_300_lines(self) -> None:
        self.assertNotIn('"--tail=300"', self.runner)
        self.assertIn('"--tail=-1"', self.runner)
        self.assertIn('"--previous"', self.runner)

    def test_control_plane_streams_are_drained_concurrently_to_separate_files(self) -> None:
        self.assertIn('$startInfo.RedirectStandardOutput = $true', self.runner)
        self.assertIn('$startInfo.RedirectStandardError = $true', self.runner)
        self.assertIn('StandardOutput.BaseStream.CopyToAsync($hostStdoutStream)', self.runner)
        self.assertIn('StandardError.BaseStream.CopyToAsync($hostStderrStream)', self.runner)
        self.assertIn('"control-plane.stdout.log"', self.runner)
        self.assertIn('"control-plane.stderr.log"', self.runner)
        self.assertNotIn('BeginOutputReadLine', self.runner)
        self.assertNotIn('Register-ObjectEvent', self.runner)

    def test_shutdown_flushes_logs_and_bundles_before_resource_cleanup(self) -> None:
        final = self.runner.rsplit('\nfinally {', 1)[1]
        self.assertLess(final.index('Write-KubernetesPoolDiagnostics'), final.index('Stop-ProcessTree -Process $hostProcess'))
        self.assertLess(final.index('Stop-ProcessTree -Process $hostProcess'), final.index('Task]::WaitAll'))
        self.assertLess(final.index('$stream.Dispose()'), final.index('Compress-Archive'))
        self.assertLess(final.index('Compress-Archive'), final.index('Remove-PoolResources'))
        self.assertIn('Failure bundle:', final)

    def test_artifact_probe_compares_local_pin_with_exact_current_pool_pods(self) -> None:
        self.assertIn('--image-id ([string]$runtimeIdentity.localImageId)', self.runner)
        diagnostics = self.runner.split('function Write-KubernetesPoolDiagnostics {', 1)[1].split('function Remove-PoolResources {', 1)[0]
        self.assertIn('Get-PoolResources -Namespace $Namespace -PoolId $PoolId', diagnostics)
        self.assertIn('--pod $podName --container runtime-pool --expected $localArtifactPath', diagnostics)
        self.assertNotIn('$pod.status.containerStatuses.imageID -ne', diagnostics)

    def test_diagnostics_do_not_change_scheduler_timeout_or_image_loading(self) -> None:
        self.assertIn('& $minikube image load $RuntimeImage --overwrite', self.runner)
        self.assertIn('"--terminal-timeout-seconds", "300"', self.runner)
        self.assertIn('AddSeconds(360)', self.runner)
        self.assertIn('AddSeconds(180)', self.runner)
        self.assertNotIn('docker system prune', self.runner)
        self.assertNotIn('minikube delete', self.runner)


if __name__ == "__main__":
    unittest.main()
