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
