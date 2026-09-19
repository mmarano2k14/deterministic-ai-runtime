param(
    [string]$ContainerEngine,
    [string]$BaseImage = "python:3.12-alpine",
    [int]$RegistryPort = 5000,
    [string]$ImageName = "multiplexed-ai-hosted-worker-python",
    [switch]$RunTests,
    [switch]$KeepRegistryRunning
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-ContainerEngine {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    # Windows PowerShell 5.1 promotes redirected native stderr to ErrorRecord objects.
    # Keep native stderr captured for diagnostics without letting the repository-wide
    # Stop preference turn expected non-zero probe commands into terminating errors.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = @(& $script:EnginePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "Container engine command failed ($exitCode): $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Resolve-EnginePath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Container engine executable was not found: $resolved"
        }
        return $resolved
    }

    $command = Get-Command docker.exe -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        $command = Get-Command docker -ErrorAction SilentlyContinue
    }
    if ($null -eq $command) {
        throw "Docker-compatible container engine was not found on PATH. Pass -ContainerEngine with an absolute executable path."
    }

    return [System.IO.Path]::GetFullPath($command.Source)
}

function Resolve-ExactBaseImage {
    param([string]$Image)

    Write-Host "Resolving hosted-worker base image: $Image"
    Invoke-ContainerEngine -Arguments @("pull", "--platform", "linux/amd64", $Image) | Out-Null

    $inspect = Invoke-ContainerEngine -Arguments @("image", "inspect", $Image, "--format", "{{json .RepoDigests}}")
    $json = ($inspect.Output -join "").Trim()
    if ([string]::IsNullOrWhiteSpace($json) -or $json -eq "null") {
        throw "Base image has no repository digest after pull: $Image"
    }

    $digests = @($json | ConvertFrom-Json)
    $exact = $digests | Where-Object { $_ -match '@sha256:[0-9a-f]{64}$' } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($exact)) {
        throw "Could not resolve an exact repository digest for base image: $Image"
    }

    return $exact
}

function Ensure-LocalRegistry {
    param(
        [string]$Name,
        [int]$Port
    )

    $existing = Invoke-ContainerEngine -Arguments @("container", "inspect", $Name) -AllowFailure
    $created = $false
    $started = $false

    if ($existing.ExitCode -ne 0) {
        $registryImage = Invoke-ContainerEngine -Arguments @("image", "inspect", "registry:2") -AllowFailure
        if ($registryImage.ExitCode -ne 0) {
            Write-Host "Pulling local test registry image explicitly..."
            Invoke-ContainerEngine -Arguments @("pull", "registry:2") | Out-Null
        }

        Write-Host "Starting local test registry on 127.0.0.1:$Port..."
        Invoke-ContainerEngine -Arguments @(
            "run",
            "--detach",
            "--name", $Name,
            "--publish", "127.0.0.1:${Port}:5000",
            "registry:2"
        ) | Out-Null
        $created = $true
        $started = $true
    }
    else {
        $running = Invoke-ContainerEngine -Arguments @("inspect", "--format", "{{.State.Running}}", $Name)
        if ((($running.Output -join "").Trim()) -ne "true") {
            Write-Host "Starting existing local test registry: $Name"
            Invoke-ContainerEngine -Arguments @("start", $Name) | Out-Null
            $started = $true
        }
    }

    return [pscustomobject]@{
        Created = $created
        StartedByScript = $started
    }
}

function Push-WithRetry {
    param([string]$Reference)

    $last = $null
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        $last = Invoke-ContainerEngine -Arguments @("push", $Reference) -AllowFailure
        if ($last.ExitCode -eq 0) {
            return $last
        }
        Start-Sleep -Milliseconds 250
    }

    throw "Could not push hosted-worker image to the local registry.`n$($last.Output -join [Environment]::NewLine)"
}

$script:EnginePath = Resolve-EnginePath -RequestedPath $ContainerEngine
$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
if ([string]::IsNullOrWhiteSpace($repoRoot)) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..\..")).Path
}
else {
    $repoRoot = [System.IO.Path]::GetFullPath($repoRoot.Trim())
}
Write-Host "Container engine: $script:EnginePath"
Invoke-ContainerEngine -Arguments @("version") | Out-Null

$baseExact = Resolve-ExactBaseImage -Image $BaseImage
Write-Host "Pinned base image: $baseExact"

$registryName = "multiplexed-ai-test-registry-$RegistryPort"
$registryState = Ensure-LocalRegistry -Name $registryName -Port $RegistryPort
$repository = "localhost:$RegistryPort/$ImageName"
$candidate = "${repository}:candidate"
$dockerfile = Join-Path $PSScriptRoot "Dockerfile"

try {
    Write-Host "Building Linux/amd64 production hosted-worker test image..."
    Invoke-ContainerEngine -Arguments @(
        "build",
        "--platform", "linux/amd64",
        "--build-arg", "BASE_IMAGE=$baseExact",
        "--tag", $candidate,
        "--file", $dockerfile,
        $repoRoot
    ) | Out-Null

    Write-Host "Publishing hosted-worker image to the local registry to obtain its OCI manifest digest..."
    $push = Push-WithRetry -Reference $candidate
    $pushText = $push.Output -join [Environment]::NewLine
    $matches = [System.Text.RegularExpressions.Regex]::Matches($pushText, 'digest:\s*(sha256:[0-9a-f]{64})')
    if ($matches.Count -eq 0) {
        throw "The registry push completed but no OCI manifest digest was reported.`n$pushText"
    }

    $manifestDigest = $matches[$matches.Count - 1].Groups[1].Value
    $exactReference = "$repository@$manifestDigest"

    Write-Host "Preloading exact image reference: $exactReference"
    Invoke-ContainerEngine -Arguments @("pull", "--platform", "linux/amd64", $exactReference) | Out-Null
    Invoke-ContainerEngine -Arguments @("image", "inspect", $exactReference) | Out-Null
}
finally {
    if ($registryState.StartedByScript -and -not $KeepRegistryRunning) {
        Write-Host "Stopping local registry before runtime validation. The exact hosted-worker image remains preloaded locally."
        Invoke-ContainerEngine -Arguments @("stop", $registryName) -AllowFailure | Out-Null
    }
}

$offlineInspect = Invoke-ContainerEngine -Arguments @("image", "inspect", $exactReference) -AllowFailure
if ($offlineInspect.ExitCode -ne 0) {
    throw "The exact repository@digest reference is not available in the local engine after preparation: $exactReference"
}

$versionProbe = Invoke-ContainerEngine -Arguments @(
    "run", "--rm", "--pull=never",
    "--entrypoint", "python3",
    $exactReference,
    "-I", "-S", "-B", "-X", "utf8",
    "-c", "import platform; print(platform.python_version())"
)
$runtimeVersion = (($versionProbe.Output | Select-Object -Last 1) -as [string]).Trim()
if ($runtimeVersion -notmatch '^3\.(12|13)\.[0-9]+$') {
    throw "Prepared production Python worker image reported an unsupported runtime version: $runtimeVersion"
}

$env:MULTIPLEXED_AI_TEST_CONTAINER_ENGINE = $script:EnginePath
$env:MULTIPLEXED_AI_TEST_CONTAINER_IMAGE = $exactReference
$env:MULTIPLEXED_AI_TEST_CONTAINER_RUNTIME_VERSION = $runtimeVersion

Write-Host ""
Write-Host "Real-engine test environment prepared:"
Write-Host "MULTIPLEXED_AI_TEST_CONTAINER_ENGINE=$($env:MULTIPLEXED_AI_TEST_CONTAINER_ENGINE)"
Write-Host "MULTIPLEXED_AI_TEST_CONTAINER_IMAGE=$($env:MULTIPLEXED_AI_TEST_CONTAINER_IMAGE)"
Write-Host "MULTIPLEXED_AI_TEST_CONTAINER_RUNTIME_VERSION=$($env:MULTIPLEXED_AI_TEST_CONTAINER_RUNTIME_VERSION)"
Write-Host ""
Write-Host "PowerShell variables for this process are already configured."
Write-Host "CMD equivalents for another shell:"
Write-Host "set MULTIPLEXED_AI_TEST_CONTAINER_ENGINE=$($env:MULTIPLEXED_AI_TEST_CONTAINER_ENGINE)"
Write-Host "set MULTIPLEXED_AI_TEST_CONTAINER_IMAGE=$($env:MULTIPLEXED_AI_TEST_CONTAINER_IMAGE)"
Write-Host "set MULTIPLEXED_AI_TEST_CONTAINER_RUNTIME_VERSION=$($env:MULTIPLEXED_AI_TEST_CONTAINER_RUNTIME_VERSION)"

if ($RunTests) {
    $project = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj"))
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Test project was not found: $project"
    }

    Write-Host ""
    Write-Host "Running ContainerRealEngine tests..."
    & dotnet test $project --filter "Category=ContainerRealEngine"
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
