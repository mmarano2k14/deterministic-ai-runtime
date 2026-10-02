$ErrorActionPreference = "Stop"

$demoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$repoRoot = (Resolve-Path (Join-Path $demoRoot "..\..")).Path

$packageRoot = Join-Path $demoRoot ".packages"
$dotnetPackages = Join-Path $packageRoot "dotnet"
$typescriptPackages = Join-Path $packageRoot "typescript"
$pythonPackages = Join-Path $packageRoot "python"

New-Item -ItemType Directory -Force -Path $dotnetPackages | Out-Null
New-Item -ItemType Directory -Force -Path $typescriptPackages | Out-Null
New-Item -ItemType Directory -Force -Path $pythonPackages | Out-Null

Write-Host "[demo-bootstrap] Building local .NET SDK packages..."
dotnet pack `
    (Join-Path $repoRoot "implementations\dotnet\src\Multiplexed.AI.Sdk.Contracts\Multiplexed.AI.Sdk.Contracts.csproj") `
    -c Release `
    -p:PackageVersion=0.0.0-local `
    "-p:PackageOutputPath=$dotnetPackages"

dotnet pack `
    (Join-Path $repoRoot "implementations\dotnet\src\Multiplexed.AI.Sdk\Multiplexed.AI.Sdk.csproj") `
    -c Release `
    -p:PackageVersion=0.0.0-local `
    "-p:PackageOutputPath=$dotnetPackages"

Write-Host "[demo-bootstrap] Building local TypeScript SDK package..."
$nodeSdk = Join-Path $repoRoot "implementations\node\sdk"
Push-Location $nodeSdk
try {
    npm install
    npm run build
    npm pack --pack-destination $typescriptPackages
}
finally {
    Pop-Location
}

Write-Host "[demo-bootstrap] Building local Python SDK wheel..."
$pythonSdk = Join-Path $repoRoot "implementations\python\sdk"
python -m pip wheel $pythonSdk --no-deps -w $pythonPackages

Write-Host "[demo-bootstrap] Restoring .NET consumer from local SDK feed..."
dotnet restore `
    (Join-Path $demoRoot "dotnet\InteractiveAgentSdkDemo.csproj") `
    --configfile (Join-Path $demoRoot "NuGet.Config")

dotnet build `
    (Join-Path $demoRoot "dotnet\InteractiveAgentSdkDemo.csproj") `
    -c Release `
    --no-restore

Write-Host "[demo-bootstrap] Installing/building TypeScript consumer..."
$typescriptDemo = Join-Path $demoRoot "typescript"
Push-Location $typescriptDemo
try {
    npm install
    npm run build
}
finally {
    Pop-Location
}

Write-Host "[demo-bootstrap] Creating Python consumer environment..."
$pythonDemo = Join-Path $demoRoot "python"
$venv = Join-Path $pythonDemo ".venv"
python -m venv $venv

if ($IsWindows -or $env:OS -eq "Windows_NT") {
    $venvPython = Join-Path $venv "Scripts\python.exe"
}
else {
    $venvPython = Join-Path $venv "bin\python"
}

& $venvPython -m pip install --upgrade pip
& $venvPython -m pip install `
    --find-links $pythonPackages `
    -e $pythonDemo

Write-Host ""
Write-Host "[demo-bootstrap] READY"
Write-Host "Run:"
Write-Host "  .\demo\interactive-agent-sdk\run.ps1"
