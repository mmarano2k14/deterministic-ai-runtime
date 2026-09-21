"""Native stderr presentation regression tests for the local matrix runner.

Source guards run everywhere. Native checks require PowerShell; on Windows,
notice/error tests use a .cmd shim to cover the same wrapper type as npm.cmd.
These tests do not execute the SDK, tsc, a runtime, or an infrastructure matrix.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

MATRIX = Path(__file__).resolve().parents[1]
SUPPORT = MATRIX / "runtime/local/process-support.ps1"
RUNNER = MATRIX / "runtime/local/run.ps1"
SDK_PACKAGE = MATRIX.parent / "node/sdk/package.json"
POWERSHELL = shutil.which("powershell.exe") or shutil.which("pwsh")


def literal(value: str | Path) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def read_log(path: Path) -> str:
    raw = path.read_bytes()
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        return raw.decode("utf-16")
    return raw.decode("utf-8-sig", errors="replace")


class LocalNativeOutputSourceTests(unittest.TestCase):
    def test_normalization_precedes_both_formatting_sinks(self) -> None:
        helper = SUPPORT.read_text(encoding="utf-8-sig").split(
            "function ConvertTo-LocalNativeArgument", 1
        )[0]
        native = helper.index("& $Executable @Arguments 2>&1 |")
        convert = helper.index("ForEach-Object -ErrorAction Stop { $_.ToString() }")
        tee = helper.index("Tee-Object -FilePath $absoluteLogPath -ErrorAction Stop")
        self.assertLess(native, convert)
        self.assertLess(convert, tee)
        self.assertLess(tee, helper.index("Out-Host"))
        self.assertLess(tee, helper.index("if ($exitCode -ne 0)"))

    def test_no_native_output_or_failure_is_discarded(self) -> None:
        helper = SUPPORT.read_text(encoding="utf-8-sig").split(
            "function ConvertTo-LocalNativeArgument", 1
        )[0]
        self.assertNotIn("2>$null", helper)
        self.assertNotIn("Out-Null", helper)
        self.assertNotIn("SilentlyContinue", helper)
        self.assertNotIn("Where-Object", helper)
        self.assertIn('$ErrorActionPreference = "Continue"', helper)
        self.assertIn("$ErrorActionPreference = $savedErrorActionPreference", helper)
        self.assertIn("$exitCode = $LASTEXITCODE", helper)
        self.assertIn("throw \"Native command failed.", helper)

    def test_sdk_remains_a_local_build_with_no_npm_suppression(self) -> None:
        runner = RUNNER.read_text(encoding="utf-8-sig")
        self.assertIn('Push-Location ".\\implementations\\node\\sdk"', runner)
        self.assertIn('Invoke-LocalChecked -Executable $npm -Arguments @("run", "build")', runner)
        self.assertNotIn('"--silent"', runner)
        self.assertNotIn('"--loglevel', runner)
        package = json.loads(SDK_PACKAGE.read_text(encoding="utf-8-sig"))
        self.assertEqual("@multiplexed/ai-sdk", package["name"])
        self.assertEqual("tsc -p tsconfig.json", package["scripts"]["build"])


@unittest.skipUnless(POWERSHELL, "PowerShell is unavailable; native output checks require the target shell.")
class LocalNativeOutputShellTests(unittest.TestCase):
    def run_ps(self, root: Path, body: str) -> subprocess.CompletedProcess[str]:
        script = root / "check.ps1"
        script.write_text(
            '$ErrorActionPreference = "Stop"\n$ErrorView = "NormalView"\n. '
            + literal(SUPPORT) + "\n" + body,
            encoding="utf-8-sig",
        )
        return subprocess.run(
            [str(POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)],
            capture_output=True, text=True, errors="replace", timeout=30,
        )

    def child_call(self, root: Path, code: str, log: Path) -> str:
        child = root / "notice child.py"
        child.write_text(code, encoding="utf-8")
        executable = Path(sys.executable)
        arguments = [str(child)]
        if os.name == "nt":
            # Reproduce the Windows npm.cmd entry-point boundary without npm or downloads.
            shim = root / "npm-like.cmd"
            shim.write_bytes(
                ('@echo off\r\n"' + str(executable) + '" "' + str(child)
                 + '" %*\r\nexit /b %errorlevel%\r\n').encode("utf-8")
            )
            executable = shim
            arguments = ["build"]
        return (
            "Invoke-LocalChecked -Executable " + literal(executable)
            + " -Arguments @(" + ", ".join(literal(a) for a in arguments) + ")"
            + " -LogPath " + literal(log)
        )

    def assert_plain(self, text: str) -> None:
        for header in ("NativeCommandError", "CategoryInfo", "FullyQualifiedErrorId", "RemoteException"):
            self.assertNotIn(header, text)

    def test_successful_notices_stay_plain_in_console_and_log(self) -> None:
        with tempfile.TemporaryDirectory(prefix="matrix notices ") as directory:
            root = Path(directory)
            log = root / "build.log"
            notices = [
                "npm notice run @multiplexed/ai-sdk@0.0.0-dev build",
                "npm notice run tsc -p tsconfig.json",
            ]
            code = "import sys\nprint('stdout-build')\n" + "".join(
                "print(" + repr(line) + ", file=sys.stderr)\n" for line in notices
            )
            body = self.child_call(root, code, log) + '\nif ($ErrorActionPreference -ne "Stop") { exit 12 }\nexit 0\n'
            result = self.run_ps(root, body)
            console = result.stdout + result.stderr
            self.assertEqual(0, result.returncode, console)
            for text in (console, read_log(log)):
                self.assert_plain(text)
                for line in ["stdout-build", *notices]:
                    self.assertIn(line, text)

    def test_real_error_text_and_nonzero_exit_remain_blocking(self) -> None:
        with tempfile.TemporaryDirectory(prefix="matrix failures ") as directory:
            root = Path(directory)
            log = root / "failure.log"
            diagnostic = "src/index.ts(1,1): error TS2322: Type 'string' is not assignable to type 'number'."
            code = "import sys\nprint(" + repr(diagnostic) + ", file=sys.stderr)\nsys.exit(23)\n"
            body = (
                "try { " + self.child_call(root, code, log) + "\nexit 0 }\n"
                'catch { if ($ErrorActionPreference -ne "Stop") { exit 12 }; '
                '[Console]::Error.WriteLine($_.Exception.Message); exit 31 }\n'
            )
            result = self.run_ps(root, body)
            console = result.stdout + result.stderr
            self.assertEqual(31, result.returncode, console)
            self.assertIn("ExitCode=23", console)
            for text in (console, read_log(log)):
                self.assert_plain(text)
                self.assertIn(diagnostic, text)

    def test_error_record_object_is_normalized_even_on_newer_shells(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            log = root / "record.log"
            # Model the legacy native ErrorRecord explicitly on PowerShell versions
            # that no longer wrap native stderr. This does not emulate Windows 5.1.
            body = r'''
function Invoke-NoticeFixture {
    $exception = New-Object System.Exception -ArgumentList "npm notice fixture"
    $record = New-Object System.Management.Automation.ErrorRecord -ArgumentList @(
        $exception, "NativeCommandError",
        [System.Management.Automation.ErrorCategory]::NotSpecified, $null
    )
    Write-Output -InputObject $record
    $global:LASTEXITCODE = 0
}
'''
            body += ('Invoke-LocalChecked -Executable "Invoke-NoticeFixture" -Arguments @("build") -LogPath '
                     + literal(log) + "\nexit 0\n")
            result = self.run_ps(root, body)
            console = result.stdout + result.stderr
            self.assertEqual(0, result.returncode, console)
            for text in (console, read_log(log)):
                self.assert_plain(text)
                self.assertIn("npm notice fixture", text)

    def test_log_write_failure_is_not_hidden(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            log = root / "not-a-log-file"
            log.mkdir()
            body = ("try { " + self.child_call(root, "print('build-output')\n", log)
                    + '\nexit 0 }\ncatch { if ($ErrorActionPreference -ne "Stop") { exit 12 }; exit 31 }\n')
            result = self.run_ps(root, body)
            self.assertEqual(31, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
