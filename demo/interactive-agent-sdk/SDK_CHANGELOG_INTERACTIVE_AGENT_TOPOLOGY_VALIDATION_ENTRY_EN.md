---

## Interactive Agent SDK Topology Validation - Canonical Scenario Contract

## Scope

Frozen the canonical interactive-agent scenario and machine-readable acceptance contract used by the pre-main topology validation campaign.

This work introduces validation artifacts only. It does not change public SDK operations, runtime execution semantics, DAG behavior, Child DAG behavior, RBAC authority, recovery authority, or deterministic replay semantics.

---

## Canonical Scenario

Added a single semantic scenario definition shared by the .NET, TypeScript, and Python validation paths.

The root flow is:

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

The scenario preserves the existing public authentication, access-context, immutable publication, durable execution, Child DAG, human-input, final-result, and replay paths.

---

## Acceptance Contract

Added a machine-readable evidence schema requiring:

```text
publication accepted
human-review boundary reached
human input accepted
same ExecutionId after human input
terminal status Completed
all expected root steps Completed
public result available
OpenAI provider/model metadata present
response payload present
replay succeeded
replay deterministic
```

OpenAI response-text equality is explicitly excluded from deterministic assertions.

---

## Public Boundary

The topology runner is required to use the public SDK boundary only.

Validation shortcuts through matrix endpoints, direct Redis/Mongo mutation, internal DAG stores, internal controller APIs, or direct `IAiPublicSdkBoundary` invocation are excluded from the acceptance contract.

---

---

## Automated E2E Runner

Added machine-readable automated validation mode to the .NET, TypeScript, and Python interactive external SDK consumers.

The mode is enabled only through `AI_DEMO_AUTOMATED_VALIDATION` and does not replace the existing interactive presentation path.

Each client now performs the canonical validation flow directly through public SDK operations:

```text
publish
submit
observe
await durable human-review boundary
submit human input
observe terminal convergence
result
replay
```

The automated path records structured evidence through `AI_DEMO_EVIDENCE_PATH` and does not derive pass/fail state from console presentation text.

Added a common orchestration wrapper that can run one client or all three clients for an explicit runtime-provider/runtime-transport pair and validates every emitted evidence artifact against the canonical Step 1 contract.

The runner does not use matrix endpoints, direct stores, internal scheduler/controller APIs, or direct public-boundary implementation references.

The Docker demo image now carries the validation harness for subsequent topology validation work.


---

## Automated Validation Authentication Bootstrap

Hardened the automated E2E orchestrator so local validation does not require a manually pre-created bearer token.

Authentication modes are now explicit:

```text
auto
token
local-jwt
```

`auto` uses an existing `AI_RUNTIME_TOKEN` when supplied and otherwise creates a short-lived local demo JWT through the existing `create-local-jwt.py` helper.

The local JWT settings are resolved from explicit environment variables or the demo `.env` file and are mapped to the same issuer/audience/signing configuration used by the local runtime.

Provider and issuer secrets are not propagated into the external SDK processes. Before launching .NET, TypeScript, or Python consumers, the orchestrator removes:

```text
OPENAI_API_KEY
AiMcpAuthentication__SymmetricSigningKey
AI_DEMO_SIGNING_KEY
```

Only the generated bearer credential is injected as `AI_RUNTIME_TOKEN`.

This keeps OpenAI authentication server-owned and keeps the JWT signing key outside the external consumer process while preserving self-contained local validation.

---

## Local Validation Token Freshness

Hardened automated validation authentication so an inherited `AI_RUNTIME_TOKEN` cannot silently override local-demo JWT generation.

In `auto` mode:

```text
localhost / 127.0.0.1 / ::1
    -> always generate a fresh local demo JWT

non-local endpoint
    -> require AI_RUNTIME_TOKEN
```

Explicit modes remain available:

```text
token
    -> require and use AI_RUNTIME_TOKEN

local-jwt
    -> always generate a local demo JWT
```

This prevents expired or differently signed bearer tokens left in a developer shell from causing false authentication failures during local topology validation.

The generated token remains the only authentication material passed to the external SDK consumer; signing keys and provider credentials remain stripped from child-process environments.


---

## Effective Local Runtime JWT Authority

Hardened local automated validation so JWT generation can bind to the authentication authority of the runtime container that is actually serving the local endpoint.

When the target endpoint is local and the interactive-demo Compose runtime is active, the orchestrator now reads the effective runtime-container values for:

```text
AiMcpAuthentication__Issuer
AiMcpAuthentication__Audience
AiMcpAuthentication__SymmetricSigningKey
```

These values are used only in-process to generate the short-lived validation bearer token and are never printed or forwarded to the external SDK consumers.

If the local Docker runtime is not active or its effective JWT settings cannot be resolved, the orchestrator falls back to the explicit local demo authentication configuration.

This removes configuration drift between the JWT helper and an already-running local runtime while preserving the external-consumer boundary:

```text
runtime signing authority
    -> short-lived bearer token
    -> external SDK consumer

runtime signing key
    -X-> external SDK consumer

OPENAI_API_KEY
    -X-> external SDK consumer
```

The evidence runner continues to avoid console-output parsing; Docker metadata capture is used only for local validation authentication bootstrap.

---

## Automated Consumer Artifact Freshness

Hardened the Step 2 E2E orchestrator so source changes cannot be hidden by stale consumer build artifacts.

Before a validation campaign starts, the orchestrator now runs the existing local bootstrap to rebuild:

```text
.NET SDK packages and consumer
TypeScript SDK package and consumer dist
Python SDK wheel and editable consumer environment
```

This is required because the native launcher intentionally uses:

```text
dotnet run --no-build
node dist/index.js
```

and both paths can otherwise execute an older consumer that predates the automated-validation branch.

Consumer preparation runs once per `run_e2e.py` invocation. A deliberate `--skip-prepare` option is available for higher-level matrix orchestration that has already prepared artifacts from the current sources.

Provider credentials, JWT signing keys, bearer tokens, and access-context handles are removed from the bootstrap environment used by the validation orchestrator.

The launcher also emits:

```text
Automated validation mode: enabled
```

when `AI_DEMO_AUTOMATED_VALIDATION` is active, providing an immediate operator-visible proof that the machine-readable path is running rather than the interactive console path.

---

## Explicit Runtime Topology Verification

Added fail-closed runtime-topology verification to the interactive-agent SDK E2E harness.

The `runtimeProvider` and `runtimeTransport` values are no longer accepted as evidence solely because they were supplied on the validation command line.

The common E2E runner now requires a machine-generated topology attestation before any external SDK consumer can execute.

Supported validation targets remain:

```text
ProcessHostPool / HTTP
ProcessHostPool / gRPC
KubernetesPool / HTTP
KubernetesPool / gRPC
```

The topology contract separates requested and observed state:

```text
requested.runtimeProvider
requested.runtimeTransport
observed.runtimeProvider
observed.runtimeTransport
verified
checks
verifiedAtUtc
```

Accepted topology authorities are provider-specific:

```text
ProcessHostPool -> process-host-inspection
KubernetesPool -> kubernetes-cluster-inspection
```

The final SDK evidence is enriched with the verified topology attestation before canonical evidence validation.

The validator now requires:

```text
topology.verified == true
requested provider == evidence provider
requested transport == evidence transport
observed provider == evidence provider
observed transport == evidence transport
all topology checks == passed
```

Provider/transport configuration is deliberately not treated as sufficient Runtime Pool proof.

In particular, the harness does not infer `ProcessHostPool` merely from:

```text
ControlPlaneWithHttpRuntimeInstances
ControlPlaneWithGrpcRuntimeInstances
HostCreationMode = Process
```

because those configuration families also cover historical external runtime-instance hosting that is distinct from the additive ProcessHostPool boundary.

Likewise, Kubernetes-capable control-plane configuration is not accepted as proof that the exercised execution used KubernetesPool.

Added a non-secret local Docker diagnostic inspector that can report current host mode, provider name, transport family, and creation mode without exposing JWT signing material or OpenAI credentials.

The current interactive-demo Compose runtime remains configured as:

```text
AiMcpHost__Mode = ControlPlaneWithLocalRuntimeInstances
AiLocalRuntimeInstancePool__Enabled = true
```

Accordingly, the automated Step 2 runs validate the machine-readable three-client E2E path but are not counted as ProcessHostPool topology evidence.

This correction narrows the evidence claim; it does not invalidate the public SDK execution, human-input, completion, or deterministic replay results from those runs.

No public SDK contract, runtime scheduling authority, recovery authority, or execution semantics were changed by this step.

---

## ProcessHostPool Parity Harness

Added the ProcessHostPool-specific topology attestation contract used by the
interactive-agent SDK topology matrix.

A ProcessHostPool PASS now requires fresh machine-generated runtime-pool inventory
rather than provider/transport configuration labels.

Required runtime-pool evidence includes:

```text
PoolId
HostId
ProcessHost process identity
stable ProcessHost HTTP/gRPC endpoint
multiple child RuntimeInstanceId values
immutable RouteId per child runtime
child runtime process identity
Ready capacity state
CanAcceptRun = true
provider/transport identity
```

The ProcessHostPool attestor verifies live parent/child process membership, stable
endpoint reachability, identity uniqueness, transport parity, and inventory
freshness before emitting:

```text
source = process-host-inspection
runtimeProvider = ProcessHostPool
runtimeTransport = HTTP | gRPC
verified = true
```

Inventory older than five minutes is rejected. A disabled live process-tree check
cannot produce final verified topology evidence.

Added a ProcessHostPool matrix wrapper that consumes the verified attestation and
runs the existing canonical automated E2E flow across all three public SDK clients.

Target closure remains independently scoped:

```text
ProcessHostPool / HTTP  3 / 3
ProcessHostPool / gRPC  3 / 3
ProcessHostPool total   6 / 6
```

The wrapper does not introduce a new runtime launcher, scheduler, provider, queue,
or recovery authority. It consumes topology inventory generated by the existing
ProcessHostPool launcher/diagnostic authority and fails closed when that inventory
is absent or inconsistent.

This step does not treat `ControlPlaneWithHttpRuntimeInstances`,
`ControlPlaneWithGrpcRuntimeInstances`, or `HostCreationMode=Process` alone as
ProcessHostPool evidence.

---

## ProcessHostPool Production-Harness Bridge

Added an environment-gated bridge that reuses the existing production ProcessHostPool integration harness instead of introducing a parallel Runtime Pool launcher.

The bridge is installed only into the integration-test project and is inactive unless:

```text
AI_INTERACTIVE_SDK_PROCESSHOSTPOOL_VALIDATION=1
```

When enabled, the existing ProcessHostPool setup remains responsible for:

```text
logical PoolId
multiple external parent ProcessHosts
stable parent endpoints
child RuntimeInstanceOnly processes
RuntimeInstanceId membership
RouteId ownership
HTTP/gRPC transport routing
Redis/Mongo durable control-plane state
```

The bridge applies the same local JWT authority used by the interactive SDK demo to the existing MCP test host, then exposes that host through a real loopback Kestrel pass-through endpoint. External .NET, TypeScript, and Python SDK consumers therefore remain separate operating-system processes using the public HTTP MCP/authentication boundary.

A machine-readable ProcessHostPool inventory is captured while the real parent/child process tree is alive. The final attestation now requires at least two independent parent ProcessHosts and at least two child runtime processes per parent, preventing a single-parent Runtime Process Pool from being mislabeled as the hierarchical ProcessHostPool topology.

The validation launcher selects one existing HTTP or gRPC ProcessHostPool production test from `dotnet test --list-tests`, starts only that exact test, and short-circuits its historical workload only after the external three-client SDK matrix succeeds.

Normal ProcessHostPool production tests are unchanged when the validation environment flag is absent.

No runtime scheduler, provider, queue, recovery authority, or public SDK contract is introduced by the bridge.

---


### ProcessHostPool diagnostic compatibility

Aligned the production-harness bridge with the current ProcessHostPool host diagnostic surface by accepting `StableTransportEndpoint` as the authoritative stable parent transport endpoint name.

Reflection failures now include the available member names on the inspected production diagnostic type. This preserves fail-closed topology evidence while making internal diagnostic-contract drift immediately actionable without weakening the required `PoolId`, `HostId`, process, runtime, route, readiness, and transport assertions.


The diagnostic adapter now recognizes the production harness process-boundary field names `ParentProcessId`, `ChildProcessId`, and `StableTransportEndpoint`. Runtime diagnostic objects are recursively discovered and grouped by exact `RuntimeInstanceId` so runtime and route snapshots can contribute their typed fields without duplicating logical runtime membership. Missing route or process identity remains a hard validation failure.

- ProcessHostPool topology inventory now resolves child runtime membership from the control-plane runtime registry and exact route registry instead of private parent-host object graphs.
- Parent ProcessHost PID and stable transport endpoint remain mandatory live topology evidence.
- Child runtime process identifiers are retained as optional diagnostics; when present they are verified as live descendants of the owning parent ProcessHost, while SDK topology acceptance remains authoritative on PoolId, HostId, RuntimeInstanceId, RouteId, readiness, admission eligibility, and exact transport.

- ProcessHostPool SDK topology evidence now uses the control-plane runtime registry as the child-membership authority. RuntimeInstanceId, HostId, PoolId, Ready/CanAcceptRun state, provider/transport metadata, stable ProcessHost endpoint, and child processId are verified per runtime.
- Child processId is read from first-class runtime registry metadata and is live-checked as a descendant of the owning ProcessHost PID.
- RouteId remains optional diagnostic evidence for this SDK-facing topology gate because route incarnation is internal to the external ProcessHost boundary and is already validated by the dedicated Runtime Pool routing/recovery suites.

### ProcessHostPool public-boundary authorization alignment

- Preserve production JWT authentication in `GenericMcpServerTestHost` only when the environment-gated interactive SDK validation sets `Tests:UseRealJwtAuthentication=true`. Historical integration tests continue to use `FakeAuthHandler` by default.
- Apply replay-safe `mongo-redis` payload-store configuration before the production ProcessHostPool is started so the control plane and external runtime processes share the existing MongoDB/Redis durable payload authority.
- Keep public JWT authentication scoped to the MCP control-plane boundary; internal ProcessHost/runtime transport endpoints retain their existing provider transport behavior.
- Align the external SDK validation JWT tenant and tenant-group claims with the tenant selected by the active production ProcessHostPool scenario.
- Load only allowlisted server-side demo configuration from the local `.env` file. `OPENAI_API_KEY` is required by the server runtime and explicitly removed from the environment of the external SDK matrix process.
- Restore `Debug` as the default ProcessHostPool validation build configuration to avoid unrelated locks on shared `Release` outputs. `Release` remains explicitly selectable.
- Keep the bridge patches idempotent and fail-closed. The historical FakeAuthHandler registration remains intact in its default branch and no authorization bypass is introduced.

### ProcessHostPool launcher patch compatibility

- Resolve the replay-safe runtime-settings injection from the `controlPlaneSettings` assignment rather than from a specific ProcessHostPool cluster variable declaration.
- Preserve fail-closed behavior when the canonical settings assignment or later ProcessHostPool setup cannot be identified.
- Allow the validation patch to upgrade repositories that already contain the earlier authentication and matrix hook injections without duplicating them.

### ProcessHostPool tenant identity helper fix

- Add an outer validation-hook `RequiredString(object, string)` helper for required scenario identity fields used before entering the nested topology inventory adapter.
- Preserve the nested inventory-specific required-member resolver independently; no runtime contract, RBAC behavior, topology requirement, or SDK wire contract changes.
- Extend the launcher verifier to require the outer tenant identity helper and its exact use for `TenantId`, preventing the validation template from compiling with an unresolved helper reference.

### ProcessHostPool JWT scheme precedence correction

- Preserve `MatrixBearer` registration for historical matrix harness scenarios while forcing the ASP.NET Core default authenticate, challenge, scheme, and forbid selections to `Bearer` only when `Tests:UseRealJwtAuthentication=true`.
- Add a runtime fail-closed assertion that the resolved public control-plane authentication options use `Bearer` before the external SDK proxy and three-client matrix are started.
- Do not weaken RBAC, access-context creation, tenant ownership, or internal ProcessHost transport authentication.
- Keep the historical fake/test authentication paths unchanged when the environment-gated interactive SDK validation is disabled.

- Added non-secret ProcessHostPool proxy authentication diagnostics that record Authorization presence, scheme name, access-context presence, upstream status, and challenge scheme names without logging credentials or context values.


### ProcessHostPool JWT handler and bootstrap-boundary correction

- Replace the gated test-host assumption that a `Bearer` scheme name implies a real JWT handler with a dedicated `InteractiveSdkJwtBearer` scheme backed by ASP.NET Core `JwtBearerHandler`.
- Validate issuer, audience, symmetric signing key, signature, lifetime, expiration, and signed-token requirements only on the environment-gated interactive SDK path. Historical `FakeAuthHandler` and `MatrixBearer` registrations remain unchanged.
- Verify both the resolved default scheme and the concrete `JwtBearerHandler` type before starting the external SDK matrix.
- Pin `AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT` to the same loopback proxy authority as `AI_RUNTIME_ENDPOINT`, preventing stale Docker or developer-shell bootstrap endpoints from creating an RBAC context against a different host.
- Clear inherited `AI_RUNTIME_ACCESS_CONTEXT` for the topology matrix so each SDK client creates a fresh context at the attested public boundary.
- Preserve server-only signing and OpenAI credentials; no signing key or provider key is exposed to the SDK consumer process.

### ProcessHostPool child-runtime diagnostic trace

- Add an environment-gated live diagnostic tee for stdout/stderr emitted by RuntimeInstance child processes during the interactive SDK ProcessHostPool validation.
- Preserve the existing `ReadToEndAsync` drain path unchanged when `MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE` is not enabled.
- Forward only diagnostic lines related to plan execution, provider calls, invocation, retry, failure, error, or exception state to the parent ProcessHost output.
- Forward marked child-runtime diagnostics from the existing production ProcessHost capture boundary to the validation test output without changing scheduling, authorization, hosted-invocation roles, provider configuration, queue behavior, recovery, or PERF2 attribution.
- Enable the trace only from the ProcessHostPool interactive validation launcher; normal production and integration scenarios remain unchanged.

### ProcessHostPool post-completion gate diagnostics

- Added explicit post-completion validation gate reporting to the .NET, TypeScript, and Python automated consumers.
- Replay success, deterministic replay, result availability, provider payload, human-review continuity, root-step completion, and execution identity continuity are reported independently.
- The matrix runner now validates an emitted evidence document even when a consumer returns a non-zero gate result, preventing a valid evidence payload from being hidden by the consumer exit code.
- No runtime topology, authorization, hosted invocation, OpenAI provider, queue, replay engine, or PERF attribution behavior is changed.

### ProcessHostPool Mongo snapshot diagnostics

- Extended the gated child-process diagnostic stream to include Mongo execution snapshot store registration, upsert, lookup, and configuration-presence signals.
- Preserved the existing ProcessHostPool durable environment composition and did not alter Redis, MongoDB, replay, authorization, OpenAI, queue, or PERF behavior.
- The diagnostic is intended to distinguish missing Mongo child configuration from execution-identity mismatch by exposing the exact execution identifier used by child snapshot upserts.
