# Multilanguage Runtime Matrix

This matrix validates the independently consumable .NET, TypeScript and Python SDKs through the same public MCP boundary and the existing runtime authorities.

## Core matrix

The core matrix contains nine live combinations:

```text
3 external client languages x 3 hosted execution languages
```

Each scenario must prove publication, submission, observation, terminal result and public execution identity. Evidence is written only after the real SDK call path completes.

## Supported topologies

### Docker / ProcessHostPool - canonical reproducible validation

This is the canonical third-party path. The only host prerequisite is Docker with Compose support. .NET, Node, Python, MongoDB, Redis and the runtime are all supplied by containers or container build stages.

From any directory inside the repository on Windows PowerShell:

```powershell
& (Join-Path (git rev-parse --show-toplevel) "implementations\matrix\runtime\docker\run-matrix.ps1")
```

The runner performs a clean build/run, waits for the terminal verifier, prints the client/verifier exit states and the final `9/9` verifier result, and writes the complete Docker output to `matrix-full.log` at the repository root. It deliberately does not use Compose `--abort-on-container-exit` / `--exit-code-from`, because the three SDK clients are expected one-shot services and their successful exit must not terminate the runtime before the verifier runs.

Cleanup including persisted test data:

```cmd
docker compose -f .\implementations\matrix\runtime\docker\docker-compose.yml down -v --remove-orphans
```

The stack runs real MongoDB and Redis services, the real MCP runtime host, three external SDK client containers and the real hosted .NET, TypeScript and Python process workers. Client containers have no engine/runtime project reference and communicate with the runtime only over MCP HTTP.

### Local / ProcessHostPool - developer topology

The local topology exercises the same scenarios and SDK clients but uses local MongoDB, Redis, runtime, .NET, Node and Python installations.

PowerShell orchestration:

```powershell
.\implementations\matrix\runtime\local\run.ps1
```

If MongoDB and Redis are already running on their default local ports:

```powershell
.\implementations\matrix\runtime\local\run.ps1 -InfrastructureAlreadyRunning
```

The local orchestrator publishes the real runtime and .NET worker, stages the same worker-fixture layout used by Docker, starts the runtime with exact installed runtime identities, waits for the matrix manifest and then executes the same 3 x 3 core matrix.

## Runtime manifest

No environment reference is typed manually. The running host computes the exact executable/loader identities, registers the three approved publication environments and writes a runtime manifest containing:

```text
endpoint
access-context handle
a harness bearer token
exact .NET environment ref
exact TypeScript environment ref
exact Python environment ref
topology
provider
```

The manifest is harness-local evidence and is not a new public runtime API.

## Authentication and RBAC

The matrix keeps the normal MCP authentication/context middleware in the path. An explicit harness-only static bearer scheme and an isolated RBAC context are enabled only when `AiMatrixHarness:Enabled=true`.

Context rotation is disabled only in this harness because the current public SDK transport carries an access-context handle but does not yet negotiate rotated handles across MCP exchanges. This limitation is recorded rather than hidden.

## Provider semantics

This pack validates only:

```text
ProcessHostPool / HostRuntime / TrustedProcess / HostNetwork / ValidatedPaths
```

It does not claim sandboxed-container enforcement. OCI isolated workers are a separate matrix target and must be executed independently before being marked covered.
