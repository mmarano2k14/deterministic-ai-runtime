"""Publish argument/source guards and optional real PowerShell helper checks.

The native checks use Python child processes, not MSBuild or a live runtime.
They run only when Windows PowerShell or pwsh is available on PATH.
"""
from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

MATRIX_ROOT = Path(__file__).resolve().parents[1]
RUNNER = MATRIX_ROOT / "runtime/local/run.ps1"
SUPPORT = MATRIX_ROOT / "runtime/local/process-support.ps1"
POWERSHELL = shutil.which("powershell.exe") or shutil.which("pwsh")


def ps_literal(value: str | Path) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def read_native_log(path: Path) -> str:
    raw = path.read_bytes()
    # Tee-Object in Windows PowerShell writes UTF-16; pwsh writes UTF-8.
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        return raw.decode("utf-16")
    return raw.decode("utf-8-sig", errors="replace")


class LocalPublishOutputSourceGuards(unittest.TestCase):
    def setUp(self) -> None:
        self.runner = RUNNER.read_text(encoding="utf-8-sig")
        self.support = SUPPORT.read_text(encoding="utf-8-sig")
        self.publishes = [line for line in self.runner.splitlines()
                          if 'Invoke-LocalChecked -Executable $dotnet -Arguments @("publish",' in line]

    def test_all_five_publishes_use_explicit_output_properties(self) -> None:
        self.assertEqual(5, len(self.publishes))
        for line in self.publishes:
            self.assertEqual(1, line.count('"-p:PublishDir='))
            self.assertNotIn('"-o"', line)
            self.assertNotIn('"--output"', line)
            self.assertNotRegex(line, r'"PublishDir=')

    def test_project_identity_and_declared_publish_locations_are_preserved(self) -> None:
        expected = [
            ("Multiplexed.AI.McpServer.Host.csproj", "$runtimeOut"),
            ("Multiplexed.AI.HostedInvocation.DotNetWorker.csproj", "$workerOut"),
            ("Multiplexed.AI.Samples.McpEffectServer.csproj", "$effectProbeOut"),
            ("Multiplexed.AI.Samples.PublishedFunctions.csproj", "$sampleDotNetPublish"),
            ("Multiplexed.AI.Samples.PublishedPackagedFunctions.csproj", "$sampleDotNetPublish"),
        ]
        self.assertEqual(5, len(self.publishes))
        for line, (project, output) in zip(self.publishes, expected):
            self.assertEqual(1, line.count(".csproj"))
            self.assertIn(project, line)
            self.assertIn(f'"-p:PublishDir={output}"', line)
            self.assertIn('"-c", "Release"', line)
        self.assertIn('$repo = (Resolve-Path', self.runner)
        self.assertIn('$state = Join-Path $matrix ".state"', self.runner)
        self.assertIn('$sampleDotNet = Join-Path $sampleRoot "dotnet"', self.runner)
        self.assertIn('$env:MATRIX_SAMPLE_ROOT = $sampleRoot', self.runner)

    def test_cli_output_marker_is_preserved_without_sdk_pin(self) -> None:
        for line in self.publishes:
            self.assertIn('"-p:_CommandLineDefinedOutputPath=true"', line)
        self.assertNotIn("10.0.401", self.runner)
        self.assertNotIn("global.json", self.runner)
        self.assertNotIn("msbuild.exe", self.runner.lower())

    def test_all_build_stages_have_distinct_logs_in_run_directory(self) -> None:
        expected = {
            "publish-runtime.log", "publish-worker-dotnet.log",
            "publish-mcp-effect-server.log", "publish-sample-dotnet.log",
            "publish-sample-packaged-dotnet.log", "build-client-dotnet.log",
            "build-sdk-typescript.log",
        }
        actual = re.findall(r'-LogPath \(Join-Path \$logRoot "([^"]+)"\)', self.runner)
        self.assertEqual(expected, set(actual))
        self.assertEqual(len(expected), len(actual))
        self.assertIn('Compress-Archive -Path (Join-Path $logRoot "*")', self.runner)

    def test_build_capture_saves_stderr_before_fail_fast_check(self) -> None:
        helper = self.support.split("function ConvertTo-LocalNativeArgument", 1)[0]
        self.assertIn('& $Executable @Arguments 2>&1 |', helper)
        self.assertIn('Tee-Object -FilePath $absoluteLogPath -ErrorAction Stop | Out-Host', helper)
        self.assertLess(helper.index('Tee-Object'), helper.index('if ($exitCode -ne 0)'))
        self.assertIn('LogPath=$LogPath', helper)
        self.assertIn('$ErrorActionPreference = $savedErrorActionPreference', helper)

    def test_unlogged_probes_still_return_native_stdout(self) -> None:
        helper = self.support.split("function ConvertTo-LocalNativeArgument", 1)[0]
        unlogged = helper.split('if ([string]::IsNullOrWhiteSpace($LogPath)) {', 1)[1].split('\n    else {', 1)[0]
        self.assertIn('& $Executable @Arguments', unlogged)
        self.assertNotIn('Out-Host', unlogged)
        self.assertNotIn('2>&1', unlogged)
        for executable in ("$dotnet", "$node", "$python"):
            probe = next(line for line in self.runner.splitlines()
                         if 'Version = (Invoke-LocalChecked -Executable ' + executable in line)
            self.assertNotIn('-LogPath', probe)

    def test_selected_core_execution_and_existing_readiness_budgets_remain(self) -> None:
        self.assertIn('[string]$CoreScenario = "all"', self.runner)
        self.assertIn('"--scenario", $CoreScenario', self.runner)
        self.assertIn('-TimeoutSeconds 30', self.runner)
        self.assertIn('-TimeoutSeconds 120', self.runner)
        self.assertIn('Feature matrix was not run.', self.runner)

    def test_no_new_powershell_7_only_logging_parameter_or_api(self) -> None:
        self.assertNotIn('-Encoding', self.support)
        self.assertNotIn('ArgumentList.Add', self.support)
        self.assertNotIn('$IsWindows', self.support)
        for path in (RUNNER, SUPPORT):
            raw = path.read_bytes()
            self.assertIn(b'\r\n', raw)
            self.assertNotIn(b'\n', raw.replace(b'\r\n', b''))


@unittest.skipUnless(POWERSHELL, "PowerShell is not installed; native helper checks require the target shell.")
class LocalPublishNativePowerShellTests(unittest.TestCase):
    def run_ps(self, root: Path, body: str) -> subprocess.CompletedProcess[str]:
        script = root / "check.ps1"
        script.write_text(
            '$ErrorActionPreference = "Stop"\n. ' + ps_literal(SUPPORT) + '\n' + body,
            encoding="utf-8-sig",
        )
        return subprocess.run(
            [str(POWERSHELL), '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(script)],
            capture_output=True, text=True, errors="replace", timeout=30,
        )

    def call(self, child: Path, log: Path | None = None, extra: list[str] | None = None) -> str:
        arguments = [str(child), *(extra or [])]
        text = 'Invoke-LocalChecked -Executable ' + ps_literal(sys.executable)
        text += ' -Arguments @(' + ', '.join(ps_literal(value) for value in arguments) + ')'
        if log is not None:
            text += ' -LogPath ' + ps_literal(log)
        return text

    def test_parser_accepts_both_scripts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            body = ''
            for path in (RUNNER, SUPPORT):
                body += ('$tokens = $null; $parseErrors = $null\n'
                         '[void][System.Management.Automation.Language.Parser]::ParseFile(' + ps_literal(path) +
                         ', [ref]$tokens, [ref]$parseErrors)\n'
                         'if ($parseErrors.Count -ne 0) { $parseErrors | Out-Host; exit 13 }\n')
            result = self.run_ps(Path(directory), body + 'exit 0\n')
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_logged_command_preserves_both_streams(self) -> None:
        with tempfile.TemporaryDirectory(prefix="matrix output ") as directory:
            root = Path(directory)
            child = root / 'child.py'
            child.write_text("import sys\nprint('stdout-build')\nprint('stderr-build', file=sys.stderr)\n")
            log = root / 'nested logs' / 'publish.log'
            result = self.run_ps(root, self.call(child, log) + '\nexit 0\n')
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            text = read_native_log(log)
            self.assertIn('stdout-build', text)
            self.assertIn('stderr-build', text)

    def test_logged_failure_preserves_msbuild_error_and_nonzero_exit(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            child = root / 'child.py'
            child.write_text("import sys\nprint('MSBUILD : error MSB1008: Only one project can be specified.', file=sys.stderr)\nsys.exit(23)\n")
            log = root / 'publish.log'
            body = ('try { ' + self.call(child, log) + '\nexit 0 }\n'
                    'catch { [Console]::Error.WriteLine($_.Exception.Message); exit 31 }\n')
            result = self.run_ps(root, body)
            self.assertEqual(31, result.returncode, result.stdout + result.stderr)
            self.assertIn('ExitCode=23', result.stderr)
            self.assertIn('MSB1008', read_native_log(log))

    def test_publish_property_with_spaces_is_one_native_argument(self) -> None:
        with tempfile.TemporaryDirectory(prefix="matrix output ") as directory:
            root = Path(directory)
            child = root / 'argument child.py'
            child.write_text("import json,sys\nprint(json.dumps(sys.argv[1:]))\n")
            property_arg = '-p:PublishDir=' + str(root / 'publish output')
            extra = [property_arg, '-p:_CommandLineDefinedOutputPath=true']
            log = root / 'arguments.log'
            result = self.run_ps(root, self.call(child, log, extra) + '\nexit 0\n')
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual(extra, json.loads(read_native_log(log).strip()))

    def test_unlogged_probe_returns_stdout_without_console_capture(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            child = root / 'version.py'
            child.write_text("print('probe-version')\n")
            body = ('$value = (' + self.call(child) + ').Trim()\n'
                    'if ($value -ne "probe-version") { exit 15 }\nexit 0\n')
            result = self.run_ps(root, body)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_logging_restores_caller_error_preference_and_replaces_old_log(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            child = root / 'child.py'
            child.write_text("print('new-build')\n")
            log = root / 'publish.log'
            log.write_text('old-build\n')
            body = (self.call(child, log) + '\n'
                    'if ($ErrorActionPreference -ne "Stop") { exit 16 }\nexit 0\n')
            result = self.run_ps(root, body)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertNotIn('old-build', read_native_log(log))
            self.assertIn('new-build', read_native_log(log))

    def test_large_concurrent_output_does_not_deadlock(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            child = root / 'child.py'
            child.write_text("import sys, threading\nt = threading.Thread(target=lambda: sys.stderr.write('E'*65536+'\\n'))\nt.start()\nsys.stdout.write('O'*65536+'\\n')\nt.join()\n")
            log = root / 'large.log'
            result = self.run_ps(root, self.call(child, log) + '\nexit 0\n')
            self.assertEqual(0, result.returncode, result.stdout[-2000:] + result.stderr[-2000:])
            text = read_native_log(log)
            self.assertIn('O' * 1000, text)
            self.assertIn('E' * 1000, text)


if __name__ == '__main__':
    unittest.main()
