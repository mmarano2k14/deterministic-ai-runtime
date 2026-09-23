param(
    [string]$MongoExecutable = "mongod",
    [string]$RedisExecutable = "redis-server",
    [string]$DotNetExecutable = "dotnet",
    [string]$NodeExecutable = "node",
    [string]$PythonExecutable = "python",
    [switch]$InfrastructureAlreadyRunning,
    [ValidatePattern('^(all|core-(dotnet|typescript|python)-client-(dotnet|typescript|python)-worker)$')]
    [string]$CoreScenario = "all",
    [switch]$CoreOnly,
    [switch]$WatchOnly,
    [switch]$ControlOnly
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "process-support.ps1")
if ((@($CoreOnly, $WatchOnly, $ControlOnly) | Where-Object { $_ }).Count -gt 1) {
    throw "-CoreOnly, -WatchOnly and -ControlOnly are mutually exclusive."
}
if ($CoreScenario -ne "all" -and -not $CoreOnly) {
    throw "A selected core scenario requires -CoreOnly; it is not a complete local matrix run."
}
if (($WatchOnly -or $ControlOnly) -and $CoreScenario -ne "all") {
    throw "-WatchOnly/-ControlOnly cannot be combined with a selected -CoreScenario."
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
$matrix = Join-Path $repo "implementations\matrix"
$state = Join-Path $matrix ".state"
$runtimeOut = Join-Path $state "runtime"
$workerOut = Join-Path $state "worker-dotnet"
$effectProbeOut = Join-Path $state "mcp-effect-server"
$mongoData = Join-Path $state "mongo"
$manifest = Join-Path $state "runtime-manifest.json"
$sampleRoot = Join-Path $state "samples"
$sampleDotNet = Join-Path $sampleRoot "dotnet"
# Some .NET 10 CLI builds misparse a publish output whose final component is dotnet.
# Keep the client-visible layout, but compile into a fresh, non-reserved destination.
$sampleDotNetPublish = Join-Path $state ("sample-dotnet-publish-{0}" -f [Guid]::NewGuid().ToString("N"))
$sampleTypeScript = Join-Path $sampleRoot "typescript"
$samplePython = Join-Path $sampleRoot "python"
New-Item -ItemType Directory -Force -Path $state,$runtimeOut,$workerOut,$effectProbeOut,$mongoData,$sampleDotNet,$sampleDotNetPublish,$sampleTypeScript,$samplePython | Out-Null
Remove-Item $manifest -Force -ErrorAction SilentlyContinue
$runId = "local-{0}-{1}" -f (Get-Date -Format "yyyyMMdd-HHmmss"), ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$logRoot = Join-Path (Join-Path $state "diagnostics") $runId
$clientLogs = Join-Path $logRoot "clients"
New-Item -ItemType Directory -Path $clientLogs -Force | Out-Null
Write-Host "[local-sdk-matrix] Run diagnostics: $logRoot"

function Resolve-Tool([string]$name) {
    $candidates = if ($env:OS -eq "Windows_NT" -and $name -eq "npm") { @("npm.cmd", "npm.exe", "npm") } else { @($name) }
    foreach ($candidate in $candidates) {
        $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { return $command.Source }
    }
    throw "Required command was not found on PATH: $name"
}

function Resolve-PythonExecutable([string]$name) {
    $launcher = Resolve-Tool $name
    if ($env:OS -ne "Windows_NT") { return $launcher }

    $reported = $null
    try {
        $reported = (& $launcher -c "import os, sys; print(os.path.realpath(sys.executable))" 2>$null | Select-Object -Last 1)
    } catch {
        $reported = $null
    }

    if (-not [string]::IsNullOrWhiteSpace($reported)) {
        $reported = $reported.Trim()
        if ((Test-Path -LiteralPath $reported -PathType Leaf) -and $reported -notmatch "\\WindowsApps\\python(?:3(?:\.exe)?|\.exe)?$") {
            return (Resolve-Path -LiteralPath $reported).Path
        }
    }

    $py = Get-Command "py.exe" -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($py) {
        try {
            $reported = (& $py.Source -c "import os, sys; print(os.path.realpath(sys.executable))" 2>$null | Select-Object -Last 1)
            if (-not [string]::IsNullOrWhiteSpace($reported)) {
                $reported = $reported.Trim()
                if (Test-Path -LiteralPath $reported -PathType Leaf) {
                    return (Resolve-Path -LiteralPath $reported).Path
                }
            }
        } catch {
        }
    }

    if ($launcher -match "\\WindowsApps\\python(?:3(?:\.exe)?|\.exe)?$") {
        throw "Python resolved to the Windows Apps execution alias rather than a readable interpreter. Pass -PythonExecutable with the path returned by: py -c `"import sys; print(sys.executable)`""
    }

    return $launcher
}

$dotnet = Resolve-Tool $DotNetExecutable
$node = Resolve-Tool $NodeExecutable
$python = Resolve-PythonExecutable $PythonExecutable
$npm = Resolve-Tool "npm"
$mongo = $null
$redis = $null
if (-not $InfrastructureAlreadyRunning) {
    $mongo = Resolve-Tool $MongoExecutable
    $redis = Resolve-Tool $RedisExecutable
}

Push-Location $repo
$captures = @()
$scenarioSucceeded = $false
# Pass absolute PublishDir properties explicitly instead of relying on -o forwarding.
# Keep the CLI output-path marker and all existing client-visible artifact destinations.
try {
    Invoke-LocalChecked -Executable $dotnet -Arguments @("publish", ".\implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Multiplexed.AI.McpServer.Host.csproj", "-c", "Release", "-p:PublishDir=$runtimeOut", "-p:_CommandLineDefinedOutputPath=true") `
        -LogPath (Join-Path $logRoot "publish-runtime.log")
    Invoke-LocalChecked -Executable $dotnet -Arguments @("publish", ".\implementations\dotnet\workers\Multiplexed.AI.HostedInvocation.DotNetWorker\Multiplexed.AI.HostedInvocation.DotNetWorker.csproj", "-c", "Release", "-p:PublishDir=$workerOut", "-p:_CommandLineDefinedOutputPath=true") `
        -LogPath (Join-Path $logRoot "publish-worker-dotnet.log")
    Invoke-LocalChecked -Executable $dotnet -Arguments @("publish", ".\implementations\sdk\samples\mcp-effect-server\Multiplexed.AI.Samples.McpEffectServer\Multiplexed.AI.Samples.McpEffectServer.csproj", "-c", "Release", "-p:PublishDir=$effectProbeOut", "-p:_CommandLineDefinedOutputPath=true") `
        -LogPath (Join-Path $logRoot "publish-mcp-effect-server.log")
    # BEGIN LOCAL DOTNET SAMPLE PUBLISH
    Write-Host "[local-sdk-matrix] .NET sample publish staging: $sampleDotNetPublish"
    Invoke-LocalChecked -Executable $dotnet -Arguments @("publish", ".\implementations\sdk\samples\published-functions\dotnet\Multiplexed.AI.Samples.PublishedFunctions\Multiplexed.AI.Samples.PublishedFunctions.csproj", "-c", "Release", "-p:PublishDir=$sampleDotNetPublish", "-p:_CommandLineDefinedOutputPath=true") `
        -LogPath (Join-Path $logRoot "publish-sample-dotnet.log")
    Invoke-LocalChecked -Executable $dotnet -Arguments @("publish", ".\implementations\sdk\samples\published-functions\dotnet\Multiplexed.AI.Samples.PublishedPackagedFunctions\Multiplexed.AI.Samples.PublishedPackagedFunctions.csproj", "-c", "Release", "-p:PublishDir=$sampleDotNetPublish", "-p:_CommandLineDefinedOutputPath=true") `
        -LogPath (Join-Path $logRoot "publish-sample-packaged-dotnet.log")

    # Verify the complete newly published sample set before touching the client layout.
    # Existing DLLs under samples/dotnet must never stand in for a missing new output.
    foreach ($sampleAssembly in @(
        "Multiplexed.AI.Samples.PublishedFunctions.dll",
        "Multiplexed.AI.Samples.PublishedPackagedFunctions.dll",
        "Multiplexed.AI.Samples.PublishedDependency.dll"
    )) {
        $publishedSample = Join-Path $sampleDotNetPublish $sampleAssembly
        if (-not (Test-Path -LiteralPath $publishedSample -PathType Leaf) -or
            (Get-Item -LiteralPath $publishedSample).Length -eq 0) {
            throw "Required newly published sample assembly is missing or empty: $publishedSample"
        }
    }
    Get-ChildItem -LiteralPath $sampleDotNetPublish -Force |
        Copy-Item -Destination $sampleDotNet -Recurse -Force -ErrorAction Stop
    Write-Host "[local-sdk-matrix] .NET sample artifacts staged: $sampleDotNet"
    # END LOCAL DOTNET SAMPLE PUBLISH
    Copy-Item ".\implementations\sdk\samples\published-functions\typescript\functions.ts" (Join-Path $sampleTypeScript "functions.ts") -Force
    Copy-Item ".\implementations\sdk\samples\published-functions\python\functions.py" (Join-Path $samplePython "functions.py") -Force
    Invoke-LocalChecked -Executable $dotnet -Arguments @("build", ".\implementations\matrix\clients\dotnet\Multiplexed.AI.Matrix.DotNetClient\Multiplexed.AI.Matrix.DotNetClient.csproj", "-c", "Release") `
        -LogPath (Join-Path $logRoot "build-client-dotnet.log")
    Push-Location ".\implementations\node\sdk"
    try {
        Invoke-LocalChecked -Executable $npm -Arguments @("run", "build") `
            -LogPath (Join-Path $logRoot "build-sdk-typescript.log")
    }
    finally { Pop-Location }

    if (-not $InfrastructureAlreadyRunning) {
        $mongoCapture = Start-LocalLoggedProcess -Executable $mongo `
            -Arguments @("--dbpath", $mongoData, "--port", "27017", "--bind_ip", "127.0.0.1", "--quiet") `
            -WorkingDirectory $repo -LogPrefix (Join-Path $logRoot "mongo")
        $captures += $mongoCapture
        $redisCapture = Start-LocalLoggedProcess -Executable $redis `
            -Arguments @("--port", "6379", "--save", "", "--appendonly", "no") `
            -WorkingDirectory $repo -LogPrefix (Join-Path $logRoot "redis")
        $captures += $redisCapture
        Start-Sleep -Seconds 2
        if ($mongoCapture.Process.HasExited -or $redisCapture.Process.HasExited) {
            throw "A matrix-owned infrastructure process exited during startup; inspect $logRoot."
        }
    }

    $dotnetVersion = (Invoke-LocalChecked -Executable $dotnet -Arguments @("--list-runtimes") | Select-String '^Microsoft.NETCore.App\s+(10\.0\.\d+)' | Select-Object -Last 1).Matches.Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($dotnetVersion)) { throw ".NET 10.0.x runtime was not found." }
    $nodeVersion = (Invoke-LocalChecked -Executable $node -Arguments @("-p", "process.versions.node")).Trim()
    $pythonVersion = (Invoke-LocalChecked -Executable $python -Arguments @("-c", "import platform; print(platform.python_version())")).Trim()

    $env:ASPNETCORE_URLS = "http://127.0.0.1:8081"
    $env:AiMcpHost__Mode = "ControlPlaneWithLocalRuntimeInstances"
    $env:AiMcpHost__Port = "8081"
    $env:AiMcpHost__ControlPlaneId = "matrix-control"
    $env:AiEngine__ControlPlane__ControlPlaneId = "matrix-control"
    $env:AiEngine__ControlPlane__RedisDiscoveryKey = "multiplexed-ai:matrix-control"
    $env:AiMcpHost__EnableSharedQueuePump = "true"
    $env:AiSharedQueueBackgroundService__Enabled = "true"
    $env:AiSharedQueuePump__Enabled = "true"
    $env:AiLocalRuntimeInstancePool__Enabled = "true"
    $env:AiLocalRuntimeInstancePool__InstanceCount = "2"
    $env:ConnectionStrings__Redis = "localhost:6379"
    $env:ConnectionStrings__Mongo = "mongodb://localhost:27017"
    $env:Mongo__DatabaseName = "multiplexed-ai-matrix"

    # execution.watch() projects durable Decision Ledger events produced by ProcessHost runtime instances.
    # The control plane and runtime instances are separate processes, so an in-memory ledger would split the
    # event history per process and make Watch wait forever after its initial snapshot. Keep the historical
    # local/core matrix behavior unchanged, but require one shared Mongo-backed ledger for Watch-only E2E.
    if ($WatchOnly -or $ControlOnly) {
        $env:AiDecisionLedger__Provider = "mongo"
        $ledgerReason = if ($WatchOnly) { "Watch" } else { "Control/Replay" }
        Write-Host "[local-sdk-matrix] $ledgerReason Decision Ledger provider=mongo (shared across control plane and ProcessHost runtimes)."
    }
    else {
        $env:AiDecisionLedger__Provider = "inmemory"
    }
    $env:AiEngine__Snapshots__Enabled = "true"
    $env:AiEngine__Snapshots__Mongo__Enabled = "true"
    $env:AiEngine__Snapshots__Mongo__ConnectionString = "mongodb://localhost:27017"
    $env:AiEngine__Snapshots__Mongo__DatabaseName = "multiplexed-ai-matrix"

    # Keep local/process on the same replay-safe payload provider as docker/process.
    $env:AiEngine__PayloadStore__Enabled = "true"
    $env:AiEngine__PayloadStore__Provider = "mongo-redis"
    $env:AiEngine__PayloadStore__RequireReplaySafePayloads = "true"
    $env:AiEngine__PayloadStore__Mongo__Enabled = "true"
    $env:AiEngine__PayloadStore__Mongo__ConnectionString = "mongodb://localhost:27017"
    $env:AiEngine__PayloadStore__Mongo__DatabaseName = "multiplexed-ai-matrix"
    $env:AiEngine__PayloadStore__RedisCache__Enabled = "true"

    # Preserve the runtime aliases used by process-provider child composition.
    $env:AiPayloadStore__Enabled = "true"
    $env:AiPayloadStore__Provider = "mongo-redis"
    $env:AiPayloadStore__RequireReplaySafePayloads = "true"
    $env:AiPayloadStore__Mongo__Enabled = "true"
    $env:AiPayloadStore__Mongo__ConnectionString = "mongodb://localhost:27017"
    $env:AiPayloadStore__Mongo__DatabaseName = "multiplexed-ai-matrix"
    $env:AiPayloadStore__RedisCache__Enabled = "true"

    $env:AiChildDagComposition__Enabled = "true"
    $env:OPENAI_API_KEY = "matrix-not-used"

    $env:AiHostedInvocation__Enabled = "true"
    # Local children share the existing host-owned invocation polling/reconciliation services.
    $env:AiHostedInvocation__EnableLocalWorkerProfiles = "true"
    $env:AiHostedInvocation__EnableWorkerPolling = "true"
    $env:AiHostedInvocation__EnableDagReconciliation = "true"
    $env:AiHostedInvocation__TenantId = "matrix-tenant"
    $env:AiHostedInvocation__TenantGroupId = "matrix-group"
    $env:AiHostedInvocation__ControlPlaneId = "matrix-control"
    $env:AiHostedInvocation__DotNet__Reference = "matrix-dotnet"
    $env:AiHostedInvocation__DotNet__RuntimeVersion = $dotnetVersion
    $env:AiHostedInvocation__DotNet__ExecutablePath = $dotnet
    $env:AiHostedInvocation__DotNet__WorkerPath = Join-Path $workerOut "Multiplexed.AI.HostedInvocation.DotNetWorker.dll"
    $env:AiHostedInvocation__DotNet__WorkerDepsPath = Join-Path $workerOut "Multiplexed.AI.HostedInvocation.DotNetWorker.deps.json"
    $env:AiHostedInvocation__DotNet__WorkerRuntimeConfigPath = Join-Path $workerOut "Multiplexed.AI.HostedInvocation.DotNetWorker.runtimeconfig.json"
    $env:AiHostedInvocation__DotNet__WorkingDirectory = $workerOut
    $env:AiHostedInvocation__TypeScript__Reference = "matrix-typescript"
    $env:AiHostedInvocation__TypeScript__RuntimeVersion = $nodeVersion
    $env:AiHostedInvocation__TypeScript__ExecutablePath = $node
    $env:AiHostedInvocation__TypeScript__WorkerPath = Join-Path $repo "implementations\node\workers\hosted_invocation\worker.mjs"
    $env:AiHostedInvocation__TypeScript__WorkingDirectory = Join-Path $repo "implementations\node\workers\hosted_invocation"
    $env:AiHostedInvocation__Python__Reference = "matrix-python"
    $env:AiHostedInvocation__Python__RuntimeVersion = $pythonVersion
    $env:AiHostedInvocation__Python__ExecutablePath = $python
    $env:AiHostedInvocation__Python__WorkerPath = Join-Path $repo "implementations\python\workers\hosted_invocation\worker.py"
    $env:AiHostedInvocation__Python__WorkingDirectory = Join-Path $repo "implementations\python\workers\hosted_invocation"

    $env:AiMatrixHarness__Enabled = "true"
    $env:AiMatrixHarness__BearerToken = "matrix-e2e-token"
    $env:AiMatrixHarness__UserId = "matrix-user"
    $env:AiMatrixHarness__TenantId = "matrix-tenant"
    $env:AiMatrixHarness__TenantGroupId = "matrix-group"
    $env:AiMatrixHarness__Project = "matrix"
    $env:AiMatrixHarness__Namespace = "default"
    $env:AiMatrixHarness__PublicEndpoint = "http://localhost:8081/mcp"
    $env:AiMatrixHarness__Topology = "local"
    $env:AiMatrixHarness__Provider = "ProcessHostPool"
    $env:AiMatrixHarness__RuntimeProvider = "ProcessHostPool"
    $env:AiMatrixHarness__WorkerExecutionProvider = "TrustedProcess"
    $env:AiMatrixHarness__ManifestPath = $manifest
    $env:AiMatrixHarness__EffectProbeMcpEndpoint = "http://127.0.0.1:8090/mcp"
    $env:AiMatrixHarness__EffectProbeStateEndpoint = "http://localhost:8090/state"
    $env:AiMatrixHarness__EffectEvidenceEndpoint = "http://localhost:8081/matrix/mcp-effect-evidence"
    $env:AiMatrixHarness__RecoveryEndpoint = "http://localhost:8081/matrix/recovery"
    $env:AiMatrixHarness__JournalResultAcceptanceEndpoint = "http://localhost:8081/matrix/journal-result-acceptance"
    $env:MATRIX_SAMPLE_ROOT = $sampleRoot
    $env:MATRIX_CLIENT_LOG_DIR = $clientLogs
    $env:MATRIX_DOTNET_EXECUTABLE = $dotnet
    $env:MATRIX_NODE_EXECUTABLE = $node

    # These are the only background application processes owned by this runner.
    $probeCapture = Start-LocalLoggedProcess -Executable $dotnet `
        -Arguments @((Join-Path $effectProbeOut "Multiplexed.AI.Samples.McpEffectServer.dll"), "--urls", "http://127.0.0.1:8090") `
        -WorkingDirectory $repo -LogPrefix (Join-Path $logRoot "mcp-effect-server")
    $captures += $probeCapture
    Wait-LocalHttpReady -Url "http://127.0.0.1:8090/health" -TimeoutSeconds 30 -Process $probeCapture.Process

    $hostCapture = Start-LocalLoggedProcess -Executable $dotnet `
        -Arguments @((Join-Path $runtimeOut "Multiplexed.AI.McpServer.Host.dll"), "--Multiplexed.Rbac.Core:Project=matrix") `
        -WorkingDirectory $repo -LogPrefix (Join-Path $logRoot "control-plane")
    $captures += $hostCapture
    Write-Host "[local-sdk-matrix] Full control-plane output: $($hostCapture.StdoutPath)"
    Write-Host "[local-sdk-matrix] Full control-plane errors: $($hostCapture.StderrPath)"
    Wait-LocalHttpReady -Url "http://127.0.0.1:8081/health" -TimeoutSeconds 120 `
        -Process $hostCapture.Process -ManifestPath $manifest

    $manifestDocument = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $ttl = 0
    if (-not [int]::TryParse([string]$manifestDocument.executionContextTtlSeconds, [ref]$ttl) -or $ttl -le 0) {
        throw "The freshly built local runtime must expose a positive executionContextTtlSeconds."
    }
    Write-Host "[local-sdk-matrix] Execution context snapshot TTL=$($ttl)s"
    Write-Host "[local-sdk-matrix] Local hosted profiles, worker polling and DAG reconciliation enabled."

    if ($WatchOnly) {
        Invoke-LocalChecked -Executable $python -Arguments @(
            ".\implementations\matrix\watch_matrix.py", "run", "--scenario", "all",
            "--manifest", $manifest, "--no-build"
        )
        Write-Host "[local-sdk-matrix] Real MCP/HTTP execution.watch() E2E passed for .NET, TypeScript and Python external SDKs."
    }
    elseif ($ControlOnly) {
        Invoke-LocalChecked -Executable $python -Arguments @(
            ".\implementations\matrix\control_matrix.py", "run", "--scenario", "all",
            "--manifest", $manifest, "--no-build"
        )
        Write-Host "[local-sdk-matrix] Real MCP/HTTP pause/resume/input/replay E2E passed for .NET, TypeScript and Python external SDKs."
    }
    else {
        Invoke-LocalChecked -Executable $python -Arguments @(
            ".\implementations\matrix\core_matrix.py", "run", "--topology", "local",
            "--scenario", $CoreScenario, "--manifest", $manifest, "--no-build"
        )
        if ($CoreOnly) {
            Write-Host "[local-sdk-matrix] Selected core execution passed: $CoreScenario. Feature matrix was not run."
        }
        else {
            Invoke-LocalChecked -Executable $python -Arguments @(
                ".\implementations\matrix\feature_matrix.py", "run", "--scenario", "all",
                "--manifest", $manifest, "--no-build"
            )
            Write-Host "[local-sdk-matrix] Local core and supported feature scenarios passed."
        }
    }
    $scenarioSucceeded = $true
}
catch {
    $runnerFailure = $_
    # Save the original runner failure without exporting credentials or the complete manifest.
    try {
        [System.IO.File]::WriteAllText((Join-Path $logRoot "runner-error.txt"),
            ($runnerFailure | Out-String), [System.Text.UTF8Encoding]::new($false))
    }
    catch { Write-Warning "Could not save runner failure text." }
    throw $runnerFailure
}
finally {
    [array]::Reverse($captures)
    foreach ($capture in $captures) { Complete-LocalCapture -Capture $capture }
    if (-not $scenarioSucceeded) {
        try {
            $bundle = Join-Path (Join-Path $state "diagnostics") ($runId + ".zip")
            Compress-Archive -Path (Join-Path $logRoot "*") -DestinationPath $bundle -Force
            Write-Host "[local-sdk-matrix] Failure bundle: $bundle"
            # Console tails are convenience only; the archive contains the full captured streams.
            foreach ($log in @(Get-ChildItem -LiteralPath $clientLogs -Filter "*.log" -File)) {
                Write-Host "--- $($log.FullName) (tail; full file is in the bundle) ---"
                Get-Content -LiteralPath $log.FullName -Tail 70 | Out-Host
            }
            $hostErrors = Join-Path $logRoot "control-plane.stderr.log"
            if (Test-Path -LiteralPath $hostErrors -PathType Leaf) {
                Write-Host "--- control-plane.stderr.log ---"
                Get-Content -LiteralPath $hostErrors -Tail 40 | Out-Host
            }
        }
        catch { Write-Warning "Local diagnostic packaging failed: $($_.Exception.Message)" }
    }
    # The staged client layout and logs are retained; only this run's scratch output is removed.
    try {
        if (Test-Path -LiteralPath $sampleDotNetPublish -PathType Container) {
            Remove-Item -LiteralPath $sampleDotNetPublish -Recurse -Force -ErrorAction Stop
        }
    }
    catch { Write-Warning "Could not remove sample publish scratch directory: $sampleDotNetPublish" }
    Pop-Location
}
