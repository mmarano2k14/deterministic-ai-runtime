# Runtime Pool Architecture

**Status:** Implemented and end-to-end validated for ProcessHostPool and KubernetesPool over HTTP and gRPC. Historical Process and one-runtime-per-Pod Kubernetes hosting remain available as separate compatibility modes.

The Runtime Pool architecture provides reusable warm execution capacity without collapsing a runtime process into the ProcessHost or Kubernetes Pod that happens to contain it.

---

## Executive Summary

A Runtime Pool separates five concerns:

| Concept | Responsibility |
|---|---|
| `PoolId` | Logical reusable capacity group. |
| `HostId` | Immutable incarnation of one physical failure boundary. |
| `RuntimeInstanceId` | Independently registered and selectable execution capacity. |
| `RouteId` | Immutable transport-route incarnation for one runtime. |
| Failure scope | Exact runtime failure or complete host-boundary failure. |

The control plane selects an exact `RuntimeInstanceId`. Transport then reaches that exact runtime or fails explicitly.

```text
Control Plane
    selects RuntimeInstanceId
            ↓
provider / pool transport boundary
            ↓
exact route / exact runtime command path
            ↓
selected runtime only
```

The transport layer is not a hidden scheduler. It does not silently choose a healthy sibling when the selected runtime fails.

---

## Why Runtime Pools Exist

A production runtime should not permanently map a tenant, request, workflow, or execution to one operating-system process or one Kubernetes Pod.

Reusable Runtime Pools provide:

- warm capacity;
- bounded process and Pod creation;
- independently addressable runtime instances;
- exact runtime-level failure isolation;
- exact full-boundary failure recovery;
- stable HTTP and gRPC provider behavior;
- deterministic capacity reuse;
- explicit backpressure;
- recovery without losing durable execution identity;
- a scalable path from local ProcessHosts to Kubernetes Pods.

---

## Supported Hosting Topologies

### ProcessHostPool

```text
Logical Pool
    |
    +-- ProcessHost A / HostId A
    |      +-- Runtime A1
    |      +-- Runtime A2
    |      +-- Runtime A3
    |      +-- ...
    |
    +-- ProcessHost B / HostId B
           +-- Runtime B1
           +-- Runtime B2
           +-- Runtime B3
```

Each parent ProcessHost is an external operating-system process. Each child runtime is a separate operating-system process with its own `RuntimeInstanceId`.

A child crash does not imply a parent crash. A parent crash destroys the exact child membership of that parent only.

### KubernetesPool

```text
Logical Pool
    |
    +-- Pod A / PodUid A / HostId A
    |      +-- Runtime A1
    |      +-- Runtime A2
    |      +-- Runtime A3
    |      +-- ...
    |
    +-- Pod B / PodUid B / HostId B
           +-- Runtime B1
           +-- Runtime B2
           +-- Runtime B3
```

The Pod is a failure boundary, not an execution identity. Each in-Pod runtime remains independently registered, selectable, recoverable, and replaceable.

The historical Kubernetes mode remains distinct:

```text
Kubernetes
    -> one RuntimeInstanceOnly runtime per Pod/Service

KubernetesPool
    -> several independent runtime processes per Pod
```

---

## Architectural Invariants

### Identity is first-class

Correctness relies on typed identities:

```text
PoolId
HostId
RuntimeInstanceId
RouteId
TenantId
FailureId
ExecutionId
SharedRunId
LocalRunId
```

Diagnostic metadata may duplicate these values but never becomes their authority.

### Host identity is not runtime identity

```text
HostId != RuntimeInstanceId
PodUid  != RuntimeInstanceId
```

A ProcessHost or Pod can contain several independently selectable runtimes.

### Routing is exact

```text
requested runtime A2
    -> invoke A2
    -> or explicit failure
```

No sibling fallback is permitted inside the transport router.

### Recovery is not routing

Transport failure can reveal that selected capacity is unsafe. Recovery remains a lifecycle and execution-recovery responsibility.

### Current state and durable history are separate

```text
Runtime Registry
    = current runtime state and capacity

Runtime Pool Failure Journal
    = durable correctness authority for failure facts

Runtime Lifecycle Journal
    = append-only infrastructure and placement history

Decision Ledger
    = runtime and control-plane decisions

Recovery Forensics
    = per-work-item recovery evidence
```

These stores are correlated through first-class identities rather than physically merged.

---

## Runtime Pool Manager Responsibilities

The pool manager owns physical child lifecycle:

- create child runtime processes;
- assign independent runtime identities;
- allocate transport endpoints;
- wait for readiness;
- publish membership;
- maintain bounded capacity;
- drain requested children;
- observe unexpected exits;
- replace failed children;
- stop children during host shutdown.

For KubernetesPool, the Pod-level manager owns the in-Pod child process set while Kubernetes remains the outer Pod lifecycle provider.

The manager does not choose which runtime should execute a run.

---

## Readiness Boundary

A child runtime is not usable capacity merely because its process exists.

```text
process started
    ↓
runtime registration observed
    ↓
capacity publication observed
    ↓
transport path ready
    ↓
runtime exposed as selectable capacity
```

For KubernetesPool there is an additional outer boundary:

```text
Pod ready
    !=
runtime command path ready
```

The final pool capacity is derived from independently ready runtimes, not from Pod phase alone.

---

## Exact Route Authority

ProcessHostPool uses exact route incarnations to reach a selected child runtime.

```text
PoolId
HostId
RuntimeInstanceId
TransportName
    -> RouteId
    -> TransportEndpoint
    -> RouteStatus
```

The route registry protects against stale mutation and supports exact draining and forwarding leases.

For KubernetesPool, runtime transport still remains HTTP or gRPC. Kubernetes provides lifecycle and network/service boundaries; it does not replace provider identity.

---

## Stable HTTP and gRPC Behavior

HTTP and gRPC use the same runtime correctness contract.

### HTTP

The ProcessHostPool stable endpoint forwards an existing runtime command envelope to the exact child runtime.

### gRPC

The ProcessHostPool stable endpoint reuses the existing generated runtime command service and validates exact response identity.

### KubernetesPool

KubernetesPool preserves the configured HTTP or gRPC provider semantics through the Pod/Gateway transport path. The Pod itself never becomes the command provider.

---

## Forwarding Leases and Draining

A forwarding lease closes the race between route resolution and shutdown.

```text
resolve runtime
    ↓
acquire exact forwarding lease
    ↓
recheck safety
    ↓
invoke exact child transport
    ↓
release lease
```

When a runtime drains:

- new forwarding leases are rejected;
- active forwarding operations finish;
- the route drains deterministically;
- the runtime can then stop;
- sibling routes remain independent.

---

## Child Runtime Failure

A child runtime failure is scoped to one runtime identity.

```text
child exits unexpectedly
    ↓
record exact durable FailureId
    ↓
mark exact runtime unsafe
    ↓
remove / suppress exact route or capacity
    ↓
enumerate exact assigned work
    ↓
claim recovery authority
    ↓
resume or redispatch exact work
    ↓
replace child membership
```

The parent ProcessHost or Pod remains alive and healthy siblings retain their identities.

---

## Full Boundary Failure

A parent ProcessHost or Kubernetes Pod failure is a different scope.

```text
HostId / PodUid disappears
    ↓
identify exact failed membership
    ↓
suppress all runtimes from that boundary
    ↓
leave sibling boundaries selectable
    ↓
replace failed host boundary
    ↓
recover only work owned by failed runtimes
```

The boundary identity never replaces the child execution identities in the run index or durable DAG state.

---

## Warm Reuse and Bounded Capacity

Runtime Pools reuse converged capacity before creating replacement boundaries.

A validated 3 × 5 topology means:

```text
3 ProcessHosts or Pods
× 5 runtimes per boundary
= 15 active runtime slots
```

Production validation submits full-capacity waves, injects failures while work is live, waits for exact recovery and convergence, then reuses the same warm pool for another cycle without intermediate cleanup.

The transport router remains exact. Capacity selection and scale-out stay in the control plane.

---

## Hierarchical Capacity Selection

The runtime can reason about capacity hierarchically:

```text
ready runtime slot
    inside
existing ProcessHost / Pod
    inside
bounded logical Pool
    inside
cluster / machine capacity
```

This enables a production preference order such as:

```text
reuse ready warm runtime capacity
    ↓
reuse available capacity in an existing host boundary
    ↓
create another bounded host boundary when allowed
    ↓
let the infrastructure layer add node capacity when required
```

The local-machine production proofs validate the first three levels within configured bounds. Cluster node autoscaling remains an infrastructure concern outside the transport router.

---

## Durable Failure and Lifecycle Evidence

Runtime Pool correctness now distinguishes failure authority from infrastructure history.

### Runtime Pool Failure Journal

Durable MongoDB failure facts contain exact identities such as:

```text
FailureId
Scope
PoolId
HostId
RuntimeInstanceId
RouteId
ObservedAtUtc
```

The same durable authority can be observed by an external parent ProcessHost and by the control plane.

### Runtime Lifecycle Journal

The append-only lifecycle journal records creation, registration, readiness, failure, suppression, deletion, replacement, and run placement history.

The same incident identity binds the failure fact and historical audit trail.

See:

- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)

---

## Dependency Injection and Compatibility

Runtime Pool composition is opt-in and does not reinterpret historical modes.

```text
Fixture
Process
Kubernetes
Attach
ProcessHostPool / ProcessPool composition
KubernetesPool
```

The exact configuration surface depends on the host composition, but the architectural invariant is stable: Runtime Pool behavior must be explicitly selected.

---

## End-to-End Validation Matrix

The same hierarchical failure contract is validated across:

```text
gRPC + ProcessHostPool   PASS
HTTP + ProcessHostPool   PASS
gRPC + KubernetesPool    PASS
HTTP + KubernetesPool    PASS
```

Each final scenario uses:

```text
3 failure boundaries
5 runtimes per boundary
15 active runtimes
5 full-capacity waves per cycle
75 DAGs per cycle
2 warm cycles
150 DAGs total
50 logical steps per DAG
7500 logical steps total
2 child runtime crashes
2 full-boundary crashes
12 recovered runs
```

Across all four final scenarios:

```text
600 submitted DAGs
600 completed DAGs
30000 logical steps
8 child runtime crashes
8 full-boundary crashes
48 recovered runs
600 replay proofs
0 lost runs
0 failed runs
0 duplicate dispatch
0 configured-capacity violations
```

See [Runtime Pool Production Validation](runtime-pool-production-validation.md) for the proof contract and evidence boundaries.

---

## Current Boundaries

The implemented Runtime Pool foundation is intentionally explicit about what remains outside the current proof:

- durable failure facts are shared through MongoDB;
- runtime lifecycle history is durable in MongoDB;
- current registry/capacity remains a current-state concern;
- recovery claim coordination is exact for the validated scenarios, but a fully distributed multi-control-plane durable claim/completion protocol remains future hardening;
- Redis Cluster key-slot and failover validation remains future work;
- cluster node autoscaling/HPA is infrastructure integration, not a completed runtime correctness proof;
- production dashboard and managed-hosting control surfaces remain productization work.

---

## Related Documents

- [Runtime Pool Identity Model](runtime-pool-identity-model.md)
- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)
- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
- [Architecture Overview](architecture-overview.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Kubernetes Runtime Host Provider](kubernetes-runtime-host-provider.md)
- [Testing Strategy](testing-strategy.md)
- [Runtime Pool Delivery Status](../product-roadmap/runtime-pool-roadmap.md)
