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

The runner performs a clean build/run, waits for the terminal verifier, prints the client/verifier exit states, reports the core and currently bound feature-verifier results, and writes the complete Docker output to `matrix-full.log` at the repository root. It deliberately does not use Compose `--abort-on-container-exit` / `--exit-code-from`, because the three SDK clients are expected one-shot services and their successful exit must not terminate the runtime before the verifier runs.

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

The local orchestrator publishes the real runtime and .NET worker, stages the same worker-fixture layout used by Docker, starts the runtime with exact installed runtime identities, waits for the matrix manifest, executes the 3 x 3 core matrix, and then executes the nine currently bound feature scenarios.

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

This matrix topology validates only:

```text
ProcessHostPool / HostRuntime / TrustedProcess / HostNetwork / ValidatedPaths
```

It does not claim sandboxed-container enforcement. OCI isolated workers are a separate matrix target and must be executed independently before being marked covered.

## Bound feature scenarios

The ProcessHost matrix first binds six publication/dependency feature scenarios without widening execution authority. The Python external SDK is used as the bounded feature driver because the core matrix already proves the public SDK boundary independently for .NET, TypeScript and Python clients. Feature coverage is therefore recorded only for the combinations actually executed.

Publication pinning is exercised once against each hosted worker language. Each scenario submits an execution against one immutable publication, publishes a replacement with the same logical pipeline identity but intentionally failing code, observes the still-running execution after that replacement exists, and then requires the original execution to complete while retaining its original publication reference.

Deterministic dependency packaging is exercised once against each hosted worker language with the language-specific immutable package kind: `DotNetAssemblyClosure`, `NodeLockedBundle`, and `PythonWheelBundle`. The dependency payloads are supplied through the existing publication contract and are materialized by the existing hosted workers; no package manager or runtime package installation is introduced.

The Docker verifier preserves the original nine core scenarios and additionally requires all six bound feature evidence documents. A successful live run must therefore report both `9/9 production-like Docker ProcessHostPool scenarios passed.` and `6/6 publication-pinning/dependency-package ProcessHostPool feature scenarios passed.`. Until that live run is executed, the feature scenarios remain planned/bound evidence rather than a green claim.

The current ProcessHost increment remains limited to ProcessHostPool / HostRuntime / TrustedProcess execution. Nested Child DAG execution, durable MCP effect evidence, cancellation, recovery, replay/result acceptance, and OCI/container-provider isolation remain outside this increment.

### Hosted custom policy family scenarios

The production-like process matrix also binds the three hosted custom policy families currently supported by the runtime through the public Python SDK client. The scenarios are deliberately behavioral rather than publication-only:

- `concurrency` runs a Python hosted policy that explicitly allows a native `hello-world` step; custom-to-native policy fallback remains forbidden.
- `retry` runs a TypeScript hosted policy that returns `stop` against the deterministic `fail-once-then-succeed` fixture. The expected execution status is `Failed`; a silent policy drop would instead allow the normal retry path to recover.
- `delegation` runs a .NET hosted policy that returns `deny` at the existing `execution.child-dag` checkpoint. The expected execution status is `Failed`; the child is not dispatched. This does not claim nested Child DAG execution coverage, which remains a separate matrix target.

These three scenarios reuse immutable publication pinning, the existing hosted worker transport, policy-family adapters, and the existing runtime policy checkpoints. No alternate policy engine or scheduler is introduced.

The Docker verifier therefore requires nine core scenarios, six publication/dependency scenarios, and three hosted custom-policy scenarios. A successful live run must report all three bounded verifier summaries before the matrix runner can return GREEN.
