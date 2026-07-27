# Runtime Pool Identity Model

## Status

Step 1 foundation for the Runtime Pool architecture.

This document defines the identity contract introduced before any Runtime Pool Manager,
transport router, or Kubernetes Runtime Pool Pod is created.

## Compatibility Boundary

The existing Kubernetes mode remains unchanged:

```text
AiRuntimeHostCreationMode.Kubernetes = 2
    -> KubernetesAiRuntimeHostCreationStrategy
    -> one RuntimeInstanceOnly runtime per Pod/Service
```

The future Runtime Pool hosting path will be introduced through a separate opt-in mode and
strategy. Step 1 does not create or activate that mode.

## First-Class Identities

```text
PoolId
  -> logical runtime pool

HostId
  -> immutable identity of one exact host incarnation

RuntimeInstanceId
  -> independent identity of one exact runtime process
```

Several runtime instances may share the same `PoolId` and `HostId`, but each one must keep a
distinct `RuntimeInstanceId`.

For Kubernetes Runtime Pool hosting, the provider maps the Kubernetes Pod UID to the generic
`HostId`. Kubernetes-specific names and labels are not authoritative host identities.

## Validation Rules

1. `RuntimeInstanceId` is always required.
2. `PoolId` and `HostId` reject whitespace-only values.
3. A registration that defines `PoolId` must also define `HostId`.
4. `HostId` may exist without `PoolId` so current Process and Kubernetes modes remain compatible.
5. Metadata is never used to infer pool or host membership.

## Metadata Boundary

Metadata is limited to optional diagnostic, observability, dashboard, label, version, zone, or
provider-specific information.

Metadata must not control:

- routing;
- membership;
- lifecycle;
- draining;
- capacity selection;
- admission;
- recovery.

Any value required for correctness must be represented by a typed first-class property and, when
necessary, an explicit index or store contract.

## Membership Queries

The Step 1 membership reader exposes:

```text
ListByPoolIdAsync(poolId)
ListByHostIdAsync(hostId)
ListHostIdsByPoolIdAsync(poolId)
```

Stopped runtime instances are excluded from active membership. Draining and unhealthy instances
remain visible because lifecycle and recovery still need to enumerate them.

## Out of Scope

Step 1 does not add:

- a Runtime Pool Manager;
- child runtime process creation;
- a transport router;
- grouped host failure suppression;
- hierarchical capacity selection;
- a new Kubernetes host creation mode;
- Redis Cluster key partitioning.

Those capabilities belong to later roadmap steps.
