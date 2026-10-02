# Interactive Agent SDK Demo

This directory contains a real external-application demo for the Deterministic AI Runtime. The same runtime-native agent pipeline is consumed through the public .NET, TypeScript, and Python SDKs.

## Validation status

The three source-mode vertical slices have completed the real authenticated public-SDK E2E path:

```text
.NET        GREEN
TypeScript GREEN
Python     GREEN
```

Each validated path includes immutable publication, execution submission, a runtime-native OpenAI prompt, Child DAG delegation, durable parent continuation, human input on the same execution, terminal business-result publication, result retrieval, and deterministic replay.

Docker packaging is provided as a reproducible distribution path. It should be validated on the target Docker engine before the containerized path is marked GREEN.

## What the demo shows

```text
external SDK consumer
    ↓
real JWT Bearer
    ↓
POST /auth/access-context
    ↓
server-created RBAC access context
    ↓
public MCP SDK transport
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
durable child result
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

The external SDK consumer does not reference runtime, engine, control-plane, persistence, matrix, or test projects. Runtime authority remains on the server.

## Console modes

Presentation mode is the default. It suppresses noisy raw Watch events and shows the meaningful execution transitions:

```text
[>] Publishing immutable pipeline
[OK] Published interactive-agent-sdk-dotnet@1
[>] Submitting durable execution
[>] Planning
[OK] Planning
[>] Delegated analysis
[WAIT] Delegated analysis is waiting for the child agent
[OK] Child agent completed
[WAIT] Human review boundary reached
...
[OK] Final OpenAI answer
[OK] Business result published
[OK] Execution completed
```

The terminal view displays the actual final OpenAI response together with provider/model/token metadata, execution state, pipeline step states, and the human-review decision.

Set this to restore raw SDK command/watch diagnostics:

```text
AI_DEMO_VERBOSE=true
```

## OpenAI boundary

The pipeline uses the runtime-native `ai.prompt` step with provider `openai`.

The external consumer supplies only the model name through:

```text
OPENAI_MODEL
```

`OPENAI_API_KEY` remains server-owned. It is not serialized into publication content, execution input, Watch events, human input, or SDK transport payloads.

The root `execution.publish-result` publishes the complete final `ai.prompt` data object, not only its text value. This lets the public SDK result expose the real answer plus runtime-produced model/token metadata without adding a demo-only side channel.

## Docker: two-minute path

Requirements:

- Docker with Compose support
- an OpenAI API key

From this directory:

```powershell
Copy-Item .env.docker.example .env
```

Edit `.env` and set:

```text
OPENAI_API_KEY=...
```

Build and start MongoDB, Redis, and the runtime:

```powershell
docker compose up -d --build mongo redis runtime
```

Start the packaged multi-SDK launcher:

```powershell
docker compose --profile demo run --build --rm demo
```

On Windows the same sequence is wrapped by:

```powershell
.\docker-demo.ps1
```

Choose:

```text
1. .NET
2. TypeScript
3. Python
```

At the human-review boundary enter `i`, approve or reject, and optionally provide feedback. After terminal completion enter `x` to run deterministic replay validation.

Stop the environment with:

```powershell
docker compose down
```

To follow runtime logs:

```powershell
docker compose logs -f runtime
```

### Docker topology

```text
docker compose
│
├── mongo
├── redis
├── runtime
│   ├── public MCP :8081
│   ├── JWT/access-context boundary
│   ├── local runtime-instance pool
│   ├── shared queue pump
│   ├── Mongo snapshots/payload persistence
│   ├── Mongo decision ledger/replay metadata
│   ├── Redis bounded payload cache
│   ├── Child DAG composition
│   ├── OpenAI provider
│   └── deterministic replay
│
└── demo (interactive profile)
    ├── packaged .NET external SDK consumer
    ├── packaged TypeScript external SDK consumer
    └── packaged Python external SDK consumer
```

The demo container does **not** receive `OPENAI_API_KEY`; only the runtime service receives it.

For local Docker convenience, the demo container can create its own short-lived HS256 bearer token using the local signing configuration shared with the runtime container. The default symmetric key in `.env.docker.example` is intentionally a local-demo credential and must never be reused in production.

## Native/source-mode build

The bootstrap builds local SDK packages and consumes those packages from the three external demo applications. Nothing is published.

Windows:

```cmd
.\demo\interactive-agent-sdk\scripts\bootstrap.cmd
```

PowerShell:

```powershell
python .\demo\interactive-agent-sdk\scripts\bootstrap.py
```

The smoke validation performed by the bootstrap sends no authentication, access-context, runtime, or OpenAI request.

## Native/source-mode run

Required consumer configuration:

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
AI_DEMO_VERBOSE
```

If `AI_RUNTIME_ACCESS_CONTEXT` is absent, the demo calls the real `/auth/access-context` boundary using `AI_RUNTIME_TOKEN`, captures the returned handle, and lets the SDK transport manage subsequent handle rotation.

Windows:

```cmd
.\demo\interactive-agent-sdk\run.cmd
```

PowerShell:

```powershell
.\demo\interactive-agent-sdk\run.ps1
```

## Local standalone JWT

Production consumers should obtain `AI_RUNTIME_TOKEN` from the trusted identity provider.

For local development only, the repository contains `scripts/create-local-jwt.py`. It creates a real HS256 JWT for the standalone `JwtBearer` host; it does not install a fake authentication handler.

Example host configuration:

```powershell
$env:AiMcpAuthentication__Enabled="true"
$env:AiMcpAuthentication__Issuer="multiplexed-local"
$env:AiMcpAuthentication__Audience="multiplexed-ai-sdk"
$env:AiMcpAuthentication__SymmetricSigningKey="replace-with-at-least-32-bytes-of-local-secret"
$env:OPENAI_API_KEY="<openai-key>"
```

Example consumer token creation:

```powershell
$env:AiMcpAuthentication__Issuer="multiplexed-local"
$env:AiMcpAuthentication__Audience="multiplexed-ai-sdk"
$env:AiMcpAuthentication__SymmetricSigningKey="replace-with-at-least-32-bytes-of-local-secret"
$env:AI_RUNTIME_TOKEN = python .\demo\interactive-agent-sdk\scripts\create-local-jwt.py
```

The helper grants only the capabilities required by this demo:

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

The signing key is issuer authority and must never be distributed to normal production consumers.

## Human input and replay

While the execution is active:

```text
[p] pause
[r] resume
[i] submit human input
[c] cancel
[s] status
[q] detach the local console without cancelling
```

When `await-review` is durably parked, use `i`. The public input operation resumes the same execution and exact waiting step; it does not create a second execution.

After terminal convergence:

```text
[x] deterministic replay validation
[q] exit
```

Replay validates the existing durable execution. It does not re-run OpenAI or create a second execution.

## No matrix or demo-only runtime path

The demo does not use `/matrix/*`, matrix state, test harness APIs, direct runtime stores, internal scheduler/controller services, or a direct `IAiPublicSdkBoundary` reference. All runtime interaction crosses the external public SDK boundary.
