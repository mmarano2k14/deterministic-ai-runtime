# Runtime Pool Architecture

**Status:** Process-host Runtime Pool implemented and validated for exact HTTP and gRPC routing, real child-process lifecycle, targeted replacement, failure isolation, and opt-in composition. Kubernetes Runtime Pool Pods remain roadmap work; the existing one-runtime-per-Pod Kubernetes modes are unchanged.

The Runtime Pool architecture introduces a hosting layer in which one stable host endpoint can manage several independently registered runtime instances without collapsing their identities into the host process.

The design is intentionally additive. Existing local, Process, Attach, and Kubernetes hosting modes remain valid and do not implicitly opt into Runtime Pool behavior.

---

## Executive Summary

A Runtime Pool separates four concepts that are often incorrectly treated as one:

| Concept | Responsibility |
|---|---|
| `PoolId` | Logical reusable capacity group. |
| `HostId` | Immutable incarnation of the process host or, in the future, Kubernetes Pod. |
| `RuntimeInstanceId` | Independently registered, selectable execution capacity. |
| `RouteId` | Immutable transport-route incarnation for one runtime instance. |

The control plane selects an exact `RuntimeInstanceId`. A stable pool endpoint receives the command and forwards it to the exact child route.

```text
Control Plane
    selects RuntimeInstanceId = A2
            ↓
stable Runtime Pool endpoint
            ↓
exact route registry lookup
            ↓
A2 child endpoint
```

The router does not choose another runtime, retry against a sibling, or perform recovery. Capacity selection remains a control-plane responsibility. Recovery remains a lifecycle and execution-recovery responsibility.

---

## Why a Runtime Pool Exists

A production deployment should not require one tenant, one run, or one logical runtime instance to map permanently to one operating-system process or one Kubernetes Pod.

A reusable Runtime Pool provides:

- warm capacity;
- bounded process creation;
- independently addressable runtime instances;
- targeted replacement after child failure;
- stable transport endpoints;
- exact routing;
- graceful draining;
- future hierarchical selection across runtime, Pod, and node boundaries.

The current process-host implementation proves this model with real `RuntimeInstanceOnly` child processes.

---

## Architectural Invariants

The architecture is built around the following non-negotiable invariants.

### Identity is first-class

Correctness must use typed fields, not metadata parsing.

```text
PoolId
HostId
RuntimeInstanceId
RouteId
```

Diagnostic metadata may duplicate those values for logs and telemetry, but it is never the authority for routing, lifecycle, or recovery.

### Host identity is not runtime identity

```text
HostId != RuntimeInstanceId
```

A host can contain several independently registered runtime instances. The host endpoint is transport infrastructure. The runtime instance is selectable execution capacity.

### Routing is exact

```text
requested A2
    -> A2
    -> or explicit failure
```

There is no sibling fallback inside the transport router.

### Lifecycle and routing are separate

The pool manager owns child creation, readiness, draining, termination, and replacement.

The route registry owns exact transport reachability.

The transport router owns exact forwarding after route acquisition.

### Recovery is not routing

A transport failure may reveal that a runtime is unsafe, but the router does not recover work or choose replacement capacity.

---

## Process-Host Topology

The implemented process-host topology is:

```text
Process Pool Host
    PoolId = pool-01
    HostId = host-incarnation-01

    stable HTTP endpoint
    stable gRPC endpoint
    route registry
    failure journal
    capacity safety registry
    recovery claim store

    child A1
        RuntimeInstanceId = runtime-1
        RouteId = route-1
        transport endpoint = child endpoint 1

    child A2
        RuntimeInstanceId = runtime-2
        RouteId = route-2
        transport endpoint = child endpoint 2

    child A3
        RuntimeInstanceId = runtime-3
        RouteId = route-3
        transport endpoint = child endpoint 3
```

Each child is a real external `RuntimeInstanceOnly` process.

---

## Identity Model

### `PoolId`

`PoolId` identifies the logical pool across the lifetime of one pool composition.

It groups children that belong to the same reusable capacity boundary.

### `HostId`

`HostId` identifies one immutable host incarnation.

For the current process-host implementation, it is created when the pool manager starts.

For the future Kubernetes Runtime Pool implementation, it will map to the Kubernetes Pod UID at the provider boundary.

A restarted process or replaced Pod must receive a new `HostId`.

### `RuntimeInstanceId`

`RuntimeInstanceId` identifies independently selectable execution capacity.

It remains distinct from:

- the pool;
- the process-host identity;
- the stable transport endpoint;
- the provider name;
- the tenant identity.

### `RouteId`

`RouteId` identifies one immutable route incarnation.

If A1 fails and replacement A4 is created:

```text
A1 RuntimeInstanceId != A4 RuntimeInstanceId
A1 RouteId           != A4 RouteId
```

A stale route mutation cannot affect a newer route incarnation.

---

## Runtime Pool Manager

The process-host Runtime Pool Manager is responsible for:

- creating real child processes;
- assigning independent runtime identities;
- allocating child transport endpoints;
- waiting for runtime readiness;
- publishing membership;
- maintaining minimum capacity;
- draining requested children;
- observing unexpected exits;
- replacing only the failed child;
- stopping all children during host shutdown.

The manager does not route commands. It exposes lifecycle state to the routing and failure layers.

---

## Readiness Boundary

A child is not exposed as ready capacity until all required readiness conditions are satisfied.

```text
process started
    ↓
runtime registration observed
    ↓
capacity publication observed
    ↓
transport route registered
    ↓
child exposed as ready
```

For gRPC children, the typed process start plan projects HTTP/2 Kestrel settings to the exact allocated endpoint.

HTTP children remain on their existing HTTP configuration.

---

## Exact Route Registry

The route registry maps:

```text
PoolId
HostId
RuntimeInstanceId
TransportName
    -> RouteId
    -> TransportEndpoint
    -> RouteStatus
```

Supported resolution outcomes include:

- `Resolved`;
- `NotFound`;
- `PoolMismatch`;
- `HostMismatch`;
- `TransportMismatch`;
- `Draining`;
- `Suppressed`.

The registry provides:

- idempotent registration of identical authority;
- conflict detection for incompatible rebinding;
- immutable `RouteId` protection;
- exact route removal;
- exact route draining;
- host-local route listing;
- forwarding leases.

---

## Forwarding Leases and Draining

A forwarding lease closes the race between route resolution and graceful shutdown.

```text
resolve A2
    ↓
acquire A2 forwarding lease
    ↓
invoke A2 transport
    ↓
release lease
```

When A2 begins draining:

- new forwarding leases are rejected;
- active forwarding operations are allowed to finish;
- the pool manager waits for route drain completion;
- only A2 is stopped;
- sibling routes remain independent.

The router also rechecks capacity suppression after lease acquisition. This closes the race in which a runtime becomes unsafe after lookup but before transport invocation.

---

## Stable HTTP Endpoint

The stable HTTP endpoint is:

```text
POST /runtime-pool/commands
```

It reuses the existing:

- `AiRuntimeInstanceCommandRequest`;
- `AiRuntimeInstanceCommandResult`;
- runtime command operations;
- queue-control contracts.

The endpoint resolves the local `PoolId` and `HostId` from the pool manager and accepts the target `RuntimeInstanceId` from the existing command request.

It forwards to the exact child endpoint:

```text
POST /runtime-instance/commands
```

A response claiming another `RuntimeInstanceId` is rejected.

---

## Stable gRPC Endpoint

The stable gRPC endpoint reuses the existing generated service:

```text
AiRuntimeInstanceCommandGrpc.ExecuteCommand
```

It also reuses the existing JSON envelopes:

- `AiRuntimeInstanceGrpcCommandRequest`;
- `AiRuntimeInstanceGrpcCommandResponse`.

No second `.proto` contract is introduced.

The gRPC router creates a client for the exact child endpoint, forwards the existing command envelope, validates the response identity, and disposes the child channel deterministically.

---

## No Silent Fallback

The stable endpoint is not a scheduler.

```text
A2 requested
    -> A2 route resolved
    -> A2 transport invoked
```

The following behavior is explicitly forbidden:

```text
A2 unavailable
    -> silently send to A1, A3, or A4
```

Instead, the caller receives an explicit structured failure such as:

```text
runtime-pool-route-not-found
runtime-pool-route-draining
runtime-pool-capacity-suppressed
runtime-pool-http-forwarding-failed
runtime-pool-grpc-forwarding-failed
```

This preserves a clean boundary between selection, routing, and recovery.

---

## Targeted Child Replacement

When A1 exits unexpectedly:

```text
A1 exits
    ↓
record exact A1 failure
    ↓
suppress exact A1 capacity
    ↓
remove exact A1 route
    ↓
publish completion to pool manager
    ↓
start replacement A4
```

A2 and A3 keep:

- the same `RuntimeInstanceId`;
- the same `RouteId`;
- the same `PoolId`;
- the same `HostId`;
- independent routability.

A4 receives:

- a new `RuntimeInstanceId`;
- a new `RouteId`;
- the same logical `PoolId`;
- the same current `HostId`.

---

## Dependency Injection and Compatibility

The Runtime Pool is opt-in through:

```text
AddAiRuntimeProcessPool(...)
```

The composition registers the pool manager, route registry, HTTP and gRPC routing, failure journal, capacity safety, assigned-work enumeration, recovery claims, and claimed recovery executor.

Existing modes remain unchanged:

```text
Fixture
Process
Kubernetes
Attach
```

The existing Kubernetes mode continues to mean one `RuntimeInstanceOnly` runtime per Pod/Service.

The future Kubernetes Runtime Pool will be a new mode rather than a semantic rewrite of the existing mode.

---

## Current Validation Evidence

The Runtime Pool foundation has been validated through:

- real external `RuntimeInstanceOnly` child processes;
- three-child initial capacity;
- real operating-system kill of A1;
- targeted A4 replacement;
- A2/A3 identity and route preservation;
- exact HTTP stable-endpoint routing;
- exact gRPC stable-endpoint routing;
- route-drain concurrency tests;
- stale route mutation protection;
- response identity validation;
- exact failure journaling;
- exact capacity suppression;
- exact assigned-work enumeration;
- deterministic claim arbitration;
- claimed recovery transition execution.

Regression gates passed after the Runtime Pool work:

```text
historical Process HTTP: P10
historical Process gRPC: P10
existing Kubernetes HTTP mode: P5
existing Kubernetes gRPC mode: P5
```

These regressions demonstrate that the opt-in Runtime Pool did not replace or break the historical hosting modes.

---

## Current Boundaries

The current implementation is intentionally scoped.

- Process-host Runtime Pool composition is implemented.
- Failure journal, capacity safety registry, and recovery claim store are local in-memory components.
- Stable HTTP and gRPC routes are implemented.
- Real process failure and replacement are implemented.
- Exact recovery orchestration is implemented over existing recovery interfaces.
- Kubernetes Runtime Pool Pods are not implemented.
- Host-wide suppression is reserved for the future Pod UID boundary.
- Distributed claim durability across multiple control planes remains roadmap work.
- Redis Cluster key-slot strategy remains roadmap work.

---

## Kubernetes Runtime Pool Direction

The planned Kubernetes topology is:

```text
Kubernetes cluster
    node
        Pod / HostId = PodUid-01
            RuntimeInstanceId A1
            RuntimeInstanceId A2
            RuntimeInstanceId A3

        Pod / HostId = PodUid-02
            RuntimeInstanceId B1
            RuntimeInstanceId B2
```

A child runtime failure suppresses one `RuntimeInstanceId`.

A Pod failure suppresses every `RuntimeInstanceId` whose `HostId` matches the failed Pod UID.

The current one-runtime-per-Pod mode remains available for workloads that prefer that isolation boundary.

---

## Hierarchical Capacity Direction

Future capacity selection is expected to follow this hierarchy:

```text
1. ready warm runtime in an existing pool host
2. available capacity in an existing Pod
3. create a new Runtime Pool Pod
4. allow cluster node autoscaling when necessary
```

Selection remains outside the transport router.

---

## Redis Cluster Direction

Redis Cluster compatibility requires explicit key-slot design.

Future work must define:

- which atomic state groups share a hash slot;
- which Lua operations remain single-slot;
- tenant or cell partitioning;
- failover behavior;
- recovery claim durability;
- route and host state durability;
- multi-control-plane ownership.

This work follows Kubernetes Runtime Pool lifecycle validation.

---

## Related Documents

- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)
- [Architecture Overview](architecture-overview.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Provider-Agnostic Process-Host Recovery](provider-agnostic-process-host-recovery.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Testing Strategy](testing-strategy.md)
- [Runtime Pool Product Roadmap](../product-roadmap/runtime-pool-roadmap.md)
