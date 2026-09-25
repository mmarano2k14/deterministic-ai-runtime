# Interactive Agent SDK Demo

**Status:** GREEN in native/source mode and in the packaged Docker presentation path for .NET, TypeScript/JavaScript, and Python external SDK consumers.

## Purpose

The interactive agent demo is a presentation-oriented external-consumer scenario for the public SDK boundary. It demonstrates the same runtime-native durable agent workflow from all three supported SDK client languages without moving orchestration authority into the client.

The demo lives under:

```text
demo/interactive-agent-sdk/
```

The three consumers use the public MCP SDK boundary only. They do not reference runtime, engine, persistence, control-plane, matrix, or test assemblies.

## End-to-end flow

```text
external SDK consumer
    -> Bearer JWT
    -> server-created RBAC access context
    -> public MCP SDK transport
    -> immutable pipeline publication
    -> durable execution submission
    -> root ai.prompt
    -> Child DAG delegation
    -> child ai.prompt
    -> durable child result
    -> parent continuation
    -> execution.await-input
    -> human approval/rejection on the same execution
    -> final ai.prompt
    -> business-result publication
    -> terminal execution result
    -> deterministic replay validation
```

The `ExecutionId` remains unchanged across the human-review wait and resume boundary. The server remains authoritative for publication pinning, scheduling, Child DAG lifecycle, durable waiting, input acceptance, finalization, recovery, and replay.

## Authentication and OpenAI boundary

The demo uses a real Bearer JWT plus the server-created RBAC access-context boundary. When an access-context handle is not supplied, the demo obtains one through the runtime authentication endpoint and lets the SDK transport manage subsequent handle rotation.

`OPENAI_API_KEY` remains runtime-owned. The external SDK consumer sends the selected model name but does not receive or transport the OpenAI credential. The final public result is produced from the runtime-native `ai.prompt` output rather than from a demo-only side channel.

## Presentation console

Presentation mode suppresses low-level Watch noise and keeps the durable state transitions visible:

```text
[>] Publishing immutable pipeline
[OK] Published ...
[>] Submitting durable execution
[>] Planning
[OK] Planning
[>] Delegated analysis
[WAIT] Delegated analysis is waiting for the child agent
[>] Human review
[WAIT] Human review boundary reached
...
[OK] Final OpenAI answer
[OK] Business result published
[OK] Execution completed
```

After completion, the terminal view shows the public result, provider/model metadata, durable execution state, step states, and accepted human-review input. `AI_DEMO_VERBOSE=true` restores raw SDK command and Watch diagnostics.

## Docker presentation path

From `demo/interactive-agent-sdk`:

```powershell
Copy-Item .env.docker.example .env
```

Set the server-owned OpenAI credential in `.env`, then run:

```powershell
.\docker-demo.ps1
```

The launcher offers:

```text
1. .NET
2. TypeScript
3. Python
```

At the human-review boundary, enter `i`, approve or reject, and optionally add feedback. After terminal completion, enter `x` to execute deterministic replay validation.

The Compose topology contains MongoDB, Redis, the runtime/control-plane host, and one packaged demo image containing the independently built .NET, TypeScript, and Python external consumers. The demo container does not receive `OPENAI_API_KEY`.

## Local SDK package consumption

The Docker presentation image builds and consumes local external SDK artifacts rather than runtime assemblies. The .NET demo uses the local package identity:

```text
Multiplexed.AI.Sdk.Contracts 0.0.0-local
Multiplexed.AI.Sdk           0.0.0-local
```

The Docker build verifies that the external consumer resolves those exact local packages before publish. Package and publish output directories are passed explicitly as MSBuild properties so the container build remains stable with the repository's `.packages/dotnet` and `/out/dotnet` directory names.

## Validated Docker matrix

The containerized presentation path is GREEN for all three external SDK clients:

| External SDK consumer | Durable execution | Human review/resume | Terminal result | Deterministic replay |
| --- | --- | --- | --- | --- |
| .NET | `Completed` | GREEN | all pipeline steps `Completed` | GREEN |
| TypeScript | `Completed` | GREEN | all pipeline steps `Completed` | GREEN |
| Python | `Completed` | GREEN | all pipeline steps `Completed` | GREEN |

Each validated path exercised immutable publication, durable submission, runtime-native OpenAI execution, Child DAG delegation, parent continuation, human input on the same execution, business-result publication, terminal result retrieval, and deterministic replay.

One Python presentation run observed a transient business-result publication failure followed by a successful retry/convergence before terminal `Completed`; deterministic replay then succeeded. This demonstrates successful durable convergence for that run, not a claim that publication always succeeds on the first attempt.

## What this validation proves

The demo establishes that:

- independently packaged .NET, TypeScript, and Python SDK consumers can drive the same real public-runtime workflow;
- authentication and RBAC context remain server-controlled;
- human review parks and resumes the same durable execution;
- Child DAG delegation and parent continuation remain runtime-owned;
- the terminal public result can expose the actual runtime-native OpenAI result and metadata;
- deterministic replay succeeds after completion for all three Docker consumers;
- the containerized consumers remain separated from runtime implementation assemblies and credentials.

## What this validation does not prove

This presentation scenario does not replace the broader runtime matrices. It does not by itself prove:

- full client-language x hosted-worker-language coverage;
- every recovery/failure boundary exercised by the 37-scenario Docker runtime matrix;
- KubernetesPool parity for all three SDK clients;
- public NuGet/npm/Python-registry publication;
- generic exactly-once external side effects;
- production identity-provider configuration or production secret management.

For those boundaries, use the dedicated validation documents.

## Related documentation

- [External SDK Quickstart](external-sdk-quickstart.md)
- [External SDK Libraries](external-sdk-libraries.md)
- [External SDK Libraries Validation](external-sdk-libraries-validation.md)
- [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md)
- [Public SDK Boundary](public-sdk-boundary.md)
- [Replay and Audit](replay-and-audit.md)
