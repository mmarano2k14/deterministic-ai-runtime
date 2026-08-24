# Durable Child DAG Composition

**Status:** **Implemented / validated**  
**Status date:** 2026-08-24  
**Validation boundary:** native durable Child DAG composition is implemented and validated through recursive `ChildDepth = 3` production scenarios. The lifecycle-observation promotion gate is closed through the centralized Event Manager, canonical events, Runtime Lifecycle Journal, durable Ledger, Recovery Forensics, replay, and EventDriven production validation. The high-scale `5×5×5×2×Depth3` profiles validate the same recursive contract at larger bounded capacity. Exact nested child-step accounting remains a separate proof-hardening item and is not implied by the root-step ledger proof.

---

## Purpose

Durable Child DAG composition allows a running DAG step to delegate work to another DAG execution, wait without holding runtime capacity, and resume the same durable parent execution when the child reaches a terminal result.

The capability is designed for nested workflows, long-running orchestration, controlled delegation, and multi-agent execution without introducing a second orchestration engine.

```text
Parent execution P
    ↓
ExecuteChildDag
    ↓
Child execution C1
    ↓
optional nested child C2 / C3
    ↓
durable child completion
    ↓
durable continuation
    ↓
same parent ExecutionId resumes
```

The runtime remains the execution authority. Child DAG composition reuses the existing DAG engine, shared queue, execution store, policy engine, recovery path, Ledger, tracing, and Forensics.

---

## Historical Promotion Gate — Now Closed

The capability was previously held behind a promotion gate while recursive execution transitions were still inferred too heavily from polling and timeout diagnostics. That gate has now been closed.

The engine lifecycle observation contract now centralizes and correlates the nested lifecycle through canonical events and the existing observability surfaces:

```text
Lifecycle Events
      ↕
Durable Ledger
      ↕
Forensics
```

The required nested lifecycle includes, at minimum:

```text
Child started
→ Child waiting / running
→ Child completed
→ Continuation pending
→ Continuation scheduled
→ Continuation delivered
→ Continuation consumed
→ Parent WaitingForExternal → Ready
→ Parent resumed
→ Parent completed
```

This is an observability and proof-completeness requirement. It is intentionally **not** solved by adding a second event bus, second lifecycle store, second ledger, or Child-DAG-specific recovery engine.

---

## Core Invariants

The implementation follows these invariants:

- reuse the existing execution engine and DAG state model;
- reuse the existing shared queue and dispatch path;
- reuse the existing policy engine and delegation policy evaluation;
- reuse the existing recovery ownership and execution-resume mechanisms;
- keep `ChildDepth = 0` behavior unchanged;
- derive deterministic child invocation identity from the parent invocation context;
- persist the durable child execution identity before child-side effects;
- freeze the child definition and invocation input required for deterministic recovery;
- move the parent step to `WaitingForExternal` before releasing its claim, lease, and runtime capacity;
- persist child completion before scheduling parent continuation;
- make continuation delivery at-least-once while keeping logical continuation idempotent;
- preserve the same durable `ExecutionId` across physical runtime or Pod recovery;
- keep physical execution attempts distinct through `LocalRunId` and runtime identity;
- do not create a duplicate scheduler, queue, store, recovery system, event system, or orchestration model.

---

## Durable Identity Model

Child DAG composition extends the existing runtime identity model rather than replacing it.

Important identities include:

```text
Parent ExecutionId
Parent StepId / StepKey
ChildInvocationKey
ChildExecutionId
SharedRunId
LocalRunId
RuntimeInstanceId
ControlPlaneId
TenantId / TenantGroupId
CorrelationId / CausationId
```

The logical child invocation is deterministic. Retries, duplicate delivery, recovery, and continuation redrive must converge on the same durable child relation and the same logical child execution for the same invocation generation.

Physical attempts remain separate from logical execution identity:

```text
one durable ExecutionId
    ↓
zero or more physical LocalRunId attempts
    ↓
possibly different RuntimeInstanceId / Pod UID after recovery
```

This separation is essential for exact recovery and forensic reconstruction.

---

## Durable Composition Flow

The native composition step is exposed as:

```text
execution.child-dag
```

Its orchestration flow is:

```text
freeze child definition + invocation input
        ↓
create/load durable child relation
        ↓
evaluate delegation policy
        ↓
allocate durable ChildExecutionId
        ↓
create child execution exactly once
        ↓
dispatch through the existing shared queue
        ↓
park parent step as WaitingForExternal
        ↓
child executes / retries / recovers normally
        ↓
freeze terminal child result
        ↓
schedule deterministic continuation
        ↓
resume the same parent ExecutionId
        ↓
consume child result and continue parent DAG
```

The Child DAG path therefore composes existing runtime primitives instead of bypassing them.

---

## `WaitingForExternal` and Capacity Release

A parent waiting for a child must not consume a runtime slot indefinitely.

The durable sequence is:

```text
business wait becomes durable
        ↓
Park
        ↓
WaitingForExternal
        ↓
step claim released
concurrency lease released
runtime capacity released
```

When the durable child result becomes available, continuation moves the parent step back into an executable state and the same parent `ExecutionId` resumes through the normal runtime path.

This is a critical difference between durable orchestration and an in-memory nested call stack.

---

## Continuation Semantics

Child completion and parent continuation are separate durable facts.

```text
child terminal
    ↓
authoritative child result frozen
    ↓
relation Completed
    ↓
continuation Pending
    ↓
continuation Scheduled
    ↓
existing shared queue delivery
    ↓
parent step WaitingForExternal → Ready
    ↓
parent resumes
    ↓
relation converges to Resumed
```

Continuation uses deterministic shared-run identity and existing queue ownership semantics so duplicate delivery can converge without creating duplicate logical continuation.

Terminal or incompatible parent state suppresses or retires obsolete continuation delivery rather than allowing a stale delivery to mutate a terminal execution.

---

## Recovery Semantics

Child DAG recovery does not introduce a special recovery engine.

The same runtime recovery mechanisms remain authoritative:

- in-flight resume preserves the durable `ExecutionId`;
- physical replacement can change `RuntimeInstanceId`, `LocalRunId`, ProcessHost, or Pod UID;
- failed physical attempts remain historical evidence;
- shared-run ownership determines which physical attempt is authoritative;
- replay, Ledger, trace, lifecycle, and Forensics evidence remain correlated with the durable execution.

Example:

```text
ChildExecutionId = stable
ExecutionId      = stable

runtime process dies
        ↓
new RuntimeInstanceId
new LocalRunId
same durable ExecutionId
        ↓
child continues
        ↓
parent continuation resumes same parent ExecutionId
```

---

## Policy and Tenant Isolation

Delegation uses the existing Policy Engine rather than a Child-DAG-specific authorization model.

The durable relation records policy outcome and execution identity under the existing execution context. Tenant, tenant-group, RBAC, project, pipeline, provider, and other execution context rules remain part of the same runtime boundary.

Nested execution must therefore preserve the same isolation properties already required by distributed runtime execution:

- no cross-tenant child visibility;
- no cross-tenant dispatch;
- no cross-tenant recovery ownership;
- no cross-tenant Ledger or Forensics leakage;
- no bypass of configured delegation policy.

---

## Validation Depth Terminology

The production composition primitive is generic. `ChildDepth` is a **validation-scenario parameter** used by the production test matrix to express how many nested delegation levels are composed for a proof run. It is not a separate orchestration engine or a production-only execution mode.

---

## Current Validation Evidence

### Historical behavior — `ChildDepth = 0`

The existing non-child execution behavior remains the compatibility baseline and must remain unchanged.

### Recursive baseline — `ChildDepth = 1`

The historical Depth1 production baseline is a full gRPC Kubernetes Runtime Pool warm-reuse scenario:

```text
5 Pods × 5 runtime processes
= 25 bounded runtime slots

2 warm-reuse cycles
50 parent runs per cycle
100 parent runs total
2550 parent logical steps per cycle
5100 parent logical steps total
```

The scenario proves, per cycle:

- native child DAG composition;
- exact in-Pod runtime process kill after durable progress;
- automatic recovery preserving the same durable `ExecutionId`;
- replacement runtime while the parent Pod and sibling runtimes survive;
- workload drain and bounded-capacity reconvergence;
- deletion of a distinct busy Pod;
- Pod replacement and exact work recovery;
- warm-capacity reuse across cycles;
- replay, Ledger, trace, lifecycle, and recovery Forensics evidence;
- no duplicate dispatch;
- no lost run;
- no Pod/runtime capacity exceed;
- deterministic final cleanup.

### Recursive closure — `ChildDepth = 2`

The complete recursive path has been validated beyond the earlier focused Depth-2 scenarios. The previous final-continuation ambiguity was addressed through deterministic lifecycle observation and authoritative durable DAG-state verification rather than by increasing watchdogs.

`ChildDepth = 2` is now part of the green recursive validation ladder.

### Recursive validation — `ChildDepth = 3`

Depth 3 is now validated through both an intermediate recursive proof and a larger high-scale profile:

```text
3×3×3×2×Depth3       GREEN — recursive Depth3 validation
5×5×5×2×Depth3       GREEN — high-scale validation
```

The high-scale profile executes 250 parent DAGs across two warm-reuse cycles and proves 12,750 exact **root parent** logical steps together with recursive durable terminality, real child-runtime failure, distinct parent-boundary failure, same-`ExecutionId` resume, replay, lifecycle, Ledger, trace, and Recovery Forensics evidence.

---

## Engine Lifecycle Observation — Implemented

Nested execution state is now directly explainable through the centralized event architecture.

The implementation must reuse and align the existing:

- lifecycle event manager and lifecycle events;
- durable Decision Ledger;
- Runtime Lifecycle Journal where infrastructure lifecycle applies;
- execution/recovery Forensics;
- replay and trace correlation.

No parallel source of truth is required or desired.

The target diagnostic experience is that a stalled parent can be explained immediately as, for example:

```text
ChildCompleted                ✅
ContinuationPending           ✅
ContinuationScheduled         ✅
ContinuationDelivered         ❌
ParentResumed                 ❌
```

rather than being inferred only from a timeout after several minutes.

---

## Production Validation Boundary

The former promotion gates are now closed for the current recursive validation baseline:

- full engine lifecycle transitions are observable through the centralized canonical event infrastructure;
- durable Ledger, Runtime Lifecycle Journal, and Recovery Forensics carry correlated lifecycle evidence;
- `ChildDepth = 2` recursive recovery is green;
- `ChildDepth = 3` is green at both intermediate and high-scale profiles;
- `ChildDepth = 0` compatibility and the `ChildDepth = 1` recovery baseline remain green;
- replay, Ledger, trace, lifecycle, and Forensics remain coherent after nested recovery;
- no duplicate durable dispatch or lost parent run is detected in the validated scenarios;
- no timeout increase was used as a substitute for a missing correctness transition.

The capability is therefore documented as **Implemented / validated** under the current proof boundary.

This promotion does not overstate the proof boundary. Current high-scale exact logical-step accounting covers root parent steps. Exact recursive child-step accounting across every nested level, deterministic multi-seed failure schedules, and atomic runtime-ownership overlap proof remain future hardening work.

---

## Multi-Agent Direction

Child DAG composition is the runtime primitive that enables durable multi-agent orchestration without turning the runtime into an agent framework.

```text
Orchestrator DAG
    ├── Market / data agent DAG
    ├── Strategy agent DAG
    ├── Risk agent DAG
    ├── Policy / compliance agent DAG
    └── Execution agent DAG
```

Each delegated unit can remain a normal durable DAG execution with its own execution identity, policy evaluation, runtime placement, recovery history, replay evidence, Ledger entries, and Forensics.

The runtime owns deterministic execution. Agent behavior remains a layer above it.

---

## Non-Goals

Child DAG composition does not:

- introduce a second DAG engine;
- introduce a second event bus or event manager;
- introduce a Child-DAG-only shared queue;
- introduce a Child-DAG-only recovery reconciler;
- make transport routing responsible for orchestration;
- keep a physical worker blocked while waiting for a child;
- claim unlimited nesting as production-ready;
- replace the existing Ledger, Runtime Lifecycle Journal, tracing, or Forensics stores.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Engine Event Observation and Lifecycle Catalog](engine-event-observation.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
- [Observability](observability.md)
- [Testing Strategy](testing-strategy.md)
