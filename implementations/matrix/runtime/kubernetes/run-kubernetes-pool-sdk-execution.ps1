param(
    [string]$RuntimeImage = "multiplexed-ai-runtime:matrix-sdk",
    [string]$ProfilePath,
    [string]$ManifestPath,
    [string]$SdkEvidencePath,
    [string]$KubernetesEvidencePath,
    [switch]$SkipImageBuild,
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
        if (-not $async.AsyncWaitHandle.WaitOne(1500)) { return $false }
        $client.EndConnect($async)
        return $true
    }
    catch { return $false }
    finally { $client.Dispose() }
}

function Invoke-Text {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $output = & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $Executable $($Arguments -join ' ')"
    }
    return (($output | Out-String).Trim())
}

function ConvertTo-NativeProcessArgument {
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

function Wait-HttpReady {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [System.Diagnostics.Process]$Process
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -ne $Process -and $Process.HasExited) {
            throw "Control-plane host exited before becoming ready. ExitCode=$($Process.ExitCode)"
        }
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 2 | Out-Null
            return
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Timed out waiting for $Url"
}

function Get-ScaleOutDiagnostics {
    param(
        [Parameter(Mandatory = $true)][string]$Url
    )
    return Invoke-RestMethod -UseBasicParsing -Uri $Url -TimeoutSec 3
}

function Wait-ScaleOutWatcherReady {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$ControlPlaneId,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [System.Diagnostics.Process]$Process
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastState = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -ne $Process -and $Process.HasExited) {
            throw "Control-plane host exited before the scale-out watcher became ready. ExitCode=$($Process.ExitCode)"
        }
        try {
            $lastState = Get-ScaleOutDiagnostics -Url $Url
            if ($lastState.watcherRegistered -eq $true -and
                $lastState.watcherReady -eq $true -and
                [string]$lastState.resolvedControlPlaneId -eq $ControlPlaneId) {
                return $lastState
            }
        }
        catch {
            # The matrix endpoint can race Kestrel startup; retry within the bounded readiness window.
        }
        Start-Sleep -Milliseconds 250
    }

    $rendered = if ($null -eq $lastState) { "<unavailable>" } else { $lastState | ConvertTo-Json -Depth 10 -Compress }
    throw "Scale-out watcher did not become ready for controlPlaneId '$ControlPlaneId' within $TimeoutSeconds seconds. State=$rendered"
}

function Start-NativeProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.UseShellExecute = $false
    $info.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeProcessArgument -Value ([string]$_) }) -join " ")
    $process = [System.Diagnostics.Process]::Start($info)
    if ($null -eq $process) {
        throw "Failed to start native process: $Executable"
    }
    return $process
}

function Stop-ProcessTree {
    param([System.Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try {
        if ($Process.HasExited) { return }
    }
    catch { return }

    try {
        if ($env:OS -eq "Windows_NT") {
            $taskkill = Get-Command taskkill.exe -ErrorAction SilentlyContinue
            if ($null -ne $taskkill) {
                & $taskkill.Source /PID $Process.Id /T /F | Out-Null
                try { $Process.WaitForExit(15000) | Out-Null } catch { }
                return
            }
        }
        $Process.Kill()
        try { $Process.WaitForExit(15000) | Out-Null } catch { }
    }
    catch { }
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $json = $Value | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText(
        $Path,
        $json,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-PoolResources {
    param(
        [Parameter(Mandatory = $true)][string]$Namespace,
        [Parameter(Mandatory = $true)][string]$PoolId,
        [Parameter(Mandatory = $true)][string]$Kubectl
    )
    $podsDocument = (Invoke-Text $Kubectl @("--request-timeout=5s", "get", "pods", "-n", $Namespace, "-l", "multiplexed.ai/runtime-pool=true", "-o", "json")) | ConvertFrom-Json
    $servicesDocument = (Invoke-Text $Kubectl @("--request-timeout=5s", "get", "services", "-n", $Namespace, "-l", "multiplexed.ai/runtime-pool=true", "-o", "json")) | ConvertFrom-Json
    $pods = @($podsDocument.items | Where-Object { $_.metadata.annotations.'multiplexed.ai/pool-id' -eq $PoolId })
    $services = @($servicesDocument.items | Where-Object { $_.metadata.annotations.'multiplexed.ai/pool-id' -eq $PoolId })
    return [pscustomobject]@{ Pods = $pods; Services = $services }
}

function Write-KubectlDiagnostic {
    param(
        [Parameter(Mandatory = $true)][string]$Kubectl,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $process = $null
    try {
        $info = New-Object System.Diagnostics.ProcessStartInfo
        $info.FileName = $Kubectl
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeProcessArgument -Value ([string]$_) }) -join " ")
        $process = [System.Diagnostics.Process]::Start($info)
        if ($null -eq $process) { throw "Unable to start kubectl diagnostics." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            throw "kubectl diagnostics exceeded the 15-second capture budget."
        }
        $streams = [System.Threading.Tasks.Task[]]@($stdout, $stderr)
        if (-not [System.Threading.Tasks.Task]::WaitAll($streams, 2000)) {
            throw "kubectl diagnostic streams did not close after process exit."
        }
        $text = ([string]$stdout.Result) + ([string]$stderr.Result)
        [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($false))
        $text | Out-Host
        if ($process.ExitCode -ne 0) {
            Write-Warning "kubectl diagnostics exited with code $($process.ExitCode); output retained in $Path"
        }
    }
    catch {
        # Diagnostic failures must not replace the original readiness or SDK failure.
        Write-Warning "Unable to capture $Path : $($_.Exception.Message)"
    }
    finally {
        Stop-ProcessTree -Process $process
        if ($null -ne $process) { $process.Dispose() }
    }
}

function Write-KubernetesPoolDiagnostics {
    param(
        [Parameter(Mandatory = $true)][string]$Namespace,
        [Parameter(Mandatory = $true)][string]$PoolId,
        [Parameter(Mandatory = $true)][string]$Kubectl,
        [Parameter(Mandatory = $true)][string]$ScaleOutUrl
    )
    Write-Warning "KubernetesPool diagnostics for pool '$PoolId' in namespace '$Namespace'."
    try {
        $diagnosticsPath = Join-Path (Join-Path $stateRoot "diagnostics") $PoolId
        New-Item -ItemType Directory -Path $diagnosticsPath -Force | Out-Null
        try {
            $scaleOut = Get-ScaleOutDiagnostics -Url $ScaleOutUrl
            Write-Host "--- MATRIX SCALE-OUT STATE ---"
            ($scaleOut | ConvertTo-Json -Depth 10) | Out-Host
            Write-Utf8Json -Value $scaleOut -Path (Join-Path $diagnosticsPath "scaleout.json")
        }
        catch {
            Write-Warning "Scale-out diagnostic endpoint failed: $($_.Exception.Message)"
        }

        # Only inspect resources carrying this run's exact pool annotation.
        $resources = Get-PoolResources -Namespace $Namespace -PoolId $PoolId -Kubectl $Kubectl
        Write-Utf8Json -Value $resources -Path (Join-Path $diagnosticsPath "resources.json")
        if (@($resources.Pods).Count -eq 0) {
            Write-Warning "No Pod remains for the current pool '$PoolId'. Historical pools are not included."
        }
        foreach ($pod in @($resources.Pods)) {
            $podName = [string]$pod.metadata.name
            Write-Host "--- CURRENT POOL POD: $podName ---"
            Write-KubectlDiagnostic -Kubectl $Kubectl `
                -Arguments @("--request-timeout=5s", "describe", "pod", $podName, "-n", $Namespace) `
                -Path (Join-Path $diagnosticsPath "$podName.describe.txt")
            Write-KubectlDiagnostic -Kubectl $Kubectl `
                -Arguments @("--request-timeout=5s", "logs", $podName, "-n", $Namespace, "--all-containers=true", "--timestamps=true", "--tail=-1", "--pod-running-timeout=5s") `
                -Path (Join-Path $diagnosticsPath "$podName.logs.txt")
        }
        foreach ($pod in @($resources.Pods)) {
            $podName = [string]$pod.metadata.name
            if ($pod.status.phase -eq "Running") {
                # Compare the same on-disk files, not a Docker index digest with a CRI image ID.
                & $python $runtimeArtifactProbe pod --kubectl $Kubectl --namespace $Namespace `
                    --pod $podName --container runtime-pool --expected $localArtifactPath `
                    --output (Join-Path $diagnosticsPath "$podName.artifacts.json")
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "Selected runtime artifact comparison is unavailable; the SDK failure is preserved."
                }
            }
            $restarted = @($pod.status.containerStatuses | Where-Object { $_.restartCount -gt 0 })
            if ($restarted.Count -gt 0) {
                Write-KubectlDiagnostic -Kubectl $Kubectl `
                    -Arguments @("--request-timeout=5s", "logs", $podName, "-n", $Namespace, "--all-containers=true", "--timestamps=true", "--previous", "--tail=-1", "--pod-running-timeout=5s") `
                    -Path (Join-Path $diagnosticsPath "$podName.previous.logs.txt")
            }
        }
        $script:poolDiagnosticsCollected = $true
        foreach ($service in @($resources.Services)) {
            $serviceName = [string]$service.metadata.name
            Write-KubectlDiagnostic -Kubectl $Kubectl `
                -Arguments @("--request-timeout=5s", "get", "service", $serviceName, "-n", $Namespace, "-o", "json") `
                -Path (Join-Path $diagnosticsPath "$serviceName.service.json")
        }
        Write-Host "[kubernetes-sdk-matrix] Current-pool diagnostics retained in $diagnosticsPath"
    }
    catch {
        Write-Warning "KubernetesPool diagnostic collection failed: $($_.Exception.Message)"
    }
}

function Remove-PoolResources {
    param(
        [Parameter(Mandatory = $true)][string]$Namespace,
        [Parameter(Mandatory = $true)][string]$PoolId,
        [Parameter(Mandatory = $true)][string]$Kubectl
    )
    try {
        $resources = Get-PoolResources -Namespace $Namespace -PoolId $PoolId -Kubectl $Kubectl
        $serviceNames = @($resources.Services | ForEach-Object { $_.metadata.name })

        foreach ($kind in @("httproutes.gateway.networking.k8s.io", "grpcroutes.gateway.networking.k8s.io")) {
            $routeJson = & $Kubectl get $kind -n $Namespace -o json 2>$null
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($routeJson | Out-String))) { continue }
            $routeDocument = (($routeJson | Out-String) | ConvertFrom-Json)
            foreach ($route in @($routeDocument.items)) {
                $backendNames = @(
                    $route.spec.rules |
                        ForEach-Object { $_.backendRefs } |
                        ForEach-Object { $_.name }
                )
                if (@($backendNames | Where-Object { $serviceNames -contains $_ }).Count -gt 0) {
                    & $Kubectl delete $kind $route.metadata.name -n $Namespace --ignore-not-found=true | Out-Null
                }
            }
        }

        foreach ($service in @($resources.Services)) {
            & $Kubectl delete service $service.metadata.name -n $Namespace --ignore-not-found=true | Out-Null
        }
        foreach ($pod in @($resources.Pods)) {
            & $Kubectl delete pod $pod.metadata.name -n $Namespace --ignore-not-found=true --wait=false | Out-Null
        }
    }
    catch {
        Write-Warning "Kubernetes matrix cleanup was incomplete: $($_.Exception.Message)"
    }
}

$dotnet = Require-Command "dotnet"
$python = Require-Command "python"
$node = Require-Command "node"
$docker = Require-Command "docker"
$kubectl = Require-Command "kubectl"
$minikube = Require-Command "minikube"

if ([string]::IsNullOrWhiteSpace($RuntimeImage)) {
    throw "RuntimeImage is required."
}
$isExactImage = $RuntimeImage -match '^.+@sha256:[0-9a-fA-F]{64}$'
if ($RequireImmutableImage -and -not $isExactImage) {
    throw "-RequireImmutableImage requires RuntimeImage in repository@sha256:<64-hex> form."
}
if ($isExactImage -and -not $SkipImageBuild) {
    Write-Host "[kubernetes-sdk-matrix] Exact image supplied; local image build is skipped."
    $SkipImageBuild = $true
}

$stateRoot = Join-Path $repoRoot ".matrix-kubernetes-sdk"
$evidenceRoot = Join-Path $repoRoot "implementations\matrix\evidence\kubernetes"
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null

$priorExecutionEvidencePath = Join-Path $evidenceRoot "feature-runtime-provider-kubernetes-pool-http-command-routing.json"
$priorRecoveryEvidencePath = Join-Path $evidenceRoot "feature-runtime-provider-kubernetes-pool-hierarchical-recovery.json"

if ([string]::IsNullOrWhiteSpace($ProfilePath)) { $ProfilePath = Join-Path $stateRoot "profile.json" }
if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $stateRoot "runtime-manifest.json" }
if ([string]::IsNullOrWhiteSpace($SdkEvidencePath)) { $SdkEvidencePath = Join-Path $evidenceRoot "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker.json" }
if ([string]::IsNullOrWhiteSpace($KubernetesEvidencePath)) { $KubernetesEvidencePath = Join-Path $evidenceRoot "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker.kubernetes.json" }
$ClientDiagnosticLogPath = Join-Path $stateRoot "external-sdk-client.log"
$hostStdoutPath = Join-Path $stateRoot "control-plane.stdout.log"
$hostStderrPath = Join-Path $stateRoot "control-plane.stderr.log"
$localArtifactPath = Join-Path $stateRoot "runtime-artifacts.local.json"
$runtimeArtifactProbe = Join-Path $PSScriptRoot "runtime_artifact_probe.py"
$ProfilePath = [System.IO.Path]::GetFullPath($ProfilePath)
$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
$SdkEvidencePath = [System.IO.Path]::GetFullPath($SdkEvidencePath)
$KubernetesEvidencePath = [System.IO.Path]::GetFullPath($KubernetesEvidencePath)
foreach ($path in @($ProfilePath, $ManifestPath, $SdkEvidencePath, $KubernetesEvidencePath, $ClientDiagnosticLogPath, $hostStdoutPath, $hostStderrPath, $localArtifactPath)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}

if (-not $SkipInfrastructurePreflight) {
    Write-Host "[kubernetes-sdk-matrix] Checking active Kubernetes cluster..."
    & $kubectl cluster-info | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "kubectl cannot reach the active Kubernetes cluster." }
    & $kubectl get namespace ai-runtime -o name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Required Kubernetes namespace 'ai-runtime' was not found." }
    if (-not (Test-TcpPort -HostName "127.0.0.1" -Port 6379)) { throw "Redis is not reachable on 127.0.0.1:6379." }
    if (-not (Test-TcpPort -HostName "127.0.0.1" -Port 27017)) { throw "MongoDB is not reachable on 127.0.0.1:27017." }
}

if (-not (Test-Path -LiteralPath $priorExecutionEvidencePath -PathType Leaf)) {
    $executionRunner = Join-Path $PSScriptRoot "run-kubernetes-pool-execution.ps1"
    if (-not (Test-Path -LiteralPath $executionRunner -PathType Leaf)) {
        throw "KubernetesPool execution evidence is missing and the execution sidecar runner was not found: $executionRunner"
    }

    Write-Host "[kubernetes-sdk-matrix] Prior live-routing evidence is missing; refreshing the lightweight KubernetesPool execution sidecar automatically..."
    & $executionRunner -SkipInfrastructurePreflight
    if ($LASTEXITCODE -ne 0) {
        throw "Automatic refresh of KubernetesPool execution evidence failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $priorExecutionEvidencePath -PathType Leaf)) {
        throw "KubernetesPool execution sidecar completed but did not produce evidence: $priorExecutionEvidencePath"
    }
}

$priorRecoveryEvidenceAvailable = Test-Path -LiteralPath $priorRecoveryEvidencePath -PathType Leaf
if (-not $priorRecoveryEvidenceAvailable) {
    Write-Warning "Prior KubernetesPool recovery evidence is not present. The external-SDK scenario will still run; only the final cross-topology closure will be skipped. Re-run run-kubernetes-pool-recovery.ps1 only if final closure evidence must be regenerated."
}

$runtimeDockerfile = Join-Path $repoRoot "implementations\matrix\runtime\kubernetes\runtime-pool.Dockerfile"
if (-not $SkipImageBuild) {
    Write-Host "[kubernetes-sdk-matrix] Building Kubernetes Runtime Pool image with production hosted workers: $RuntimeImage"
    & $docker build -f $runtimeDockerfile -t $RuntimeImage $repoRoot
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." }
}

& $docker image inspect $RuntimeImage | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Runtime image is not available to the local Docker engine: $RuntimeImage" }

Write-Host "[kubernetes-sdk-matrix] Loading Runtime Pool image into Minikube..."
& $minikube image load $RuntimeImage --overwrite
if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." }

# Resolve image aliases before advertising or registering a hosted worker profile.
# The production launch-path guard rejects symbolic links before a child becomes Ready.
$runtimeImageProbe = Join-Path $PSScriptRoot "runtime_image_probe.py"
Write-Host "[kubernetes-sdk-matrix] Resolving canonical runtime executable paths inside the image..."
$runtimeIdentity = (Invoke-Text $python @($runtimeImageProbe, "--docker", $docker, "--image", $RuntimeImage)) | ConvertFrom-Json
Write-Utf8Json -Value $runtimeIdentity -Path (Join-Path $stateRoot "runtime-image-identity.json")
# Optional read-only evidence. Failure to inspect must not replace the execution outcome.
& $python $runtimeArtifactProbe local --docker $docker --image-id ([string]$runtimeIdentity.localImageId) --output $localArtifactPath
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Local artifact fingerprints are unavailable; execution diagnostics will retain that limitation."
}
$imageArchitecture = [string]$runtimeIdentity.architecture
$imageDotnetExecutable = [string]$runtimeIdentity.dotnet.executablePath
$imageDotnetVersion = [string]$runtimeIdentity.dotnet.version
$imageNodeExecutable = [string]$runtimeIdentity.typescript.executablePath
$imageNodeVersion = [string]$runtimeIdentity.typescript.version
$imagePythonExecutable = [string]$runtimeIdentity.python.executablePath
$imagePythonVersion = [string]$runtimeIdentity.python.version
$imagePythonSha256 = [string]$runtimeIdentity.python.sha256
Write-Host "[kubernetes-sdk-matrix] Runtime executables: dotnet=$imageDotnetExecutable; node=$imageNodeExecutable; python=$imagePythonExecutable"

$controlPlaneHostOutput = Join-Path $stateRoot "control-plane-host"
Remove-Item -Recurse -Force $controlPlaneHostOutput -ErrorAction SilentlyContinue
$hostProject = Join-Path $repoRoot "implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Multiplexed.AI.McpServer.Host.csproj"
Write-Host "[kubernetes-sdk-matrix] Publishing publication-only control-plane host..."
& $dotnet publish $hostProject -c Release -o $controlPlaneHostOutput --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." }

$controlPlaneHostDll = Join-Path $controlPlaneHostOutput "Multiplexed.AI.McpServer.Host.dll"

$profileVariables = @{
    MULTIPLEXED_AI_MATRIX_KUBERNETES_SDK_PROFILE_PATH = $ProfilePath
    MULTIPLEXED_AI_MATRIX_KUBERNETES_SDK_MANIFEST_PATH = $ManifestPath
    MULTIPLEXED_AI_MATRIX_KUBERNETES_SDK_RUNTIME_IMAGE = $RuntimeImage
    MULTIPLEXED_AI_MATRIX_KUBERNETES_SDK_REQUIRE_IMMUTABLE_IMAGE = ($(if ($RequireImmutableImage) { "true" } else { "false" }))
    MULTIPLEXED_AI_MATRIX_CONTROL_PLANE_HOST_DLL = $controlPlaneHostDll
    MULTIPLEXED_AI_MATRIX_IMAGE_ARCHITECTURE = $imageArchitecture
    MULTIPLEXED_AI_MATRIX_IMAGE_DOTNET_VERSION = $imageDotnetVersion
    MULTIPLEXED_AI_MATRIX_IMAGE_DOTNET_EXECUTABLE = $imageDotnetExecutable
    MULTIPLEXED_AI_MATRIX_IMAGE_NODE_VERSION = $imageNodeVersion
    MULTIPLEXED_AI_MATRIX_IMAGE_NODE_EXECUTABLE = $imageNodeExecutable
    MULTIPLEXED_AI_MATRIX_IMAGE_PYTHON_VERSION = $imagePythonVersion
    MULTIPLEXED_AI_MATRIX_IMAGE_PYTHON_EXECUTABLE = $imagePythonExecutable
    MULTIPLEXED_AI_MATRIX_IMAGE_PYTHON_SHA256 = $imagePythonSha256
}
$previousVariables = @{}
foreach ($entry in $profileVariables.GetEnumerator()) {
    $previousVariables[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key)
    [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value)
}

$hostProcess = $null
$hostStdoutStream = $null
$hostStderrStream = $null
$hostOutputTasks = @()
$scenarioSucceeded = $false
$script:poolDiagnosticsCollected = $false
$clientProcess = $null
$profile = $null
try {
    $integrationProject = Join-Path $repoRoot "implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\Multiplexed.AI.McpServer.Tests.Integration.csproj"
    $profileFilter = "FullyQualifiedName~HttpKubernetesPoolExternalSdkMatrixProfileTests.Http_KubernetesPool_Should_Write_ExternalSdk_Matrix_Profile"
    Write-Host "[kubernetes-sdk-matrix] Composing production KubernetesPool control-plane profile..."
    & $dotnet test $integrationProject -c Debug --filter $profileFilter --logger "console;verbosity=minimal" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $ProfilePath -PathType Leaf)) { throw "Profile writer did not produce: $ProfilePath" }

    $profile = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json
    $settings = $profile.settings

    # Snapshot.Project is ownership data, not the host-owned TRN builder configuration.
    # Reject an incomplete profile before starting the control plane or creating any SDK run.
    $contextProject = [string]$settings.'AiMatrixHarness:Project'
    $controlPlaneProject = [string]$settings.'Multiplexed.Rbac.Core:Project'
    $runtimeChildProject = [string]$settings.'AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:Multiplexed.Rbac.Core__Project'
    if ([string]::IsNullOrWhiteSpace($contextProject) -or
        -not [string]::Equals($contextProject, $controlPlaneProject, [System.StringComparison]::Ordinal) -or
        -not [string]::Equals($contextProject, $runtimeChildProject, [System.StringComparison]::Ordinal)) {
        throw "Kubernetes SDK RBAC project mismatch: context='$contextProject'; control-plane='$controlPlaneProject'; runtime-child='$runtimeChildProject'. The generated profile must explicitly align all three projects."
    }
    Write-Host "[kubernetes-sdk-matrix] RBAC project aligned: context=$contextProject; control-plane=$controlPlaneProject; runtime-child=$runtimeChildProject"

    $processArguments = @($controlPlaneHostDll)
    foreach ($property in @($settings.PSObject.Properties | Sort-Object Name)) {
        if ($null -ne $property.Value) {
            $processArguments += "--$($property.Name)=$($property.Value)"
        }
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $dotnet
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = (($processArguments | ForEach-Object { ConvertTo-NativeProcessArgument -Value ([string]$_) }) -join " ")
    $startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = "http://127.0.0.1:8081"

    Write-Host "[kubernetes-sdk-matrix] Starting real control-plane host with runtimeProvider=KubernetesPool..."
    $hostStdoutStream = [System.IO.File]::Open($hostStdoutPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
    $hostStderrStream = [System.IO.File]::Open($hostStderrPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
    $hostProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $hostProcess) { throw "Failed to start the control-plane host." }
    # Drain both pipes concurrently without PowerShell event handlers or an in-memory log buffer.
    $hostOutputTasks = [System.Threading.Tasks.Task[]]@(
        $hostProcess.StandardOutput.BaseStream.CopyToAsync($hostStdoutStream),
        $hostProcess.StandardError.BaseStream.CopyToAsync($hostStderrStream)
    )
    Write-Host "[kubernetes-sdk-matrix] Full control-plane output: $hostStdoutPath"
    Write-Host "[kubernetes-sdk-matrix] Full control-plane errors: $hostStderrPath"

    Wait-HttpReady -Url "http://127.0.0.1:8081/health" -TimeoutSeconds 180 -Process $hostProcess
    $manifestDeadline = [DateTime]::UtcNow.AddMinutes(1)
    while (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf) -and [DateTime]::UtcNow -lt $manifestDeadline) {
        if ($hostProcess.HasExited) { throw "Control-plane host exited before writing the SDK manifest." }
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Matrix manifest was not produced: $ManifestPath" }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $contextTtlProperty = $manifest.PSObject.Properties["executionContextTtlSeconds"]
    $contextTtlSeconds = 0
    if ($null -eq $contextTtlProperty -or
        -not [int]::TryParse([string]$contextTtlProperty.Value, [ref]$contextTtlSeconds) -or
        $contextTtlSeconds -le 0) {
        throw "Matrix manifest must expose a positive executionContextTtlSeconds before SDK submission. Rebuild the control-plane host with the current matrix bootstrap."
    }
    Write-Host "[kubernetes-sdk-matrix] Execution context snapshot TTL=$($contextTtlSeconds)s"
    $pythonEnvironmentRef = [string]$manifest.environmentRefs.python
    if ([string]::IsNullOrWhiteSpace($pythonEnvironmentRef)) {
        throw "Matrix manifest did not expose the Python publication environment reference."
    }
    $encodedEnvironmentRef = [System.Uri]::EscapeDataString($pythonEnvironmentRef)
    $publicationEnvironmentUrl = "http://127.0.0.1:8081/matrix/publication-environment/$encodedEnvironmentRef"
    Write-Host "[kubernetes-sdk-matrix] Verifying publication-only runtime catalog entry '$pythonEnvironmentRef' before SDK publication..."
    try {
        $publicationEnvironment = Invoke-RestMethod -UseBasicParsing -Uri $publicationEnvironmentUrl -TimeoutSec 5
    }
    catch {
        throw "Publication-only runtime '$pythonEnvironmentRef' is not available before SDK execution. $($_.Exception.Message)"
    }
    if ($publicationEnvironment.found -ne $true -or
        [string]$publicationEnvironment.executionLanguage -ne "python" -or
        [string]$publicationEnvironment.reference -ne $pythonEnvironmentRef) {
        $renderedPublicationEnvironment = $publicationEnvironment | ConvertTo-Json -Depth 10 -Compress
        throw "Publication-only runtime catalog preflight failed. State=$renderedPublicationEnvironment"
    }
    Write-Host "[kubernetes-sdk-matrix] Publication runtime ready. reference=$($publicationEnvironment.reference); version=$($publicationEnvironment.runtimeVersion); artifact=$($publicationEnvironment.executionDescriptor.artifactKind)"

    $namespaceName = [string]$profile.namespaceName
    $poolId = [string]$profile.poolId
    $scaleOutUrl = "http://127.0.0.1:8081/matrix/scaleout"

    Write-Host "[kubernetes-sdk-matrix] Waiting for the existing scale-out watcher to complete its first store scan..."
    $scaleOutReady = Wait-ScaleOutWatcherReady `
        -Url $scaleOutUrl `
        -ControlPlaneId ([string]$profile.controlPlaneId) `
        -TimeoutSeconds 60 `
        -Process $hostProcess
    Write-Host "[kubernetes-sdk-matrix] Scale-out watcher ready. watcherId=$($scaleOutReady.watcherId); controlPlaneId=$($scaleOutReady.resolvedControlPlaneId)"

    $client = Join-Path $repoRoot "implementations\matrix\clients\python\run.py"
    $scenarioId = "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker"
    $clientArguments = @(
        $client,
        "--manifest", $ManifestPath,
        "--worker", "python",
        "--scenario-id", $scenarioId,
        "--evidence", $SdkEvidencePath,
        "--diagnostic-log", $ClientDiagnosticLogPath,
        "--terminal-timeout-seconds", "300",
        "--require-output-marker"
    )
    Write-Host "[kubernetes-sdk-matrix] Publishing and executing external Python SDK code through KubernetesPool..."
    $clientProcess = Start-NativeProcess -Executable $python -Arguments $clientArguments

    $podDeadline = [DateTime]::UtcNow.AddSeconds(180)
    $clientDeadline = [DateTime]::UtcNow.AddSeconds(360)
    $poolObserved = $false
    $lastScaleOutSignature = $null
    while (-not $clientProcess.HasExited) {
        if ($hostProcess.HasExited) {
            throw "Control-plane host exited while the external SDK execution was active. ExitCode=$($hostProcess.ExitCode)"
        }

        $scaleOutState = $null
        try { $scaleOutState = Get-ScaleOutDiagnostics -Url $scaleOutUrl } catch { }
        if ($null -ne $scaleOutState) {
            $currentRequests = @($scaleOutState.requests)
            if ($currentRequests.Count -gt 0) {
                $scaleOutSignature = (($currentRequests | ForEach-Object { "{0}:{1}:{2}" -f $_.requestId, $_.status, $_.fulfilledRuntimeInstanceId }) -join "|")
                if ($scaleOutSignature -ne $lastScaleOutSignature) {
                    $lastScaleOutSignature = $scaleOutSignature
                    foreach ($requestState in $currentRequests) {
                        Write-Host "[kubernetes-sdk-matrix] Scale-out request: id=$($requestState.requestId) status=$($requestState.status) provider=$($requestState.providerHint) target=$($requestState.requestedTargetInstanceCount) runtime=$($requestState.fulfilledRuntimeInstanceId)"
                    }
                }
            }
            $rejected = @($currentRequests | Where-Object { $_.status -eq "Rejected" })
            if ($rejected.Count -gt 0) {
                Write-KubernetesPoolDiagnostics -Namespace $namespaceName -PoolId $poolId -Kubectl $kubectl -ScaleOutUrl $scaleOutUrl
                $details = ($rejected | ConvertTo-Json -Depth 10 -Compress)
                throw "KubernetesPool scale-out request was rejected. $details"
            }
        }

        # Keep observing this exact pool after its first appearance. A Pod can start and
        # terminate immediately; waiting only for scale-out rejection hides that failure.
        $currentResources = Get-PoolResources -Namespace $namespaceName -PoolId $poolId -Kubectl $kubectl
        $currentPods = @($currentResources.Pods)
        if ($currentPods.Count -gt 0) {
            if (-not $poolObserved) {
                $poolObserved = $true
                foreach ($currentPod in $currentPods) {
                    Write-Host "[kubernetes-sdk-matrix] Current pool Pod observed: $($currentPod.metadata.name) phase=$($currentPod.status.phase)"
                }
            }
            $terminalPods = @($currentPods | Where-Object {
                $_.status.phase -eq "Failed" -or $_.status.phase -eq "Succeeded"
            })
            if ($terminalPods.Count -gt 0) {
                Write-KubernetesPoolDiagnostics -Namespace $namespaceName -PoolId $poolId -Kubectl $kubectl -ScaleOutUrl $scaleOutUrl
                $details = ($terminalPods | ForEach-Object {
                    [PSCustomObject]@{
                        podName = [string]$_.metadata.name
                        status = $_.status
                    }
                } | ConvertTo-Json -Depth 10 -Compress)
                throw "KubernetesPool Pod terminated before external SDK completion. $details"
            }
        }
        elseif (-not $poolObserved -and [DateTime]::UtcNow -ge $podDeadline) {
            Write-KubernetesPoolDiagnostics -Namespace $namespaceName -PoolId $poolId -Kubectl $kubectl -ScaleOutUrl $scaleOutUrl
            throw "No KubernetesPool Pod was created for current pool '$poolId' within 180 seconds after SDK submission."
        }

        if ([DateTime]::UtcNow -ge $clientDeadline) {
            Write-KubernetesPoolDiagnostics -Namespace $namespaceName -PoolId $poolId -Kubectl $kubectl -ScaleOutUrl $scaleOutUrl
            throw "External SDK client did not finish within 360 seconds."
        }
        Start-Sleep -Seconds 2
    }

    if ($clientProcess.ExitCode -ne 0) {
        Write-Host "================ EXTERNAL SDK CLIENT ================"
        if (Test-Path -LiteralPath $ClientDiagnosticLogPath -PathType Leaf) {
            Get-Content -LiteralPath $ClientDiagnosticLogPath | Out-Host
        }
        else {
            Write-Host "No client diagnostic log was produced."
        }
        Write-Host "====================================================="

        $failureScaleOutState = $null
        try { $failureScaleOutState = Get-ScaleOutDiagnostics -Url $scaleOutUrl } catch { }
        $failureScaleOutRequests = @($(if ($null -eq $failureScaleOutState) { @() } else { @($failureScaleOutState.requests) }))
        if (@($failureScaleOutRequests).Count -eq 0) {
            Write-Warning "The external SDK client failed before any scale-out request existed. KubernetesPool was not entered; skipping the large cluster resource dump."
            if ($null -ne $failureScaleOutState) {
                Write-Host "--- MATRIX SCALE-OUT STATE ---"
                ($failureScaleOutState | ConvertTo-Json -Depth 10) | Out-Host
            }
        }
        else {
            Write-KubernetesPoolDiagnostics -Namespace $namespaceName -PoolId $poolId -Kubectl $kubectl -ScaleOutUrl $scaleOutUrl
        }
        throw "External SDK client failed before KubernetesPool closure. ExitCode=$($clientProcess.ExitCode); DiagnosticLog=$ClientDiagnosticLogPath"
    }

    if (Test-Path -LiteralPath $ClientDiagnosticLogPath -PathType Leaf) {
        Write-Host "[kubernetes-sdk-matrix] External SDK client stage log:"
        Get-Content -LiteralPath $ClientDiagnosticLogPath | ForEach-Object { Write-Host "  $_" }
    }

    $resources = Get-PoolResources -Namespace $namespaceName -PoolId $poolId -Kubectl $kubectl
    $pods = @($resources.Pods)
    $services = @($resources.Services)
    if ($pods.Count -lt 1) { throw "External SDK execution completed but no KubernetesPool Pod was observed for pool '$poolId'." }
    if ($services.Count -lt 1) { throw "External SDK execution completed but no KubernetesPool Service was observed for pool '$poolId'." }

    $readyPodNames = @(
        $pods |
            Where-Object { @($_.status.conditions | Where-Object { $_.type -eq "Ready" -and $_.status -eq "True" }).Count -gt 0 } |
            ForEach-Object { $_.metadata.name }
    )
    if ($readyPodNames.Count -lt 1) { throw "No KubernetesPool Pod is Ready after external SDK execution." }
    $runtimeContainerImages = @($pods | ForEach-Object { $_.spec.containers } | ForEach-Object { $_.image } | Sort-Object -Unique)

    $kubernetesEvidence = [ordered]@{
        schemaVersion = 1
        scenarioId = $scenarioId
        status = "passed"
        topology = "kubernetes"
        runtimeProvider = "KubernetesPool"
        workerExecutionProvider = "TrustedProcess"
        submitMode = [string]$profile.submitMode
        scaleOutWatcherReady = $true
        runtimeImage = [string]$profile.runtimeImage
        immutableRuntimeImage = [bool]$profile.immutableRuntimeImage
        namespaceName = $namespaceName
        poolId = $poolId
        podNames = @($pods | ForEach-Object { $_.metadata.name })
        readyPodNames = $readyPodNames
        serviceNames = @($services | ForEach-Object { $_.metadata.name })
        runtimeContainerImages = $runtimeContainerImages
        authority = $profile.authority
        evidenceKinds = @(
            "external-sdk-publication",
            "publication-only-runtime-identity",
            "control-plane-worker-polling-disabled",
            "runtime-worker-polling-enabled",
            "scale-out-watcher-ready",
            "kubernetes-runtime-pool-ready",
            "published-function-terminal-result"
        )
        recordedAtUtc = [DateTime]::UtcNow.ToString("O")
    }
    Write-Utf8Json -Value $kubernetesEvidence -Path $KubernetesEvidencePath

    $verifier = Join-Path $PSScriptRoot "sdk_verifier.py"
    $verifierArgs = @(
        $verifier,
        "--profile", $ProfilePath,
        "--sdk-evidence", $SdkEvidencePath,
        "--kubernetes-evidence", $KubernetesEvidencePath
    )
    if ($RequireImmutableImage) { $verifierArgs += "--require-immutable-image" }
    Write-Host "[kubernetes-sdk-matrix] Verifying external SDK + live Kubernetes evidence..."
    & $python @verifierArgs
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." }

    if ($priorRecoveryEvidenceAvailable) {
        $closureVerifier = Join-Path $PSScriptRoot "closure_verifier.py"
        $closureArgs = @(
            $closureVerifier,
            "--execution-evidence", $priorExecutionEvidencePath,
            "--recovery-evidence", $priorRecoveryEvidencePath,
            "--sdk-profile", $ProfilePath,
            "--sdk-evidence", $SdkEvidencePath,
            "--sdk-kubernetes-evidence", $KubernetesEvidencePath
        )
        if ($RequireImmutableImage) { $closureArgs += "--require-immutable-image" }
        Write-Host "[kubernetes-sdk-matrix] Verifying final KubernetesPool branch closure against prior execution/recovery evidence..."
        & $python @closureArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Final closure verification failed even though all three evidence documents are present."
        }
    }
    else {
        Write-Warning "External SDK execution is GREEN, but final KubernetesPool closure is pending because hierarchical-recovery evidence is absent."
        Write-Host "FINAL CLOSURE PENDING - run run-kubernetes-pool-recovery.ps1 only if that evidence needs to be regenerated, then rerun this SDK runner."
    }
    $scenarioSucceeded = $true
}
finally {
    # Capture the current Pod before stopping its owning control plane or deleting resources.
    if (-not $scenarioSucceeded -and $null -ne $profile -and -not $script:poolDiagnosticsCollected) {
        Write-KubernetesPoolDiagnostics -Namespace ([string]$profile.namespaceName) `
            -PoolId ([string]$profile.poolId) -Kubectl $kubectl -ScaleOutUrl "http://127.0.0.1:8081/matrix/scaleout"
    }
    Stop-ProcessTree -Process $clientProcess
    Stop-ProcessTree -Process $hostProcess
    if ($hostOutputTasks.Count -gt 0) {
        try {
            if (-not [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$hostOutputTasks, 5000)) {
                Write-Warning "Control-plane log pipes did not close within the capture shutdown budget; files may be partial."
            }
        }
        catch { Write-Warning "Control-plane log capture ended with an error: $($_.Exception.Message)" }
    }
    foreach ($stream in @($hostStdoutStream, $hostStderrStream)) {
        if ($null -ne $stream) {
            try { $stream.Dispose() } catch { Write-Warning "Could not close a control-plane log stream." }
        }
    }
    if (-not $scenarioSucceeded -and $null -ne $profile) {
        try {
            $diagnosticsPath = Join-Path (Join-Path $stateRoot "diagnostics") ([string]$profile.poolId)
            New-Item -ItemType Directory -Path $diagnosticsPath -Force | Out-Null
            foreach ($sourcePath in @($hostStdoutPath, $hostStderrPath, $ClientDiagnosticLogPath, $localArtifactPath, (Join-Path $stateRoot "runtime-image-identity.json"))) {
                if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
                    Copy-Item -LiteralPath $sourcePath -Destination $diagnosticsPath -Force
                }
            }
            # Do not bundle the manifest or full profile: they can contain access-context credentials.
            $diagnosticsZip = Join-Path (Join-Path $stateRoot "diagnostics") ("{0}-{1}.zip" -f $profile.poolId, (Get-Date -Format "yyyyMMdd-HHmmss"))
            Compress-Archive -Path (Join-Path $diagnosticsPath "*") -DestinationPath $diagnosticsZip -Force
            Write-Host "[kubernetes-sdk-matrix] Failure bundle: $diagnosticsZip"
            foreach ($logPath in @($hostStdoutPath, $hostStderrPath)) {
                if (Test-Path -LiteralPath $logPath -PathType Leaf) {
                    Write-Host "--- $logPath (console tail only; bundle contains the complete file) ---"
                    Get-Content -LiteralPath $logPath -Tail 60 | Out-Host
                }
            }
        }
        catch { Write-Warning "Could not package failure diagnostics: $($_.Exception.Message)" }
    }
    if ($null -ne $profile) {
        Remove-PoolResources -Namespace ([string]$profile.namespaceName) -PoolId ([string]$profile.poolId) -Kubectl $kubectl
    }
    foreach ($entry in $previousVariables.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
}
