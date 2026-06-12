# Runtime Discovery, Registry, and Capacity

Status: Implemented foundation / validated for MCP, Redis, local runtime pools, and HTTP pooled runtime scenarios.

This document describes the runtime discovery, registry, and capacity model used by the Deterministic AI Runtime control plane.

It explains how MCP control-plane hosts, runtime-only hosts, runtime instance registration, runtime capacity publication, shared queue pump readiness, provider dispatch, and shutdown cleanup work together.

This document complements:

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [Testing Strategy](testing-strategy.md)

---

## Purpose

Runtime discovery, registry, and capacity are the visibility foundation for distributed runtime hosting.

They answer operational questions such as:

- Which logical control plane is active?
- Which MCP/control-plane host owns the discovery descriptor?
- Which runtime-only hosts belong to the same logical control plane?
- Which runtime instances are registered?
- Which runtime instances are ready?
- Which runtime instances can accept runs?
- Which runtime instances are paused, draining, unhealthy, or stopped?
- How many workers are available?
- How many run slots are available?
- Which provider should be used to contact a runtime instance?
- Can the shared queue pump safely start dispatching?
- Can shutdown cleanup happen without rediscovering a control-plane id?

This layer is required for:

- MCP control-plane operation
- runtime-only host registration
- local runtime instance pools
- HTTP pooled runtime providers
- shared queue pump readiness
- dispatch-time admission
- provider routing
- future Kubernetes runtime pods
- future autoscaling and dashboards.

---

## High-Level Model

The current model is:

```text
MCP Control Plane
    ↓
Redis Control-Plane Discovery Store
    ↓
ControlPlaneIdResolver
    ↓
RuntimeInstanceOnly Host
    ↓
Runtime Instance Registry
    ↓
Runtime Capacity Store
    ↓
Shared Queue Pump Readiness Gate
    ↓
Admission / Provider Dispatch
    ↓
Runtime Instance Local Queue
```

The MCP control plane publishes the logical control-plane identity.

Runtime-only hosts resolve that identity before registering runtime instances or publishing capacity.

The registry tracks runtime lifecycle and identity.

The capacity store tracks scheduling visibility.

Admission and the shared queue pump use registry and capacity data before dispatching work.

---

## Identity Model

The discovery and registry layer separates several identities.

```text
ControlPlaneId
    logical shared control-plane scope used by Redis stores

ControlPlaneHostId
    physical/logical host publishing or owning the control-plane descriptor

RuntimeInstanceId
    dispatchable runtime instance identity

RuntimeId
    local runtime identity inside a host or runtime pool

WorkerId
    worker identity inside a runtime instance
```

These identities must not be collapsed.

A common mistake is to treat the parent HTTP host as the dispatch target.

In the HTTP pooled runtime model, that is incorrect.

```text
HTTP host identity != dispatch target
runtime-http-* child instance == dispatch target
```

Example:

```text
MCP Control Plane
    ↓
HTTP Runtime Provider
    ↓
RuntimeInstanceOnly HTTP Host
    ↓
Local Runtime Instance Pool
    ↓
runtime-http-1
runtime-http-2
runtime-http-3
```

The dispatchable identities are the child runtime instances.

---

## Control-Plane Discovery Store

The control-plane discovery store publishes and reads the active control-plane descriptor.

In Redis-backed scenarios, the discovery descriptor allows runtime-only hosts to join the correct logical control-plane scope.

The discovery descriptor should include enough information to identify:

- logical control-plane id
- control-plane host id
- discovery owner
- discovery timestamp
- optional metadata
- optional TTL / expiration.

Conceptual descriptor:

```text
ControlPlaneId = cp-heavy-dispatch-tests
ControlPlaneHostId = host-abc123
DiscoveryKey = multiplexed-ai:cp-heavy-dispatch-tests
PublishedAtUtc = ...
Owner = MCP control-plane host
```

The MCP server can publish this descriptor at startup.

Runtime-only hosts can require discovery and block startup until the descriptor is available.

---

## ControlPlaneIdResolver

The control-plane id resolver is responsible for resolving the logical control-plane id used by Redis-backed stores.

Runtime-only hosts should not guess or generate a different control-plane id when discovery is required.

They should resolve the MCP-published identity.

```text
RuntimeInstanceOnly Host
    ↓
ControlPlaneIdResolver
    ↓
Redis Control-Plane Discovery Store
    ↓
Resolved ControlPlaneId
```

The resolved control-plane id is then used for:

- runtime instance registration
- runtime heartbeat
- runtime capacity publication
- provider metadata
- shared queue pump readiness
- capacity lookup
- registry lookup.

This prevents split-brain test and production behavior where MCP uses one Redis scope and runtime hosts register under another.

---

## Runtime Instance Registry

The runtime instance registry tracks visible runtime instances.

It supports:

- register runtime instance
- heartbeat runtime instance
- get runtime instance
- list runtime instances
- mark runtime instance as draining
- unregister runtime instance.

Runtime registry entries include:

- runtime instance id
- runtime role
- status
- hostname
- process id
- worker count
- queue capacity
- run slot information
- provider metadata
- registered timestamp
- last heartbeat timestamp
- metadata.

Roles are important.

```text
ControlPlane
    visible but not dispatchable

Runtime
    visible and dispatchable if ready/capacity allows it
```

The control-plane host should not be selected as a runtime execution target.

---

## Runtime Capacity Store

The runtime capacity store publishes live capacity descriptors.

Capacity descriptors are the main scheduling visibility model.

They include:

```text
RuntimeInstanceId
Role
Status
ProviderName
ProviderEndpoint
WorkerCount
ActiveWorkerCount
AvailableWorkerCount
MaxLocalWorkersPerExecution
QueuedRunCount
RunningRunCount
ActiveRunCount
QueueCapacity
MaxConcurrentRuns
AvailableRunSlots
IsQueuePaused
CanAcceptRun
SnapshotAtUtc
LastHeartbeatAtUtc
```

Capacity descriptors are used by:

- admission
- shared queue pump readiness
- MCP runtime instance tools
- runtime provider dispatch
- dashboard/API visibility
- future autoscaling decisions.

A runtime instance should only be eligible for dispatch when capacity indicates that it can accept work.

Conceptually:

```text
CanAcceptRun = queue not paused
            + queue capacity available
            + run slot available
            + worker available
```

---

## Provider Metadata

Runtime capacity descriptors also carry provider metadata.

Provider metadata tells the provider router how to contact the runtime instance.

Examples:

```text
provider.name = local
provider.transport = in-memory
```

```text
provider.name = http
provider.transport = http
provider.endpoint = http://localhost:5001/runtime-instance/commands
```

Future examples:

```text
provider.name = redis-command-queue
provider.commandQueueKey = ai:runtime:runtime-1:commands
```

```text
provider.name = grpc
provider.endpoint = grpc://runtime-1.ai-runtime.svc.cluster.local:5001
```

Provider metadata must identify the transport.

It must not replace runtime capacity.

The provider tells the control plane how to contact the runtime instance.

The capacity descriptor tells the control plane whether the runtime instance should receive work.

---

## Registration Flow

A runtime-only host that requires discovery should follow this startup flow:

```text
RuntimeInstanceOnly Host starts
    ↓
Resolve ControlPlaneId from discovery
    ↓
Create local runtime instance pool
    ↓
Register child runtime instances
    ↓
Publish capacity descriptors
    ↓
Heartbeat registry and capacity
    ↓
Runtime instances become visible to admission and MCP
```

For HTTP pooled runtime hosting:

```text
RuntimeInstanceOnly HTTP Host
    ↓
Local Runtime Instance Pool
    ↓
runtime-http-1 registers
runtime-http-2 registers
runtime-http-3 registers
    ↓
capacity descriptors publish provider.name = http
    ↓
MCP / shared queue pump can dispatch
```

The parent HTTP host is not the dispatch target.

The child runtime instances are the dispatch targets.

---

## Heartbeat Flow

Runtime instances should continue to update registry and capacity visibility.

```text
Runtime instance heartbeat
    ↓
Update registry last heartbeat
    ↓
Read local queue state
    ↓
Read worker capacity
    ↓
Publish capacity descriptor
```

Heartbeat should reflect:

- queue paused state
- queued run count
- running run count
- active run count
- available run slots
- active worker count
- available worker count
- can accept run.

This keeps admission and MCP visibility current.

---

## Shared Queue Pump Readiness

The background shared queue pump should not dispatch before runtime capacity is visible.

Readiness gate:

```text
Background pump starts
    ↓
Resolve control-plane identity
    ↓
List runtime instances / capacity descriptors
    ↓
Find at least one ready dispatchable runtime instance
    ↓
Start pump loop
```

This prevents queued work from being drained before runtime-only hosts have registered.

Readiness should use runtime capacity rather than only process startup.

A process can be started but still not dispatchable.

---

## Admission and Reservation

Admission uses runtime capacity descriptors to select a target runtime instance.

In Redis-backed heavy dispatch scenarios, the runtime uses an admission reservation store to protect selected capacity during dispatch.

Conceptual flow:

```text
List capacity descriptors
    ↓
Select eligible runtime instance
    ↓
Try reserve selected capacity
    ↓
Dispatch through provider
    ↓
If dispatch succeeds:
        local queue / heartbeat reflects real usage
    ↓
If dispatch fails:
        release or expire reservation
```

The Redis admission reservation store is validated in heavy HTTP dispatch scenarios.

Lua-based slot reservation can still be added later for stronger atomic coordination in production multi-control-plane deployments.

---

## Shutdown and Cleanup

Shutdown must be safe, idempotent, and best-effort.

Important cleanup operations:

- mark runtime instance draining when appropriate
- unregister runtime instance
- remove capacity descriptor
- stop local runtime hosts
- delete discovery descriptor only when owned by the current control-plane host.

The key rule:

```text
Cleanup must not require rediscovery after the runtime instance has already registered or published capacity.
```

During shutdown, Redis discovery, logging providers, or service providers may already be stopping.

Registry unregister and capacity descriptor removal should reuse the known resolved control-plane id for the runtime instance.

This avoids shutdown timeouts and disposed-object failures.

---

## Self-Healing and TTL Direction

The registry and capacity store should continue moving toward self-healing behavior.

Recommended production hardening:

```text
1. Registry entries should have TTL or heartbeat-based expiration.
2. Capacity descriptors should have TTL or heartbeat-based expiration.
3. ListAsync should ignore or clean stale entries.
4. MarkDraining should stop new dispatch.
5. Unregister should remove registry and capacity entries when possible.
6. StopAsync cleanup should be best-effort.
7. Test cleanup should remain safety-only, not the primary lifecycle mechanism.
```

Heartbeat should publish capacity before a runtime instance becomes dispatchable.

The pump should start only after readiness is observed.

---

## HTTP Pooled Runtime Model

The validated HTTP runtime model is:

```text
MCP Control Plane
    ↓
HTTP Runtime Provider
    ↓
RuntimeInstanceOnly HTTP Host
    ↓
Local Runtime Instance Pool
    ↓
runtime-http-1
runtime-http-2
runtime-http-3
```

The HTTP provider contacts the runtime HTTP command endpoint.

The runtime host routes the command to the selected child runtime instance.

The selected child runtime instance owns:

- local queue
- worker pool
- run slots
- runtime capacity descriptor
- heartbeat
- background controller.

Assertions should validate assignment to the child runtime identity.

They should not assume that all runs are assigned to a fixed parent HTTP host identity.

---

## MCP Visibility

MCP runtime instance tools should expose registry and capacity visibility.

Useful MCP output includes:

```text
RuntimeInstanceId
Role
Status
WorkerCount
ActiveWorkerCount
AvailableWorkerCount
MaxLocalWorkersPerExecution
QueuedRunCount
RunningRunCount
ActiveRunCount
QueueCapacity
MaxConcurrentRuns
AvailableRunSlots
IsQueuePaused
CanAcceptRun
LastHeartbeatAtUtc
ProviderName
ProviderEndpoint
```

This gives MCP enough operational visibility to act as a temporary dashboard before a full UI exists.

---

## Validated Evidence

The current implementation has been validated through MCP, Redis, local runtime pool, and HTTP pooled runtime provider scenarios.

Heavy HTTP dispatch evidence:

```text
Runs = 50
StepsPerRun = 100
RuntimeInstances = runtime-http-1, runtime-http-2, runtime-http-3
RedisAiSharedRunStore = validated
RedisAiSharedQueue = validated
RedisAiRuntimeAdmissionReservationStore = validated
```

Runtime visibility evidence:

```text
Redis runtime registry = validated
Redis runtime capacity store = validated
Redis control-plane discovery store = validated
ControlPlaneIdResolver = validated
Runtime-only host identity resolution = validated
```

Replay/control-plane evidence:

```text
Replay = Success
Replay report = Success
Ledger = Success
Trace = Available
ReplayValid = True
FingerprintMatches = True
IssueCount = 0
```

Shutdown evidence:

```text
Runtime unregister cleanup = validated
Capacity descriptor cleanup = validated
Discovery shutdown cleanup = validated
Cleanup without late rediscovery dependency = validated
Repeated StopAsync / DisposeAsync safety = validated
```

---

## Current Status

| Area | Status |
|---|---|
| Redis control-plane discovery store | Implemented / validated |
| Control-plane id resolver | Implemented / validated |
| Runtime instance registry | Implemented / validated |
| Redis runtime instance registry | Implemented / validated |
| Runtime capacity store | Implemented / validated |
| Redis runtime capacity store | Implemented / validated |
| Runtime capacity publication | Implemented / validated |
| Runtime capacity cleanup | Implemented / validated |
| Runtime heartbeat | Implemented / validated |
| Runtime role separation | Implemented / validated |
| MCP runtime visibility | Implemented / validated |
| Shared queue pump readiness gate | Implemented / validated |
| Redis admission reservation store | Implemented / validated |
| HTTP pooled runtime identity | Implemented / validated |
| Shutdown cleanup without late rediscovery | Implemented / validated |
| Registry/capacity TTL hardening | Planned |
| Registry self-healing ListAsync cleanup | Planned |
| Kubernetes pod metadata integration | Planned |
| Kubernetes autoscaling integration | Planned |

---

## Current Limitations

The current implementation does not yet provide:

- Kubernetes pod metadata provider
- Kubernetes autoscaling adapter
- Redis command queue provider
- gRPC runtime provider
- production multi-control-plane leader election
- fully hardened registry/capacity TTL self-healing
- production dashboard UI
- full provider capability negotiation.

These are intentionally separate from the current validated discovery, registry, and capacity foundation.

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| MCP Control Plane | Publishes discovery, exposes tools, submits shared runs, drains queue, observes runtime state. |
| Control-Plane Discovery Store | Stores the active logical MCP/control-plane descriptor. |
| ControlPlaneIdResolver | Resolves the logical control-plane id for runtime-only hosts and Redis stores. |
| RuntimeInstanceOnly Host | Resolves discovery, registers runtime instances, publishes capacity, hosts local queues/workers. |
| Runtime Instance Registry | Tracks runtime identities, roles, status, heartbeat, lifecycle, and metadata. |
| Runtime Capacity Store | Tracks live run/worker capacity descriptors used by admission and readiness. |
| Admission Controller | Selects runtime targets based on visible capacity and policy. |
| Admission Reservation Store | Protects selected runtime capacity during dispatch in Redis-backed scenarios. |
| Shared Queue Pump | Waits for readiness and dispatches queued shared runs. |
| Runtime Provider | Contacts the selected runtime instance through local, HTTP, or future transport. |
| Local Runtime Queue | Owns `RunId` lifecycle and execution start. |
| DAG Engine | Owns durable `ExecutionId` execution and deterministic step transitions. |

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document describes the runtime discovery, registry, and capacity foundation.

Do not present Kubernetes autoscaling, gRPC dispatch, Redis command queue dispatch, or production dashboard features as completed capabilities until they are implemented and validated.
