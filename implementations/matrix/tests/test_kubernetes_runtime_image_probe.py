from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


PROBE_PATH = Path(__file__).resolve().parents[1] / "runtime" / "kubernetes" / "runtime_image_probe.py"
SPEC = importlib.util.spec_from_file_location("kubernetes_runtime_image_probe", PROBE_PATH)
assert SPEC is not None and SPEC.loader is not None
PROBE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROBE)

IMAGE = "multiplexed-ai-runtime:matrix-sdk"
IMAGE_ID = "sha256:" + "a" * 64
PYTHON_HASH = "b" * 64
DOCKER = r"C:\Program Files\Docker\Docker\resources\bin\docker.exe"


class RuntimeImageProbeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.metadata = [{"Id": IMAGE_ID, "Os": "linux", "Architecture": "amd64"}]
        self.resolutions = {
            "/usr/share/dotnet/dotnet": "/usr/share/dotnet/dotnet",
            "/usr/local/bin/node": "/usr/local/bin/node",
            "/usr/local/bin/python3": "/usr/local/bin/python3.12",
        }
        self.versions = {
            "/usr/share/dotnet/dotnet": "Microsoft.NETCore.App 10.0.8 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n"
            "Microsoft.NETCore.App 10.0.10 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n"
            "Microsoft.AspNetCore.App 10.0.10 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]\n",
            "/usr/local/bin/node": "v26.5.0\n",
            "/usr/local/bin/python3.12": "Python 3.12.12\n",
        }
        self.hash_output = PYTHON_HASH + "  /usr/local/bin/python3.12\n"
        self.calls: list[list[str]] = []

    def _run(self, arguments: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
        self.calls.append(arguments)
        self.assertEqual(DOCKER, arguments[0])
        self.assertIs(False, kwargs["shell"])
        self.assertIs(False, kwargs["check"])
        self.assertEqual(60, kwargs["timeout"])
        self.assertIs(True, kwargs["capture_output"])
        if arguments[1:3] == ["image", "inspect"]:
            return subprocess.CompletedProcess(arguments, 0, json.dumps(self.metadata), "")
        self.assertEqual(["run", "--rm", "--network", "none", "--entrypoint"], arguments[1:6])
        executable = arguments[6]
        self.assertEqual(IMAGE_ID, arguments[7])
        if executable == "/usr/bin/readlink":
            self.assertEqual("-e", arguments[8])
            output = self.resolutions[arguments[9]]
        elif executable == "/usr/bin/sha256sum":
            self.assertEqual(["/usr/local/bin/python3.12"], arguments[8:])
            output = self.hash_output
        else:
            self.assertEqual(
                ["--list-runtimes"] if executable.endswith("/dotnet") else ["--version"],
                arguments[8:],
            )
            output = self.versions[executable]
        return subprocess.CompletedProcess(arguments, 0, output, "")

    def test_python_alias_is_resolved_before_version_hash_and_profile_output(self) -> None:
        with patch.object(PROBE.subprocess, "run", side_effect=self._run):
            result = PROBE.probe_runtime_image(DOCKER, IMAGE)
        self.assertEqual("/usr/local/bin/python3.12", result["python"]["executablePath"])
        self.assertEqual(PYTHON_HASH, result["python"]["sha256"])
        self.assertEqual("3.12.12", result["python"]["version"])
        self.assertEqual("10.0.10", result["dotnet"]["version"])
        self.assertEqual("26.5.0", result["typescript"]["version"])
        self.assertEqual(IMAGE, result["runtimeImage"])
        self.assertEqual(IMAGE_ID, result["localImageId"])
        self.assertEqual("amd64", result["architecture"])
        self.assertEqual(8, len(self.calls))
        alias_uses = [args for args in self.calls if "/usr/local/bin/python3" in args]
        self.assertEqual(1, len(alias_uses))
        self.assertEqual("/usr/bin/readlink", alias_uses[0][6])
        self.assertNotIn("-c", [value for args in self.calls for value in args])
        self.assertNotIn("sh", [value for args in self.calls for value in args])

    def test_node_versions_are_discovered_without_a_22_24_allowlist(self) -> None:
        for version in ("22.20.0", "24.9.0", "26.5.0", "28.1.0"):
            with self.subTest(version=version):
                self.versions["/usr/local/bin/node"] = f"v{version}\n"
                with patch.object(PROBE.subprocess, "run", side_effect=self._run):
                    result = PROBE.probe_runtime_image(DOCKER, IMAGE)
                self.assertEqual(version, result["typescript"]["version"])

    def test_invalid_canonical_path_is_not_advertised(self) -> None:
        for value in ("", "/", "python3.12", "/usr/bin/../bin/python3.12", "/usr//bin/python3.12", "/one\n/two"):
            with self.subTest(path=value):
                self.resolutions["/usr/local/bin/python3"] = value
                with patch.object(PROBE.subprocess, "run", side_effect=self._run):
                    with self.assertRaises(PROBE.RuntimeImageProbeError):
                        PROBE.probe_runtime_image(DOCKER, IMAGE)

    def test_missing_or_dangling_image_path_fails_closed(self) -> None:
        original = self._run

        def missing(arguments: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
            if arguments[-1] == "/usr/local/bin/python3":
                return subprocess.CompletedProcess(arguments, 1, "", "readlink: missing target")
            return original(arguments, **kwargs)

        with patch.object(PROBE.subprocess, "run", side_effect=missing):
            with self.assertRaisesRegex(PROBE.RuntimeImageProbeError, "missing target"):
                PROBE.probe_runtime_image(DOCKER, IMAGE)

    def test_metadata_must_identify_one_linux_image(self) -> None:
        for metadata in (
            [], {}, [None], [{}, {}],
            [{"Id": "bad", "Os": "linux", "Architecture": "amd64"}],
            [{"Id": IMAGE_ID, "Os": "windows", "Architecture": "amd64"}],
            [{"Id": IMAGE_ID, "Os": "linux", "Architecture": ""}],
        ):
            with self.subTest(metadata=metadata):
                self.metadata = metadata
                with patch.object(PROBE.subprocess, "run", side_effect=self._run):
                    with self.assertRaises(PROBE.RuntimeImageProbeError):
                        PROBE.probe_runtime_image(DOCKER, IMAGE)

    def test_invalid_inspection_json_has_a_targeted_error(self) -> None:
        with patch.object(PROBE.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "invalid", "")):
            with self.assertRaisesRegex(PROBE.RuntimeImageProbeError, "valid JSON"):
                PROBE.probe_runtime_image(DOCKER, IMAGE)

    def test_incomplete_runtime_versions_and_hashes_are_rejected(self) -> None:
        for language, invalid in (
            ("/usr/share/dotnet/dotnet", "Microsoft.NETCore.App 9.0.1 [/runtimes]"),
            ("/usr/local/bin/node", "v26.5.0-preview"),
            ("/usr/local/bin/python3.12", "Python 3.12"),
        ):
            with self.subTest(runtime=language):
                previous = self.versions[language]
                self.versions[language] = invalid
                with patch.object(PROBE.subprocess, "run", side_effect=self._run):
                    with self.assertRaises(PROBE.RuntimeImageProbeError):
                        PROBE.probe_runtime_image(DOCKER, IMAGE)
                self.versions[language] = previous
        for invalid in ("", "not-a-digest  /usr/local/bin/python3.12"):
            self.hash_output = invalid
            with patch.object(PROBE.subprocess, "run", side_effect=self._run):
                with self.assertRaisesRegex(PROBE.RuntimeImageProbeError, "SHA-256"):
                    PROBE.probe_runtime_image(DOCKER, IMAGE)

    def test_native_start_and_timeout_errors_are_bounded_failures(self) -> None:
        for error in (FileNotFoundError("docker"), subprocess.TimeoutExpired("docker", 60)):
            with self.subTest(error=type(error).__name__):
                with patch.object(PROBE.subprocess, "run", side_effect=error):
                    with self.assertRaises(PROBE.RuntimeImageProbeError):
                        PROBE.probe_runtime_image(DOCKER, IMAGE)

    def test_cli_outputs_json_on_success_and_only_diagnostics_on_failure(self) -> None:
        stdout = io.StringIO()
        with patch.object(PROBE.subprocess, "run", side_effect=self._run), contextlib.redirect_stdout(stdout):
            status = PROBE.main(["--docker", DOCKER, "--image", IMAGE])
        self.assertEqual(0, status)
        self.assertEqual("/usr/local/bin/python3.12", json.loads(stdout.getvalue())["python"]["executablePath"])
        stdout, stderr = io.StringIO(), io.StringIO()
        with patch.object(PROBE.subprocess, "run", side_effect=OSError("unavailable")):
            with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                status = PROBE.main(["--docker", DOCKER, "--image", IMAGE])
        self.assertEqual(1, status)
        self.assertEqual("", stdout.getvalue())
        self.assertIn("unavailable", stderr.getvalue())

    @unittest.skipUnless(os.name == "posix" and shutil.which("readlink"), "GNU readlink requires a POSIX test host")
    def test_real_symbolic_link_is_resolved_to_an_existing_regular_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory, "python3.12")
            target.write_text("runtime fixture", encoding="utf-8")
            alias = Path(directory, "python3")
            alias.symlink_to(target.name)
            self.assertTrue(alias.is_symlink())

            def image_readlink(docker: str, image_id: str, executable: str, *arguments: str) -> str:
                self.assertEqual("/usr/bin/readlink", executable)
                return subprocess.check_output([executable, *arguments], text=True).strip()

            with patch.object(PROBE, "_image_text", side_effect=image_readlink):
                resolved = PROBE._canonical_executable(DOCKER, IMAGE_ID, str(alias))
            self.assertEqual(str(target.resolve()), resolved)
            self.assertTrue(Path(resolved).is_file())
            self.assertFalse(Path(resolved).is_symlink())


if __name__ == "__main__":
    unittest.main()
