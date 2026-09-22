# Context Resolution and Helpers

Status: Documentation split in progress.

This document describes the context resolution and helper layer used by the Deterministic AI Runtime.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

The runtime is config-driven, policy-driven, plugin-driven, and state-driven.

To make these pieces work together, the runtime needs a layer that transforms declarative configuration and execution state into concrete runtime contexts.

This layer is responsible for resolving:

- execution context
- step context
- input bindings
- state values
- previous step outputs
- payload references
- rehydrated payloads
- provider metadata
- model metadata
- operation metadata
- retry context
- retention context
- concurrency context
- policy context
- RAG retrieval context

The purpose of context resolution is simple:

```text
Declarative pipeline configuration
        +
runtime execution state
        +
payload / resolver data
        ↓
concrete context used by steps, policies, and runtime services
```

This keeps the execution engine clean and prevents every service from manually rebuilding runtime context.

---

## Why Context Resolution Matters

Without a context resolution layer, the runtime would quickly become hard to maintain.

Every runner, step executor, policy, resolver, and helper would need to manually know:

- how pipeline input is stored
- how previous step outputs are represented
- how compacted payloads are rehydrated
- how provider/model/operation metadata is read
- how retry context is built
- how retention context is built
- how concurrency context is built
- how policy context is built
- how RAG context is normalized

That would create duplicated logic and inconsistent behavior.

Context resolution centralizes this work.

The result is:

```text
Engine remains orchestration-focused.
Helpers resolve context.
Plugins execute domain behavior.
Policies decide runtime behavior.
Runtime stores state safely.
```

---

## Core Principle

The core principle is:

```text
The runtime should not scatter context-building logic across the engine.
```

Context-building should be explicit, reusable, and testable.

A step executor should receive a clear execution context.

A policy should receive a clear policy context.

A concurrency engine should receive a clear concurrency context.

A retention engine should receive a clear retention context.

A RAG provider should receive a normalized retrieval context.

---

## Runtime Context vs Step Context

The runtime works with different levels of context.

### Runtime Execution Context

The runtime execution context describes the overall execution.

It may include:

- `ExecutionId`
- pipeline name
- pipeline version
- execution mode
- runtime instance id
- initial input state
- execution status
- execution metadata
- control state
- DAG state reference

### Step Execution Context

The step execution context describes one step being executed.

It may include:

- `ExecutionId`
- step name
- step key
- resolved input
- step configuration
- upstream results
- provider metadata
- model metadata
- operation metadata
- cancellation token
- payload resolver access
- observability context

The runtime context is broader.

The step context is focused on executing one step safely.

---

## Input Binding Resolution

Input bindings describe how a step receives data.

Example:

```json
{
  "input": {
    "candidateId": "state.candidateId",
    "jobId": "state.jobId",
    "context": "steps.merge.result.data"
  }
}
```

The input resolver is responsible for converting this declarative input into concrete values.

A simplified flow is:

```text
Step input binding
        ↓
Resolve source expression
        ↓
Read from execution state or previous step output
        ↓
Rehydrate payload if needed
        ↓
Build resolved input object
        ↓
Pass input to step executor
```

This keeps steps from manually walking execution state.

---

## State-Based Inputs

State-based inputs read from the initial execution state or runtime state.

Example:

```json
{
  "candidateId": "state.candidateId"
}
```

This means:

```text
Read candidateId from execution input state.
```

The input resolver should handle:

- missing values
- null values
- nested paths
- type conversion where appropriate
- diagnostics when resolution fails

---

## Previous Step Output Resolution

Many steps depend on outputs from previous steps.

Example:

```json
{
  "context": "steps.merge.result.data"
}
```

This means:

```text
Read result.data from the completed step named merge.
```

The resolver must ensure:

- the source step exists
- the source step is completed
- the required output exists
- compacted payloads can be rehydrated
- missing outputs produce clear diagnostics

This is especially important for DAG workflows where downstream steps consume upstream results.

---

## Payload Reference Resolution

Retention and compaction may move large step outputs out of Redis hot state.

When this happens, step outputs may be represented as references instead of inline data.

The resolver must detect:

- inline payload data
- compacted payload references
- archived payload references
- evicted step data
- externalized result objects

Then it must load the required data from the appropriate storage layer.

A simplified flow is:

```text
Step needs upstream output
        ↓
Output is inline?
        ↓
Yes: return directly
No:
        ↓
Resolve payload reference
        ↓
Load from cache / archive / payload store
        ↓
Return rehydrated value
```

This allows retention to reduce hot state without breaking step execution.

---

## Resolver and Rehydration

Rehydration is the process of restoring externalized data when needed.

The resolver may use a layered lookup model:

```text
Redis hot state
        ↓
local in-memory cache
        ↓
Redis cached payload index
        ↓
MongoDB archive/index
        ↓
payload store
```

The exact implementation can evolve, but the principle remains:

```text
Steps should not care whether input came from Redis hot state or durable payload storage.
```

The resolver hides storage complexity from step executors.

---

## Provider / Model / Operation Context

AI and RAG steps often need provider metadata.

Step configuration may include:

```json
{
  "provider": "openai",
  "model": "gpt-4.1",
  "operation": "llm.chat"
}
```

RAG steps may include:

```json
{
  "provider": "relational",
  "providerKey": "sqlserver",
  "operation": "candidate.byId",
  "executionMode": "provider"
}
```

The context helper should normalize this metadata into a runtime context.

This metadata can then be used by:

- step plugins
- provider dispatchers
- concurrency policies
- generic throttle rules
- observability
- future cost governance

---

## Concurrency Context Helpers

The concurrency engine needs a context describing what is about to execute.

A concurrency context may include:

- execution id
- pipeline key
- step name
- step key
- runtime instance id
- provider
- model
- operation

The helper layer builds this context from pipeline definition, step definition, and runtime metadata.

A simplified flow is:

```text
Pipeline config + step config
        ↓
Resolve provider/model/operation
        ↓
Build AiConcurrencyContext
        ↓
Apply concurrency policies
        ↓
Acquire Redis concurrency lease
        ↓
Attempt DAG claim
```

This ensures provider/model/operation throttling is applied consistently.

---

## Policy Context Helpers

Policies need structured runtime context.

Retry policies may need:

- failure reason
- exception metadata
- retry count
- step metadata
- provider/model/operation metadata

Retention policies may need:

- completed step count
- inline payload size
- execution state size
- terminal lifecycle state
- payload references

Concurrency policies may need:

- provider
- model
- operation
- pipeline key
- step key
- runtime instance id

The helper layer should build the correct policy context for each engine.

This avoids every policy engine duplicating context extraction logic.

---

## Retry Context Resolution

Retry context is created when a step fails.

It may include:

- execution id
- step name
- step key
- failure details
- current retry count
- retry configuration
- provider/model/operation metadata
- policy definitions
- previous retry state

The retry engine uses this context to evaluate configured retry policies and compute the next retry decision.

---

## Retention Context Resolution

Retention context is created when retention triggers are evaluated.

It may include:

- execution id
- completed step count
- inline payload sizes
- hot state size estimate
- payload references
- terminal lifecycle state
- configured retention policies
- replay/snapshot requirements

The retention engine uses this context to compute retention decisions and produce a retention execution plan.

---

## RAG Context Resolution

RAG steps depend heavily on context resolution.

A `rag.retrieval` step may need:

- provider
- providerKey
- operation
- executionMode
- input bindings
- query parameters
- previous step references
- retrieval-specific config

A `rag.merge` step may need:

- source steps
- resolved upstream outputs
- stable source ordering
- payload rehydration

A `rag.compose` step may need:

- source step
- composer key
- merged data
- deterministic composition settings

The context resolver ensures that RAG steps receive normalized, ready-to-use context instead of manually reading raw DAG state.

---

## Context Resolution and Step Plugins

Step plugins should not manually rebuild runtime context.

Instead, the runtime should provide:

- resolved input
- step configuration
- execution metadata
- payload resolver access
- observability context

This keeps plugin code focused on domain-specific behavior.

Example:

```text
Bad:
Plugin manually searches execution state for upstream results.

Good:
Input resolver gives plugin the resolved upstream data.
```

This improves maintainability and reduces bugs.

---

## Context Resolution and Replay

Replay depends on consistent context resolution.

If replay restores execution state, the resolver should still be able to resolve:

- restored step outputs
- payload references
- archived payloads
- retry state
- terminal status
- replay-safe metadata

Context resolution must not rely only on volatile runtime fields such as:

- old claim tokens
- old worker ids
- expired leases
- process-local state

Replay-safe context should be derived from durable execution state and payload references.

---

## Context Resolution and Observability

Context helpers should provide enough metadata for observability.

Useful context includes:

- execution id
- run id when available
- pipeline name
- pipeline version
- step name
- step key
- provider
- provider key
- model
- operation
- policy key
- concurrency scope
- payload reference
- resolver source

This allows logs, metrics, traces, and diagnostics to explain what happened.

---

## Helper Classes and Engine Decomposition

The runtime should avoid putting all orchestration logic into one large engine class.

Helper classes can isolate specific responsibilities such as:

- execution creation
- pipeline resolution
- input binding resolution
- step context building
- concurrency context building
- policy context building
- retention context building
- payload resolution
- replay fingerprint creation
- finalization support

This keeps the engine smaller and easier to test.

The goal is not to create unnecessary abstraction.

The goal is to isolate repeatable runtime responsibilities.

---

## Common Helper Responsibilities

Typical helper responsibilities include:

| Helper Area | Responsibility |
|---|---|
| Pipeline resolver | Normalize and validate pipeline definitions. |
| Input resolver | Resolve state and step output bindings. |
| Step context builder | Build context passed to step executors. |
| Payload resolver | Rehydrate compacted or externalized payloads. |
| Concurrency context builder | Build provider/model/operation-aware concurrency context. |
| Policy context builder | Build retry, retention, or concurrency policy context. |
| RAG context resolver | Normalize retrieval/merge/compose context. |
| Replay helper | Build fingerprints and replay-safe comparison data. |
| Finalization helper | Support terminal status and snapshot lifecycle. |

---

## Safety Rules

Context resolution should follow these rules:

1. Do not duplicate context-building logic across runtime services.
2. Resolve inputs before executing step plugins.
3. Fail clearly when required input paths are missing.
4. Do not bypass payload rehydration for compacted data.
5. Keep provider/model/operation metadata normalized.
6. Keep policy context explicit and testable.
7. Do not let plugins mutate DAG state directly.
8. Do not rely on volatile claim tokens for replay-safe context.
9. Preserve stable step names and step keys.
10. Keep context resolution deterministic for the same execution state.

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Input binding references missing state value | Resolver fails clearly with diagnostics. |
| Input binding references missing step | Resolver fails before plugin execution. |
| Source step is not completed | Step should not execute or resolver should fail safely. |
| Source payload was compacted | Resolver rehydrates from payload store. |
| Source payload was evicted | Archive-backed resolver reconstructs data if available. |
| Provider metadata is missing | Policy or provider resolver may deny/fail depending on contract. |
| Unknown providerKey | Provider resolver fails safely. |
| Unknown operation | Dispatcher fails safely. |
| Policy context cannot be built | Runtime should deny or fail safely with diagnostics. |
| Replay tries to use stale claim metadata | Replay-safe context ignores volatile ownership fields. |

---

## Validated Behavior

The context resolution and helper layer is validated through runtime usage and tests covering:

- input binding resolution from execution state
- previous step output resolution
- RAG retrieval context construction
- RAG merge source resolution
- RAG compose source resolution
- provider/model/operation metadata propagation
- concurrency context construction
- policy context construction
- payload resolver and rehydration behavior
- retention-compatible output resolution
- replay-safe fingerprint and restored-state comparison foundations
- failure when required context cannot be resolved

Dedicated public documentation for every helper class remains in progress.

---

## Current Status

| Capability | Status |
|---|---|
| Input binding resolution | Implemented / foundation available |
| Previous step output resolution | Implemented / foundation available |
| Step execution context construction | Implemented / foundation available |
| Payload reference resolution | Implemented / validated foundations |
| Resolver-backed rehydration | Implemented / validated |
| Provider/model/operation context | Implemented / validated |
| Concurrency context construction | Implemented / validated |
| Retry policy context construction | Implemented / validated |
| Retention context construction | Implemented / validated |
| RAG context resolution | Implemented / foundation available |
| Replay-safe fingerprint helpers | Implemented / validated foundations |
| Full public helper API documentation | Planned |
| Advanced schema-based input validation | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Input resolver | Resolves input bindings from state and previous step outputs. |
| Step context builder | Creates execution context for step plugins. |
| Payload resolver | Rehydrates compacted or evicted payloads. |
| Provider context helper | Extracts provider/providerKey/model/operation metadata. |
| Concurrency context builder | Builds context used by concurrency policies and Redis gate. |
| Policy context builder | Builds context for retry, retention, and concurrency policies. |
| RAG context resolver | Builds retrieval, merge, and compose context. |
| Replay helper | Builds replay-safe fingerprints and comparison data. |
| Engine helper services | Keep orchestration classes smaller and testable. |

---

## Summary

Context resolution is the connective tissue of the runtime.

It turns declarative pipeline configuration and execution state into the concrete data required by:

- step plugins
- policy engines
- concurrency admission
- retention decisions
- RAG providers
- replay validation
- observability

Without this layer, the runtime would scatter context-building logic across the engine and plugins.

With it, the runtime stays modular, testable, and deterministic.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Step Plugins](step-plugins.md)
- [RAG Pipelines](rag-pipelines.md)
- [Distributed Execution](distributed-execution.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability](observability.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
