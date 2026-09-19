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

The local orchestrator publishes the real runtime and .NET worker, stages the same worker-fixture layout used by Docker, starts the runtime with exact installed runtime identities, waits for the matrix manifest, executes the 3 x 3 core matrix, and then executes the eighteen Python-driven feature scenarios plus the three native external-client dependency-firewall checks. Cancellation remains exercised independently by all three native SDK client containers.

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

The current ProcessHost coverage remains limited to ProcessHostPool / HostRuntime / TrustedProcess execution. Durable recovery and durable journal result acceptance are now exercised below, while OCI/container-provider isolation remains outside the currently bound scenarios. Durable running cancellation is exercised independently by all three external SDK clients.

### Hosted custom policy family scenarios

The production-like process matrix also binds the three hosted custom policy families currently supported by the runtime through the public Python SDK client. The scenarios are deliberately behavioral rather than publication-only:

- `concurrency` runs a Python hosted policy that explicitly allows a native `hello-world` step; custom-to-native policy fallback remains forbidden.
- `retry` runs a TypeScript hosted policy that returns `stop` against the deterministic `fail-once-then-succeed` fixture. The expected execution status is `Failed`; a silent policy drop would instead allow the normal retry path to recover.
- `delegation` runs a .NET hosted policy that returns `deny` at the existing `execution.child-dag` checkpoint. The expected execution status is `Failed`; the child is not dispatched. This policy-denial proof remains distinct from the nested Child DAG execution scenarios below.

These three scenarios reuse immutable publication pinning, the existing hosted worker transport, policy-family adapters, and the existing runtime policy checkpoints. No alternate policy engine or scheduler is introduced.

### Nested Child DAG scenarios

The production-like process matrix additionally binds three depth-two published Child DAG scenarios through the external Python SDK client, one for each hosted worker language. Each root publication contains an inline child DAG, which in turn contains an inline grandchild DAG with one custom hosted `leaf` declaration.

The custom leaf is bound through the immutable publication call site `/invoke-child/invoke-grandchild`. The grandchild default/step language is the worker language under test (`dotnet`, `typescript`, or `python`), while both parent Child DAG transitions continue to use the existing `execution.child-dag` scheduler, child-relation persistence, publication child-run binding, shared-queue continuation, and finalization authorities.

The evidence is intentionally bounded to what the live public path proves: immutable nested publication, root submission, nested child dispatch, hosted grandchild custom declaration execution, parent continuation, terminal observation, and `Completed` root result. It does not claim recovery or failure-injection coverage for nested Child DAGs; those remain separate roadmap targets.

The Docker verifier therefore requires nine core scenarios, six publication/dependency scenarios, three hosted custom-policy scenarios, and three nested Child DAG scenarios before durable MCP effect evidence is added below.

### Durable MCP effect evidence scenarios

The process matrix binds two language-free MCP effect scenarios through the external Python SDK client and the public `Mcp` invocation contract. A matrix-only loopback MCP probe is server-owned and configured through the normal outbound connection catalog; pipeline input never supplies an endpoint, credential or connection revision.

`completed-local-replay` executes a tool that records one physical `tools/call` and returns an explicit MCP tool error. The durable transport persists that confirmed remote result as `Completed`. The DAG then performs its configured logical retry for the same execution/step identity, and the durable fence must replay the stored result locally. The probe must still report exactly one physical call.

`uncertain-blocks-blind-resend` executes a tool that records one physical `tools/call` and then exceeds the server-owned invocation deadline after the dispatch boundary has been crossed. The durable evidence must become `Uncertain`. The DAG performs one configured logical retry, but the durable fence must reject that retry before a second physical emission. The probe must still report exactly one physical call.

Matrix-only diagnostics expose the durable evidence status and retained DAG retry count without exposing endpoint or credential material. These diagnostics are disabled with the matrix harness and are not public runtime APIs. The scenarios prove bounded durable effect evidence behavior only; they do not claim generic exactly-once delivery or automatic reconciliation of uncertain effects.

The Docker verifier now requires nine core scenarios, six publication/dependency scenarios, three hosted custom-policy scenarios, three nested Child DAG scenarios, and two durable MCP effect evidence scenarios. A successful live run must report all five bounded verifier summaries before the matrix runner can return GREEN. The resulting gate is 23/23 executed scenarios.

## Cancellation coverage

The Docker ProcessHostPool matrix exercises `sdk.execution.cancel` independently through the .NET, TypeScript, and Python external SDKs. Each client submits a same-language long-running hosted execution, observes the hosted step in an active state, requests durable cancellation, validates the acknowledgement metadata, and requires public observation/result convergence to `Cancelled`. Cancelling an SDK transport call does not satisfy this target.

## Recovery coverage

The ProcessHostPool matrix binds two recovery paths through the existing runtime execution recovery reconciler. The matrix-only recovery endpoint is diagnostic orchestration only: it marks runtime availability and seeds durable ownership evidence, while the production recovery reconciler, shared queue, shared-run ownership resolver, runtime execution index, execution-control service and DAG store retain mutation authority.

`in-flight-resume` starts a public native DAG execution, waits until its step is actively running, marks the owning runtime unavailable and invokes the production recovery reconciler. The failed local run must become `requeued-for-recovery`, the shared run must be requeued through recovery metadata, and the original durable `ExecutionId` must resume and converge to `Completed`. A new execution identity does not satisfy this scenario.

`local-queued-redispatch` begins from an active public template while its authoritative shared-run dispatch record is still present. The harness copies that `RunRequest`, normalizes it into true not-yet-started local-queued work by clearing the preallocated execution identity/snapshot, seeds one real shared-queue/local-run ownership with no `ExecutionId`, assigns it to an unavailable runtime and invokes the same recovery reconciler. Recovery must requeue that ownership, a healthy runtime must accept the shared run, a new durable execution identity must be created, and that replacement execution must converge to `Completed`.

These scenarios exercise recovery authorities only; they do not claim provider host restart or process-kill ownership. Physical crash/kill coverage remains a separate failure-injection concern.

## Durable journal result-acceptance coverage

Two matrix scenarios use the production Mongo-backed `IAiDurableInvocationStore` through fresh `AiDurableInvocationJournal` instances. They do not call store-specific Mongo methods and do not bypass lease/epoch/result CAS rules.

`accepted-result-replay` prepares one durable operation, acquires epoch-one lease authority, accepts one terminal result, reconstructs a fresh journal over the same durable store, and submits the identical result with the same assignment. The first completion must be `Accepted`; the replay must be `AlreadyAccepted`; the terminal result hash and pending continuation intent must remain unchanged.

`duplicate-delivery-convergence` submits eight concurrent identical result deliveries under the same live lease. Exactly one delivery must become `Accepted`, the remaining seven must converge as `AlreadyAccepted`, no delivery may cross the lease fence as `LeaseRejected`, and the durable record must retain one authoritative terminal result with pending continuation intent.

With these four scenarios, the production-like Docker gate reaches 30 runtime/behavioral scenarios before the exact coverage closure below.

## Exact executed-coverage closure

The final ProcessHost Pack 3 closure adds three external client dependency-firewall scenarios, executed independently inside the published .NET client artifact, compiled TypeScript SDK artifact, and Python SDK source/distribution boundary. These checks do not exercise a hosted worker; they prove that the external SDK artifact remains independent from engine/runtime repository dependencies.

The .NET check inspects the SDK and public-contract assembly reference graphs plus the published client bundle. The TypeScript check inspects declared package dependencies and compiled `dist` import specifiers. The Python check inspects declared `pyproject.toml` dependencies and absolute SDK source imports. Any repository runtime/engine dependency fails the client before verifier execution.

The canonical Docker verifier requires all 33 executed scenarios: 9 core + 6 publication/dependency + 3 custom policy + 3 nested Child DAG + 2 durable MCP effect + 3 cancellation + 2 recovery + 2 journal result-acceptance + 3 external client dependency-firewall scenarios. It also writes `executed-coverage-closure.json` from the evidence set and records the exact topology/provider as `docker` / `ProcessHostPool`.

Two roadmap coverage targets remain deliberately **NOT EXECUTED** in Pack 3: `worker-isolation-provider` and `isolation-artifact-selection`. Their `sandboxed-container` and `OciImage` values belong to Pack 4 and are not inferred from `TrustedProcess` / `HostRuntime` evidence. This keeps the 33 executed scenarios bounded to combinations that actually ran.

Fixture-backed workers are still used by this Pack 3 matrix. Removing those fixtures and rerunning the full matrix through non-fixture execution artifacts is the separate closure gate that follows this increment.

## Fixture-free execution artifacts

The matrix does not own worker-function fixtures. External clients publish reusable sample artifacts from `implementations/sdk/samples/published-functions`, while execution is performed by the production hosted-invocation workers under `implementations/dotnet/workers`, `implementations/node/workers`, and `implementations/python/workers`. Durable MCP effect scenarios use the standalone sample MCP server under `implementations/sdk/samples/mcp-effect-server`.
