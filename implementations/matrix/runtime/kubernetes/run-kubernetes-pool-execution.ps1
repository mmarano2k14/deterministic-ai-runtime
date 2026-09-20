param(
    [string]$RuntimeImageRepository,
    [string]$RuntimeImageDigest,
    [string]$EvidencePath,
    [switch]$RequireImmutableImage,
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
    $EvidencePath = Join-Path $repoRoot "implementations\matrix\evidence\kubernetes\feature-runtime-provider-kubernetes-pool-http-command-routing.json"
}
$EvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
$evidenceDirectory = Split-Path -Parent $EvidencePath
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
Remove-Item -LiteralPath $EvidencePath -Force -ErrorAction SilentlyContinue

$hasRepository = -not [string]::IsNullOrWhiteSpace($RuntimeImageRepository)
$hasDigest = -not [string]::IsNullOrWhiteSpace($RuntimeImageDigest)
if ($hasRepository -xor $hasDigest) {
    throw "RuntimeImageRepository and RuntimeImageDigest must be supplied together."
}
if ($RequireImmutableImage -and -not ($hasRepository -and $hasDigest)) {
    throw "-RequireImmutableImage requires RuntimeImageRepository and RuntimeImageDigest."
}
if ($hasDigest -and $RuntimeImageDigest -notmatch '^sha256:[0-9a-fA-F]{64}$') {
    throw "RuntimeImageDigest must be sha256:<64-hex>."
}

if (-not $SkipInfrastructurePreflight) {
    Write-Host "[kubernetes-matrix] Checking active Kubernetes cluster..."
    & $kubectl cluster-info | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "kubectl cannot reach the active Kubernetes cluster."
    }

    & $kubectl get namespace ai-runtime -o name | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Required Kubernetes namespace 'ai-runtime' was not found."
    }

    if (-not (Test-TcpPort -HostName "127.0.0.1" -Port 6379)) {
        throw "Redis is not reachable on 127.0.0.1:6379. Existing KubernetesPool production tests require host.minikube.internal:6379 from Pods."
    }
    if (-not (Test-TcpPort -HostName "127.0.0.1" -Port 27017)) {
        throw "MongoDB is not reachable on 127.0.0.1:27017. Existing KubernetesPool production tests require host.minikube.internal:27017 from Pods."
    }
}

$previousEvidence = $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_EVIDENCE_PATH
$previousRepository = $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_REPOSITORY
$previousDigest = $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_DIGEST

try {
    $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_EVIDENCE_PATH = $EvidencePath

    if ($hasRepository -and $hasDigest) {
        $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_REPOSITORY = $RuntimeImageRepository.Trim()
        $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_DIGEST = $RuntimeImageDigest.Trim()
        Write-Host "[kubernetes-matrix] Runtime image: $($env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_REPOSITORY)@$($env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_DIGEST)"
    }
    else {
        Remove-Item Env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_REPOSITORY -ErrorAction SilentlyContinue
        Remove-Item Env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_DIGEST -ErrorAction SilentlyContinue
        Write-Host "[kubernetes-matrix] Runtime image: existing KubernetesPool production-test image profile"
    }

    $project = Join-Path $repoRoot "implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\Multiplexed.AI.McpServer.Tests.Integration.csproj"
    $filter = "FullyQualifiedName~HttpKubernetesPoolMcpCommandScenarioTests.Http_KubernetesPool_Should_Route_Exact_Commands_To_All_InPod_Children"

    Write-Host "[kubernetes-matrix] Running existing KubernetesPool SDK/Pod/Service execution scenario..."
    & $dotnet test $project -c Debug --filter $filter --logger "console;verbosity=detailed" --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
        throw "KubernetesPool test passed but did not produce matrix evidence: $EvidencePath"
    }

    $verifier = Join-Path $PSScriptRoot "verifier.py"
    $verifierArgs = @($verifier, "--evidence", $EvidencePath)
    if ($RequireImmutableImage) {
        $verifierArgs += "--require-immutable-image"
    }

    Write-Host "[kubernetes-matrix] Verifying machine-readable execution evidence..."
    & $python @verifierArgs
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_EVIDENCE_PATH = $previousEvidence
    $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_REPOSITORY = $previousRepository
    $env:MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_DIGEST = $previousDigest
}
