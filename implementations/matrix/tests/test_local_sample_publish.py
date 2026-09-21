"""Local .NET sample output-name compatibility and unchanged client layout.

Source guards run everywhere. PowerShell-only checks use an explicit fake publisher.
The separate Windows PowerShell + .NET 10+ test really publishes the repository's
three sample projects in an isolated temporary copy, without runtime infrastructure.
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
MATRIX = ROOT / "implementations/matrix"
RUNNER = MATRIX / "runtime/local/run.ps1"
SUPPORT = MATRIX / "runtime/local/process-support.ps1"
POWERSHELL = shutil.which("powershell.exe") or shutil.which("pwsh")
DOTNET = shutil.which("dotnet")
ASSEMBLIES = (
    "Multiplexed.AI.Samples.PublishedFunctions.dll",
    "Multiplexed.AI.Samples.PublishedPackagedFunctions.dll",
    "Multiplexed.AI.Samples.PublishedDependency.dll",
)


def literal(value: str | Path) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def sample_block() -> str:
    source = RUNNER.read_text(encoding="utf-8-sig")
    return source.split("# BEGIN LOCAL DOTNET SAMPLE PUBLISH\n", 1)[1].split(
        "# END LOCAL DOTNET SAMPLE PUBLISH", 1
    )[0]


def setup_script(root: Path) -> str:
    source = RUNNER.read_text(encoding="utf-8-sig")
    stage_assignment = next(line for line in source.splitlines()
                            if line.startswith("$sampleDotNetPublish = "))
    return (
        '$ErrorActionPreference = "Stop"\n'
        + ". " + literal(SUPPORT) + "\n"
        + "$repo = " + literal(root) + "\n"
        + '$state = Join-Path $repo ".state"\n'
        + '$sampleRoot = Join-Path $state "samples"\n'
        + '$sampleDotNet = Join-Path $sampleRoot "dotnet"\n'
        + stage_assignment + "\n"
        + '$logRoot = Join-Path $state "logs"\n'
        + 'New-Item -ItemType Directory -Force -Path $state,$sampleDotNet,$sampleDotNetPublish,$logRoot | Out-Null\n'
    )


def execute_ps(root: Path, body: str, timeout: int = 45) -> subprocess.CompletedProcess[str]:
    script = root / "sample-check.ps1"
    script.write_text(setup_script(root) + body, encoding="utf-8-sig")
    return subprocess.run(
        [str(POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)],
        cwd=root, capture_output=True, text=True, errors="replace", timeout=timeout,
    )


class LocalSamplePublishSourceGuards(unittest.TestCase):
    def setUp(self) -> None:
        self.source = RUNNER.read_text(encoding="utf-8-sig")
        self.block = sample_block()

    def test_publish_destination_has_fresh_non_reserved_leaf(self) -> None:
        self.assertIn('"sample-dotnet-publish-{0}" -f [Guid]::NewGuid().ToString("N")', self.source)
        self.assertEqual(2, self.block.count('"-p:PublishDir=$sampleDotNetPublish"'))
        self.assertNotIn('"-p:PublishDir=$sampleDotNet"', self.source)
        self.assertIn('$sampleDotNet,$sampleDotNetPublish,$sampleTypeScript', self.source)

    def test_client_visible_directory_contract_is_not_renamed(self) -> None:
        self.assertIn('$sampleDotNet = Join-Path $sampleRoot "dotnet"', self.source)
        self.assertIn('$env:MATRIX_SAMPLE_ROOT = $sampleRoot', self.source)
        self.assertIn('Copy-Item -Destination $sampleDotNet -Recurse -Force -ErrorAction Stop', self.block)
        self.assertNotIn('$env:MATRIX_SAMPLE_ROOT = $sampleDotNetPublish', self.source)

    def test_all_new_assemblies_are_validated_before_any_copy(self) -> None:
        copy_position = self.block.index('Copy-Item -Destination')
        for assembly in ASSEMBLIES:
            self.assertLess(self.block.index('"' + assembly + '"'), copy_position)
        self.assertIn('Join-Path $sampleDotNetPublish $sampleAssembly', self.block)
        self.assertIn('Test-Path -LiteralPath $publishedSample -PathType Leaf', self.block)
        self.assertIn('(Get-Item -LiteralPath $publishedSample).Length -eq 0', self.block)
        self.assertLess(self.block.index('throw "Required newly published'), copy_position)

    def test_both_publishes_finish_before_materialization_and_host_start(self) -> None:
        self.assertEqual(2, self.block.count('Invoke-LocalChecked -Executable $dotnet'))
        self.assertLess(self.block.index('publish-sample-packaged-dotnet.log'),
                        self.block.index('foreach ($sampleAssembly'))
        self.assertLess(self.source.index('# END LOCAL DOTNET SAMPLE PUBLISH'),
                        self.source.index('$hostCapture = Start-LocalLoggedProcess'))
        self.assertNotIn('catch', self.block)

    def test_cleanup_is_limited_to_this_invocations_scratch_directory(self) -> None:
        cleanup = self.source.rsplit('finally {', 1)[1]
        self.assertIn('Remove-Item -LiteralPath $sampleDotNetPublish -Recurse -Force', cleanup)
        self.assertNotRegex(cleanup, r'Remove-Item\s+-LiteralPath\s+\$(?:sampleDotNet|sampleRoot|state)\s')
        self.assertIn('Write-Warning "Could not remove sample publish scratch', cleanup)

    def test_publish_stage_keeps_projects_release_logs_and_no_toolchain_pin(self) -> None:
        for name in ('PublishedFunctions', 'PublishedPackagedFunctions'):
            self.assertIn(f'Multiplexed.AI.Samples.{name}.csproj', self.block)
        self.assertEqual(2, self.block.count('"-c", "Release"'))
        self.assertEqual(2, self.block.count('"-p:_CommandLineDefinedOutputPath=true"'))
        self.assertIn('publish-sample-dotnet.log', self.block)
        self.assertIn('publish-sample-packaged-dotnet.log', self.block)
        self.assertNotIn('10.0.401', self.source)
        self.assertNotIn('global.json', self.source)


# Deliberately fake builds: these exercise the runner's actual staging block, not MSBuild.
FAKE_PUBLISHER = r'''
$dotnet = "not-a-real-dotnet-build"
$script:PublishCallCount = 0
function Invoke-LocalChecked {
    param([string]$Executable, [string[]]$Arguments, [string]$LogPath)
    $script:PublishCallCount++
    $outputArgument = @($Arguments | Where-Object { $_.StartsWith("-p:PublishDir=") })
    if ($outputArgument.Count -ne 1) { throw "Exactly one explicit output property is required." }
    $destination = $outputArgument[0].Substring("-p:PublishDir=".Length)
    if ([System.IO.Path]::GetFileName($destination) -eq "dotnet") { throw "Reserved publish output leaf." }
    if ($script:PublishCallCount -eq 1) {
        [System.IO.File]::WriteAllText((Join-Path $destination "Multiplexed.AI.Samples.PublishedFunctions.dll"), "fresh-functions")
    }
    else {
        if ($script:FailSecondPublish) { throw "Simulated second publish failure." }
        [System.IO.File]::WriteAllText((Join-Path $destination "Multiplexed.AI.Samples.PublishedPackagedFunctions.dll"), "fresh-packaged")
        if (-not $script:OmitDependency) {
            [System.IO.File]::WriteAllText((Join-Path $destination "Multiplexed.AI.Samples.PublishedDependency.dll"), "fresh-dependency")
        }
    }
}
'''


@unittest.skipUnless(POWERSHELL, "PowerShell is absent; sample-staging shell checks skipped.")
class LocalSampleStagingPowerShellTests(unittest.TestCase):
    def test_success_copies_fresh_outputs_to_dotnet_directory(self) -> None:
        with tempfile.TemporaryDirectory(prefix="matrix sample path ") as directory:
            root = Path(directory)
            result = execute_ps(root, FAKE_PUBLISHER + sample_block() + '\nif ($script:PublishCallCount -ne 2) { exit 19 }\n')
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            target = root / '.state/samples/dotnet'
            for assembly in ASSEMBLIES:
                self.assertTrue((target / assembly).read_text().startswith('fresh-'))
            self.assertIn('.NET sample artifacts staged:', result.stdout)

    def test_old_dependency_cannot_replace_missing_new_output(self) -> None:
        with tempfile.TemporaryDirectory(prefix="matrix missing output ") as directory:
            root = Path(directory)
            canonical = root / '.state/samples/dotnet'
            canonical.mkdir(parents=True)
            for assembly in ASSEMBLIES:
                (canonical / assembly).write_text('old-file')
            body = (FAKE_PUBLISHER + '$script:OmitDependency = $true\n'
                    + 'try {\n' + sample_block()
                    + '\nexit 0\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 23 }\n')
            result = execute_ps(root, body)
            self.assertEqual(23, result.returncode, result.stdout + result.stderr)
            self.assertIn('Required newly published sample assembly is missing or empty', result.stderr)
            for assembly in ASSEMBLIES:
                self.assertEqual('old-file', (canonical / assembly).read_text())

    def test_failed_second_publish_does_not_copy_partial_outputs(self) -> None:
        with tempfile.TemporaryDirectory(prefix="matrix publish failure ") as directory:
            root = Path(directory)
            body = (FAKE_PUBLISHER + '$script:FailSecondPublish = $true\n'
                    + 'try {\n' + sample_block()
                    + '\nexit 0\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 29 }\n')
            result = execute_ps(root, body)
            self.assertEqual(29, result.returncode, result.stdout + result.stderr)
            self.assertIn('Simulated second publish failure', result.stderr)
            self.assertEqual([], list((root / '.state/samples/dotnet').iterdir()))
            self.assertNotIn('.NET sample artifacts staged:', result.stdout)


@unittest.skipUnless(os.name == "nt" and POWERSHELL and DOTNET,
                     "Real sample publish requires Windows PowerShell/pwsh and .NET 10+.")
class LocalSampleRealPublishTests(unittest.TestCase):
    def test_real_samples_publish_and_stage_without_runtime_infrastructure(self) -> None:
        version = subprocess.run([str(DOTNET), '--version'], cwd=ROOT,
                                 capture_output=True, text=True, timeout=30)
        self.assertEqual(0, version.returncode, version.stdout + version.stderr)
        major = re.match(r'\s*(\d+)\.', version.stdout)
        if major is None or int(major.group(1)) < 10:
            self.skipTest('The current repository-selected SDK does not provide .NET 10 support.')
        with tempfile.TemporaryDirectory(prefix='matrix real publish ') as directory:
            root = Path(directory)
            projects = Path('implementations/sdk/samples/published-functions/dotnet')
            shutil.copytree(ROOT / projects, root / projects,
                            ignore=shutil.ignore_patterns('bin', 'obj'))
            body = ('$dotnet = ' + literal(DOTNET) + '\nPush-Location $repo\n'
                    + 'try {\n' + sample_block() + '\n} finally { Pop-Location }\n')
            result = execute_ps(root, body, timeout=180)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            for assembly in ASSEMBLIES:
                data = (root / '.state/samples/dotnet' / assembly).read_bytes()
                self.assertTrue(data.startswith(b'MZ'), assembly)
            for log in ('publish-sample-dotnet.log', 'publish-sample-packaged-dotnet.log'):
                self.assertGreater((root / '.state/logs' / log).stat().st_size, 0)


if __name__ == '__main__':
    unittest.main()
