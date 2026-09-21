# Native-process support for the local matrix. Compatible with Windows PowerShell 5.1.
# Never uses shell background event handlers or process-name-wide termination.

function Invoke-LocalChecked {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$Arguments,
        [string]$LogPath
    )
    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        # Probes rely on receiving stdout as their return value.
        & $Executable @Arguments
        $exitCode = $LASTEXITCODE
    }
    else {
        $absoluteLogPath = [System.IO.Path]::GetFullPath($LogPath)
        [void][System.IO.Directory]::CreateDirectory(
            [System.IO.Path]::GetDirectoryName($absoluteLogPath))
        Write-Host "[local-sdk-matrix] Native command log: $absoluteLogPath"
        $savedErrorActionPreference = $ErrorActionPreference
        try {
            # Windows PowerShell 5.1 can expose redirected native stderr as ErrorRecord.
            # Drain and save it before checking the native exit status. This local scope
            # does not weaken the runner's fail-fast behavior or alter caller preferences.
            $ErrorActionPreference = "Continue"
            $PSNativeCommandUseErrorActionPreference = $false
            # Normalize redirected native ErrorRecord values before the formatters.
            # Keep every message, including real compiler errors; only the native exit
            # status determines success. No npm-specific filtering or suppression.
            & $Executable @Arguments 2>&1 |
                ForEach-Object -ErrorAction Stop { $_.ToString() } |
                Tee-Object -FilePath $absoluteLogPath -ErrorAction Stop | Out-Host
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $savedErrorActionPreference
        }
    }
    if ($exitCode -ne 0) {
        throw "Native command failed. Executable=$Executable; ExitCode=$exitCode; Arguments=$($Arguments -join ' '); LogPath=$LogPath"
    }
}

function ConvertTo-LocalNativeArgument {
    param([AllowEmptyString()][string]$Value)

    if ($null -eq $Value -or $Value.Length -eq 0) {
        return '""'
    }

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashCount = 0

    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }

        if ($character -eq '"') {
            if ($backslashCount -gt 0) {
                [void]$builder.Append(('\' * (($backslashCount * 2) + 1)))
            }
            else {
                [void]$builder.Append('\')
            }
            [void]$builder.Append('"')
            $backslashCount = 0
            continue
        }

        if ($backslashCount -gt 0) {
            [void]$builder.Append(('\' * $backslashCount))
            $backslashCount = 0
        }

        [void]$builder.Append($character)
    }

    if ($backslashCount -gt 0) {
        [void]$builder.Append(('\' * ($backslashCount * 2)))
    }

    [void]$builder.Append('"')
    return $builder.ToString()
}

function Stop-LocalProcessTree {
    param([System.Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try {
        if ($Process.HasExited) { return }
        if ($env:OS -eq "Windows_NT") {
            $taskkill = Get-Command taskkill.exe -CommandType Application -ErrorAction SilentlyContinue
            if ($null -ne $taskkill) {
                & $taskkill.Source /PID $Process.Id /T /F 2>$null | Out-Null
            }
        }
        if (-not $Process.HasExited) { $Process.Kill() }
        [void]$Process.WaitForExit(10000)
    }
    catch { Write-Warning "Could not stop matrix process $($Process.Id): $($_.Exception.Message)" }
}

function Start-LocalLoggedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$LogPrefix
    )
    $stdout = $null
    $stderr = $null
    $process = $null
    try {
        $stdoutPath = $LogPrefix + ".stdout.log"
        $stderrPath = $LogPrefix + ".stderr.log"
        $stdout = [System.IO.File]::Open($stdoutPath, [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
        $stderr = [System.IO.File]::Open($stderrPath, [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
        $info = New-Object System.Diagnostics.ProcessStartInfo
        $info.FileName = $Executable
        $info.WorkingDirectory = $WorkingDirectory
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.Arguments = (($Arguments | ForEach-Object {
            ConvertTo-LocalNativeArgument -Value ([string]$_)
        }) -join " ")
        $process = [System.Diagnostics.Process]::Start($info)
        if ($null -eq $process) { throw "Unable to start $Executable" }
        # Drain both byte streams immediately, without loading whole logs into memory.
        $tasks = [System.Threading.Tasks.Task[]]@(
            $process.StandardOutput.BaseStream.CopyToAsync($stdout),
            $process.StandardError.BaseStream.CopyToAsync($stderr)
        )
        return [pscustomobject]@{
            Process = $process
            Streams = @($stdout, $stderr)
            Tasks = $tasks
            StdoutPath = $stdoutPath
            StderrPath = $stderrPath
        }
    }
    catch {
        Stop-LocalProcessTree -Process $process
        if ($null -ne $stdout) { $stdout.Dispose() }
        if ($null -ne $stderr) { $stderr.Dispose() }
        throw
    }
}

function Complete-LocalCapture {
    param([Parameter(Mandatory = $true)]$Capture)
    Stop-LocalProcessTree -Process $Capture.Process
    try {
        if (-not [System.Threading.Tasks.Task]::WaitAll(
            [System.Threading.Tasks.Task[]]$Capture.Tasks, 5000)) {
            Write-Warning "Process log pipes did not close within the capture budget; files may be partial."
        }
    }
    catch { Write-Warning "Process log capture ended with an error: $($_.Exception.Message)" }
    foreach ($stream in $Capture.Streams) {
        try { $stream.Dispose() } catch { Write-Warning "Could not close a matrix log stream." }
    }
    try { $Capture.Process.Dispose() } catch { }
}

function Wait-LocalHttpReady {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [string]$ManifestPath
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Matrix service exited before readiness. Url=$Url; ExitCode=$($Process.ExitCode)"
        }
        $hasManifest = [string]::IsNullOrWhiteSpace($ManifestPath) -or
            (Test-Path -LiteralPath $ManifestPath -PathType Leaf)
        if ($hasManifest) {
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 2 | Out-Null
                return
            }
            catch { }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Matrix service did not become ready within $TimeoutSeconds seconds: $Url"
}
