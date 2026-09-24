# Interactive Agent SDK Demo

This directory demonstrates a real external application consuming the Deterministic AI Runtime through the public SDK boundary.

## Current status

```text
.NET interactive agent vertical slice     IMPLEMENTED / TARGET E2E VALIDATION REQUIRED
TypeScript consumer scaffold              AVAILABLE
Python consumer scaffold                  AVAILABLE
```

The .NET consumer references only the locally packed external `Multiplexed.AI.Sdk` package.

It does not reference runtime, engine, control-plane, persistence, matrix, or test projects.

## .NET execution flow

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

The child definition contains no further Child DAG call site, so delegation is structurally bounded to one level in this demo.

## No matrix or demo-only control path

The .NET agent does not use:

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

The external .NET consumer reads:

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

The current public SDK submission boundary carries the initial JSON document into the runtime's existing execution input slot.

For this demo the submitted document is:

```json
{
  "userPrompt": "...",
  "requestId": "..."
}
```

The root prompt receives that document through `state.input` and explicitly reads the `userPrompt` field. No diagnostic metadata is used as business input.

## Child result consumption

The child agent publishes its analysis with:

```text
execution.publish-result
```

The parent consumes the frozen Child DAG payload through:

```text
steps.delegate-analysis.result.payload.data.result
```

This uses the same durable Child DAG result snapshot and payload resolver used by the runtime; there is no demo-specific side channel.

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

The console also runs `sdk.execution.watch` and prints public snapshots/events.

After terminal convergence:

```text
[x] deterministic replay validation
[q] exit
```

Replay validates the existing durable execution and does not create a second execution.

## Configuration

Required for the .NET vertical slice:

```text
AI_RUNTIME_ENDPOINT
OPENAI_MODEL
```

Optional public transport configuration:

```text
AI_RUNTIME_TOKEN
AI_RUNTIME_ACCESS_CONTEXT
AI_RUNTIME_ACCESS_CONTEXT_HEADER
```

`AI_RUNTIME_DOTNET_ENVIRONMENT_REF` is not required by this native-step .NET slice.

The TypeScript and Python scaffold consumers still require their language-specific environment references.

## Build

The existing bootstrap remains local-package only:

```cmd
.\demo\interactive-agent-sdk\scripts\bootstrap.cmd
```

It packs the SDKs locally and publishes nothing.

The .NET smoke run uses:

```text
AI_DEMO_SMOKE=1
```

and only constructs the external SDK client. It sends no runtime request.

## Run

After bootstrap and runtime configuration:

```cmd
.\demo\interactive-agent-sdk\run.cmd
```

Choose:

```text
1. .NET
```

## Validation status

The code is prepared against the current public SDK contracts and the already validated runtime primitives:

```text
execution.child-dag
execution.await-input
execution.publish-result
steps.<step>.result.payload...
```

A target-environment E2E run is still required before the complete .NET agent flow is marked GREEN.
