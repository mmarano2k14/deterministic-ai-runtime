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
