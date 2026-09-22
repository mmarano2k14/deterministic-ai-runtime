# Runtime Pool Identity Model

**Status:** Implemented and validated across ProcessHostPool and KubernetesPool hosting.

This document defines the correctness identities used by reusable Runtime Pools. The model is independent from transport and keeps the runtime execution identity separate from the physical failure boundary that hosts it.

---

## Compatibility Boundary

The historical Kubernetes mode remains available and keeps its original semantics:

```text
AiRuntimeHostCreationMode.Kubernetes
    -> one RuntimeInstanceOnly runtime per Pod/Service
```

Runtime Pool hosting is additive and explicit:

```text
ProcessHostPool
    -> one external parent ProcessHost
    -> several independent child runtime processes

KubernetesPool
    -> one Kubernetes Pod failure boundary
    -> several independent child runtime processes
```

The one-runtime-per-Pod Kubernetes mode is not reinterpreted as KubernetesPool.

---

## First-Class Identities

```text
PoolId
  -> logical reusable capacity group

HostId
  -> immutable identity of one exact host incarnation or failure boundary

RuntimeInstanceId
  -> independent identity of one exact runtime process

RouteId
  -> immutable transport-route incarnation for one runtime instance
```

Several runtimes may share the same `PoolId` and `HostId`, but every runtime keeps a distinct `RuntimeInstanceId`.

For KubernetesPool, the physical Pod UID is the provider-level failure-boundary identity and is correlated with the generic host identity. Pod names, labels, and metadata are operational evidence; they are not the correctness authority.

---

## Identity Invariants

1. `RuntimeInstanceId` is always explicit for selectable execution capacity.
2. `PoolId` identifies the reusable logical pool, not one physical process or Pod.
3. `HostId` identifies one immutable host incarnation.
4. `RuntimeInstanceId` is never inferred from `HostId`.
5. A replacement runtime receives a fresh runtime identity.
6. A replaced ProcessHost or Pod receives a fresh host incarnation identity.
7. `RouteId` is immutable and changes when a route incarnation changes.
8. Metadata is never parsed to infer pool membership, routing authority, lifecycle authority, tenant ownership, or recovery authority.
9. Tenant ownership is validated from typed runtime state such as `TenantId`, not from runtime-name conventions.

---

## Failure-Boundary Semantics

A runtime process and its host boundary are intentionally different identities.

```text
child runtime failure
    scope = RuntimeInstanceId
    parent ProcessHost / Pod remains alive
    healthy siblings remain valid capacity

parent ProcessHost failure
    scope = HostId
    every child runtime in that host incarnation is lost

Kubernetes Pod failure
    scope = Pod UID / HostId boundary
    every child runtime in that Pod is lost
```

This distinction is essential for exact recovery and for preventing a transport or Pod identity from becoming an execution identity.

---

## Membership Queries

The runtime membership model supports typed queries such as:

```text
ListByPoolIdAsync(poolId)
ListByHostIdAsync(hostId)
ListHostIdsByPoolIdAsync(poolId)
```

Stopped identities are excluded from active capacity. Draining, failed, historical, and replaced identities remain available through their appropriate lifecycle or forensic stores so history is not destroyed merely to simplify current-state queries.

---

## Metadata Boundary

Metadata is limited to optional diagnostic, observability, dashboard, label, version, zone, or provider-specific information.

Metadata must not control:

- routing;
- membership;
- lifecycle;
- draining;
- capacity selection;
- admission;
- tenant authorization;
- recovery;
- failure ownership.

Any value required for correctness must be represented by a typed first-class property and, when necessary, an explicit durable index or store contract.

---

## Related Documents

- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)
- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
