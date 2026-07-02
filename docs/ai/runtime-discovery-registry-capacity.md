# Runtime Discovery, Registry, and Capacity

Status: Implemented foundation / validated for MCP, Redis, local runtime pools, Redis-backed scale-out request lifecycle, local runtime scale-out, fulfilled-run requeue, HTTP pooled runtime scenarios, process-host HTTP runtime crash recovery, unsafe runtime capacity suppression, fulfilled replacement capacity visibility, and multi-tenant runtime isolation across shared, dedicated, and hybrid tenant modes.

This document describes the runtime discovery, registry, and capacity model used by the Deterministic AI Runtime control plane.

It explains how MCP control-plane hosts, runtime-only hosts, runtime instance registration, runtime capacity publication, tenant-aware runtime visibility, shared queue pump readiness, provider dispatch, provider-based scale-out, fulfilled-run requeue, unsafe runtime detection, crash recovery visibility, replacement runtime capacity, and shutdown cleanup work together.

This document complements:

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
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
- Which runtime instances are visible to the current tenant?
- Which runtime instances are shared, dedicated, or hybrid?
- Which runtime instances are paused, draining, unhealthy, or stopped?
- How many workers are available?
- How many run slots are available?
- Which provider should be used to contact a runtime instance?
- Can the shared queue pump safely start dispatching?
- Can scale-out create new tenant-scoped runtime capacity when no runtime instance is available?
- Can a fulfilled scale-out request make the original shared run dispatchable again?
- Which runtime instances became unsafe and must no longer receive new work?
- Can assigned work from an unsafe runtime be recovered without using local queue state as durable truth?
- Can replacement capacity become visible only to the impacted tenant?
- Can safe tenants remain visible, dispatchable, and unaffected while other tenants recover?
- Can shutdown cleanup happen without rediscovering a control-plane id?

This layer is required for:

- MCP control-plane operation
- runtime-only host registration
- local runtime instance pools
- HTTP pooled runtime providers
- shared queue pump readiness
- tenant-aware dispatch-time admission
- tenant-visible registry and capacity filtering
- provider routing
- provider-based scale-out
- tenant-scoped local runtime scale-out
- fulfilled scale-out run requeue
- runtime instance health reconciliation
- runtime crash recovery reconciliation
- tenant-scoped replacement capacity selection
- safe-tenant non-impact validation
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
Tenant Visibility Evaluator
    ↓
Shared Queue Pump Readiness Gate
    ↓
Admission / Provider Dispatch / Provider Scale-Out
    ↓
Runtime Instance Local Queue
```

The MCP control plane publishes the logical control-plane identity.

Runtime-only hosts resolve that identity before registering runtime instances or publishing capacity.

The registry tracks runtime lifecycle and identity.

The capacity store tracks scheduling visibility.

The tenant visibility evaluator filters registry entries and capacity descriptors according to the current `ExecutionContextSnapshot` tenant boundary.

Admission and the shared queue pump use registry and capacity data before dispatching work.

When admission cannot find tenant-visible capacity and scale-out is enabled, the same visibility model supports the scale-out lifecycle:

```text
Admission = RequestScaleOut
    ↓
Tenant runtime settings copied to scale-out request
    ↓
Redis scale-out request persisted
    ↓
Scale-out watcher
    ↓
Provider-based scale-out selector
    ↓
Local runtime scaler or future Kubernetes scaler
    ↓
Tenant-scoped runtime instance registration
    ↓
Tenant-scoped runtime capacity publication
    ↓
Fulfilled shared run requeue
    ↓
Shared queue pump restores ExecutionContextSnapshot
    ↓
Tenant-aware dispatch-time admission
    ↓
Runtime instance dispatch
```

The same registry and capacity visibility model is also part of runtime crash recovery:

```text
Runtime heartbeat becomes stale or unsafe
    ↓
Health reconciliation suppresses unsafe capacity
    ↓
Admission stops selecting the unsafe runtime
    ↓
Execution recovery reconciliation enumerates assigned work
    ↓
In-flight executions resume with the same ExecutionId
    ↓
Local queued shared runs are redispatched through durable SharedRunId
    ↓
Replacement capacity is created or selected inside the same tenant scope
    ↓
Registry and capacity expose only tenant-visible replacement runtime capacity
    ↓
Ledger, trace, replay, and forensics prove recovery after convergence
```

Important boundary:

```text
Runtime health reconciliation decides whether capacity is safe for routing.
Execution recovery reconciliation recovers work already assigned to unsafe capacity.
Provider transport failures are signals, not lifecycle ownership.
```

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

TenantId
    durable tenant boundary from ExecutionContextSnapshot

TenantGroupId
    optional tenant group boundary from ExecutionContextSnapshot
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

The same rule applies to dynamically created local runtime instances.

For shared/default local scale-out, the dispatchable runtime instance may look like:

```text
host-abc123:runtime-instance-1
```

For dedicated tenant scale-out, the dispatchable runtime instance may look like:

```text
host-abc123:tenant-a-runtime-1
```

For hybrid tenant scale-out, the dispatchable runtime instance may look like:

```text
host-abc123:tenant-b-runtime-1
```

That dynamically created runtime instance must register and publish capacity before admission can dispatch the requeued run to it.

---

## Durable Tenant Boundary

The durable tenant boundary is `ExecutionContextSnapshot.TenantId`.

`ContextKey` is useful for RBAC lookup, correlation, and debugging, but it is not the durable runtime partition.

Runtime metadata may duplicate tenant fields for observability, but it must not become the source of truth when a strong tenant field exists.

The rule is:

```text
ExecutionContextSnapshot.TenantId
    = durable tenant boundary

ContextKey
    = volatile RBAC / correlation / debugging key

Metadata
    = observability duplicate only
```

Every distributed or background hop that needs to evaluate registry/capacity visibility must either carry or restore the durable `ExecutionContextSnapshot`.

This affects:

- shared run creation
- shared run Redis persistence
- shared queue dispatch
- dispatch-time admission
- local runtime queue enqueue
- background controller processing
- direct runtime integration tests
- future HTTP/gRPC/Kubernetes provider paths.

---

## Tenant Runtime Isolation Modes

Runtime instances can be interpreted through three isolation modes:

| Mode | Meaning |
|---|---|
| Shared | Capacity belongs to the shared/default runtime pool. |
| Dedicated | Capacity belongs to one tenant or tenant group only. |
| Hybrid | Capacity belongs to one tenant or tenant group, while the tenant may also fall back to shared capacity if its settings allow it. |

Current hardcoded tenant settings used by the foundation are:

```text
tenant-a
    IsolationMode = Dedicated
    PreferDedicatedCapacity = true
    AllowSharedFallback = false
    MaxRuntimeInstances = 3
    RuntimeInstanceIdPrefix = tenant-a-runtime
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 5
    LocalQueueCapacity = 500

tenant-b
    IsolationMode = Hybrid
    PreferDedicatedCapacity = true
    AllowSharedFallback = true
    MaxRuntimeInstances = 2
    RuntimeInstanceIdPrefix = tenant-b-runtime
    WorkerCountPerInstance = 5
    MaxConcurrentRunsPerInstance = 3
    LocalQueueCapacity = 250

default / unknown / test-tenant
    IsolationMode = Shared
    PreferDedicatedCapacity = false
    AllowSharedFallback = true
    MaxRuntimeInstances = 1
    RuntimeInstanceIdPrefix = runtime-instance
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 3
```

These settings are currently foundation settings.

Later, they can move to a database-backed or configuration-backed tenant settings provider.

---

## Tenant Visibility Rules

Registry and capacity listing are tenant-aware.

The visibility rules are intentionally strict.

### Shared runtime visibility

A shared runtime instance is visible when:

```text
current tenant settings are Shared
```

or:

```text
current tenant settings allow shared fallback
```

This allows a hybrid tenant to use shared capacity when fallback is enabled.

Example:

```text
tenant-b Hybrid + AllowSharedFallback = true
    can see Shared runtime-instance-1
```

A dedicated tenant with fallback disabled cannot see shared capacity.

Example:

```text
tenant-a Dedicated + AllowSharedFallback = false
    cannot see runtime-instance-1
```

### Dedicated runtime visibility

A dedicated runtime instance is visible only when the current tenant or tenant group matches the runtime descriptor.

```text
Runtime descriptor TenantId == current TenantId
    or
Runtime descriptor TenantGroupId == current TenantGroupId
```

Example:

```text
tenant-a can see tenant-a-runtime-1
tenant-b cannot see tenant-a-runtime-1
```

### Hybrid runtime visibility

A hybrid runtime instance is also owned capacity.

It is visible only when the current tenant or tenant group matches the runtime descriptor.

```text
Runtime descriptor TenantId == current TenantId
    or
Runtime descriptor TenantGroupId == current TenantGroupId
```

`AllowSharedFallback` does not make an unowned hybrid runtime visible.

This is an important safety rule:

```text
Hybrid runtime without owner
    is not visible just because AllowSharedFallback = true
```

Hybrid fallback means:

```text
Hybrid tenant may see Shared runtime capacity
```

It does not mean:

```text
Tenant may see unowned Hybrid runtime capacity
```

---

## Runtime Isolation Descriptor Fields

Runtime registry entries and capacity descriptors can expose tenant isolation through strong fields and metadata duplicates.

Strong fields should be preferred by runtime code.

Important fields include:

```text
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
```

Metadata duplicates may use canonical keys such as:

```text
tenantId
tenant.id
tenantGroupId
tenant.groupId
isolationMode
preferDedicatedCapacity
allowSharedFallback
runtimeInstanceIdPrefix
workerCountPerInstance
maxConcurrentRunsPerInstance
localQueueCapacity
```

The purpose of duplicated metadata is diagnostics and observability.

It is not the primary tenant boundary when strong fields are available.

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
- tenant id
- tenant group id
- isolation mode
- tenant fallback flags
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

`ListAsync` should return entries visible to the current execution context.

For multi-tenant execution, the registry must not leak dedicated or hybrid capacity across tenants.

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
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
RuntimeInstanceIdPrefix
ScaleOutRequestId when relevant
ScaleOutSourceRuntimeInstanceId when relevant
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
- future autoscaling decisions
- deciding whether a fulfilled scale-out run can be dispatched.

A runtime instance should only be eligible for dispatch when capacity indicates that it can accept work.

Conceptually:

```text
CanAcceptRun = queue not paused
            + queue capacity available
            + run slot available
            + worker available
            + tenant-visible to current execution context
```

`ListAsync` and `GetAsync` should respect tenant visibility when a current execution context exists.

This prevents admission from selecting capacity that belongs to another tenant.

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

It must not replace tenant isolation fields.

The provider tells the control plane how to contact the runtime instance.

The capacity descriptor tells the control plane whether the runtime instance should receive work.

The tenant visibility evaluator tells the control plane whether the current tenant is allowed to see that runtime instance.

Scale-out provider selection also uses provider identity.

For scale-out requests, the provider name can be resolved from:

```text
AiRuntimeScaleOutProviderRequest.ProviderHint
    -> AiRuntimeInstanceRegistrationOptions.ProviderName
    -> local
```

The scale-out provider itself remains part of the same provider model.

`IAiRuntimeScaleOutProvider` extends `IAiRuntimeInstanceProvider`.

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

For dynamically created local runtime instances, the scale-out registration flow is:

```text
Scale-out request fulfilled by local provider
    ↓
AiLocalRuntimeInstanceScaler creates child runtime host
    ↓
child runtime starts
    ↓
child runtime registers runtime instance
    ↓
child runtime publishes tenant-scoped capacity
    ↓
shared run is requeued
    ↓
pump can dispatch to the new runtime instance
```

The fulfilled scale-out request is not enough by itself.

The new runtime instance must become visible through registry and capacity before dispatch-time admission can select it.

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

- tenant isolation metadata
- queue paused state
- queued run count
- running run count
- active run count
- available run slots
- active worker count
- available worker count
- can accept run.

This keeps admission and MCP visibility current.

For scale-out-created runtime instances, the first registration/capacity publication acts as the readiness signal that allows the requeued run to be dispatched.

---

## Crash Recovery Visibility

Registry and capacity stores are not only used for normal dispatch and scale-out. They are also part of the crash recovery safety model.

When a runtime instance becomes unsafe, it must stop being eligible for new admission. Existing work assigned to that runtime is then handled by execution recovery reconciliation.

The important distinction is:

```text
Unsafe capacity suppression
    = stop routing new work to an unsafe runtime

Assigned work recovery
    = recover work that was already dispatched to that runtime
```

These responsibilities must remain separate. A health reconciler should not execute DAG recovery directly. A provider should not restart or kill runtimes directly. The execution recovery reconciler should recover assigned work using durable state.

Recovery source of truth:

```text
shared run store
shared queue
runtime run execution index
DAG execution store
runtime registry
runtime capacity store
ledger / trace / forensics evidence
```

The local runtime queue is intentionally not the source of truth. It can disappear with the process.

Validated process-host crash recovery uses this model:

```text
real RuntimeInstanceOnly process killed
    ↓
heartbeat stops
    ↓
runtime becomes unsafe for admission
    ↓
assigned work is enumerated
    ↓
in-flight execution resumes with preserved ExecutionId
    ↓
local queued work is redispatched through SharedRunId
    ↓
replacement runtime registers and publishes tenant-scoped capacity
    ↓
safe tenant capacity remains visible and unaffected
```

Recovery is complete only after the recovered work has converged and the proof surface is available through ledger, trace, replay, and recovery forensics.

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
Find at least one ready dispatchable runtime instance visible to the current dispatch context
    ↓
Start pump loop
```

This prevents queued work from being drained before runtime-only hosts have registered.

Readiness should use runtime capacity rather than only process startup.

A process can be started but still not dispatchable.

This is also important after scale-out fulfillment.

The scale-out watcher can create a runtime instance, but the pump should still dispatch based on visible capacity, not on the provider result alone.

```text
Scale-out provider result = success
    ↓
request marked Fulfilled
    ↓
shared run requeued
    ↓
pump restores shared run ExecutionContextSnapshot
    ↓
pump dispatches based on tenant-visible registry + capacity
```

---

## Admission and Reservation

Admission uses runtime capacity descriptors to select a target runtime instance.

In Redis-backed heavy dispatch scenarios, the runtime uses an admission reservation store to protect selected capacity during dispatch.

Conceptual flow:

```text
Restore or read ExecutionContextSnapshot
    ↓
List tenant-visible registry and capacity descriptors
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

When admission cannot find an eligible tenant-visible runtime instance and scale-out is enabled, admission can return `RequestScaleOut`.

In that case, the shared run is not dispatched immediately.

```text
No eligible tenant-visible runtime capacity
    ↓
Admission = RequestScaleOut
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Tenant runtime settings copied to request
    ↓
Scale-out request persisted
```

After the request is fulfilled and the run is requeued, dispatch-time admission runs again and can select the newly visible runtime capacity.

---

## Scale-Out and Capacity Visibility

Scale-out depends on discovery, registry, capacity visibility, and tenant runtime settings.

The current validated local scale-out flow is:

```text
MCP run submitted
    ↓
ExecutionContextSnapshot persisted with shared run
    ↓
DirectDispatch admission
    ↓
No executable tenant-visible runtime capacity
    ↓
Admission = RequestScaleOut
    ↓
Tenant runtime settings copied into Redis scale-out request
    ↓
AiRuntimeScaleOutRequestWatcherHostedService observes request
    ↓
AiRuntimeScaleOutProviderSelector resolves local provider
    ↓
LocalAiRuntimeInstanceProvider delegates to AiLocalRuntimeInstanceScaler
    ↓
AiLocalRuntimeInstanceScaler creates tenant-scoped local runtime instance
    ↓
runtime instance registers
    ↓
capacity descriptor is published
    ↓
scale-out request is marked Fulfilled
    ↓
shared run is requeued
    ↓
shared queue pump restores tenant context and dispatches after capacity is visible
```

The important architectural point is that scale-out fulfillment and dispatch remain separate.

```text
Scale-out fulfillment
    = capacity creation succeeded

Dispatch
    = pump/admission/provider selected and delivered the run
```

This protects the same shared queue ownership guarantees used by normal queue-first dispatch.

Validated tenant-aware local scale-out evidence:

```text
default / test tenant
    Initial tenant-visible capacity = 0
    Admission = RequestScaleOut
    RuntimeInstanceIdPrefix = runtime-instance
    ScaleOutRequest.Status = Fulfilled
    FulfilledRuntimeInstanceId contains :runtime-instance-1
    SharedRun.Status = Dispatched
    RuntimeRunStatus = completed

tenant-a Dedicated
    Admission = RequestScaleOut
    IsolationMode = Dedicated
    AllowSharedFallback = false
    RuntimeInstanceIdPrefix = tenant-a-runtime
    FulfilledRuntimeInstanceId contains :tenant-a-runtime-1
    Shared runtime fallback is not allowed

tenant-b Hybrid
    Admission = RequestScaleOut when no tenant/shared capacity is available
    IsolationMode = Hybrid
    AllowSharedFallback = true
    RuntimeInstanceIdPrefix = tenant-b-runtime
    FulfilledRuntimeInstanceId contains :tenant-b-runtime-1
    Shared runtime fallback is allowed when shared capacity is visible
```

Future Kubernetes scale-out should reuse the same visibility flow.

```text
Kubernetes scaler creates or expands runtime pods
    ↓
runtime pod registers with tenant isolation metadata
    ↓
runtime pod publishes tenant-scoped capacity
    ↓
scale-out request fulfilled
    ↓
shared run requeued
    ↓
pump dispatches normally through tenant-aware admission
```

---

## Local Runtime Scaler Scope

The local runtime scaler must create capacity inside the requested tenant runtime scope.

It must not use the global number of local hosts as the decision boundary.

The correct local scale-out rule is:

```text
Count matching local hosts by RuntimeInstanceIdPrefix
```

not:

```text
Count all local hosts globally
```

Examples:

```text
Shared runtime prefix
    runtime-instance

Dedicated tenant-a prefix
    tenant-a-runtime

Hybrid tenant-b prefix
    tenant-b-runtime
```

If a shared runtime already exists, that must not prevent creation of a dedicated tenant runtime.

Example:

```text
Existing host: host-abc:runtime-instance-1
Request: tenant-a Dedicated, target count = 1, prefix = tenant-a-runtime
Correct result: create host-abc:tenant-a-runtime-1
Incorrect result: reuse or no-op because global host count is already 1
```

This ensures dedicated and hybrid tenant capacity is not accidentally collapsed into the shared runtime pool.

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
4. Tenant visibility filters should ignore stale or stopped capacity.
5. MarkDraining should stop new dispatch.
6. Unregister should remove registry and capacity entries when possible.
7. StopAsync cleanup should be best-effort.
8. Test cleanup should remain safety-only, not the primary lifecycle mechanism.
```

Heartbeat should publish capacity before a runtime instance becomes dispatchable.

The pump should start only after readiness is observed.

Scale-out-created instances follow the same rule.

The provider may create capacity, but dispatch should depend on the runtime instance becoming visible through registry and capacity stores.

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

Future HTTP/gRPC/Kubernetes tenant propagation should preserve the same rule:

```text
ExecutionContextSnapshot must travel with the dispatched run.
Runtime registry/capacity visibility must remain tenant-aware.
```

---

## MCP Visibility

MCP runtime instance tools should expose registry and capacity visibility.

Useful MCP output includes:

```text
RuntimeInstanceId
Role
Status
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
RuntimeInstanceIdPrefix
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
ScaleOutRequestId when relevant
ScaleOutSourceRuntimeInstanceId when relevant
```

This gives MCP enough operational visibility to act as a temporary dashboard before a full UI exists.

MCP visibility must still respect tenant visibility rules when a tenant context is available.

---

## Validated Evidence

The current implementation has been validated through MCP, Redis, local runtime pool, tenant-aware local scale-out, and HTTP pooled runtime provider scenarios.

Tenant isolation evidence:

```text
tenant-a Dedicated
    sees tenant-a dedicated capacity
    does not fall back to shared capacity
    does not see tenant-b hybrid capacity
    scale-out creates tenant-a-runtime-1

tenant-b Hybrid
    sees tenant-b hybrid capacity
    may fall back to shared capacity when allowed
    does not see tenant-a dedicated capacity
    scale-out creates tenant-b-runtime-1 when needed

default / test tenant Shared
    sees shared capacity
    does not see tenant-a dedicated capacity
    does not see tenant-b hybrid capacity
    scale-out creates runtime-instance-1
```

Redis local scale-out evidence:

```text
Initial ActiveLocalInstances = 0
Admission = RequestScaleOut
SharedRun.Status = ScaleOutRequested
ScaleOutRequest.Status = Fulfilled
ScaleOutRuntimeInstanceId = host-...:runtime-instance-1 or tenant-specific prefix
ActiveLocalInstances = 1
SharedRun.Status = Dispatched
QueueStatus = Dispatched
LocalRunId = available
ExecutionId = available
RuntimeRunStatus = completed
```

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
Redis registry tenant visibility filtering = validated
Redis capacity tenant visibility filtering = validated
Control-plane discovery store = validated
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

Full regression evidence:

```text
1036 tests passing
```

Real process-host crash recovery evidence:

```text
Tenant A runtime process killed
Tenant B runtime process killed
Safe tenant runtime process not killed
Impacted in-flight executions resumed with preserved ExecutionId
Impacted local queued work redispatched through durable SharedRunId
Replacement runtime capacity registered and visible per tenant
Safe tenant completed normal runs with zero recovery work
Safe tenant recovery forensics = 0
Safe tenant recovery contamination visible from impacted ledger queries = 0
Cross-tenant ledger leak detected = false
Replay / ledger / trace / forensics proof validated after convergence
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
| Tenant visibility evaluator | Implemented / validated |
| Shared runtime visibility | Implemented / validated |
| Dedicated runtime visibility | Implemented / validated |
| Hybrid runtime visibility | Implemented / validated |
| Tenant-aware registry filtering | Implemented / validated |
| Tenant-aware capacity filtering | Implemented / validated |
| Tenant-aware admission capacity selection | Implemented / validated |
| Runtime capacity publication | Implemented / validated |
| Runtime capacity cleanup | Implemented / validated |
| Runtime heartbeat | Implemented / validated |
| Runtime unsafe-capacity suppression | Implemented / validated |
| Runtime crash recovery visibility | Implemented / validated |
| Process-host replacement capacity visibility | Implemented / validated |
| Safe tenant non-impact during crash recovery | Implemented / validated |
| Runtime role separation | Implemented / validated |
| MCP runtime visibility | Implemented / validated |
| Shared queue pump readiness gate | Implemented / validated |
| Redis admission reservation store | Implemented / validated |
| Redis scale-out request store | Implemented / validated |
| Scale-out request tenant fields | Implemented / validated |
| Store-backed scale-out request publisher | Implemented / validated |
| Scale-out request watcher | Implemented / validated |
| Provider-based scale-out selector | Implemented / validated |
| Local runtime scale-out capacity publication | Implemented / validated |
| Local runtime scaler scoped by runtime prefix | Implemented / validated |
| Fulfilled scale-out run requeue | Implemented / validated |
| MCP Redis local scale-out execution | Implemented / validated |
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
- Kubernetes pod/deployment scale-out implementation
- Redis command queue provider
- gRPC runtime provider
- production multi-control-plane leader election
- fully hardened registry/capacity TTL self-healing
- database-backed tenant runtime settings provider
- production dashboard UI
- full provider capability negotiation.

These are intentionally separate from the current validated discovery, registry, capacity, and tenant isolation foundation.

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| MCP Control Plane | Publishes discovery, exposes tools, submits shared runs, drains queue, observes runtime state. |
| Control-Plane Discovery Store | Stores the active logical MCP/control-plane descriptor. |
| ControlPlaneIdResolver | Resolves the logical control-plane id for runtime-only hosts and Redis stores. |
| RuntimeInstanceOnly Host | Resolves discovery, registers runtime instances, publishes capacity, hosts local queues/workers. |
| Runtime Instance Registry | Tracks runtime identities, roles, status, heartbeat, lifecycle, tenant isolation, and metadata. |
| Runtime Capacity Store | Tracks live run/worker capacity descriptors used by admission and readiness. |
| Runtime Visibility Evaluator | Applies Shared/Dedicated/Hybrid tenant visibility rules. |
| Tenant Runtime Settings Provider | Resolves tenant runtime mode, fallback, and runtime sizing settings. |
| Admission Controller | Selects runtime targets based on tenant-visible capacity and policy. |
| Admission Reservation Store | Protects selected runtime capacity during dispatch in Redis-backed scenarios. |
| Scale-Out Request Store | Persists tenant-aware scale-out requests and tracks pending, observed, fulfilled, and rejected lifecycle. |
| Scale-Out Watcher | Observes pending scale-out requests and delegates capacity creation to a scale-out-capable provider. |
| Scale-Out Provider Selector | Resolves the provider used to create capacity using the existing runtime provider model. |
| Local Runtime Scaler | Creates tenant-scoped runtime capacity based on `RuntimeInstanceIdPrefix`. |
| Fulfilled Run Requeue Service | Requeues a shared run after scale-out fulfillment so dispatch stays owned by the pump. |
| Shared Queue Pump | Waits for readiness and dispatches queued shared runs. |
| Runtime Provider | Contacts the selected runtime instance through local, HTTP, or future transport. |
| Local Runtime Queue | Owns `RunId` lifecycle and execution start. |
| DAG Engine | Owns durable `ExecutionId` execution and deterministic step transitions. |

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document describes the runtime discovery, registry, capacity, tenant visibility, unsafe runtime visibility, and crash recovery capacity foundation.

Do not present Kubernetes autoscaling, gRPC dispatch, Redis command queue dispatch, production dashboard features, database-backed tenant settings, or production multi-control-plane leader election as completed capabilities until they are implemented and validated.
