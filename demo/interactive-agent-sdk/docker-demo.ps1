$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    if (-not (Test-Path ".env")) {
        Copy-Item ".env.docker.example" ".env"
        throw "Created .env from .env.docker.example. Set OPENAI_API_KEY in .env, then run this command again."
    }

    docker compose up -d --build mongo redis runtime
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    docker compose --profile demo run --build --rm demo
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
