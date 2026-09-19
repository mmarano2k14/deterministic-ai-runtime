#!/bin/sh
set -eu

resolve_executable() {
  candidate="$(command -v "$1")"
  resolved="$(readlink -f "$candidate")"
  [ -n "$resolved" ] && [ -f "$resolved" ] && [ -x "$resolved" ] || {
    echo "Unable to resolve a canonical executable for '$1'." >&2
    exit 1
  }
  printf '%s\n' "$resolved"
}

DOTNET_EXE="$(resolve_executable dotnet)"
NODE_EXE="$(resolve_executable node)"
PYTHON_EXE="$(resolve_executable python3)"
DOTNET_VERSION="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" && $2 ~ /^10\.0\.[0-9]+$/ { version=$2 } END { print version }')"
[ -n "$DOTNET_VERSION" ] || { echo ".NET 10.0.x runtime was not found." >&2; exit 1; }
NODE_VERSION="$(node -p 'process.versions.node')"
PYTHON_VERSION="$(python3 -c 'import platform; print(platform.python_version())')"

export AiMcpHost__Mode="ControlPlaneWithLocalRuntimeInstances"
export AiMcpHost__Port="8080"
export AiMcpHost__ControlPlaneId="matrix-control"
export AiEngine__ControlPlane__ControlPlaneId="matrix-control"
export AiEngine__ControlPlane__RedisDiscoveryKey="multiplexed-ai:matrix-control"
export AiMcpHost__EnableSharedQueuePump="true"
export AiSharedQueueBackgroundService__Enabled="true"
export AiSharedQueuePump__Enabled="true"
export AiLocalRuntimeInstancePool__Enabled="true"
export AiLocalRuntimeInstancePool__InstanceCount="2"
export ConnectionStrings__Redis="redis:6379"
export ConnectionStrings__Mongo="mongodb://mongo:27017"
export Mongo__DatabaseName="multiplexed-ai-matrix"
export AiEngine__Snapshots__Enabled="true"
export AiEngine__Snapshots__Mongo__Enabled="true"
export AiEngine__Snapshots__Mongo__ConnectionString="mongodb://mongo:27017"
export AiEngine__Snapshots__Mongo__DatabaseName="multiplexed-ai-matrix"

# Production-like replay-safe payload storage. MongoDB is the durable source of truth
# and Redis is only the bounded cache used by the existing runtime provider.
export AiEngine__PayloadStore__Enabled="true"
export AiEngine__PayloadStore__Provider="mongo-redis"
export AiEngine__PayloadStore__RequireReplaySafePayloads="true"
export AiEngine__PayloadStore__Mongo__Enabled="true"
export AiEngine__PayloadStore__Mongo__ConnectionString="mongodb://mongo:27017"
export AiEngine__PayloadStore__Mongo__DatabaseName="multiplexed-ai-matrix"
export AiEngine__PayloadStore__RedisCache__Enabled="true"

# Preserve the runtime aliases used by process-provider child composition.
export AiPayloadStore__Enabled="true"
export AiPayloadStore__Provider="mongo-redis"
export AiPayloadStore__RequireReplaySafePayloads="true"
export AiPayloadStore__Mongo__Enabled="true"
export AiPayloadStore__Mongo__ConnectionString="mongodb://mongo:27017"
export AiPayloadStore__Mongo__DatabaseName="multiplexed-ai-matrix"
export AiPayloadStore__RedisCache__Enabled="true"

export AiChildDagComposition__Enabled="true"
export OPENAI_API_KEY="matrix-not-used"

export AiHostedInvocation__Enabled="true"
export AiHostedInvocation__TenantId="matrix-tenant"
export AiHostedInvocation__TenantGroupId="matrix-group"
export AiHostedInvocation__ControlPlaneId="matrix-control"
export AiHostedInvocation__MaxConcurrentProcesses="6"
export AiHostedInvocation__DotNet__Reference="matrix-dotnet"
export AiHostedInvocation__DotNet__RuntimeVersion="$DOTNET_VERSION"
export AiHostedInvocation__DotNet__ExecutablePath="$DOTNET_EXE"
export AiHostedInvocation__DotNet__WorkerPath="/app/workers/dotnet/Multiplexed.AI.HostedInvocation.DotNetWorker.dll"
export AiHostedInvocation__DotNet__WorkerDepsPath="/app/workers/dotnet/Multiplexed.AI.HostedInvocation.DotNetWorker.deps.json"
export AiHostedInvocation__DotNet__WorkerRuntimeConfigPath="/app/workers/dotnet/Multiplexed.AI.HostedInvocation.DotNetWorker.runtimeconfig.json"
export AiHostedInvocation__DotNet__WorkingDirectory="/app/workers/dotnet"
export AiHostedInvocation__TypeScript__Reference="matrix-typescript"
export AiHostedInvocation__TypeScript__RuntimeVersion="$NODE_VERSION"
export AiHostedInvocation__TypeScript__ExecutablePath="$NODE_EXE"
export AiHostedInvocation__TypeScript__WorkerPath="/app/workers/typescript/worker.mjs"
export AiHostedInvocation__TypeScript__WorkingDirectory="/app/workers/typescript"
export AiHostedInvocation__Python__Reference="matrix-python"
export AiHostedInvocation__Python__RuntimeVersion="$PYTHON_VERSION"
export AiHostedInvocation__Python__ExecutablePath="$PYTHON_EXE"
export AiHostedInvocation__Python__WorkerPath="/app/workers/python/worker.py"
export AiHostedInvocation__Python__WorkingDirectory="/app/workers/python"

export AiMatrixHarness__Enabled="true"
export AiMatrixHarness__BearerToken="matrix-e2e-token"
export AiMatrixHarness__UserId="matrix-user"
export AiMatrixHarness__TenantId="matrix-tenant"
export AiMatrixHarness__TenantGroupId="matrix-group"
export AiMatrixHarness__Project="matrix"
export AiMatrixHarness__Namespace="default"
export AiMatrixHarness__PublicEndpoint="http://runtime:8080/mcp"
export AiMatrixHarness__Topology="docker"
export AiMatrixHarness__Provider="ProcessHostPool"
export AiMatrixHarness__ManifestPath="/matrix/state/runtime-manifest.json"
export AiMatrixHarness__EffectProbeMcpEndpoint="http://127.0.0.1:8090/mcp"
export AiMatrixHarness__EffectProbeStateEndpoint="http://runtime:8090/state"
export AiMatrixHarness__EffectEvidenceEndpoint="http://runtime:8080/matrix/mcp-effect-evidence"
export AiMatrixHarness__RecoveryEndpoint="http://runtime:8080/matrix/recovery"
export AiMatrixHarness__JournalResultAcceptanceEndpoint="http://runtime:8080/matrix/journal-result-acceptance"

dotnet /app/mcp-effect-server/Multiplexed.AI.Samples.McpEffectServer.dll --urls http://0.0.0.0:8090 &
PROBE_PID="$!"
for _ in $(seq 1 100); do
  if python3 -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8090/health', timeout=1).read()" >/dev/null 2>&1; then
    break
  fi
  kill -0 "$PROBE_PID" >/dev/null 2>&1 || { echo "MCP effect sample server exited before becoming ready." >&2; exit 1; }
  sleep 0.1
done
python3 -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8090/health', timeout=1).read()" >/dev/null 2>&1 || {
  echo "MCP effect sample server did not become ready." >&2
  exit 1
}

exec dotnet /app/runtime/Multiplexed.AI.McpServer.Host.dll --Multiplexed.Rbac.Core:Project=matrix
