# Retry and Recovery

Status: Implemented and validated for config-driven retry, policy-driven retry, stale running step recovery, runtime instance crash recovery, real process-host recovery, tenant-isolated recovery, recovery forensics, ledger/trace/replay proof, and safe-tenant non-impact validation.

This document describes the retry and recovery model used by the Deterministic AI Runtime.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

Production AI workflows fail frequently.

Failures can come from:

- LLM provider timeouts
- transient API failures
- network errors
- database failures
- rate limits
- malformed provider responses
- temporary infrastructure issues
- worker crashes
- process restarts

The runtime separates two different concepts:

```text
Retry
= the step executed and failed

Recovery
= the worker disappeared, crashed, or abandoned ownership
```

This separation is critical.

A failed step may consume retry budget.

A crashed worker should not automatically consume retry budget.

---

## Retry vs Recovery

Retry and recovery solve different problems.

| Concept | Meaning | Retry Budget Consumed? |
|---|---|---|
| Retry | The step ran and returned an error or exception. | Yes, if retry is allowed. |
| Recovery | The worker claimed the step but did not complete or fail it. | No. |

This distinction keeps execution deterministic and fair.

A worker crash should not be treated the same way as a business or provider failure.

The same separation now exists at two recovery levels:

```text
Step-level recovery
= a worker claimed a step and disappeared before completing or failing it

Runtime-instance crash recovery
= an entire runtime process became unsafe and the control plane must recover every work item assigned to that runtime
```

These two levels are related, but they are not the same responsibility.

Step-level recovery repairs abandoned step ownership inside an existing execution.

Runtime-instance crash recovery repairs ownership of assigned shared runs and executions after a runtime process dies.

Both paths must preserve retry fairness: infrastructure failure must not consume business retry budget.

---

## Recovery Layers

The runtime has three distinct failure-handling layers.

| Layer | Trigger | Scope | Durable Identity | Retry Budget Consumed? |
|---|---|---|---|---|
| Step retry | Step completed with error or exception. | One step. | `ExecutionId` + step id. | Yes, if retry is allowed. |
| Stale step recovery | Worker claimed a step and abandoned ownership. | One running step. | `ExecutionId` + claim token. | No. |
| Runtime instance crash recovery | Runtime process becomes unsafe or disappears. | All work assigned to the failed runtime. | `SharedRunId`, `LocalRunId`, and sometimes `ExecutionId`. | No. |

The important rule is:

```text
Retry handles business/provider failure.
Recovery handles infrastructure ownership loss.
```

Runtime instance crash recovery is intentionally broader than stale step recovery. It can recover:

- in-flight DAG executions that already have a durable `ExecutionId`;
- local queued shared runs that were assigned to the dead runtime but did not yet create an `ExecutionId`.

This is why recovery cannot be implemented only inside the DAG store. Some recovered work does not have a DAG execution yet.

---

## Why This Matters

Retry is no longer:

> “try again if it fails”

It becomes:

> “evaluate runtime state and decide what is allowed”

This brings:

- deterministic retry behavior
- explicit failure handling
- no hidden local retry loops
- policy-based retry classification
- distributed-safe retry scheduling
- observable retry decisions
- future adaptive retry behavior

The runtime does not hide retries inside step executors.

Retry is part of the execution state machine.

---

## Retry Model

Retry behavior is explicit runtime state.

The runtime does not rely on hidden local retry loops.

Instead, when a step fails:

1. the failure is reported to the runtime
2. retry configuration is resolved
3. configured retry policies are resolved
4. retry policies are evaluated
5. policy results are aggregated
6. a retry decision is produced
7. `RetryState` is updated
8. the state transition is persisted atomically through Redis Lua

This makes retry behavior:

- observable
- deterministic
- policy-driven
- distributed-safe
- replay-friendly

---

## Config-Driven and Policy-Driven Retry Engine

The retry system is both config-driven and policy-driven.

Retry behavior is defined through:

```text
config.retry
```

At runtime, the Retry Engine resolves this configuration, extracts configured policy definitions, and delegates decision-making to the Policy Engine.

The Policy Engine executes policies for the `Retry` kind and returns structured outcomes.

The Retry Engine then interprets those outcomes and applies the appropriate retry decision.

This introduces a strict separation between:

- retry configuration
- retry policy evaluation
- retry decision aggregation
- retry state mutation
- distributed state persistence

Retry is no longer handled through hidden local retry loops.

It is handled through explicit runtime state.

---

## Retry Execution Flow

The retry execution flow is:

```text
Step failure
        ↓
Retry Engine
        ↓
Resolve config.retry
        ↓
Resolve configured policy definitions
        ↓
Execute retry policies
        ↓
Aggregate policy results
        ↓
Produce retry decision
        ↓
Apply decision to RetryState
        ↓
Persist state transition through Redis Lua
```

The policy layer decides whether retry should be allowed.

The Redis DAG store persists the resulting state transition safely.

---

## Config-Driven Retry

Retry behavior is configured through step configuration.

A step may declare `config.retry`.

Example:

```json
{
  "name": "summarize",
  "stepKey": "llm.summary",
  "config": {
    "retry": {
      "policies": [
        "retry.transient.default",
        {
          "name": "retry.timeout.default",
          "kind": "Retry",
          "config": {
            "code": "timeout"
          }
        }
      ],
      "maxRetries": 2,
      "strategy": "Fixed",
      "baseDelayMs": 500,
      "maxDelayMs": 5000,
      "jitter": false
    }
  }
}
```

The runtime resolves this configuration when the step fails.

Retry behavior is therefore controlled by pipeline or step definition, not by hidden executor code.

---

## Policy-Driven Retry

Retry is policy-driven through the shared Policy Engine V2 model.

The retry engine delegates classification and decision support to retry policies.

A retry policy may decide whether an error is retryable based on:

- exception type
- error code
- provider response
- timeout classification
- operation type
- step metadata
- configured policy data

Structured policies are supported.

The retry engine executes policies for the `Retry` policy kind.

---

## Legacy and Structured Retry Policies

The runtime supports both legacy string policies and structured policy objects.

Legacy format:

```json
{
  "policies": [
    "retry.transient.default"
  ]
}
```

Structured format:

```json
{
  "policies": [
    {
      "name": "retry.timeout.default",
      "kind": "Retry",
      "config": {
        "code": "timeout"
      }
    }
  ]
}
```

This keeps old pipeline JSON valid while enabling policy-specific configuration.

The `name` field is used for policy registry lookup.

The `kind` field identifies the policy kind when needed.

The `config` field carries policy-specific configuration.

---

## Architecture Responsibilities

| Component | Responsibility |
|---|---|
| Policy Registry | Stores available policies and maps them by key and kind. |
| Policy Engine | Resolves and executes policies based on execution context and policy kind. |
| Retry Engine | Resolves `config.retry`, executes retry policies, computes retry decisions, and applies retry state changes. |
| Redis DAG Store | Applies distributed retry transitions atomically through Lua scripts. |

This separation keeps retry behavior modular while keeping distributed state mutation centralized.

---

## Retry Engine Responsibilities

The retry engine is responsible for:

- resolving retry configuration
- resolving configured retry policies
- executing retry policy logic
- aggregating policy results
- deciding whether retry is allowed
- calculating the next retry time
- updating `RetryState`
- producing diagnostics

The retry engine does not directly mutate distributed state by itself.

State mutation is persisted through the DAG store using controlled transitions.

---

## Retry State Model

Retry configuration and retry runtime state are separate.

```text
Retry configuration
= policies, max retries, delay strategy, base delay, max delay, jitter

RetryState
= retry count, last retry time, next retry time, retry reason, policy result metadata
```

Configuration defines behavior.

Runtime state records what already happened.

Retry state may include:

- retry count
- max retries
- last retry timestamp
- next retry timestamp
- last failure reason
- last policy key
- retry strategy metadata
- retry decision metadata

This separation keeps retry behavior deterministic, inspectable, and replayable.

---

## WaitingForRetry

When a step fails but retry is still allowed, the runtime moves the step to:

```text
WaitingForRetry
```

The step remains paused until its retry window opens.

A step in `WaitingForRetry` can only be claimed again when:

```text
UtcNow >= NextRetryAtUtc
```

This prevents retry storms and keeps retry timing explicit.

Workers will not attempt to execute the step before the retry window opens.

---

## Retry Delay Strategies

The runtime supports retry delay strategies such as:

- fixed delay
- exponential delay
- maximum delay cap
- optional jitter

Example behavior:

```text
Attempt 1 fails
        ↓
RetryCount = 1
NextRetryAtUtc = now + base delay

Attempt 2 fails
        ↓
RetryCount = 2
NextRetryAtUtc = now + next delay
```

Jitter can be used to avoid thundering-herd retry behavior.

When determinism is required for tests, jitter should be disabled.

---

## Distributed Retry Safety

In distributed DAG execution, retry state transitions must be atomic.

The Redis DAG store ensures that:

- only the current claim owner can fail or complete a step
- retry count is updated consistently
- retry windows are respected
- only one worker can reclaim a retry-ready step
- stale workers cannot overwrite retry state
- failed final state cannot be overwritten by late workers

Redis Lua transitions protect these operations.

This keeps retry behavior safe across multiple workers.

---

## Atomic Failure Transition

When a worker reports failure, the DAG store validates:

- the execution exists
- the step exists
- the step is currently running
- the claim token matches
- the step is owned by the reporting worker
- the retry decision is valid

Then the step is moved to either:

```text
WaitingForRetry
```

or:

```text
Failed
```

The decision depends on retry budget and policy outcome.

---

## Retry Exhaustion

If the retry budget is exhausted, the step becomes terminal failed.

Example:

```text
RetryCount = MaxRetries
        ↓
Step fails again
        ↓
No retry allowed
        ↓
Step status = Failed
        ↓
Execution convergence evaluates failure
```

Retry exhaustion must be explicit and observable.

The execution should not hang after retry exhaustion.

---

## Recovery Model

Recovery handles abandoned work.

A step may be abandoned when:

- the worker crashes
- the process is killed
- the machine restarts
- the worker loses connection
- the worker never reports completion or failure

In this case, the step may remain in `Running`.

Recovery logic detects stale `Running` steps and makes them eligible again.

---

## Leases and Time-Based Ownership

A claim is not permanent.

Each claimed step has time-based ownership metadata such as:

- claimed timestamp
- claim timeout / ownership window
- claim token
- worker identity metadata

This ensures that a worker cannot hold a step forever.

If the worker finishes within the ownership window, the step completes or fails normally.

If the worker crashes, the ownership window eventually expires and the step becomes recoverable.

---

## Stale Running Step Recovery

A step is considered stale when it has been running longer than its allowed ownership window.

Recovery can move the step:

```text
Running
        ↓
Ready
```

The claim owner is cleared.

The step can then be claimed by another worker.

This does not mean the step logic failed.

It means the worker ownership became invalid.

---

## Recovery Does Not Consume Retry Budget

Recovery must not increment retry count.

Example:

```text
Worker A claims step
        ↓
Worker A crashes
        ↓
Recovery resets step to Ready
        ↓
RetryCount remains unchanged
```

This prevents infrastructure failures from consuming business retry budget.

The retry count reflects step failures, not worker crashes.

---

## Runtime Instance Crash Recovery

Runtime instance crash recovery handles a stronger failure mode than stale step ownership.

Instead of one worker abandoning one step, an entire runtime process becomes unsafe. At that point, the control plane must recover every work item that was assigned to that runtime instance.

Assigned work can be in different states:

```text
InFlightExecution
    SharedRunId exists
    LocalRunId exists
    ExecutionId exists
    DAG execution has already started

LocalQueued
    SharedRunId exists
    LocalRunId exists
    ExecutionId does not exist yet
    run was dispatched to the runtime-local queue but not started
```

These states require different recovery paths.

### In-flight execution recovery

An in-flight execution must resume the same durable execution identity.

```text
failed runtime process
    ↓
assigned in-flight execution discovered
    ↓
SharedRun requeued for resume
    ↓
failed LocalRun marked requeued for recovery
    ↓
replacement runtime selected
    ↓
replacement LocalRun registered
    ↓
resume context seeded
    ↓
DAG resumes with the same ExecutionId
```

The invariant is:

```text
ExecutionIdBefore == ExecutionIdAfter
```

A recovered in-flight DAG execution is not a new execution. It is the same durable execution continuing on a replacement runtime instance.

### Local queued run recovery

A local queued run cannot resume a DAG execution because no durable execution has been created yet.

The recovery path is therefore durable redispatch through the shared run identity.

```text
failed runtime process
    ↓
assigned local queued run discovered
    ↓
SharedRun requeued for local-queued recovery
    ↓
failed LocalRun marked requeued for recovery
    ↓
replacement runtime selected
    ↓
replacement LocalRun registered
    ↓
run executes normally and creates a new ExecutionId
```

The invariant is:

```text
SharedRunId is preserved.
Original LocalRunId is marked failed/requeued.
Replacement LocalRunId is new.
ExecutionId is created only when the replacement runtime starts DAG execution.
```

This avoids duplicate submissions while accepting that the old runtime-local queue is gone.

---

## Recovery Source of Truth

The runtime-local queue is not durable truth.

A local queue is allowed to die with the process that owns it. Recovery must not depend on reading state from the dead runtime.

The durable recovery source of truth is:

```text
SharedRunStore
SharedQueue
RuntimeRunExecutionIndex
DAG execution store
Runtime registry
Runtime capacity store
Recovery forensics store
Ledger / trace / replay evidence
```

This design is intentional.

The system does not pretend a dead local queue survived. It reconstructs the correct recovery path from durable shared-run, runtime-index, registry/capacity, and DAG state.

---

## Health Reconciliation vs Execution Recovery

Runtime health and execution recovery are separate responsibilities.

```text
RuntimeInstanceHealthReconciler
    detects stale / unhealthy / draining / unsafe runtime capacity
    prevents unsafe capacity from being selected for new dispatch

Execution recovery reconciler
    enumerates work assigned to an unsafe runtime
    recovers in-flight executions and local queued runs
    writes recovery forensics and ledger evidence
```

The HTTP provider is not the lifecycle owner and does not own recovery.

HTTP transport failures such as `http-circuit-open`, timeout, endpoint unavailable, or command failure are transport/endpoint health signals. They can contribute to runtime health decisions, but they must not directly restart or replace the runtime from the HTTP command provider.

The lifecycle owner is the component that creates or attaches runtime capacity, such as:

```text
Runtime Host Manager
Process host creation strategy
Local runtime scaler
Future Kubernetes provider / host manager mode
External supervisor
```

The validated boundary is:

```text
HTTP provider reports transport failure
    ↓
health reconciliation prevents unsafe routing
    ↓
execution recovery reconciles assigned work if runtime becomes unsafe
    ↓
provider / lifecycle owner creates or attaches replacement capacity when required
```

---

## Tenant-Isolated Runtime Crash Recovery

Runtime instance crash recovery is tenant-scoped.

The recovery reconciler must not treat a runtime crash as a global panic event. It must recover only the work assigned to the failed tenant runtime instance.

Validated invariants include:

```text
Impacted tenant work is recovered.
Unrelated tenant work is not recovered.
Safe tenant runtime is not killed.
Safe tenant receives no recovery forensics.
Safe tenant has zero recovered work.
Safe tenant has zero recovery entries visible from impacted tenant queries.
Cross-tenant ledger leakage is zero.
```

The safe tenant proof matters because it validates isolation, not just recovery.

A system that can recover failed work but contaminates another tenant's observability surface is not tenant-isolated recovery.

---

## Runtime Recovery Forensics

Runtime instance crash recovery writes per-work-item forensics records.

In-flight recovery forensics use a durable identity shape similar to:

```text
runtime-recovery:{ExecutionId}:{SharedRunId}:{FailedLocalRunId}
```

Local queued recovery forensics use:

```text
runtime-recovery:local-queued:{SharedRunId}:{FailedLocalRunId}
```

Each record is linked to a runtime failure incident:

```text
runtime-failure:{RuntimeInstanceId}
```

The in-flight recovery timeline is expected to include:

```text
execution.recovery.candidate.detected
→ shared.run.requeued.for.resume
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
→ dag.resume.started
→ dag.resume.completed
→ execution.recovery.completed
```

The local queued recovery timeline is expected to include:

```text
SharedRunRequeuedForLocalQueuedRecovery
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
```

Forensics are not transient logs. They are durable, queryable recovery evidence exposed through MCP runtime recovery tooling.

---

## Replay, Ledger, and Trace After Recovery

Recovery is not considered fully proven only because the DAG eventually completes.

A recovered execution must remain observable after convergence.

Validated recovery proof requires:

```text
execution ledger evidence
execution trace evidence
completion evidence
step completion evidence
strict replay validation
replay report
replay ledger
replay trace
runtime recovery forensics
control-plane causal-chain ledger evidence
```

This turns recovery from an operational claim into an auditable contract.

```text
Recovery without replay is operational resilience.
Recovery with replay, ledger, trace, and forensics is audit resilience.
```

---

## Stale Worker Protection

A stale worker may wake up after recovery.

The runtime protects against this with claim tokens.

Example:

```text
Worker A claims step with token A
        ↓
Worker A becomes stale
        ↓
Recovery clears ownership
        ↓
Worker B claims step with token B
        ↓
Worker B completes step
        ↓
Worker A tries to complete with token A
        ↓
Update rejected
```

This prevents stale workers from corrupting valid state.

---

## Retry and Recovery Together

Retry and recovery can interact.

Example:

```text
Step is Ready
        ↓
Worker A claims step
        ↓
Worker A executes and fails step
        ↓
Runtime schedules retry
        ↓
Step = WaitingForRetry
        ↓
Retry window opens
        ↓
Worker B claims step
        ↓
Worker B crashes
        ↓
Recovery moves step back to Ready
        ↓
RetryCount is unchanged by recovery
```

The retry count reflects step failures, not worker crashes.

---

## Retry-Aware Claiming

A worker can claim a step when it is:

- `Ready`
- or retry-ready from `WaitingForRetry`

A `WaitingForRetry` step is retry-ready only when:

```text
UtcNow >= NextRetryAtUtc
```

The claim operation must check retry timing atomically.

This prevents multiple workers from racing to claim the same retry-ready step.

---

## Interaction with Execution Control State

Execution control can block retry-ready work.

Example:

```text
Step = WaitingForRetry
Retry window opens
Execution status = Paused
        ↓
Step remains unclaimed
```

Execution control has priority over scheduling.

This means pause, cancel, and waiting-for-input can stop retry advancement safely.

---

## Interaction with Concurrency and Throttling

Retry-ready steps still need to pass concurrency admission.

A safe order is:

```text
Check execution control gate
        ↓
Resolve retry eligibility
        ↓
Resolve concurrency config
        ↓
Evaluate policy admission
        ↓
Acquire concurrency lease
        ↓
Claim retry-ready step
```

If capacity is denied, the step remains unclaimed.

Retry state is not changed by concurrency denial.

---

## Interaction with Retention

Retry and recovery must remain compatible with retention and compaction.

Completed or historical step payloads may be externalized.

Retry state for active steps must remain available in hot execution state.

Retention should not remove state required to continue active retry scheduling.

---

## Interaction with Replay

Retry state is part of deterministic execution history.

Replay foundations may need to restore:

- step status
- retry count
- retry timestamps
- terminal failed state
- completed state
- execution fingerprint

A deterministic replay validation should be able to compare retry-related outcomes.

---

## Observability

Retry and recovery behavior is observable.

Useful signals include:

- retry policy execution
- retry decision outcome
- retry attempt count
- retry exhaustion
- retry delay behavior
- next retry time
- recovery count
- stale running step recovery
- claim token mismatch
- failure reason
- failure correlation by step and execution
- step final failure

These signals make retry behavior debuggable instead of implicit.

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Step fails transiently | Retry policy decides whether retry is allowed. |
| Retry allowed | Retry count is increased and step moves to `WaitingForRetry`. |
| Retry window not open | Step is not claimable. |
| Retry budget exhausted | Step becomes `Failed`. |
| Worker crashes while running | Recovery returns stale step to `Ready`. |
| Runtime process is killed with in-flight DAG execution | Execution recovery resumes the same `ExecutionId` on replacement runtime capacity. |
| Runtime process is killed with local queued runs | Shared runs are redispatched through durable `SharedRunId` without duplicate submission. |
| Two tenant runtime processes crash in the same recovery window | Each tenant recovers only its assigned work. |
| Third tenant remains safe during other tenant crashes | Safe tenant completes normally with zero recovery work, zero recovery forensics, and zero ledger contamination. |
| Stale worker completes late | Claim token mismatch rejects update. |
| Retry-ready step during pause | Claim is blocked by execution control. |
| Multiple workers claim retry-ready step | Redis Lua allows only one owner. |
| Concurrency denied | Step remains unclaimed; retry state unchanged. |
| Non-retryable failure | Step becomes `Failed` and convergence evaluates failure. |
| Retry storm risk | Delayed `WaitingForRetry` state prevents immediate aggressive loops. |

---

## Validated Behavior

The retry and recovery implementation is validated through integration tests covering:

- config-driven retry
- policy-driven retry
- structured retry policy objects
- legacy string policy compatibility
- `WaitingForRetry`
- retry count updates
- retry timing / next retry window
- retry exhaustion
- Redis Lua retry transitions
- claim-token-protected failure updates
- distributed retry safety
- stale running step recovery
- recovery without retry budget consumption
- retry-aware claiming after retry window opens
- execution-control blocking of retry-ready work
- concurrency denial without retry state mutation
- runtime instance crash recovery for real process-host runtimes
- in-flight DAG resume with preserved `ExecutionId`
- local queued shared-run redispatch through durable `SharedRunId`
- recovery without business retry budget consumption
- tenant-isolated recovery across multiple impacted tenants
- safe tenant non-impact validation during concurrent tenant crashes
- runtime recovery forensics timelines for in-flight and local queued work
- recovery ledger, trace, replay, and control-plane causal-chain validation

---

## Current Status

| Capability | Status |
|---|---|
| Config-driven retry | Implemented / validated |
| Policy-driven retry | Implemented / validated |
| Legacy string retry policies | Implemented / validated |
| Structured retry policy definitions | Implemented / validated |
| Retry state model | Implemented / validated |
| `WaitingForRetry` status | Implemented / validated |
| Retry timing / next retry window | Implemented / validated |
| Redis Lua retry transitions | Implemented / validated |
| Claim-token-protected failure updates | Implemented / validated |
| Retry exhaustion | Implemented / validated |
| Stale running step recovery | Implemented / validated |
| Recovery without retry budget consumption | Implemented / validated |
| Distributed retry safety | Implemented / validated |
| Retry observability foundations | Implemented / foundation available |
| Runtime instance crash recovery | Implemented / validated |
| In-flight DAG resume after runtime crash | Implemented / validated |
| Local queued run recovery after runtime crash | Implemented / validated |
| Recovery forensics timelines | Implemented / validated |
| Tenant-isolated crash recovery | Implemented / validated |
| Safe tenant non-impact proof | Implemented / validated |
| Recovery ledger / trace / replay proof | Implemented / validated |
| Control-plane recovery causal-chain ledger | Implemented / validated |
| Adaptive retry policies | Planned |
| Rich retry audit history | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Policy Registry | Stores retry policies and maps them by key and kind. |
| Policy Engine | Executes retry policies by `Retry` policy kind. |
| Retry Engine | Resolves `config.retry`, executes policies, aggregates policy results, and computes retry decisions. |
| Redis DAG Store | Persists retry/failure transitions atomically through Lua scripts. |
| Claim Service | Ensures retry-ready steps are claimed safely. |
| Recovery Logic | Detects stale running steps and releases ownership. |
| Runtime Instance Health Reconciler | Detects unsafe runtime capacity and prevents unsafe routing. |
| Execution Recovery Reconciler | Recovers assigned work from unsafe runtime instances, including in-flight executions and local queued runs. |
| Shared Run Store / Shared Queue | Preserve durable shared-run identity and enable redispatch after runtime crash. |
| Runtime Run Execution Index | Links shared runs, local runs, runtime assignments, and execution identity for recovery. |
| Recovery Forensics Store | Records durable per-work-item recovery timelines and runtime failure incidents. |
| Execution Control Gate | Blocks retry advancement when execution is paused, cancelled, or waiting for input. |
| Concurrency Engine | Ensures retry-ready work still respects distributed concurrency admission. |
| Observability Layer | Records retry and recovery behavior. |

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Distributed Execution](distributed-execution.md)
- [Execution Control State](execution-control-state.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Replay and Audit](replay-and-audit.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

Do not collapse retry, stale step recovery, runtime health reconciliation, and runtime instance crash recovery into a single concept.