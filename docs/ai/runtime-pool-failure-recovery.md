# Runtime Pool Failure Recovery

**Status:** Implemented and end-to-end validated for child-runtime and full-boundary failure in ProcessHostPool and KubernetesPool over HTTP and gRPC.

This document describes how one exact failure fact becomes one exact recovery operation while preserving durable DAG identity, sibling safety, bounded capacity, and historical evidence.

---

## Executive Summary

Child runtime failure:

```text
runtime process fails
    ↓
immutable durable FailureId
    ↓
exact runtime marked unsafe
    ↓
exact assigned work enumerated
    ↓
one recovery claim acquired
    ↓
in-flight execution resumes same ExecutionId
    ↓
replacement child restores membership
```

Full boundary failure:

```text
ProcessHost or Pod fails
    ↓
exact HostId / PodUid membership captured
    ↓
all runtimes from failed boundary suppressed
    ↓
other boundaries stay selectable
    ↓
replacement boundary created
    ↓
only failed membership work recovered
```

The router never performs sibling fallback and the failure boundary never replaces the child runtime identities.

---

## Recovery Identities

| Identity | Purpose |
|---|---|
| `FailureId` | Immutable failure observation and incident correlation. |
| `PoolId` | Logical pool authority. |
| `HostId` | Exact parent host or Pod incarnation. |
| `RuntimeInstanceId` | Exact runtime execution capacity. |
| `RouteId` | Exact route incarnation when the topology uses route registration. |
| `ClaimId` | Deterministic recovery-claim identity. |
| `LeaseId` | Active claim acquisition generation. |
| `LocalRunId` | Runtime-local assigned-work identity. |
| `ExecutionId` | Durable DAG execution identity. |
| `SharedRunId` | Durable shared-run identity used for redispatch and ownership. |
| `TenantId` / `TenantGroupId` | Typed tenant isolation boundary. |

Metadata is never parsed to determine recovery authority.

---

## Durable Failure Observation

Unexpected failure is recorded through the Runtime Pool Failure Journal.

A runtime-instance observation preserves:

```text
FailureId
Scope = RuntimeInstance
PoolId
HostId
RuntimeInstanceId
RouteId
ObservedAtUtc
```

A complete host-boundary failure uses host-membership scope and identifies the exact failed membership of one ProcessHost or Kubernetes Pod incarnation.

The durable MongoDB implementation allows the process that observes failure and the control plane that coordinates recovery to share the same authority.

See [Runtime Pool Failure Authority](runtime-pool-failure-authority.md).

---

## Lifecycle Ordering

For a child failure, the ordering preserves identity before route and capacity mutation:

```text
record failure
    before
mark exact capacity unsafe
    before
remove / suppress exact route
    before
publish child completion
    before
create replacement child
```

For a full boundary failure, exact failed membership is captured before replacement membership can be confused with the failed host incarnation.

---

## Current State, Failure Facts, and History

Recovery reads several distinct authorities:

```text
Runtime Registry
    -> current state

Runtime Pool Failure Journal
    -> immutable failure fact

Runtime Lifecycle Journal
    -> append-only host/runtime/placement history

Runtime Run/Execution Index
    -> exact assigned work

Recovery Claim Store
    -> mutation exclusivity
```

One store is not used as an accidental substitute for another.

---

## Exact Capacity Suppression

Child failure suppresses only the exact failed runtime identity.

```text
unsafe = { failed runtime }
safe   = { siblings + valid replacement capacity }
```

A full host-boundary failure suppresses the exact membership of that host incarnation while leaving other ProcessHosts or Pods selectable.

Historical failure evidence remains durable. Current recovery scopes suppression evidence by the current `FailureId` and failure scope rather than counting every historical suppression ever associated with the host.

---

## Assigned-Work Enumeration

Recovery reuses the durable runtime-run execution index rather than creating a competing work inventory.

The enumerator validates failure and safety authority before accepting a candidate.

Candidate identity includes:

```text
FailureId
PoolId
HostId
RuntimeInstanceId
RouteId
LocalRunId
ExecutionId
SharedRunId
TenantId
TenantGroupId
WorkKind
Status
```

A candidate escaping the failed runtime or failed membership boundary is rejected.

---

## In-Flight Recovery

An in-flight run already has a durable `ExecutionId`.

Recovery must preserve it:

```text
ExecutionIdBefore == ExecutionIdAfter
```

The existing DAG recovery transition remains responsible for resume metadata, step-claim convergence, run-index mutation, and recovery evidence.

The final production proofs kill a runtime after at least 25 of 50 steps and verify the same execution continues after recovery.

---

## Local-Queued Recovery

Local runtime queues are not durable truth.

When a queued item has not yet started a DAG execution, recovery redispatches from durable shared-run identity:

```text
SharedRunId present
ExecutionId absent
```

The same contract prevents a vanished local queue from becoming a lost run.

---

## Deterministic Inventory and Claim Authority

Before mutation authority is granted, recoverable work is normalized and fingerprinted from first-class identity.

A deterministic claim binds failure authority to the exact inventory.

```text
failure authority
    + inventory fingerprint
    + candidate count
    -> ClaimId
```

Each acquisition has an active `LeaseId`. Stale lease generations cannot authorize transitions after a newer acquisition owns the claim.

The validated production scenarios exercise the exact Runtime Pool claim and recovery executor path. A fully distributed multi-control-plane durable claim/completion protocol remains separate future hardening.

---

## Exact Child Recovery

The final child recovery proof requires:

```text
CandidateCount = 1
AcceptedCount  = 1
RejectedCount  = 0
RecoveredRunCount = 1
```

It also requires:

- same `ExecutionId` across in-flight recovery;
- parent ProcessHost or Pod survives;
- healthy sibling identities remain unchanged;
- child membership returns to the configured bound;
- one forensic record exists for the affected run.

---

## Exact Parent ProcessHost Recovery

A distinct fully busy ProcessHost is selected only after warm capacity has reconverged.

With five runtimes per ProcessHost:

```text
FailedRuntimeCount = 5
CandidateCount     = 5
AcceptedCount      = 5
RejectedCount      = 0
RecoveredRunCount  = 5
```

The replacement ProcessHost receives a fresh host incarnation and five fresh child runtime identities.

---

## Exact Kubernetes Pod Recovery

The same hierarchical contract applies to a KubernetesPool Pod.

The test force-deletes a distinct fully busy Pod after the child-runtime failure has already converged.

With five runtimes per Pod:

```text
failed Pod membership = 5 runtimes
recovered work         = 5 runs
surviving Pod count    = 2
replacement Pod count  = 1
final active Pods      = 3
final active runtimes  = 15
```

The child runtime failure and the later Pod failure are separate incidents with separate recovery scope.

---

## Deterministic Failure Waves

The combined scenario does not rely on timing luck.

For a 3 × 5 pool with five submission waves:

```text
waves 1-4 = 60 DAGs
    ↓
kill one child runtime at >= 25 / 50 steps
    ↓
recover exactly one run
    ↓
drain initial workload
    ↓
wait exact warm topology and capacity
    ↓
wave 5 = 15 DAGs
    ↓
select distinct boundary with 5 / 5 active runtimes
    ↓
kill ProcessHost or Pod
    ↓
recover exactly five runs
```

No extra runs are added to make the failure easier to hit. The configured total remains 75 DAGs per cycle.

---

## Warm-Reuse Proof

The scenario executes two complete cycles with no intermediate cleanup.

Cycle two must start with the final topology produced by cycle one:

```text
ColdStart = false
ReusedBoundaryCount = 3
ReusedRuntimeCount  = 15
CleanupSincePreviousCycle = false
```

This proves the recovery mechanism does not merely work on a fresh pool; the repaired topology remains reusable production capacity.

---

## Replay, Ledger, Trace, Lifecycle, and Forensics

Recovery is not considered correct merely because all runs eventually finish.

Every completed scenario validates:

- exact dispatch;
- DAG completion;
- deterministic replay;
- execution ledger;
- logical-step ledger identity;
- trace evidence;
- runtime lifecycle history;
- recovery forensics;
- no duplicate dispatch;
- no lost run;
- no failed run;
- no configured capacity overflow.

---

## Final Validation Matrix

```text
gRPC + ProcessHostPool   PASS
HTTP + ProcessHostPool   PASS
gRPC + KubernetesPool    PASS
HTTP + KubernetesPool    PASS
```

Each final scenario validates:

```text
150 / 150 DAGs completed
7500 logical steps
2 child runtime crashes
2 full-boundary crashes
12 recovered runs
150 replay proofs
0 lost runs
0 failed runs
0 duplicate dispatch
0 capacity violations
```

See [Runtime Pool Production Validation](runtime-pool-production-validation.md).

---

## Current Boundaries

The validated implementation already provides shared durable failure facts and durable lifecycle history.

The main remaining distributed-systems hardening boundaries are:

- durable multi-control-plane recovery-claim ownership and completion semantics;
- Redis Cluster key-slot and failover validation;
- broader multi-node cluster stress and autoscaling integration;
- managed-hosting control-plane leadership and operational packaging.

These are future scale and deployment concerns, not missing evidence for the current single-control-plane Runtime Pool correctness contract.

---

## Related Documents

- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Identity Model](runtime-pool-identity-model.md)
- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Testing Strategy](testing-strategy.md)
