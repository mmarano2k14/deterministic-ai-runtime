# Crash Consistency & Transition Semantics

## Overview

This document defines the execution guarantees of the AI DAG runtime under:

- distributed execution
- worker crashes
- network failures
- retry and recovery scenarios

The goal is to ensure:

- deterministic outcomes
- no duplicated execution
- no lost progress
- safe recovery at any point

---

# Core Principles

## 1. Determinism

The system must converge to the same final state regardless of:

- execution order
- worker count
- transient failures

---

## 2. Monotonic State

Execution state can only move forward:

- None → Ready → Running → WaitingForRetry → Completed / Failed

Terminal states are **final and immutable**.

---

## 3. Distributed Safety

At any given time:

- only one worker owns a step execution
- ownership is enforced via claim token + lease

---

## 4. Separation of Concerns

- Retry = business failure (counted)
- Recovery = infrastructure failure (not counted)

---

# Transition Semantics

## Legend

| Property     | Meaning |
|-------------|--------|
| Atomic      | Operation is executed as a single indivisible unit |
| Idempotent  | Repeated execution produces the same result |
| Monotonic   | State can only move forward |
| Best-effort | No strict guarantee, but safe to retry |

---

# Step-Level Transitions

---

## 1. Claim Step (Ready → Running)

### Description
Worker acquires ownership of a step.

### Guarantees
- Atomic: YES (Redis Lua)
- Idempotent: NO
- Monotonic: YES
- Best-effort: NO

### Crash Scenarios

| Timing | Behavior |
|--------|--------|
| Before claim | No effect |
| During claim | Atomic → either claimed or not |
| After claim | Step is Running with valid lease |

### Recovery Behavior
- Lease expiration allows reclaim

---

## 2. Execute Step

### Description
Worker executes business logic.

### Guarantees
- Atomic: NO
- Idempotent: SHOULD BE (step responsibility)
- Monotonic: N/A
- Best-effort: YES

### Crash Scenarios

| Timing | Behavior |
|--------|--------|
| Before execution | Step remains Running |
| During execution | Partial execution possible |
| After execution | Depends on persistence |

### Recovery Behavior
- Step will be reclaimed if lease expires

---

## 3. Complete Step (Running → Completed)

### Guarantees
- Atomic: YES (store operation)
- Idempotent: YES
- Monotonic: YES
- Best-effort: NO

### Crash Scenarios

| Timing | Behavior |
|--------|--------|
| Before persist | Step remains Running |
| During persist | Atomic write guarantees consistency |
| After persist | Step is Completed |

### Recovery Behavior
- Completed step is never re-executed

---

## 4. Fail Step (Running → Failed)

### Guarantees
- Atomic: YES
- Idempotent: YES
- Monotonic: YES
- Best-effort: NO

### Crash Scenarios

Same as completion.

---

## 5. Retry Scheduling (Running → WaitingForRetry)

### Guarantees
- Atomic: YES
- Idempotent: YES
- Monotonic: YES
- Best-effort: NO

### Crash Scenarios

| Timing | Behavior |
|--------|--------|
| Before scheduling | Step still Running |
| After scheduling | Retry preserved |

### Recovery Behavior
- Retry window drives next eligibility

---

## 6. Retry Reclaim (WaitingForRetry → Ready)

### Guarantees
- Atomic: YES
- Idempotent: YES
- Monotonic: YES
- Best-effort: NO

---

## 7. Timeout Recovery (Running → Ready)

### Description
Expired lease allows reclaim.

### Guarantees
- Atomic: YES
- Idempotent: YES
- Monotonic: YES
- Best-effort: NO

### Key Rule
Recovery MUST NOT increment RetryCount.

---

# Execution-Level Transitions

---

## 8. Convergence Evaluation

### Description
Determines global execution status.

### Guarantees
- Atomic: YES (pure evaluation)
- Idempotent: YES
- Monotonic: YES
- Best-effort: NO

### Rules

Execution is:

- Completed → all steps completed
- Failed → no valid retry path remains
- Waiting → future work exists
- Running → active claims exist

---

## 9. Finalization (Execution → Terminal)

### Description
Persist final execution state.

### Guarantees
- Atomic: YES (Redis Lua)
- Idempotent: YES
- Monotonic: YES
- Best-effort: NO

### Protection

- requires optimistic key match
- refuses if already terminal
- refuses if convergence not satisfied

---

# Crash Consistency Guarantees

## 1. No Lost Work

- Running steps are recoverable via lease expiration
- Retry states are persisted before release

---

## 2. No Duplicate Execution

- Claim token ensures exclusive ownership
- Redis atomic operations prevent double claim

---

## 3. No Premature Completion

Execution cannot finalize if:

- running step exists
- retry is pending
- reclaimable work exists

---

## 4. Safe Replay

All operations are:

- replay-safe
- idempotent where required
- guarded by invariants

---

# Failure Model

System tolerates:

- worker crash at any point
- Redis latency
- duplicate execution attempts
- concurrent recovery scans

---

# Deterministic Convergence

Final state depends ONLY on:

- step states
- retry budgets
- timing windows

NOT on:

- worker ordering
- timing differences
- retry scheduling race conditions

---

# Summary

This runtime guarantees:

- deterministic execution
- distributed safety
- strong recovery model
- no corruption under concurrency

It is designed for:

- high-scale AI orchestration
- fault-tolerant pipelines
- production-grade distributed systems