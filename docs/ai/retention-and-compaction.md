# Retention and Compaction

Status: Documentation split in progress.

This document describes the retention, compaction, payload externalization, and rehydration model used by the Deterministic AI Runtime to keep hot execution state bounded.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

AI workflows can generate large and growing amounts of data.

Examples include:

- LLM responses
- retrieved documents
- RAG batches
- merged contexts
- composed prompts
- intermediate structured outputs
- execution metadata
- traces and diagnostics

If all of this data remains inline in Redis hot state, execution state can grow without control.

The retention and compaction system exists to prevent unbounded hot state growth while preserving execution correctness.

The goal is:

```text
Keep Redis fast and bounded.
Preserve full data in durable storage.
Allow rehydration when data is needed again.
```

---

## Why This Matters

Adaptive memory management enables:

- long-running workflows
- large-scale AI pipeline execution
- controlled Redis memory usage
- controlled infrastructure cost
- stable runtime performance
- replay and audit foundations
- deterministic execution under memory pressure

Without adaptive memory control, AI pipelines quickly become impractical in production.

Large payloads would accumulate in Redis, execution state would become expensive to read and write, and long-running workflows would eventually become unstable.

The retention system makes memory behavior controlled instead of accidental.

---

## Adaptive Memory Model

Retention is not a one-time cleanup operation.

It is a continuous runtime process applied during execution and terminal lifecycle handling.

The adaptive memory model is based on:

- retention
- compaction
- controlled eviction
- payload externalization
- resolver-backed rehydration

The runtime decides which data should remain hot and which data should move to durable storage.

This keeps Redis lightweight while ensuring that no required data is lost.

---

## Hot State vs Cold Storage

The runtime separates active execution state from durable payload storage.

```text
Redis
= hot state
= fast coordination state
= active execution data
= minimal metadata required for scheduling

MongoDB / payload store
= cold durable storage
= large payloads
= snapshots
= archived step data
= replay and audit foundations
```

Redis should not become a long-term document store.

MongoDB or another durable payload store is used for larger execution data.

This hybrid memory model allows the runtime to preserve correctness while keeping active state small.

---

## Why Retention Is Required

Without retention, AI runtime state can grow indefinitely.

This creates several risks:

- Redis memory pressure
- slower execution state reads
- expensive hot infrastructure
- large serialized execution records
- difficult replay and inspection
- poor performance for large DAGs
- unstable long-running workflows

Retention makes memory growth predictable.

---

## Core Concept

Retention determines which execution data should remain hot and which data should be externalized, compacted, or evicted.

The runtime must preserve enough information to continue execution safely.

Retention must never break:

- DAG dependency evaluation
- completed step resolution
- retry state
- replay foundations
- finalization
- deterministic convergence

---

## Retention Triggers

Retention is not a random cleanup operation.

It is triggered by explicit conditions such as:

- number of completed steps in hot state
- inline payload size
- total estimated execution state size
- execution duration
- configured retention policy
- terminal lifecycle processing
- memory pressure thresholds
- step-specific retention rules

Examples:

```text
MaxCompletedStepsInState exceeded
        ↓
Retention evaluation runs

InlinePayloadBytes exceeds threshold
        ↓
Compaction is selected
```

Triggers decide **when** retention should run.

Policies and the retention engine decide **what** should happen.

---

## Retention Decision Flow

Retention is applied through a structured process.

The intended flow is:

```text
Retention trigger
        ↓
Retention engine
        ↓
Resolve config.retention
        ↓
Execute retention policies
        ↓
Compute retention decision
        ↓
Create retention execution plan
        ↓
Apply compaction / eviction safely
```

The original runtime design separates:

1. trigger evaluation
2. retention decision computation
3. policy application
4. execution plan creation
5. compaction / eviction execution

This keeps retention predictable, controlled, and consistent across executions.

---

## Policy-Driven Retention

Retention is part of the shared Policy Engine V2 model.

The runtime uses a unified policy model across:

- retry
- retention
- concurrency

Each engine resolves its own configuration section:

- `config.retry`
- `config.retention`
- `config.concurrency`

For retention, this means retention behavior is not only hardcoded in the retention coordinator.

Retention decisions are policy-driven through configured retention policies resolved by the policy engine.

Retention policies can influence decisions such as:

- whether compaction should be applied
- whether eviction should be applied
- whether a hybrid retention strategy should be used
- which completed steps should remain in hot state
- which payloads should be externalized
- whether retention is safe for the current execution state
- whether a terminal snapshot or replay foundation requires preservation

This mirrors the same architectural principle used by retry and concurrency:

```text
Runtime behavior is configured and policy-driven.
State mutation remains controlled by the runtime.
```

---

## Retention Strategies

The runtime uses three main retention strategies:

```text
Compact
Evict
Hybrid
```

Each strategy has a different purpose.

---

## Compaction

Compaction reduces memory usage by removing large inline payloads from Redis while keeping the step visible in hot state.

After compaction:

- step metadata remains in Redis
- step status remains available
- dependency information remains available
- references to external payloads remain available
- full payload is stored externally
- Redis keeps a lightweight reference

This is useful when the step is still logically important but its full data does not need to remain inline.

Example:

```text
Completed step with large result
        ↓
Persist full payload externally
        ↓
Replace inline result with reference metadata
        ↓
Keep step visible in Redis state
```

The structure of the execution state remains intact.

Downstream steps can still resolve the data through the resolver.

---

## Eviction

Eviction removes selected completed step data from hot state more aggressively.

After eviction:

- full step data is stored externally
- the step may no longer be fully present in Redis hot state
- references may remain for lookup
- resolver or archive index is responsible for future access
- execution state remains safe because required references are preserved

Eviction is useful when:

- memory pressure is high
- step data is no longer needed directly in hot state
- data can be reconstructed or resolved from durable storage

Eviction must only happen when it is safe.

---

## Hybrid Retention

Hybrid retention combines compaction and eviction.

A typical hybrid flow is:

```text
Large completed payload
        ↓
Compact payload first
        ↓
Persist full data externally
        ↓
Keep metadata/reference hot
        ↓
Later evict from hot state when safe
        ↓
Resolve from archive/payload store if needed
```

Hybrid retention provides a balance between:

- performance
- keeping enough metadata hot
- reducing Redis memory usage
- preserving full historical data
- allowing downstream resolution

---

## Payload Externalization

Payload externalization stores large step outputs outside Redis.

The externalized payload may include:

- full LLM response
- retrieved documents
- RAG result batches
- merged context
- composed context
- structured output data
- large intermediate step outputs

Redis keeps only:

- step status
- payload reference
- payload id
- minimal metadata
- resolution hints
- archive/index reference if required

This allows the runtime to continue executing while keeping hot state small.

---

## What Stays in Redis

After compaction or externalization, Redis should keep enough information for runtime execution to continue.

This may include:

- step status
- dependency metadata
- minimal execution metadata
- payload references
- identifiers required for resolver lookup
- retry state if the step is still active or retryable
- claim ownership state if the step is running
- control and finalization metadata where required

This ensures that:

- the DAG can still be evaluated
- dependencies remain valid
- execution can continue
- replay and resolver foundations remain intact

---

## Payload Storage

Externalized payloads are stored in a persistent layer.

This layer is designed for:

- large data storage
- durability
- efficient retrieval
- flexible schema
- nested payload structures
- replay and audit foundations

MongoDB or a dedicated payload store can act as the durable payload layer.

---

## Safe Retention Order

Retention must be applied in a safe order.

A safe retention order is:

```text
1. Identify retention candidates
2. Persist full payload externally
3. Write payload/index reference
4. Compact hot state
5. Optionally evict hot data
6. Validate resolver can still access required data
```

The runtime must never remove hot data before the full payload is safely stored.

---

## Resolver and Rehydration

Rehydration is the process of retrieving externalized data when it is needed again.

Instead of keeping all data in Redis at all times, the runtime:

- loads data on demand
- reconstructs the required context
- continues execution normally

The resolver is responsible for rehydration.

This allows the runtime to reduce memory usage while still providing full data access.

---

## Layered Resolver Architecture

The resolver can use a layered approach to retrieve data efficiently.

A typical resolution order is:

```text
Redis hot state
        ↓
local in-memory cache
        ↓
Redis / cached payload index
        ↓
MongoDB archive/index
        ↓
payload store
```

Each layer acts as a fallback for the previous one.

This keeps frequent access fast while allowing cold data to be recovered from durable storage.

---

## Resolution Flow

When a step needs data:

```text
Step needs data
        ↓
Resolver checks Redis hot state
        ↓
If not found, resolver checks local/cache layer
        ↓
If still not found, resolver checks MongoDB/archive index
        ↓
If necessary, resolver loads full payload
        ↓
Resolved data is returned to the runtime or step
```

This process should be transparent to step executors.

A step should not need to know whether its input was still hot in Redis or rehydrated from durable storage.

---

## Archive-Backed Reconstruction

When step data is evicted from hot state, the runtime may rely on archive-backed reconstruction.

Archive-backed reconstruction allows the resolver to locate and rebuild required completed step data.

This is important for:

- downstream dependency resolution
- replay validation
- terminal snapshot consistency
- large DAG execution
- memory-bounded hot state

The runtime must be able to prove that retention does not make required completed steps inaccessible.

---

## Retention and DAG Dependencies

Retention must respect DAG dependency requirements.

A completed step may still be needed by downstream steps.

Therefore, retention cannot simply delete completed data blindly.

The runtime must ensure that downstream steps can still resolve:

- dependency status
- required output value
- payload reference
- archived result data

This is why compaction and resolver-backed rehydration are required.

---

## Retention and Retry

Retry state for active or retryable steps must remain available.

Retention should not remove state required for:

- `WaitingForRetry`
- retry count
- next retry time
- failure reason
- claim ownership
- active execution control

Retention is primarily concerned with completed and safely externalizable data.

---

## Retention and Replay

Retention supports replay by ensuring full execution data can be reconstructed from durable storage.

Replay foundations depend on:

- terminal snapshots
- payload references
- archived step data
- deterministic execution fingerprints
- restored state comparison

Retention must preserve enough information for replay validation.

A compacted or evicted payload should still be recoverable if replay or audit requires it.

---

## Retention and Observability

Retention behavior is observable.

Important signals include:

- retention triggered
- retention policy selected
- retention decision timing
- compact count
- evict count
- hybrid action count
- payload externalized
- resolver hit or miss
- archive reconstruction success
- archive reconstruction failure
- hot state size before retention
- hot state size after retention

These signals help:

- monitor memory usage
- tune retention policies
- optimize system behavior
- detect unsafe retention
- diagnose resolver failures

---

## Retention and Distributed Execution

Retention must be safe under distributed execution.

Multiple workers may be advancing execution while retention occurs.

The runtime must ensure:

- retention does not remove data required by active work
- state transitions remain atomic
- completed data is externalized before removal
- resolver can reconstruct evicted data
- finalization remains deterministic
- stale workers cannot overwrite compacted state incorrectly

Retention should be coordinated through the runtime lifecycle rather than arbitrary mutation.

---

## Inline Payload Size

The runtime may estimate inline payload size to decide whether compaction is needed.

A large inline payload can be externalized to reduce Redis pressure.

Conceptually:

```text
Step result contains large data
        ↓
Inline size exceeds threshold
        ↓
Payload is persisted externally
        ↓
Inline result is replaced by reference
```

After compaction, inline size should be reduced or cleared.

---

## Terminal Retention

Terminal lifecycle processing may trigger retention work.

When an execution completes, fails, or is cancelled, the runtime may need to:

- persist terminal snapshot
- externalize large payloads
- compact hot state
- evict unnecessary hot state
- preserve replayable state
- preserve audit-relevant data

Terminal retention must be careful not to delete data required by replay.

---

## Example: Compaction Flow

```text
Step completed with large RAG result
        ↓
Retention trigger detects inline payload size
        ↓
Retention engine resolves config.retention
        ↓
Retention policy allows compaction
        ↓
Full result is stored in payload store
        ↓
Payload reference is attached to step metadata
        ↓
Inline data is removed from Redis
        ↓
Step remains Completed
        ↓
Downstream resolver can rehydrate result
```

---

## Example: Eviction Flow

```text
Execution has many completed steps
        ↓
Retention threshold exceeded
        ↓
Retention policy selects eviction candidate
        ↓
Full step data persisted externally
        ↓
Archive/index reference is stored
        ↓
Step payload removed from hot state
        ↓
Resolver reconstructs if needed later
```

---

## Example: Hybrid Flow

```text
Large completed step
        ↓
Retention policy selects hybrid strategy
        ↓
Compact first
        ↓
Keep metadata hot
        ↓
Later memory threshold still exceeded
        ↓
Evict from hot state
        ↓
Archive-backed resolver remains responsible for access
```

---

## Safety and Guarantees

Retention must not break execution.

The runtime must ensure that:

- dependencies are respected
- required data remains accessible
- step outputs remain consistent
- downstream steps can still execute
- replay foundations remain valid
- deterministic convergence is preserved
- finalization can still create required snapshots
- retention failures do not destructively remove data

This is critical for maintaining deterministic runtime behavior.

---

## Safety Rules

Retention must follow these safety rules:

1. Never delete full data before external persistence succeeds.
2. Never remove active running step state required for ownership.
3. Never remove retry state required for scheduling.
4. Never break dependency resolution.
5. Never break terminal snapshot creation.
6. Never break replay validation.
7. Always preserve a resolvable reference for externalized data.
8. Always make retention behavior observable.
9. Prefer compaction before eviction when downstream access may still be required.
10. Keep hot state bounded without losing execution correctness.

---

## Should Everything Be Controlled?

The runtime should not control every business detail.

It should control every runtime behavior that can affect correctness, safety, cost, replayability, or distributed convergence.

That includes:

- execution state transitions
- step ownership
- retry decisions
- recovery decisions
- retention decisions
- concurrency admission
- throttling
- cancellation
- pause/resume behavior
- human-in-the-loop blocking
- snapshot and replay boundaries
- payload externalization
- resolver reconstruction

The runtime should not control:

- business-specific step logic
- the exact domain meaning of a step result
- application UI decisions
- product-specific workflows beyond declared pipeline configuration
- provider-specific internal behavior outside runtime contracts

The architecture is therefore:

```text
Runtime controls execution guarantees.
Plugins control domain behavior.
Configuration and policies control runtime decisions.
```

This keeps the system enterprise-safe without turning the runtime into a monolith that owns every business decision.

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Large step payload | Payload is externalized and compacted. |
| Too many completed steps in hot state | Retention selects compaction or eviction candidates. |
| Downstream step needs compacted data | Resolver rehydrates from payload store. |
| Evicted completed step is required later | Archive-backed resolver reconstructs it. |
| Replay requires terminal data | Snapshot and payload references preserve replay foundations. |
| Redis memory pressure grows | Retention reduces hot state footprint. |
| Payload externalization fails | Hot state should not be destructively compacted. |
| Resolver cannot find payload | Runtime records failure and exposes diagnostics. |
| Retention runs during distributed execution | Atomic transitions and resolver references preserve correctness. |

---

## Validated Behavior

The retention and compaction implementation is validated through tests covering:

- retention trigger evaluation
- policy-driven retention decisions
- `config.retention` resolution
- compaction
- eviction
- hybrid retention
- payload externalization
- resolver rehydration
- archive-backed reconstruction foundations
- completed step accessibility after retention
- terminal lifecycle compatibility
- replay compatibility foundations
- hot state reduction without losing required data

The most important validated guarantee is:

```text
Retention can reduce hot state without making required data inaccessible.
```

---

## Current Status

| Capability | Status |
|---|---|
| Policy-driven retention engine | Implemented / validated |
| `config.retention` policy resolution | Implemented / validated |
| Retention triggers | Implemented / validated |
| Retention decision flow | Implemented / validated |
| Compaction strategy | Implemented / validated |
| Eviction strategy | Implemented / validated |
| Hybrid retention strategy | Implemented / validated |
| Payload externalization | Implemented / validated |
| Redis hot state reduction | Implemented / validated |
| Resolver-backed rehydration | Implemented / validated |
| Archive-backed reconstruction foundations | Implemented / validated foundations |
| Retention observability foundations | Implemented / foundation available |
| Terminal lifecycle compatibility | Implemented / validated |
| Replay compatibility foundations | Implemented / validated foundations |
| Advanced adaptive memory policies | Planned |
| Rich retention dashboard | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Retention engine | Resolves `config.retention`, executes retention policies, and computes retention decisions. |
| Policy engine | Executes retention policies using the shared Policy Engine V2 model. |
| Retention triggers | Decide when retention should run. |
| Retention coordinator | Applies retention safely during execution lifecycle. |
| Payload store | Persists full externalized payload data. |
| Archive/index store | Tracks where evicted data can be resolved. |
| Resolver | Rehydrates compacted or evicted data. |
| Redis DAG store | Stores hot execution state and compacted references. |
| Observability layer | Records retention and resolver behavior. |

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Distributed Execution](distributed-execution.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability](observability.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
