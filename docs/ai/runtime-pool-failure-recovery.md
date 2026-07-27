# Runtime Pool Failure Recovery

**Status:** Implemented and validated for the opt-in process-host Runtime Pool. The current recovery coordination stores are local to one pool host. Durable distributed coordination for Kubernetes Runtime Pool Pods and multiple control planes remains roadmap work.

This document describes how one exact failed runtime instance is converted into one exact, claimed recovery operation without contaminating sibling capacity.

---

## Executive Summary

The recovery chain is:

```text
A1 process exits unexpectedly
    ↓
FailureId records exact A1 failure
    ↓
A1 capacity is suppressed
    ↓
A1 route is removed
    ↓
A4 replacement starts
    ↓
A1 assigned work is enumerated
    ↓
one deterministic recovery claim is acquired
    ↓
in-flight work resumes the same ExecutionId
local-queued work redispatches from SharedRunId
    ↓
claim remains held until explicit release
```

The safety set is exact:

```text
unsafe = { A1 }
safe   = { A2, A3, A4 }
```

---

## Recovery Identities

The recovery model uses explicit immutable identities.

| Identity | Purpose |
|---|---|
| `FailureId` | One immutable failure observation. |
| `PoolId` | Logical pool authority. |
| `HostId` | Host-incarnation authority. |
| `RuntimeInstanceId` | Exact failed capacity. |
| `RouteId` | Exact failed transport-route incarnation. |
| `ClaimId` | Deterministic claim over exact failure authority and inventory. |
| `LeaseId` | Unique active acquisition generation. |
| `LocalRunId` | Runtime-local assigned work identity. |
| `ExecutionId` | Existing durable DAG execution identity, when started. |
| `SharedRunId` | Durable shared-run identity used for redispatch. |

Metadata is not parsed to determine failure or recovery authority.

---

## Failure Observation

An unexpected child exit creates an `AiRuntimePoolFailureObservation`.

The observation contains:

```text
FailureId
FailureScope
FailureKind
PoolId
HostId
RuntimeInstanceId
RouteId
ExitCode
ObservedAtUtc
FailureMessage
```

The current process-host scope is:

```text
FailureScope = RuntimeInstance
```

Host-wide failure scope is reserved for future Kubernetes Pod failure handling.

Requested shutdown does not create a failure observation.

---

## Lifecycle Ordering

The ordering is deliberate:

```text
record failure
    before
suppress capacity
    before
remove route
    before
publish completion to pool manager
    before
start replacement
```

This preserves the exact failed route and runtime identity before the route disappears and before replacement capacity can be created.

A failure-observer error does not skip route cleanup. It is surfaced as a lifecycle fault.

---

## Failure Journal

The process-host implementation uses a thread-safe in-memory failure journal.

It supports:

- idempotent recording of the same immutable failure;
- conflict detection when one `FailureId` is rebound;
- lookup by `FailureId`;
- lookup by `HostId`;
- lookup by `RuntimeInstanceId`.

A1 failure lookup cannot return A2 or A3 observations.

---

## Exact Capacity Suppression

The failure observer projects runtime-instance failures into immutable capacity suppression.

A suppression contains:

```text
FailureId
PoolId
HostId
RuntimeInstanceId
RouteId
SuppressedAtUtc
```

There is deliberately no unsuppress operation for the same immutable `RuntimeInstanceId`.

Replacement capacity receives a new runtime identity and therefore starts safe.

---

## Routing Safety

HTTP and gRPC routing check suppression:

1. before route-lease acquisition;
2. after route-lease acquisition.

The second check closes the race where the runtime becomes unsafe between lookup and transport invocation.

Suppressed capacity returns:

```text
runtime-pool-capacity-suppressed
```

The router never falls back to a sibling.

---

## Assigned-Work Enumeration

Recovery reuses the existing durable runtime-run index:

```text
IAiRuntimeRunExecutionIndex
    .ListRecoverableByRuntimeInstanceAsync(RuntimeInstanceId)
```

A second work index is not introduced.

Enumeration is allowed only when the failure observation and capacity suppression agree on:

```text
FailureId
PoolId
HostId
RuntimeInstanceId
RouteId
```

A sibling entry returned for A1 is rejected as a runtime-boundary violation.

---

## Candidate Model

Each candidate preserves first-class identity:

```text
FailureId
PoolId
HostId
RuntimeInstanceId
RouteId
LocalRunId
ExecutionId
Status
TenantId
TenantGroupId
SharedRunId
WorkKind
CreatedAtUtc
```

Candidates are ordered deterministically:

```text
1. InFlight
2. LocalQueued
3. OtherRecoverable
```

Then by timestamp and `LocalRunId`.

---

## In-Flight Recovery

An in-flight candidate has a durable `ExecutionId`.

```text
RuntimeInstanceId = A1
LocalRunId        = local-a1-flight
ExecutionId       = execution-a1
```

Recovery must preserve that `ExecutionId`.

The claimed executor resolves existing shared-run ownership and delegates to the existing recovery transition service with `DryRun = false`.

The transition boundary remains responsible for durable pause/requeue/resume metadata, DAG step-claim recovery, index mutation, and recovery evidence.

---

## Local-Queued Recovery

A local-queued candidate has:

```text
SharedRunId present
ExecutionId absent
```

The local queue is not durable truth. If the process dies, the local queue disappears.

Recovery redispatches from durable shared-run state.

A local-queued candidate carrying an `ExecutionId`, or missing `SharedRunId`, is rejected before mutation.

---

## Other Recoverable States

`OtherRecoverable` candidates do not trigger an implicit mutation.

They receive a deterministic no-mutation result:

```text
unsupported-recovery-candidate-kind
```

This avoids inventing recovery behavior for a state that has not been explicitly defined.

---

## Deterministic Inventory Fingerprint

Before mutation authority is granted, the inventory is fingerprinted with SHA-256.

The fingerprint covers ordered first-class candidate identity:

```text
LocalRunId
ExecutionId
Status
TenantId
TenantGroupId
SharedRunId
WorkKind
CreatedAtUtc
```

Diagnostic metadata is excluded.

Changing candidate identity or order changes the fingerprint.

---

## Atomic Recovery Claim

The claim contains:

```text
ClaimId
FailureId
PoolId
HostId
RuntimeInstanceId
RouteId
InventoryFingerprint
CandidateCount
ClaimedBy
ClaimedAtUtc
```

`ClaimId` is deterministic for the exact authority and inventory.

Concurrent acquisition semantics are:

```text
20 coordinators
    -> 1 Acquired
    -> 19 AlreadyClaimed
```

Only the acquired result contains a lease.

---

## Lease Incarnation and Stale-Lease Protection

Every acquisition receives a unique `LeaseId`.

```text
same deterministic ClaimId
new acquisition
    -> new LeaseId
```

The claim store verifies that the supplied `LeaseId` still owns the active claim.

This prevents a released lease from authorizing transitions after another coordinator reacquires the same deterministic claim.

The private release token is not exposed.

---

## Claimed Recovery Executor

The executor accepts only an `Acquired` claim with an active lease.

Before every candidate it validates:

- claim status;
- lease presence;
- lease not released;
- active lease generation;
- claim/inventory authority;
- inventory fingerprint;
- candidate boundary.

It then resolves ownership and validates:

- `RuntimeInstanceId`;
- `LocalRunId`;
- `ExecutionId`;
- `SharedRunId`;
- `TenantId`;
- `TenantGroupId`.

The existing transition result is also checked for identity escape.

---

## Claim Release Semantics

The executor does not release the claim.

```text
execute transitions
    ↓
return deterministic outcomes
    ↓
caller durably observes completion
    ↓
caller releases lease
```

If a transition throws, the claim remains active.

This prevents another coordinator from silently starting a second recovery attempt while the first attempt has an unresolved outcome.

---

## Exact Failure and Recovery Boundary

The complete exact boundary is:

```text
failure authority
    = FailureId + PoolId + HostId + RuntimeInstanceId + RouteId

work authority
    = failure authority
      + LocalRunId
      + ExecutionId or SharedRunId
      + tenant identity

mutation authority
    = work authority
      + InventoryFingerprint
      + active ClaimId
      + active LeaseId
```

A mismatch at any layer rejects the operation.

---

## Real Process-Host Proof

The final infrastructure proof uses:

- three real external `RuntimeInstanceOnly` child processes;
- a real operating-system kill of A1;
- real pool lifecycle and A4 replacement;
- real failure journaling;
- real capacity suppression;
- real route removal;
- real assigned-work enumeration;
- real claim arbitration;
- real claimed-recovery executor.

The transition interfaces are provided by a deterministic fixture-owned adapter so the test can inspect every exact ownership and mutation request without duplicating a full DAG workload inside this infrastructure-specific scenario.

The broader runtime suite separately validates real DAG resume, local-queued redispatch, replay, ledger, trace, forensics, and tenant isolation under process loss.

---

## Validated Assertions

The implementation validates:

- exactly one A1 failure;
- exactly one A1 suppression;
- no A2/A3/A4 suppression;
- A2/A3 `RouteId` preservation;
- fresh A4 runtime and route identity;
- A1-only assigned-work enumeration;
- deterministic candidate order;
- one claim winner under concurrency;
- denied coordinators receive no lease;
- same `ExecutionId` for in-flight recovery;
- exact `SharedRunId` for local-queued redispatch;
- no sibling ownership or transition escape;
- claim remains active after execution;
- explicit, idempotent release;
- stale lease rejection after reacquisition.

---

## Current Boundaries

The current process-host recovery coordination is local to one pool host.

The following remain roadmap work:

- durable distributed failure journal;
- durable distributed capacity-safety registry;
- durable distributed recovery claim store;
- multi-control-plane claim ownership;
- host-wide suppression by Kubernetes Pod UID;
- recovery after complete pool-host loss;
- Redis Cluster key-slot and failover validation.

---

## Related Documents

- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Provider-Agnostic Process-Host Recovery](provider-agnostic-process-host-recovery.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Testing Strategy](testing-strategy.md)
- [Runtime Pool Product Roadmap](../product-roadmap/runtime-pool-roadmap.md)
