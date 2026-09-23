# Interactive Agent SDK Demo

This directory is the consumer-facing demonstration for the external .NET, TypeScript / JavaScript, and Python SDKs.

## Current increment

The current increment establishes only the clean consumer foundation:

- one interactive launcher;
- one independent consumer project per SDK language;
- local package consumption for the external SDKs;
- shared public connection/environment configuration;
- no direct runtime/engine project references.

The interactive OpenAI agent, Child DAG, Watch timeline, Human Input, Pause/Resume, Cancel, and Replay behavior are **not implemented or validated by this scaffold yet**.

## Local packages only

The bootstrap builds local package artifacts and never publishes them.

```text
.NET        -> local .nupkg
TypeScript -> local .tgz
Python     -> local .whl
```

Generated package artifacts are stored under:

```text
demo/interactive-agent-sdk/.packages/
```

The bootstrap contains no `dotnet nuget push`, `npm publish`, or Python package upload operation.

Normal dependency restore/install may download third-party dependencies from their configured package registries.

## Build and validate the consumers

Windows:

```cmd
.\demo\interactive-agent-sdk\scripts\bootstrap.cmd
```

or directly:

```powershell
python .\demo\interactive-agent-sdk\scripts\bootstrap.py
```

The bootstrap:

1. deletes stale local demo packages and consumer build outputs;
2. packs `Multiplexed.AI.Sdk.Contracts` and `Multiplexed.AI.Sdk` with the local-only version `0.0.0-local`;
3. verifies the expected NuGet packages exist;
4. packs the local TypeScript SDK tarball;
5. builds the local Python SDK wheel;
6. restores the .NET consumer into a demo-local NuGet cache and verifies `project.assets.json` resolved the local SDK version;
7. builds the .NET and TypeScript consumers;
8. installs the local Python wheel in the demo virtual environment;
9. starts all three consumers with smoke configuration.

The smoke checks construct the external SDK clients but intentionally do not send a runtime request.

The bootstrap fails immediately if any native command returns a non-zero exit code. It prints `READY` only after all package, build, install, and smoke checks pass.

## Run

After bootstrap succeeds and the real environment variables are configured:

```cmd
.\demo\interactive-agent-sdk\run.cmd
```

or:

```powershell
python .\demo\interactive-agent-sdk\launcher\launcher.py
```

The launcher presents:

```text
==================================================
 Deterministic AI Runtime - Interactive SDK Agent
==================================================

Choose SDK:

  1. .NET
  2. TypeScript
  3. Python
```

## Configuration

Required by the current scaffold:

```text
AI_RUNTIME_ENDPOINT
AI_RUNTIME_DOTNET_ENVIRONMENT_REF
AI_RUNTIME_TYPESCRIPT_ENVIRONMENT_REF
AI_RUNTIME_PYTHON_ENVIRONMENT_REF
```

Only the language-specific environment ref for the selected SDK is required by the launcher.

Optional public transport configuration:

```text
AI_RUNTIME_TOKEN
AI_RUNTIME_ACCESS_CONTEXT
AI_RUNTIME_ACCESS_CONTEXT_HEADER
```

Reserved for the next agent implementation increment:

```text
OPENAI_API_KEY
OPENAI_MODEL
```

The scaffold reports whether those OpenAI variables are configured but does not consume them.

## Consumer boundary

The demo consumes external SDK packages only.

It must not reference runtime/engine, control-plane, persistence, queue, DAG-store, invocation-journal, or matrix/test projects.

```text
demo application
    ↓
external SDK package
    ↓
MCP Streamable HTTP
    ↓
public SDK boundary
    ↓
runtime
```

## Next increment

The next increment will implement the first real agent vertical slice in the .NET consumer before reproducing the same behavior independently in TypeScript and Python.

No agent behavior is claimed by this scaffold.
