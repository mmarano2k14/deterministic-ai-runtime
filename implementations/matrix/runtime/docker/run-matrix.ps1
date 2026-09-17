$ErrorActionPreference = "Stop"

$composeFile = Join-Path $PSScriptRoot "docker-compose.yml"
$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)

if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
}

$logFile = Join-Path $repoRoot "matrix-full.log"
Remove-Item $logFile -Force -ErrorAction SilentlyContinue

function Invoke-ComposeLogged {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Arguments
    )

    $command = "docker compose -f `"$composeFile`" $Arguments >> `"$logFile`" 2>&1"
    & cmd.exe /d /s /c $command
    return $LASTEXITCODE
}

function Get-ServiceState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Service
    )

    $id = (& docker compose -f $composeFile ps -aq $Service 2>$null | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($id)) {
        return [pscustomobject]@{
            Service = $Service
            Status = "missing"
            ExitCode = $null
        }
    }

    $status = (& docker inspect -f "{{.State.Status}}" $id).Trim()
    $exitCode = $null
    if ($status -eq "exited") {
        $exitCode = [int]((& docker inspect -f "{{.State.ExitCode}}" $id).Trim())
    }

    return [pscustomobject]@{
        Service = $Service
        Status = $status
        ExitCode = $exitCode
    }
}

Write-Host "[matrix] Cleaning previous stack..."
$null = Invoke-ComposeLogged "down -v --remove-orphans"

Write-Host "[matrix] Building images... full output -> $logFile"
$buildCode = Invoke-ComposeLogged "build"
if ($buildCode -ne 0) {
    Write-Host "`n================ BUILD FAILED ================"
    Get-Content $logFile -Tail 120
    Write-Host "================================================"
    exit $buildCode
}

Write-Host "[matrix] Starting production-like Docker ProcessHostPool matrix..."
$upCode = Invoke-ComposeLogged "up -d"

if ($upCode -eq 0) {
    Write-Host "[matrix] Waiting for matrix verifier..."
    $null = Invoke-ComposeLogged "wait matrix-verifier"
}

$null = Invoke-ComposeLogged "logs --no-color"

$states = @(
    Get-ServiceState "client-dotnet"
    Get-ServiceState "client-typescript"
    Get-ServiceState "client-python"
    Get-ServiceState "matrix-verifier"
)

Write-Host "`n================ MATRIX SERVICES ================"
foreach ($state in $states) {
    $exitText = if ($null -eq $state.ExitCode) { "-" } else { [string]$state.ExitCode }
    Write-Host ("{0,-20} status={1,-10} exit={2}" -f $state.Service, $state.Status, $exitText)
}
Write-Host "================================================="

Write-Host "`n================ MATRIX VERIFIER ================"
& docker compose -f $composeFile logs --no-color matrix-verifier
Write-Host "================================================="

$verifier = $states | Where-Object { $_.Service -eq "matrix-verifier" }
$clientFailures = @($states | Where-Object {
    $_.Service -like "client-*" -and ($_.Status -ne "exited" -or $_.ExitCode -ne 0)
})

if ($upCode -eq 0 -and $clientFailures.Count -eq 0 -and $verifier.Status -eq "exited" -and $verifier.ExitCode -eq 0) {
    Write-Host "GREEN - MATRIX PASSED"
    Write-Host "Full log: $logFile"
    exit 0
}

Write-Host "RED - MATRIX FAILED"
if ($clientFailures.Count -gt 0) {
    Write-Host "`nFailing client logs:"
    foreach ($failure in $clientFailures) {
        Write-Host "`n--- $($failure.Service) ---"
        & docker compose -f $composeFile logs --no-color --tail=80 $failure.Service
    }
}

Write-Host "`nFull log: $logFile"
exit 1
