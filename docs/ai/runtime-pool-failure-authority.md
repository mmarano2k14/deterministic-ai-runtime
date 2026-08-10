# Runtime Pool Failure Authority

**Status:** Implemented and validated with a shared durable MongoDB failure journal across external ProcessHostPool processes and the control plane.

This document defines the correctness authority for Runtime Pool failure facts and explains how that authority differs from lifecycle history, recovery claims, and recovery forensics.

---

## Why a Separate Failure Authority Exists

A failure must remain identifiable after the failed route, process, ProcessHost, or Pod disappears.

The runtime therefore records one immutable failure fact before recovery mutates ownership.

```text
physical failure
    ↓
immutable FailureId
    ↓
durable failure fact
    ↓
capacity suppression / membership suppression
    ↓
exact recovery claim
    ↓
recovery transition
```

Without this durable fact, an external parent process could observe a child exit while a separate control plane has no authoritative incident identity to recover.

---

## Store Responsibilities

The runtime intentionally keeps different forms of evidence separate.

| Store | Responsibility |
|---|---|
| Runtime Registry | Current runtime state and current capacity. |
| Runtime Pool Failure Journal | Correctness authority for immutable Runtime Pool failure facts. |
| Runtime Lifecycle Journal | Append-only infrastructure and run-placement history. |
| Recovery Claim Store | Exclusive mutation authority for one recovery attempt. |
| Decision Ledger | Control-plane and runtime decision evidence. |
| Recovery Forensics | Per-work-item recovery timeline. |

The failure journal is not a replacement for the lifecycle journal. The lifecycle journal is not the claim store.

---

## Failure Observation

A failure observation preserves typed authority:

```text
FailureId
Scope
Kind
PoolId
HostId
RuntimeInstanceId
RouteId
ObservedAtUtc
```

Provider-specific diagnostics may be attached, but correctness is never reconstructed from arbitrary metadata.

---

## Failure Scopes

### Runtime instance

One child runtime fails while its parent boundary remains alive.

```text
Scope = RuntimeInstance
unsafe = exact RuntimeInstanceId
safe   = sibling RuntimeInstanceIds
```

### Host membership

One complete ProcessHost or Kubernetes Pod boundary fails.

```text
Scope = HostMembership
unsafe = exact runtime membership owned by failed HostId / PodUid
safe   = runtimes owned by other boundaries
```

The two scopes are intentionally not conflated.

---

## Shared MongoDB Authority

The production scenario composition uses a durable MongoDB implementation of the existing Runtime Pool failure journal contract.

The explicit registration binds directly to the configured:

```text
Mongo connection string
Database name
Collection name
```

It does not rely on a process-wide ambient `IMongoDatabase`, because different runtime services can legitimately use different MongoDB database registrations.

This guarantees that:

```text
external ProcessHost writes FailureId X
    ↓
control plane reads FailureId X
```

from the same durable authority.

---

## Idempotence and Conflict Safety

Recording the same immutable failure fact is idempotent.

Reusing one `FailureId` for incompatible authority is rejected rather than silently overwritten.

The journal supports exact queries by failure and membership identities, with indexes designed for failure, host, and runtime investigation.

---

## Incident Correlation With Lifecycle History

The durable lifecycle journal and failure journal remain separate stores but share the same incident identity:

```text
FailureJournal.FailureId
    ==
LifecycleJournal.RuntimeFailureIncidentId
```

This enables an investigation to move from one correctness failure fact to the complete infrastructure history without requiring the two stores to be physically merged.

---

## Historical Evidence Is Preserved

A recovery must not delete old failure evidence merely to simplify a later count.

For example, a ProcessHost may first lose one child runtime and later fail completely. The host history can therefore contain both incidents.

Current recovery proof scopes its suppression evidence by the current failure identity:

```text
FailureId = current incident
Scope     = current failure scope
```

This keeps historical forensics intact while preventing a previous child failure from being mistaken for an extra member of a later host failure.

---

## Capacity and Membership Safety

A failure fact is projected into exact safety state.

Child failure:

```text
suppress exact runtime identity
```

Host-boundary failure:

```text
suppress exact failed host membership
```

The historical registry snapshot of an intentionally failed runtime is not valid active capacity merely because it remains visible during convergence.

---

## What the Failure Journal Does Not Own

The failure journal does not:

- select replacement capacity;
- execute recovery transitions;
- decide DAG resume semantics;
- release recovery claims;
- become the current-state runtime registry;
- become the lifecycle audit history;
- become a global scheduler.

Those boundaries remain separate so the failure fact stays small, durable, immutable, and auditable.

---

## Related Documents

- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
