# Dag Recovery

## Overview

introduces the next level of distributed runtime resilience:
**deterministic recovery under real failure conditions**.

At this stage, the runtime must remain correct even when:
- a worker crashes after claiming a step
- a step remains stuck in `Running`
- a retry becomes eligible while recovery is happening
- multiple workers attempt recovery simultaneously
- convergence or finalization is interrupted mid-transition

This phase is not about adding a new feature.
It is about making the execution model **survive production reality** without losing determinism.

---

## Why this phase matters

A distributed runtime cannot be considered robust simply because it works when everything goes well.

It must also remain correct when:
- workers disappear
- timing drifts
- operations are interrupted
- recovery races happen
- multiple nodes observe the same stale state at the same time

Without recovery hardening, the runtime risks:
- double step execution
- abandoned running steps
- premature terminal finalization
- retry corruption
- non-deterministic distributed behavior

Phase 3 addresses those risks directly.

---

## Main objective

Ensure that the DAG runtime can recover safely from interrupted execution while preserving:
- exclusive ownership
- monotonic state transitions
- deterministic convergence
- atomic recovery decisions
- terminal consistency

In practical terms:

> A worker may die at any moment, and the system must still continue correctly.

---

## Scope

Phase 3 covers:
- running step lease semantics
- expired claim reclaim logic
- crash consistency around critical transitions
- recovery-aware convergence
- terminal finalization safety
- expert-level multi-worker recovery tests

It does **not** primarily focus on:
- Redis script micro-optimization
- metrics and observability polish
- long-term storage cleanup policy
- full production operations playbooks

Those belong mostly to later hardening phases.

---

## Core concepts

## 1. Running step lease model

A running step must have a clear ownership contract.

A valid distributed claim should be time-bounded and should expose enough metadata to answer:

- Who owns this step?
- Until when is this claim valid?
- Can another worker legally reclaim it?
- Is the current running state still authoritative?

Typical metadata includes:
- `ClaimedAtUtc`
- `LeaseExpiresAtUtc`
- `ClaimOwner`
- `ClaimToken`

The exact field names may vary, but the semantics must be explicit.

### Why this matters

Without an explicit lease model, the runtime cannot distinguish between:
- an actively running step
- an abandoned step
- a reclaimable step
- a stale observation of a step

That ambiguity is dangerous in distributed recovery.

---

## 2. Timed-out step reclaim

When a worker claims a step and disappears before completion, another worker must eventually be able to resume progress.

That requires a reclaim mechanism with strong guarantees:
- only expired running steps are reclaimable
- reclaim is atomic
- only one worker can reclaim ownership
- completed or failed steps cannot be reclaimed
- repeated recovery scans do not corrupt state

### Recovery rule

A timed-out step must not become executable again through guesswork.
It must become reclaimable through an **authoritative atomic transition**.

This is especially important when many workers are observing the same stale step.

---

## 3. Crash consistency

The most difficult failure cases are not clean failures.
They are **mid-transition failures**.

Examples:
- crash after claim, before work starts
- crash after work succeeds, before completion is persisted
- crash during failure persistence
- crash during retry scheduling
- crash during convergence evaluation
- crash during terminal finalization

Each critical transition must therefore be evaluated with the following questions:

- What happens if the worker crashes just before the transition?
- What happens if it crashes during the transition?
- What happens if it crashes just after the transition?
- Can the operation be replayed safely?
- Can another worker observe an intermediate state?
- Is the transition atomic, idempotent, or monotonic?

This analysis is a key part of Phase 3.

---

## 4. Recovery-aware convergence

Convergence cannot rely only on superficial status inspection.

Once retries and timed-out reclaims exist, convergence must reason about:
- valid running claims
- expired running claims
- retry windows not yet open
- retry windows already open
- reclaimable abandoned work
- terminally failed work with no remaining legal recovery path

### Recovery-safe status interpretation

#### `Completed`
An execution may be considered completed only if:
- all steps are completed
- no valid running claim exists
- no reclaimable running step exists
- no retry is pending or eligible

#### `Failed`
An execution may be considered failed only if:
- at least one terminal failure exists
- no valid retry path remains
- no valid running claim remains
- no reclaimable step can still produce progress

#### `Running`
An execution is running if:
- a valid claimed step is still active
- or immediately executable work exists now

#### `Waiting`
An execution is waiting if:
- no work is executable right now
- but future progress is still legal and possible

Examples:
- retry delay not reached yet
- dependency chain still blocked
- a running step is still valid and has not yet timed out

This distinction is critical to prevent premature failure or premature completion.

---

## 5. Terminal finalization safety

Terminal persistence must remain conservative.

The runtime must never finalize an execution while there is still a legal path for progress.

That means terminal finalization must account for:
- valid running claims
- reclaimable timed-out claims
- future retry eligibility
- unresolved distributed collisions
- optimistic concurrency conflicts during final state persistence

### Finalization rule

Terminal state must be:
- authoritative
- monotonic
- replay-safe
- conflict-safe

Once persisted, terminal state must not regress.

---

## Design principles

## Atomicity
All decisions that assign exclusive ownership must be atomic.

## Idempotence
Recovery operations must be safe if executed multiple times.

## Monotonicity
State progression must move forward only.
Terminal state must never regress.

## Conservatism
Finalization should wait rather than terminate too early.

## Determinism
Recovery must not introduce randomness into logical outcomes.

---

## Recommended implementation order

## Milestone 1 — Formalize the lease model
Clarify step ownership and timeout semantics.

## Milestone 2 — Harden reclaim behavior
Ensure expired claims can be recovered safely and atomically.

## Milestone 3 — Define crash-consistency rules
Document and verify behavior around critical interrupted transitions.

## Milestone 4 — Couple recovery and convergence
Ensure the convergence evaluator understands valid future progress.

## Milestone 5 — Harden terminal finalization
Protect final persistence against collisions and premature closure.

## Milestone 6 — Expand the distributed recovery test suite
Validate correctness under multi-worker and crash scenarios.

---

## Expected technical outcomes

After Phase 3, the runtime should be able to claim:

- distributed running steps are lease-protected
- abandoned steps can be reclaimed safely
- retries and recovery do not conflict
- convergence remains correct after crash recovery
- finalization is resistant to recovery races
- repeated recovery scans remain safe
- multi-worker crash conditions do not break determinism

This is the level where the runtime moves from **working distributed execution** to **production-grade distributed resilience**.

---

## Suggested test matrix

### Required
- single winner reclaim after lease expiration
- no reclaim before expiration
- no reclaim after step completion
- crash after claim before completion
- crash during retry scheduling
- crash during convergence
- crash during finalization
- no indefinite stuck execution when recovery path exists
- repeated recovery scans remain safe
- 20-worker reclaim race
- 50-worker reclaim race

### Strongly recommended
- simulated latency on distributed store operations
- simulated stale reads during recovery
- duplicate recovery invocations
- claim token collision tests
- terminal replay consistency tests

---

## Non-goals

This phase is not meant to fully solve:
- monitoring dashboards
- runtime metrics strategy
- long-term storage retention policy
- advanced cleanup lifecycle automation
- performance tuning of every Redis script path

Those can be layered after recovery correctness is fully secured.

---

## Conclusion

Phase 3 is the point where the runtime is hardened against the real world.

The goal is not only to execute DAG pipelines correctly under concurrency,
but to ensure they remain correct when:
- nodes fail
- steps are abandoned
- recovery races occur
- transitions are interrupted

That is what separates a functional distributed engine from an expert-level runtime design.

---

## Next phase

After:
**Phase 4 — Redis/Lua hardening, deeper cleanup guarantees, and operational readiness**