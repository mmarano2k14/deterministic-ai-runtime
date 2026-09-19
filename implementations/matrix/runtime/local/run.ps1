param(
    [string]$MongoExecutable = "mongod",
    [string]$RedisExecutable = "redis-server",
    [string]$DotNetExecutable = "dotnet",
    [string]$NodeExecutable = "node",
    [string]$PythonExecutable = "python",
    [switch]$InfrastructureAlreadyRunning
)

$ErrorActionPreference = "Stop"
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
$sampleTypeScript = Join-Path $sampleRoot "typescript"
$samplePython = Join-Path $sampleRoot "python"
New-Item -ItemType Directory -Force -Path $state,$runtimeOut,$workerOut,$effectProbeOut,$mongoData,$sampleDotNet,$sampleTypeScript,$samplePython | Out-Null
Remove-Item $manifest -Force -ErrorAction SilentlyContinue

function Resolve-Tool([string]$name) {
    $candidates = if ($IsWindows -and $name -eq "npm") { @("npm.cmd", "npm.exe", "npm") } else { @($name) }
    foreach ($candidate in $candidates) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($command) { return $command.Source }
    }
    throw "Required command was not found on PATH: $name"
}

$dotnet = Resolve-Tool $DotNetExecutable
$node = Resolve-Tool $NodeExecutable
$python = Resolve-Tool $PythonExecutable
$npm = Resolve-Tool "npm"
$mongo = $null
$redis = $null
if (-not $InfrastructureAlreadyRunning) {
    $mongo = Resolve-Tool $MongoExecutable
    $redis = Resolve-Tool $RedisExecutable
}

Push-Location $repo
$processes = @()
try {
    & $dotnet publish ".\implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Multiplexed.AI.McpServer.Host.csproj" -c Release -o $runtimeOut
    & $dotnet publish ".\implementations\dotnet\workers\Multiplexed.AI.HostedInvocation.DotNetWorker\Multiplexed.AI.HostedInvocation.DotNetWorker.csproj" -c Release -o $workerOut
    & $dotnet publish ".\implementations\sdk\samples\mcp-effect-server\Multiplexed.AI.Samples.McpEffectServer\Multiplexed.AI.Samples.McpEffectServer.csproj" -c Release -o $effectProbeOut
    & $dotnet publish ".\implementations\sdk\samples\published-functions\dotnet\Multiplexed.AI.Samples.PublishedFunctions\Multiplexed.AI.Samples.PublishedFunctions.csproj" -c Release -o $sampleDotNet
    & $dotnet publish ".\implementations\sdk\samples\published-functions\dotnet\Multiplexed.AI.Samples.PublishedPackagedFunctions\Multiplexed.AI.Samples.PublishedPackagedFunctions.csproj" -c Release -o $sampleDotNet
    Copy-Item ".\implementations\sdk\samples\published-functions\typescript\functions.ts" (Join-Path $sampleTypeScript "functions.ts") -Force
    Copy-Item ".\implementations\sdk\samples\published-functions\python\functions.py" (Join-Path $samplePython "functions.py") -Force
    & $dotnet build ".\implementations\matrix\clients\dotnet\Multiplexed.AI.Matrix.DotNetClient\Multiplexed.AI.Matrix.DotNetClient.csproj" -c Release
    Push-Location ".\implementations\node\sdk"
    & $npm run build
    Pop-Location

    if (-not $InfrastructureAlreadyRunning) {
        $processes += Start-Process -FilePath $mongo -ArgumentList @("--dbpath", $mongoData, "--port", "27017", "--bind_ip", "127.0.0.1", "--quiet") -PassThru -WindowStyle Hidden
        $processes += Start-Process -FilePath $redis -ArgumentList @("--port", "6379", "--save", "", "--appendonly", "no") -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 2
    }

    $dotnetVersion = (& $dotnet --list-runtimes | Select-String '^Microsoft.NETCore.App\s+(10\.0\.\d+)' | Select-Object -Last 1).Matches.Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($dotnetVersion)) { throw ".NET 10.0.x runtime was not found." }
    $nodeVersion = (& $node -p "process.versions.node").Trim()
    $pythonVersion = (& $python -c "import platform; print(platform.python_version())").Trim()

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

    $effectProbe = Start-Process -FilePath $dotnet -ArgumentList @((Join-Path $effectProbeOut "Multiplexed.AI.Samples.McpEffectServer.dll"), "--urls", "http://127.0.0.1:8090") -PassThru -NoNewWindow
    $processes += $effectProbe
    $probeDeadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $probeDeadline) {
        if ($effectProbe.HasExited) { throw "MCP effect sample server exited before becoming ready." }
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:8090/health" -TimeoutSec 2 | Out-Null
            break
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if ($effectProbe.HasExited) { throw "MCP effect sample server exited before runtime startup." }

    $runtime = Start-Process -FilePath $dotnet -ArgumentList @((Join-Path $runtimeOut "Multiplexed.AI.McpServer.Host.dll"), "--Multiplexed.Rbac.Core:Project=matrix") -PassThru -NoNewWindow
    $processes += $runtime

    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        if ($runtime.HasExited) { throw "Runtime exited before becoming ready." }
        if (Test-Path $manifest) {
            try {
                Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:8081/health" -TimeoutSec 2 | Out-Null
                break
            } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not (Test-Path $manifest)) { throw "Runtime manifest was not produced." }

    & $python ".\implementations\matrix\core_matrix.py" run --topology local --scenario all --manifest $manifest --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $python ".\implementations\matrix\feature_matrix.py" run --scenario all --manifest $manifest --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    [array]::Reverse($processes)
    foreach ($process in $processes) {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Pop-Location
}
