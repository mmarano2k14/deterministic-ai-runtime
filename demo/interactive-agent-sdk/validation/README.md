# Interactive Agent SDK Validation Contract

This directory freezes the canonical logical scenario used by the interactive SDK topology validation campaign.

## Source of truth

`interactive-agent-scenario.json` defines the business-neutral execution shape and durable invariants shared by the .NET, TypeScript, and Python consumers.

`evidence.schema.json` defines the minimum machine-readable evidence required for a scenario to be accepted as PASS.

## Canonical root flow

```text
plan
  -> delegate-analysis
  -> await-review
  -> final-answer
  -> publish-result
```

The delegated Child DAG is:

```text
child-analysis
  -> publish-child-result
```

## Acceptance boundary

A topology run is PASS only when all of the following are true:

```text
publication accepted
execution submitted
human-review boundary reached
human input accepted
ExecutionId unchanged across human input
terminal execution status == Completed
all five root steps == Completed
public result available
provider == openai
model metadata present
response payload present
replay succeeded == true
replay deterministic == true
```

The generated OpenAI text is intentionally not compared for equality. The model output is probabilistic; the execution lifecycle and durable evidence are the deterministic validation target.

## Public-boundary rule

The validation runner must use public SDK operations only. It must not use matrix endpoints, internal stores, internal controller APIs, or direct `IAiPublicSdkBoundary` calls to manufacture a PASS.

## Language-specific names

Pipeline publication names may remain language-specific. Semantic step names, waiting keys, durable assertions, and evidence fields are shared.

## Automated E2E runner

Step 2 adds a machine-readable validation mode to each external SDK consumer.

Enable it through:

```text
AI_DEMO_AUTOMATED_VALIDATION=1
AI_DEMO_EVIDENCE_PATH=<path-to-json>
AI_VALIDATION_RUNTIME_PROVIDER=ProcessHostPool|KubernetesPool
AI_VALIDATION_RUNTIME_TRANSPORT=HTTP|gRPC
```

The consumer drives the canonical public-SDK flow directly, submits the canonical
human review input when `await-review` becomes durable, waits for terminal state,
retrieves the public result, runs deterministic replay, and writes evidence JSON.

The orchestration wrapper is:

```powershell
python .\demo\interactive-agent-sdk\validation\run_e2e.py `
  --runtime-provider ProcessHostPool `
  --runtime-transport HTTP `
  --sdk all
```

`run_e2e.py` never parses console output. A run passes only when the emitted JSON
passes `validate_evidence.py` against the Step 1 contract.

Before the validation campaign starts, the orchestrator rebuilds the local SDK packages
and all three native consumers through the existing bootstrap. This prevents stale
`.NET --no-build` outputs or stale TypeScript `dist/` files from silently bypassing the
automated validation path. Use `--skip-prepare` only when the artifacts were already
prepared from the current sources in the same validation session.

## Validation authentication

`run_e2e.py` supports three authentication modes:

```text
auto       local endpoint: always create a fresh local demo JWT
           remote endpoint: require AI_RUNTIME_TOKEN
token      require and use AI_RUNTIME_TOKEN
local-jwt  always create a local demo JWT
```

For local Docker-backed validation, `auto` deliberately ignores any inherited `AI_RUNTIME_TOKEN` and creates a fresh bearer token on every run. This prevents stale or differently signed tokens left in the parent PowerShell session from contaminating local validation. It reads only the local JWT issuer, audience, signing key, and demo claim settings from `demo/interactive-agent-sdk/.env`. It deliberately does **not** load `OPENAI_API_KEY` into the external SDK consumer environment.

For a non-local runtime, `auto` requires an explicit `AI_RUNTIME_TOKEN`. `local-jwt` can still be selected explicitly when a non-local runtime intentionally shares the local-demo signing authority.

The generated bearer token is injected as `AI_RUNTIME_TOKEN`. The local signing key and `OPENAI_API_KEY` are stripped before launching the .NET, TypeScript, or Python consumer.

Example:

```powershell
python .\demo\interactive-agent-sdk\validation\run_e2e.py `
  --runtime-provider ProcessHostPool `
  --runtime-transport HTTP `
  --sdk all `
  --auth-mode auto
```

## Topology verification

Step 3 makes runtime topology a verified input rather than a label copied into evidence.

Every accepted E2E run now requires a machine-generated topology attestation:

```text
ProcessHostPool -> source = process-host-inspection
KubernetesPool -> source = kubernetes-cluster-inspection
```

The requested pair remains:

```text
runtimeProvider  = ProcessHostPool | KubernetesPool
runtimeTransport = HTTP | gRPC
```

but the runner refuses to execute consumers until observed topology matches that request.

The final evidence JSON contains a `topology` object with:

```text
requested provider/transport
observed provider/transport
topology authority/source
verification checks
verified = true
verification timestamp
```

`validate_evidence.py` rejects evidence when:

```text
topology.verified != true
requested provider != evidence provider
requested transport != evidence transport
observed provider != evidence provider
observed transport != evidence transport
any topology check failed
```

### Fail-closed rule

Control-plane configuration alone is not accepted as proof of Runtime Pool topology.

For example:

```text
AiMcpHost__Mode = ControlPlaneWithHttpRuntimeInstances
AiHttpRuntimeScaleOut__HostCreationMode = Process
```

proves an external HTTP/process-host configuration family, but does not by itself prove the additive `ProcessHostPool` topology. Historical one-runtime-per-process hosting uses the same provider family.

Likewise, a Kubernetes-capable control plane is not proof that the exercised execution ran through `KubernetesPool`.

The topology-specific launchers introduced by the next roadmap steps must therefore generate the attestation from actual ProcessHost or Kubernetes runtime membership.

### Diagnostic inspection

The current local Docker runtime can be inspected without exposing authentication or provider secrets:

```powershell
python .\demo\interactive-agent-sdk\validation\inspect_topology.py `
  --runtime-provider ProcessHostPool `
  --runtime-transport HTTP
```

This diagnostic output is intentionally not a PASS attestation. It reports the currently observable control-plane configuration and helps explain a mismatch.

### Explicit attestation

A topology-specific launcher passes its generated attestation to the common E2E runner:

```powershell
python .\demo\interactive-agent-sdk\validation\run_e2e.py `
  --runtime-provider ProcessHostPool `
  --runtime-transport HTTP `
  --topology-attestation <machine-generated-attestation.json> `
  --sdk all
```

Without a valid attestation, the runner exits before consumer preparation or SDK execution.

The earlier automated Step 2 runs remain proof that the machine-readable .NET, TypeScript, and Python validation paths work. Because the then-running Docker service was configured as `ControlPlaneWithLocalRuntimeInstances`, those runs are not counted as ProcessHostPool topology evidence.

## ProcessHostPool parity harness

Step 4 adds a ProcessHostPool-specific attestation and matrix wrapper. The generic
`Process` host-creation setting is still not accepted as ProcessHostPool proof.

The topology-specific runtime launcher or diagnostics path must emit a fresh
machine-generated inventory with:

```text
source = runtime-pool-diagnostics
captureId
PoolId
HostId
host process id
stable pool endpoint
>= 2 child RuntimeInstanceId values per ProcessHost
optional RouteId diagnostics when exported by the ProcessHost boundary
child process id
Ready / CanAcceptRun capacity state
provider.name / transport.name
```

The attestor verifies that:

```text
inventory age <= 300 seconds
ProcessHost process is alive
child runtime processes are alive
child runtime processes descend from the attested ProcessHost
stable pool endpoint is reachable
RuntimeInstanceId values are unique
exported RouteId values are unique when present
host and child process ids are unique
provider/transport matches the requested HTTP or gRPC path
```

Only then is a `process-host-inspection` topology attestation emitted with
`verified=true`.

Example attestation command:

```powershell
python .\demo\interactive-agent-sdk\validation\attest_processhostpool.py `
  --inventory <machine-generated-processhostpool-inventory.json> `
  --runtime-transport HTTP `
  --output .\demo\interactive-agent-sdk\validation\attestations\processhostpool-http.json
```

The common three-client matrix wrapper is:

```powershell
python .\demo\interactive-agent-sdk\validation\run_processhostpool_matrix.py `
  --runtime-transport HTTP `
  --runtime-endpoint http://localhost:8081/mcp `
  --inventory <machine-generated-processhostpool-inventory.json>
```

and equivalently for gRPC:

```powershell
python .\demo\interactive-agent-sdk\validation\run_processhostpool_matrix.py `
  --runtime-transport gRPC `
  --runtime-endpoint http://localhost:8081/mcp `
  --inventory <machine-generated-processhostpool-inventory.json>
```

The `runtime-endpoint` remains the public MCP control-plane endpoint. `HTTP` and
`gRPC` describe the runtime provider transport behind the control plane, not the
external SDK MCP transport.

A successful Step 4 closure requires:

```text
ProcessHostPool / HTTP
.NET        PASS
TypeScript PASS
Python     PASS
            3 / 3

ProcessHostPool / gRPC
.NET        PASS
TypeScript PASS
Python     PASS
            3 / 3

ProcessHostPool total = 6 / 6
```

The inventory is not a manually authored PASS file. The production launcher or
runtime-pool diagnostics path must generate it from actual membership and route
state. The live process and endpoint checks intentionally make stale or fabricated
inventory fail closed.

## Step 4 - production ProcessHostPool bridge

The ProcessHostPool topology matrix reuses the existing production integration-test
harness. It does not create a second Runtime Pool implementation.

The installer is fail-closed and environment-gated:

```powershell
python .\demo\interactive-agent-sdk\validation\apply_processhostpool_bridge.py
```

Normal integration tests are unchanged unless:

```text
AI_INTERACTIVE_SDK_PROCESSHOSTPOOL_VALIDATION=1
```

The all-in-one launcher installs the bridge, prepares consumers, builds the runtime
host and integration tests, selects one existing ProcessHostPool production test,
and executes the three SDK clients while the real pool is alive. The validation path
preserves production JWT authentication instead of the integration host's default
`FakeAuthHandler`, while historical tests keep their existing fake-auth behavior.
Replay-safe payload storage is applied before pool startup using the MongoDB/Redis
configuration already owned by the production harness. The SDK JWT tenant identity
is aligned with the active ProcessHostPool scenario tenant.

```powershell
python .\demo\interactive-agent-sdk\validation\launch_processhostpool_matrix.py `
  --runtime-transport HTTP
```

The launcher uses `Debug` by default for the runtime host/integration harness to
avoid unrelated locks on shared `Release` binaries. Use
`--build-configuration Release` only when an explicit Release validation is needed.
The launcher reads only allowlisted server settings from `demo/interactive-agent-sdk/.env`.
`OPENAI_API_KEY` stays on the server/test process and is stripped before the .NET,
TypeScript, or Python SDK consumer is launched.

For the environment-gated external SDK validation, the integration host keeps the
historical `MatrixBearer` scheme registered but post-configures ASP.NET Core so
`Bearer` is the default authenticate/challenge/forbid scheme. The bridge verifies
that resolved scheme before opening the loopback proxy; a remaining `MatrixBearer`
default fails before any SDK client is launched.

Then run the gRPC half:

```powershell
python .\demo\interactive-agent-sdk\validation\launch_processhostpool_matrix.py `
  --runtime-transport gRPC `
  --skip-prepare `
  --skip-patch
```

A successful transport run must end with:

```text
[processhost-launcher] PASS: ProcessHostPool/HTTP 3/3
```

or:

```text
[processhost-launcher] PASS: ProcessHostPool/gRPC 3/3
```

The generated inventory requires at least two independent parent ProcessHosts and
at least two child runtime processes per parent. A single-parent process pool is
not accepted as ProcessHostPool topology evidence.


### ProcessHostPool authentication diagnostics

The production-harness bridge logs only non-secret authentication metadata at the loopback proxy boundary.
For each forwarded request it records whether an Authorization header and X-Access-Context are present, the Authorization scheme name, the upstream HTTP status, and any WWW-Authenticate scheme names. Token values and access-context values are never logged.

This diagnostic distinguishes SDK credential propagation from test-host authentication failures without weakening authentication or RBAC.


### Dedicated JWT handler and bootstrap boundary

The ProcessHostPool validation path registers a dedicated `InteractiveSdkJwtBearer` handler only when `Tests:UseRealJwtAuthentication=true`. The bridge verifies that the resolved scheme is backed by ASP.NET Core `JwtBearerHandler` before any external SDK client is started. Historical `FakeAuthHandler` and `MatrixBearer` behavior is unchanged outside this gated validation path.

The matrix also pins `AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT` to `/auth/access-context` on the same loopback proxy authority as `AI_RUNTIME_ENDPOINT`. This prevents a shell or prior Docker demo from bootstrapping RBAC against another host while MCP calls target the production ProcessHostPool test host.
