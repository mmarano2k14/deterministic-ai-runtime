# Interactive Agent SDK Demo

This directory demonstrates a real external application consuming the Deterministic AI Runtime through the public SDK boundary.

## Current status

```text
.NET interactive agent vertical slice     TARGET E2E VALIDATED
TypeScript interactive agent vertical slice IMPLEMENTED / TARGET E2E VALIDATION REQUIRED
Python consumer scaffold                  AVAILABLE
```

The .NET and TypeScript consumers reference only their locally packed external SDK packages.

They do not reference runtime, engine, control-plane, persistence, matrix, or test projects.

## Authentication flow

The standalone .NET and TypeScript demos use the real host authentication boundary:

```text
real JWT Bearer
    ↓
POST /auth/access-context
    ↓
validated JWT claims
    ↓
existing IContextStore
    ↓
X-Access-Context
    ↓
public MCP SDK transport
    ↓
automatic X-Access-Context rotation
```

The external application never submits TRN capabilities in the access-context request.

Capabilities are derived by the host from the validated JWT claims.

If `AI_RUNTIME_ACCESS_CONTEXT` is not provided, the .NET or TypeScript consumer automatically calls:

```text
AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT
```

using `AI_RUNTIME_TOKEN`, captures the returned access-context handle, and initializes the public SDK transport with it. The TypeScript SDK exposes `AiSdkAccessContextBootstrapper` for the same explicit, non-retried bootstrap semantics as the .NET SDK.

The access-context bootstrap POST is not automatically retried.

## .NET and TypeScript execution flow

```text
user request
    ↓
sdk.publish_pipeline
    ↓
sdk.execution.submit
    ↓
root ai.prompt
    ↓
execution.child-dag
    ↓
child ai.prompt
    ↓
child execution.publish-result
    ↓
durable child result payload
    ↓
parent continuation
    ↓
execution.await-input
    ↓
sdk.execution.input.submit
    ↓
final ai.prompt
    ↓
execution.publish-result
    ↓
sdk.execution.result
    ↓
optional sdk.execution.replay
```

The child definition contains no further Child DAG call site, so delegation is structurally bounded to one level.

## No matrix or demo-only control path

The .NET and TypeScript agents do not use:

```text
/matrix/*
matrix-only endpoints
matrix state
test harness APIs
direct runtime stores
IAiPublicSdkBoundary
internal scheduler/controller services
```

Human input is produced by the runtime-native `execution.await-input` step and resumed through the public SDK input operation.

## OpenAI boundary

The pipeline uses the runtime-native `ai.prompt` step with provider `openai`.

The external .NET and TypeScript consumers read:

```text
OPENAI_MODEL
```

to construct the portable pipeline definition.

The OpenAI API key remains server-owned runtime configuration:

```text
OPENAI_API_KEY
```

or:

```text
OpenAI:ApiKey
```

The key is not serialized into publication content, execution input, Watch events, human input, or SDK transport payloads.

## Public execution input

The public SDK submission carries:

```json
{
  "userPrompt": "...",
  "requestId": "..."
}
```

The root prompt receives that document through `state.input`.

## Child result consumption

The child publishes its analysis with:

```text
execution.publish-result
```

The parent consumes the frozen Child DAG payload through:

```text
steps.delegate-analysis.result.payload.data.result
```

There is no demo-specific child-result side channel.

## Human input

When `await-review` reaches:

```text
WaitingForExternal
```

enter:

```text
i
```

The console submits:

```json
{
  "approved": true,
  "feedback": "..."
}
```

through:

```text
sdk.execution.input.submit
```

The same durable execution and exact parked step are resumed.

## Live console commands

While active:

```text
[p] pause
[r] resume
[i] submit human input
[c] cancel
[s] status
[q] detach local console without cancelling
```

After terminal convergence:

```text
[x] deterministic replay validation
[q] exit
```

## Required .NET / TypeScript configuration

```text
AI_RUNTIME_ENDPOINT
AI_RUNTIME_TOKEN
OPENAI_MODEL
```

Optional:

```text
AI_RUNTIME_ACCESS_CONTEXT
AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT
AI_RUNTIME_ACCESS_CONTEXT_HEADER
```

If no explicit access context is supplied, the demo obtains one automatically.

## Local standalone JWT

Production deployments should obtain `AI_RUNTIME_TOKEN` from the real identity provider.

For local development only, the repository contains a small JWT issuer utility that creates a **real HS256 JWT** accepted by the standalone `JwtBearer` host. It does not add a fake authentication handler.

Configure the host:

```powershell
$env:AiMcpAuthentication__Enabled="true"
$env:AiMcpAuthentication__Issuer="multiplexed-local"
$env:AiMcpAuthentication__Audience="multiplexed-ai-sdk"
$env:AiMcpAuthentication__SymmetricSigningKey="replace-with-at-least-32-bytes-of-local-secret"
$env:OPENAI_API_KEY="<openai-key>"
```

The default RBAC project in the host is:

```text
rbac-demo
```

The local token helper uses that same project by default.

In the consumer terminal, set the same local signing configuration only for this development workflow:

```powershell
$env:AiMcpAuthentication__Issuer="multiplexed-local"
$env:AiMcpAuthentication__Audience="multiplexed-ai-sdk"
$env:AiMcpAuthentication__SymmetricSigningKey="replace-with-at-least-32-bytes-of-local-secret"

$env:AI_RUNTIME_TOKEN = python .\demo\interactive-agent-sdk\scripts\create-local-jwt.py
$env:AI_RUNTIME_ENDPOINT="http://localhost:8081/mcp"
$env:OPENAI_MODEL="gpt-5.4"
```

The helper grants only the capabilities used by this interactive demo:

```text
code:publication:publish
code:publication:read
code:publication:execute
shared-run:execution:submit
execution:control:read
execution:control:cancel
execution:control:pause
execution:control:resume
execution:control:input
replay:execution:run
```

The symmetric signing key is issuer authority and must never be distributed to normal production consumers.

## Build

```cmd
.\demo\interactive-agent-sdk\scripts\bootstrap.cmd
```

The bootstrap remains local-package only and publishes nothing.

`AI_DEMO_SMOKE=1` sends no authentication, access-context, runtime, or OpenAI request.

## Run

```cmd
.\demo\interactive-agent-sdk\run.cmd
```

Choose:

```text
1. .NET
2. TypeScript
```

Expected authentication prelude:

```text
Authentication bootstrap:
  POST http://localhost:8081/auth/access-context
  Authorization: Bearer <redacted>
  Access context created via 'X-Access-Context'. Handle not displayed.
  Subsequent handle rotation is managed by the SDK transport.
```

After that, the normal public SDK pipeline publication begins.

## Validation status

The .NET vertical slice has completed the real authenticated public-SDK E2E path through Child DAG, durable human input, terminal result, and deterministic replay.

The TypeScript vertical slice is implemented against the same public contracts and runtime authority. A target-environment E2E run is still required before the TypeScript flow is marked GREEN.
