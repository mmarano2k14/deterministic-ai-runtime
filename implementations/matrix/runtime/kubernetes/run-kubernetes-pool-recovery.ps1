param(
    [string]$EvidencePath,
    [switch]$SkipInfrastructurePreflight
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
}
else {
    $repoRoot = [System.IO.Path]::GetFullPath($repoRoot.Trim())
}

function Require-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required command was not found on PATH: $Name"
    }
    return $command.Source
}

function Test-TcpPort {
    param(
        [Parameter(Mandatory = $true)][string]$HostName,
        [Parameter(Mandatory = $true)][int]$Port
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(1500)) {
            return $false
        }
        $client.EndConnect($async)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

$dotnet = Require-Command "dotnet"
$python = Require-Command "python"
$kubectl = Require-Command "kubectl"

if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $repoRoot "implementations\matrix\evidence\kubernetes\feature-runtime-provider-kubernetes-pool-hierarchical-recovery.json"
}
$EvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
$evidenceDirectory = Split-Path -Parent $EvidencePath
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
Remove-Item -LiteralPath $EvidencePath -Force -ErrorAction SilentlyContinue

if (-not $SkipInfrastructurePreflight) {
    Write-Host "[kubernetes-recovery-matrix] Checking active Kubernetes cluster..."
    & $kubectl cluster-info | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "kubectl cannot reach the active Kubernetes cluster."
    }

    & $kubectl get namespace ai-runtime -o name | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Required Kubernetes namespace 'ai-runtime' was not found."
    }

    if (-not (Test-TcpPort -HostName "127.0.0.1" -Port 6379)) {
        throw "Redis is not reachable on 127.0.0.1:6379. Existing KubernetesPool production recovery tests require host.minikube.internal:6379 from Pods."
    }
    if (-not (Test-TcpPort -HostName "127.0.0.1" -Port 27017)) {
        throw "MongoDB is not reachable on 127.0.0.1:27017. Existing KubernetesPool production recovery tests require host.minikube.internal:27017 from Pods."
    }
}

$previousEvidence = $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RECOVERY_EVIDENCE_PATH

try {
    $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RECOVERY_EVIDENCE_PATH = $EvidencePath

    $project = Join-Path $repoRoot "implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\Multiplexed.AI.McpServer.Tests.Integration.csproj"
    $filter = "FullyQualifiedName~HttpKubernetesRuntimePoolFullFailureProductionScenarioTests.Http_KubernetesPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario"

    Write-Host "[kubernetes-recovery-matrix] Running the existing EventDriven KubernetesPool full-failure production canary..."
    Write-Host "[kubernetes-recovery-matrix] Engine authorities remain unchanged: runtime-process kill, Pod failure, recovery, replay, ownership, ledger, trace, and cleanup are executed by the existing production harness."
    & $dotnet test $project -c Debug --filter $filter --logger "console;verbosity=detailed" --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
        throw "KubernetesPool recovery test passed but did not produce matrix evidence: $EvidencePath"
    }

    $verifier = Join-Path $PSScriptRoot "recovery_verifier.py"

    Write-Host "[kubernetes-recovery-matrix] Verifying machine-readable lifecycle/recovery evidence..."
    & $python $verifier --evidence $EvidencePath
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RECOVERY_EVIDENCE_PATH = $previousEvidence
}
