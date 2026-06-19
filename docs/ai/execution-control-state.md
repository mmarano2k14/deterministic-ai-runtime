# Distributed Execution

Status: Implemented / validated foundation, updated for RBAC-aware execution context propagation and multi-tenant runtime isolation.

This document describes how the **Deterministic AI Runtime** coordinates distributed workers and runtime instances safely.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

This document is also aligned with the current multi-tenant control-plane foundation.

Execution control remains an `ExecutionId`-level concern, but every control-plane entry point that leads to runtime execution must preserve the durable execution context:

```text
RBAC ExecutionContext
        ↓
ExecutionContextSnapshot
        ↓
SharedRunRecord / Runtime Run Request
        ↓
Background dispatcher restores context
        ↓
Tenant-aware admission, registry, capacity, and execution control
```

The durable tenant boundary is `ExecutionContextSnapshot.TenantId`.

`ContextKey` remains useful for RBAC correlation, diagnostics, and request-scoped control-plane behavior, but it is not the durable tenant isolation key.


---

## Purpose

Distributed execution is one of the core foundations of the runtime.

The goal is to allow multiple workers and distributed runtime instances to advance AI workflow execution safely without:

- duplicate step execution
- broken dependency ordering
- stale ownership corruption
- uncontrolled parallelism
- lost retry state
- corrupted terminal lifecycle
- non-deterministic final results

The runtime treats AI workflow execution as a distributed systems problem.

Workers do not own the workflow.

The execution state owns the workflow.

---

## Execution Model

The runtime executes workflows as DAGs.

Each execution is represented by:

- an `ExecutionId`
- an execution record
- a DAG state
- step states
- dependency information
- retry state
- claim ownership metadata
- result and payload references
- terminal snapshot and replay metadata when applicable

Workers advance the execution by reading shared state and attempting atomic transitions.

The basic model is:

```text
Execution state exists
        ↓
Workers inspect state
        ↓
Ready steps are identified
        ↓
Workers attempt atomic claims
        ↓
Only one worker wins ownership of each step
        ↓
Step executes
        ↓
Result is persisted through controlled transition
        ↓
Runtime evaluates convergence
```

Execution is therefore state-driven, not process-driven.

---

## Redis as Distributed Hot State

Redis is used as the active distributed coordination layer.

It stores the hot execution state required to coordinate workers.

This includes:

- active execution records
- step states
- claim tokens
- claim ownership metadata
- retry scheduling metadata
- recovery metadata
- dependency information
- distributed concurrency leases
- execution control state

Redis is not used only as a cache.

It acts as the shared coordination substrate for active execution.

---

## Hot State vs Cold Storage

Redis is the hot state layer.

It is optimized for:

- active execution state
- coordination
- atomic transitions
- low-latency scheduling
- claim ownership
- retry and recovery metadata

Redis should not hold large payloads or long-term history indefinitely.

Large payloads, terminal snapshots, archived step data, and replay-related durable data are handled through durable storage such as MongoDB and payload stores.

The separation is:

```text
Redis
= hot coordination state

MongoDB / payload store
= durable payloads, snapshots, archive, replay foundations
```

This distinction matters because distributed execution must remain fast while retention keeps hot state bounded.

---

## Atomic Coordination with Redis Lua

Critical state transitions are protected by Redis Lua scripts.

Lua is used because distributed workers may attempt the same operation at the same time.

Atomic Redis Lua transitions protect operations such as:

- claiming a ready step
- completing an owned step
- failing an owned step
- moving a failed step to `WaitingForRetry`
- recovering stale running steps
- applying finalization rules
- enforcing concurrency lease acquisition

This ensures that multiple workers can safely compete for work without corrupting state.

---

## Atomic Step Claiming

Step claiming is one of the most important distributed operations.

A worker can execute a step only if it successfully claims it.

A claim operation must verify:

- the execution exists
- the step exists
- the step is eligible
- all dependencies are completed
- the step is not already owned
- retry timing allows execution
- control state does not block claims
- concurrency admission has succeeded
- distributed capacity is available when required

If the claim succeeds:

- the step moves to `Running`
- the worker receives ownership
- a claim token is stored
- claim timestamp is recorded

Only the owning worker can complete or fail that step.

---

## Claim Tokens and Ownership

Each claimed step is protected by a claim token.

The claim token must be provided when the worker attempts to:

- complete the step
- fail the step
- schedule retry
- persist terminal step transition

If the token does not match, the update is rejected.

This prevents stale workers from overwriting valid state.

Example scenario:

```text
Worker A claims step
        ↓
Worker A becomes slow or crashes
        ↓
Recovery eventually releases the step
        ↓
Worker B claims and completes the step
        ↓
Worker A later wakes up and tries to complete
        ↓
Claim token mismatch
        ↓
Update rejected
```

This protects the execution from stale ownership corruption.

---

## Leases and Crash Recovery

Distributed execution must assume workers can crash.

When a worker claims a step, the step is associated with claim timing metadata.

If the worker crashes while the step is `Running`, the step may remain in a running state.

Recovery logic detects stale running work.

A stale running step can be moved back to `Ready` when its ownership window expires.

Important distinction:

```text
Retry
= the step logic failed

Recovery
= the worker disappeared or abandoned ownership
```

Recovery should not consume retry budget.

This distinction is important for deterministic failure semantics.

---

## Worker Crash Scenario

A typical worker crash flow is:

```text
Step is Ready
        ↓
Worker A claims step
        ↓
Step becomes Running
        ↓
Worker A crashes before completion
        ↓
Step remains Running
        ↓
Recovery detects stale ownership
        ↓
Step returns to Ready
        ↓
Worker B claims step
        ↓
Worker B completes step
```

The runtime guarantee is:

- the step does not remain stuck forever
- no invalid completion is accepted
- retry count is not consumed by crash recovery
- the execution can continue

---

## Duplicate Execution Prevention

Duplicate execution prevention is handled through:

- Redis Lua atomic claims
- claim tokens
- ownership validation
- strict state transitions
- idempotent completion rules

If multiple workers attempt to claim the same step:

```text
Worker A tries to claim step
Worker B tries to claim step
Worker C tries to claim step
        ↓
Redis Lua evaluates atomically
        ↓
Only one worker succeeds
        ↓
Others fail and must retry another eligible step
```

This ensures that only one worker owns a step at a time.

---

## Dependency-Safe Scheduling

The runtime respects DAG dependencies before execution.

A step can become eligible only when:

- all dependencies are completed
- the step is `Ready` or retry-eligible
- no other worker currently owns the step
- control state allows advancement
- concurrency admission allows execution
- distributed capacity is available

This guarantees that distributed scheduling does not violate the DAG structure.

Parallelism is allowed only where dependencies allow it.

---

## Bounded Parallel DAG Execution

The runtime supports bounded distributed parallel DAG execution.

Instead of executing one step at a time, workers can evaluate eligible DAG steps, resolve concurrency admission, acquire distributed capacity, claim ownership atomically, and execute multiple steps concurrently.

This enables:

- bounded local parallel execution
- dependency-aware scheduling
- distributed-safe multi-worker orchestration
- deterministic batch convergence
- policy-aware concurrency admission before execution
- provider/model/operation throttling across workers

Execution remains fully state-driven.

Workers:

1. evaluate eligible steps
2. resolve pipeline-level and step-level `config.concurrency`
3. create an `AiConcurrencyContext` containing pipeline, step, provider, model, and operation metadata
4. apply matching generic throttle rules
5. evaluate configured concurrency admission policies
6. acquire distributed concurrency capacity through Redis
7. atomically claim steps through Redis Lua scripts
8. execute claimed steps concurrently
9. release concurrency capacity after execution
10. persist completion/failure transitions safely

---

## Concurrency Before Claim

Concurrency admission is enforced before DAG step ownership is claimed.

A safe distributed flow is:

```text
Resolve pipeline + step config.concurrency
        ↓
Create AiConcurrencyContext
        ↓
Apply matching concurrency.throttle rules
        ↓
Evaluate configured admission policies
        ↓
Acquire Redis concurrency lease
        ↓
Attempt DAG claim
```

Important rules:

- if policy admission is denied, no Redis concurrency lease is acquired
- if Redis admission is denied, no DAG claim is attempted
- if Redis admission succeeds but the DAG claim fails, the concurrency lease is released immediately
- if the DAG claim succeeds, the lease remains owned until step execution finishes

This prevents leaked distributed capacity.

---

## Multi-Worker Execution

Multiple workers can safely process the same execution because they coordinate through shared state.

Workers do not communicate directly with each other.

They coordinate through Redis.

```text
Worker 1 ─┐
Worker 2 ─┼── Redis hot state / Lua coordination ── Execution state
Worker 3 ─┘
```

This reduces coupling and allows workers to remain stateless.

---

## RunId and ExecutionId

The runtime separates controller lifecycle identity from runtime execution identity.

```text
RunId
= controller/job lifecycle identifier

ExecutionId
= authoritative runtime execution identifier
```

This separation is critical.

A `RunId` is not a DAG execution id.

An `ExecutionId` is not a controller queue id.

The DAG store, snapshots, replay, resolver, retention, and cleanup lifecycle are addressed by `ExecutionId`.

The background controller tracks queued and running work by `RunId`.

---

## Distributed Execution Modes

The runtime supports two important execution models.

```text
Model 1:
multiple isolated executions
unique ExecutionId per run
safe snapshot/replay per execution
```

```text
Model 2:
one ExecutionId
multiple runtime instances
shared distributed DAG execution
workers competing safely for the same execution's steps
```

The distributed shared-execution model relies on:

- Redis-backed DAG state
- atomic step claiming
- lease-based ownership
- deterministic convergence
- bounded batch execution
- idempotent terminal lifecycle handling
- archive-backed resolver reconstruction after retention

This makes distributed runtime-instance execution a validated runtime capability, not only a future direction.

---

## Multi-Runtime-Instance Execution

In distributed multi-runtime-instance execution, multiple runtime workers can safely advance the same `ExecutionId` through Redis-backed DAG coordination.

Current validated behavior includes:

- strict `RunId` / `ExecutionId` separation
- isolated execution state per independent `ExecutionId`
- shared execution mode for one `ExecutionId`
- Redis-backed DAG coordination
- atomic ownership through Lua
- bounded batch execution
- retry-safe multi-worker execution
- idempotent terminal lifecycle processing
- terminal snapshot support
- retention and replay compatibility
- archive-backed resolver reconstruction after retention
- distributed execution testing scenarios

Enterprise demo and Kubernetes deployment scenarios remain roadmap items.

---

## Retry-Aware Distributed Execution

Retry behavior is coordinated through runtime state.

When a step fails and retry is allowed:

- retry count is updated
- next retry time is calculated
- step moves to `WaitingForRetry`
- the step cannot be claimed again until retry time opens

Workers must respect retry state.

A retry-ready step becomes eligible only when:

```text
UtcNow >= NextRetryAtUtc
```

This prevents retry storms and keeps retry behavior deterministic.

---

## Control-State-Aware Execution

Distributed workers must also respect execution control state.

The control gate may block claims for statuses such as:

- `Pausing`
- `Paused`
- `WaitingForInput`
- `Cancelling`
- `Cancelled`

This means pause, cancel, and human-in-the-loop control can stop new claims without corrupting DAG state.

Already claimed work may drain safely depending on the control scenario.

---

## Terminal Finalization

Distributed finalization must be idempotent.

More than one worker may observe that an execution appears terminal.

Finalization must therefore be safe if attempted multiple times.

The runtime must ensure that:

- terminal state is persisted once
- snapshots are created consistently
- cancellation override is respected
- cleanup does not remove required replay or resolver data too early
- final status remains deterministic

This is especially important when cancellation races with natural DAG completion.

---

## Deterministic Convergence Under Concurrency

The distributed execution model is designed to converge deterministically.

The final execution result should not depend on:

- which worker claimed a step
- which worker finished first
- how many workers were active
- whether a stale worker was recovered
- whether retry timing created different scheduling order
- whether retention compacted or externalized completed payloads

The final result is derived from state and valid transitions.

This is the core difference between simply running distributed work and building deterministic execution infrastructure.

---

## Distributed Execution Flow

A simplified distributed execution flow is:

```text
Start execution
        ↓
Create DAG state
        ↓
Workers poll or receive execution work
        ↓
Recover stale running steps
        ↓
Find eligible ready steps
        ↓
Apply execution control gate
        ↓
Resolve concurrency configuration
        ↓
Create concurrency context
        ↓
Apply throttle rules
        ↓
Evaluate policy admission
        ↓
Acquire distributed concurrency lease
        ↓
Claim step atomically
        ↓
Execute step
        ↓
Complete / fail / schedule retry atomically
        ↓
Release concurrency lease
        ↓
Apply retention if required
        ↓
Evaluate convergence
        ↓
Finalize execution if terminal
```

This flow keeps execution safe under concurrency, failure, retry, retention, and control-plane operations.

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| DAG execution engine | Evaluates dependencies and convergence. |
| Redis DAG store | Stores active execution state and applies atomic transitions. |
| Redis Lua scripts | Protect critical distributed state changes. |
| Worker / runner | Executes claimed steps and reports results. |
| Claim service | Acquires ownership of eligible work. |
| Retry engine | Decides retry behavior from config and policies. |
| Concurrency engine | Applies admission and distributed capacity limits. |
| Redis concurrency gate | Enforces distributed concurrency through lease-based scopes. |
| Execution control gate | Blocks or allows advancement based on control state. |
| Retention coordinator | Keeps hot state bounded while preserving required data. |
| Resolver | Reconstructs compacted or evicted data when needed. |
| Finalization service | Persists terminal state and snapshots safely. |

---

## Failure Scenarios Covered

Distributed execution is designed to handle:

| Scenario | Runtime Behavior |
|---|---|
| Worker crashes while running a step | Recovery returns stale running step to eligible state. |
| Multiple workers claim same step | Redis Lua allows only one winner. |
| Stale worker completes late | Claim token mismatch rejects update. |
| Step fails transiently | Retry state moves step to `WaitingForRetry`. |
| Retry window not open | Step is not claimable yet. |
| Execution is paused | New claims are blocked. |
| Execution is waiting for input | New claims are blocked until input is submitted. |
| Execution is cancelled | Finalization resolves to `Cancelled`. |
| Concurrency capacity is denied | No DAG claim is attempted. |
| Concurrency lease acquired but claim fails | Lease is released immediately. |
| Large payload is evicted | Resolver reconstructs from persistent storage. |
| Multiple workers observe terminal state | Finalization remains idempotent. |

---

## Current Status

| Capability | Status |
|---|---|
| Redis-backed hot execution state | Implemented |
| Redis Lua atomic step claiming | Implemented |
| Claim token ownership | Implemented |
| Retry-aware distributed state | Implemented |
| Worker crash recovery | Implemented |
| Bounded batch execution | Implemented |
| Distributed workers | Implemented |
| RunId / ExecutionId separation | Implemented |
| Runtime queue control integration | Implemented |
| Execution control gate integration | Implemented |
| Distributed multi-runtime-instance execution | Implemented / validated foundations |
| Retention and replay compatibility | Implemented / validated foundations |
| Archive-backed resolver reconstruction after retention | Foundation available |
| Kubernetes deployment scenario | Planned |
| Enterprise demo scenario | Planned |

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Execution Control State](execution-control-state.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
