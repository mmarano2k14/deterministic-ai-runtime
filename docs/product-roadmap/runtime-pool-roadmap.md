# Runtime Pool Roadmap

## Deterministic AI Runtime Platform

**Current status:** Process-host Runtime Pool identity, lifecycle, exact HTTP/gRPC routing, targeted failure isolation, capacity suppression, deterministic recovery claims, and claimed recovery execution are implemented and validated. Kubernetes Runtime Pool Pods, hierarchical capacity selection, and Redis Cluster compatibility remain planned.

---

## Product Objective

The Runtime Pool evolves runtime hosting from one execution identity per host into reusable, warm, independently addressable capacity.

The target production model is:

```text
control plane
    -> select exact runtime capacity
    -> route through stable pool endpoint
    -> execute on independently registered runtime
    -> isolate failure to runtime or host boundary
    -> recover assigned work deterministically
```

A tenant is not permanently mapped to one process or one Pod.

---

## Completed Foundation

### 1. Runtime Pool Identity Model — Completed

Implemented first-class:

- `PoolId`;
- `HostId`;
- `RuntimeInstanceId`;
- membership status;
- draining state;
- independent capacity descriptors.

The existing Kubernetes mode was preserved unchanged.

### 2. Process-Host Runtime Pool Manager — Completed

Implemented:

- several real `RuntimeInstanceOnly` child processes;
- fixture-owned and System.Diagnostics.Process launchers;
- readiness gating;
- child membership;
- minimum capacity;
- graceful shutdown;
- targeted replacement;
- opt-in dependency injection;
- real A1-to-A4 failure proof.

### 3. Exact Pool Transport Router — Completed

Implemented:

- immutable `RouteId`;
- exact route registry;
- forwarding leases;
- graceful route drain;
- stable HTTP endpoint;
- stable gRPC endpoint;
- existing DTO and gRPC contract reuse;
- exact response identity validation;
- no sibling fallback;
- real HTTP and gRPC end-to-end proofs.

### 4. Exact Pool Failure Recovery — Completed

Implemented:

- exact failure journal;
- exact runtime capacity suppression;
- suppression-aware routing;
- exact assigned-work enumeration;
- deterministic inventory fingerprint;
- atomic recovery claim;
- unique active `LeaseId`;
- stale-lease rejection;
- claimed recovery through existing ownership and transition services;
- real process-host final proof.

---

## Validation Baseline

The Runtime Pool work passed:

```text
new Runtime Pool unit and integration gates
real process-host A1 failure and A4 replacement
stable HTTP routing proof
stable gRPC routing proof
exact claimed recovery proof
```

Historical regression gates:

```text
Process HTTP P10
Process gRPC P10
Kubernetes HTTP P5
Kubernetes gRPC P5
```

The historical modes remain valid and opt-in Runtime Pool behavior does not replace them.

---

## Next: Kubernetes Runtime Pool Pod

### 5. Kubernetes Runtime Pool Pod — Planned

Introduce a new host mode in which one Pod contains:

- one Pool Manager;
- one stable HTTP/gRPC service boundary;
- several independently registered runtime instances;
- one `HostId` derived from the Pod UID.

The existing one-runtime-per-Pod Kubernetes mode remains unchanged.

### 6. Pod Failure Proof — Planned

Delete one real Runtime Pool Pod and prove:

```text
failed HostId = Pod UID
    -> suppress every RuntimeInstanceId in that Pod
    -> leave other Pods selectable
    -> recover only work assigned to failed runtimes
    -> recreate safe pool capacity
```

Host-wide failure must be atomic at the Pod UID boundary.

---

## Hierarchical Capacity

### 7. Hierarchical Capacity Selection — Planned

Capacity selection should prefer:

```text
1. ready warm runtime
2. available runtime slot in existing Pod
3. new Runtime Pool Pod
4. new cluster node
```

This hierarchy requires:

- bounded scale-out;
- admission control;
- backpressure;
- tenant quotas;
- deduplication;
- Pod and node capacity telemetry;
- clear control-plane/data-plane separation.

The transport router remains exact and does not perform this selection.

---

## Redis Cluster Compatibility

### 8. Redis Cluster Strategy — Planned

Define:

- key-slot boundaries;
- hash-tag strategy;
- atomic Lua boundaries;
- tenant/cell partitioning;
- failover behavior;
- distributed claim durability;
- pool route and membership durability;
- multi-control-plane ownership.

Redis Cluster work follows the Kubernetes Runtime Pool lifecycle because the durable state model must reflect the final host/runtime hierarchy.

---

## Production Deployment Model

The intended production topology is:

```text
multiple Kubernetes nodes
    -> multiple Runtime Pool Pods
        -> multiple warm runtime instances per Pod
            -> multiple workers per runtime
```

Supporting services include:

- shared admission queue;
- tenant-aware capacity selection;
- control-plane leadership;
- bounded provider scale-out;
- node autoscaling;
- durable recovery claims;
- Redis Cluster or cell-based coordination;
- MongoDB durable history;
- ledger, tracing, metrics, and forensics.

---

## Non-Goals

The Runtime Pool roadmap does not:

- map one tenant permanently to one process;
- make transport routing responsible for scheduling;
- make providers own recovery;
- remove the existing one-runtime-per-Pod mode;
- treat metadata as correctness authority;
- hide ambiguous fallback behavior behind retries.

---

## Related Documents

- [Runtime Pool Architecture](../ai/runtime-pool-architecture.md)
- [Runtime Pool Failure Recovery](../ai/runtime-pool-failure-recovery.md)
- [Runtime Provider and Transport Model](runtime-provider-and-transport-model.md)
- [Testing and Reliability Strategy](testing-and-reliability-strategy.md)
- [Current Foundation](current-foundation.md)
- [What Already Exists Today](what-already-exists.md)
