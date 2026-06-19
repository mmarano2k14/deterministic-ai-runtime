# Runtime Control Plane

Status: Implemented foundation / validated with shared controller, MCP, Redis stores, Redis scale-out request persistence, local runtime pools, local runtime scale-out, fulfilled-run requeue, HTTP pooled runtime scenarios, tenant-aware runtime isolation, Shared/Dedicated/Hybrid runtime visibility, and end-to-end MCP scale-out execution.

This document describes the **Runtime Control Plane foundation** used by the Deterministic AI Runtime, including replay, execution control, runtime queues, runtime instance registry/capacity, Redis discovery, shared queue orchestration, provider-based dispatch, tenant-aware runtime isolation, scale-out coordination, and MCP integration.

---

## Purpose

The runtime is no longer only responsible for executing deterministic DAG workflows.

It now also needs a control-plane layer capable of exposing safe runtime operations to external adapters such as:

- HTTP API
- MCP server
- CLI
- Dashboard
- Kubernetes control-plane pod
- Shared runtime controller

Production AI systems need more than execution.

They need to answer operational questions such as:

- can an execution be replayed?
- can an execution be paused, resumed, or cancelled?
- can a local runtime queue be paused or resumed?
- can queued work be cancelled before execution starts?
- can runtime instances register themselves?
- can runtime instances resolve the active MCP/control-plane identity before registration?
- can runtime instances publish heartbeat and capacity?
- can runtime capacity be removed cleanly during shutdown?
- can a run be assigned to a runtime instance?
- should a run be globally queued?
- should scale-out be requested?
- should a run be rejected?
- which tenant is allowed to see which runtime capacity?
- should a Dedicated tenant be isolated from shared capacity?
- should a Hybrid tenant fallback to shared capacity when tenant-owned capacity is unavailable?
- can tenant context survive asynchronous and background control-plane hops?
- can these decisions be logged and observed?

This is handled by the runtime control-plane foundation.

---

## Control Plane Scope

The runtime control plane provides adapter-neutral facades over runtime capabilities.

It does not replace the runtime engine.

It does not execute DAG steps.

It does not claim work.

It does not create Kubernetes pods.

It does not scale deployments directly.

It provides a safe layer between external operators/adapters and runtime internals.

External systems should call the control plane.

Runtime internals remain protected behind focused abstractions.

---

## High-Level Model

The control plane separates external commands from runtime internals.

```text
External Adapters
    HTTP API
    MCP Server
    CLI
    Dashboard
    Kubernetes Control Pod
            ↓
Runtime Control Plane
    Replay Control
    Execution Control
    Runtime Queue Control
    Runtime Instance Registry
    Runtime Instance Capacity
    Control-Plane Discovery
    Runtime Instance Control
    Shared Runtime Controller
    Shared Queue
    Provider Dispatch
    Run Admission
    Tenant Runtime Isolation
    Scale-Out Request Lifecycle
            ↓
Runtime Internals
    DAG Engine
    Local Queues
    Workers
    Worker Groups
    Execution Store
    Replay Service
    Execution Control Service
```

This separation prevents external systems from depending directly on internal runtime implementation details.

---

## Control Plane Areas

The current control-plane foundation includes these areas:

| Area | Responsibility |
|---|---|
| Replay | Expose replay and audit operations. |
| Execution Control | Pause, resume, cancel, human input, and control-state visibility. |
| Runtime Queue | Control the local runtime queue of one runtime instance. |
| Runtime Instances | Register, heartbeat, list, drain, and unregister runtime instances. |
| Runtime Capacity | Publish, list, and remove runtime capacity descriptors. |
| Control-Plane Discovery | Publish and resolve the logical MCP/control-plane identity used by runtime-only hosts. |
| Shared Runtime Controller | Create shared runs, queue globally, dispatch directly, request scale-out, and reject. |
| Shared Queue | Store pending global work and protect dispatch claim ownership. |
| Provider Dispatch | Deliver assigned shared runs to local or remote runtime instance queues. |
| Admission | Decide whether a run should be assigned, queued globally, scaled out, or rejected. |
| Tenant Runtime Isolation | Enforce Shared, Dedicated, and Hybrid runtime visibility using durable tenant context. |
| Scale-Out Lifecycle | Persist scale-out requests, observe pending requests, resolve scale-out-capable providers, create tenant-scoped local runtime capacity, mark requests fulfilled/rejected, and requeue fulfilled shared runs for normal pump dispatch. |
| Observability | Record started, completed, and failed control-plane operations. |

---

## Tenant-Aware Control Plane

The runtime control plane is now tenant-aware.

Tenant isolation is not based on volatile correlation metadata.

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

The control-plane rule is:

```text
ContextKey
    = RBAC / correlation / diagnostics / debug context

ExecutionContextSnapshot.TenantId
    = durable tenant boundary used by runtime isolation

Metadata
    = observability duplicate only
```

Every asynchronous or distributed control-plane hop that can leave the original request scope must carry or restore the execution context snapshot.

This includes:

- shared run persistence
- shared queue dispatch
- admission after background queue claim
- scale-out request publication
- provider-based runtime dispatch
- local runtime queue execution
- direct runtime integration tests
- future HTTP, gRPC, Redis command queue, and Kubernetes provider paths

The tenant-aware control-plane flow is:

```text
MCP / API request
    ↓
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
SharedRunRecord.ExecutionContextSnapshot
    ↓
SharedQueueDispatcher restores context
    ↓
Tenant-aware admission
    ↓
Tenant-visible registry and capacity
    ↓
Provider dispatch
    ↓
Runtime queued run carries snapshot
    ↓
Background controller restores context
    ↓
DAG execution
```

Background services must not rely on ambient `AsyncLocal` context from the original MCP request.

The durable snapshot carried by the shared run or local runtime queued run is the source of truth.

---

## Tenant Runtime Settings

Runtime isolation is driven by tenant runtime settings.

The current foundation uses a provider-backed hardcoded settings provider.

This is intentional for the first implementation stage.

Later, the same abstraction can be backed by configuration, database storage, tenant administration UI, or enterprise policy.

Current validated tenant profiles:

```text
tenant-a
    IsolationMode = Dedicated
    PreferDedicatedCapacity = true
    AllowSharedFallback = false
    RuntimeInstanceIdPrefix = tenant-a-runtime
    MaxRuntimeInstances = 3
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 5
    LocalQueueCapacity = 500

tenant-b
    IsolationMode = Hybrid
    PreferDedicatedCapacity = true
    AllowSharedFallback = true
    RuntimeInstanceIdPrefix = tenant-b-runtime
    MaxRuntimeInstances = 2
    WorkerCountPerInstance = 5
    MaxConcurrentRunsPerInstance = 3
    LocalQueueCapacity = 250

default / unknown / test-tenant
    IsolationMode = Shared
    PreferDedicatedCapacity = false
    AllowSharedFallback = true
    RuntimeInstanceIdPrefix = runtime-instance
    MaxRuntimeInstances = 1
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 3
```

The tenant settings provider is used by:

- admission
- runtime visibility evaluation
- scale-out request publication
- local runtime instance scaling
- shared queue dispatch
- Redis registry filtering
- Redis capacity filtering

---

## Runtime Isolation Modes

The runtime supports three tenant isolation modes.

| Mode | Meaning |
|---|---|
| Shared | Runtime capacity is shared/default capacity. |
| Dedicated | Runtime capacity is tenant-owned and must not fallback to shared unless explicitly allowed. |
| Hybrid | Runtime capacity can prefer tenant-owned capacity and fallback to shared capacity when configured. |

The visibility rules are strict:

```text
Shared runtime:
    visible to Shared tenants
    visible to Hybrid/Dedicated tenants only when tenant settings allow shared fallback

Dedicated runtime:
    visible only when TenantId or TenantGroupId matches

Hybrid runtime:
    visible only when TenantId or TenantGroupId matches
    AllowSharedFallback does not make an unowned Hybrid runtime visible
```

A Hybrid tenant may fallback to a Shared runtime.

An unowned Hybrid runtime is not Shared fallback.

This distinction prevents accidental cross-tenant capacity leakage.

---

## Tenant-Aware Registry and Capacity Visibility

The Redis runtime instance registry and Redis runtime instance capacity store now filter list/get results through tenant visibility.

Visibility is evaluated from:

- the current restored execution context snapshot
- the tenant id
- the tenant group id
- the runtime instance descriptor
- the runtime capacity descriptor
- the configured tenant runtime settings

This means admission and runtime tooling see only capacity that is valid for the current tenant.

Examples:

```text
tenant-a Dedicated:
    sees tenant-a-runtime-* only
    does not see shared runtime-instance-* because fallback is disabled
    does not see tenant-b-runtime-*

tenant-b Hybrid:
    sees tenant-b-runtime-*
    can see shared runtime-instance-* because fallback is enabled
    does not see tenant-a-runtime-*

test-tenant Shared:
    sees shared runtime-instance-* only
    does not see tenant-a-runtime-*
    does not see tenant-b-runtime-*
```

This filtering applies consistently to:

- registry list/get operations
- capacity list/get operations
- admission candidate selection
- shared queue dispatch
- scale-out requeue dispatch
- MCP runtime instance visibility

---

## Tenant-Aware Admission

Run admission is now tenant-aware.

Admission uses tenant-visible registry and capacity descriptors.

It must not select a runtime instance that is hidden from the current tenant.

Decision examples:

```text
tenant-a Dedicated + no tenant-a capacity:
    RequestScaleOut

tenant-a Dedicated + shared capacity only:
    RequestScaleOut
    because AllowSharedFallback = false

tenant-b Hybrid + shared capacity available:
    AssignToInstance(shared runtime)
    because AllowSharedFallback = true

tenant-b Hybrid + tenant-b capacity available:
    AssignToInstance(tenant-b runtime)

test-tenant Shared + tenant-a capacity available:
    ignored
    shared tenant cannot use dedicated tenant capacity
```

Admission decisions also carry tenant runtime settings when scale-out is requested.

This ensures the provider/scaler creates capacity inside the correct tenant scope.

---

## Tenant-Aware Scale-Out Request Lifecycle

Scale-out requests preserve tenant runtime settings as strong fields.

The scale-out request record includes:

```text
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
MaxRuntimeInstances
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
```

These fields are persisted in Redis and round-tripped through the scale-out watcher/provider flow.

Metadata may duplicate these values for observability, but metadata is not the isolation boundary.

The scale-out flow is:

```text
Admission = RequestScaleOut
    ↓
StoreBackedAiRuntimeScaleOutRequestPublisher
    ↓
RedisAiRuntimeScaleOutRequestStore
    ↓
AiRuntimeScaleOutRequestWatcherHostedService
    ↓
Provider selector
    ↓
Scale-out provider
    ↓
Tenant-scoped runtime capacity created
    ↓
Runtime registry / capacity publication
    ↓
Scale-out request marked Fulfilled
    ↓
Fulfilled shared run requeued
    ↓
Shared queue pump dispatches through normal tenant-aware admission
```

The local scaler must count matching runtime hosts by tenant runtime prefix.

It must not use the global host count.

Examples:

```text
runtime-instance-1
    shared/default runtime

tenant-a-runtime-1
    tenant-a Dedicated runtime

tenant-b-runtime-1
    tenant-b Hybrid runtime
```

This prevents a shared runtime from satisfying a Dedicated tenant scale-out request.

---

## ExecutionContextSnapshot Restoration During Dispatch

Shared queue dispatch is a background operation.

It does not run inside the original MCP/API request context.

Therefore, the dispatcher must restore the durable execution context snapshot from the shared run before running admission or dispatch.

The dispatch flow is:

```text
Claim shared queue item
    ↓
Load shared run
    ↓
Read SharedRun.ExecutionContextSnapshot
    ↓
Restore RBAC ExecutionContext
    ↓
Run tenant-aware admission
    ↓
List tenant-visible registry/capacity
    ↓
Reserve selected capacity
    ↓
Dispatch through provider
    ↓
Mark queue item dispatched
    ↓
Mark shared run dispatched
    ↓
Restore previous context / clear context
```

Without this restoration, Redis registry and capacity filters would see no tenant context and admission could incorrectly observe zero visible capacity or the wrong capacity.

Direct runtime local queue execution also requires an execution context snapshot.

If a local queued run does not carry a snapshot, the background controller fails fast instead of executing without tenant context.


---

## Replay Control Plane

The replay control plane exposes replay and audit operations through an adapter-neutral facade.

It wraps the existing replay service.

It is intended to be called later by:

- HTTP API
- MCP server
- CLI
- Dashboard
- Kubernetes control-plane layer

Replay control includes:

- replay request handling
- replay result exposure
- replay observability
- structured failure handling
- safe blocking of unsupported modes

`ReExecuteAll` remains intentionally blocked because it may re-run external providers or side effects before provider replay isolation and side-effect safety are implemented.

Replay control does not execute live runtime work.

It exposes replay and audit behavior safely.

---

## Execution Control Plane

The execution control plane exposes durable `ExecutionId`-level control operations.

It supports:

- pause execution
- resume execution
- cancel execution
- submit human input
- get execution control state

Execution control works at the durable execution lifecycle level.

```text
ExecutionId
    pause
    resume
    cancel
    waiting for input
    submit human input
    get control state
```

This control plane wraps the existing execution control service.

It does not execute DAG steps.

It does not claim work.

It does not mutate local queues.

It delegates to the durable execution control service.

---

## Runtime Queue Control Plane

The runtime queue control plane exposes local queue operations for one runtime instance.

It operates at the `RunId` level.

It supports:

- enqueue local run
- cancel local run
- cancel queued run
- pause local queue
- resume local queue
- get local run status
- get local queue status

This layer is intentionally local.

It controls the queue owned by one runtime instance.

It is not the future shared/global queue.

---

## Runtime Queue vs Execution Control

The runtime separates two identities:

```text
RunId
= controller / queue / submitted job lifecycle

ExecutionId
= durable DAG execution lifecycle
```

This distinction is critical.

A queued run can exist before an execution exists.

A queued run can be cancelled before any DAG state is created.

Once an execution starts, the run handle receives an `ExecutionId`.

From that point, execution-level operations should use execution control.

```text
If no ExecutionId exists:
    handle control at RunId / queue level.

If ExecutionId exists:
    delegate execution control to ExecutionId-level control state.
```

This avoids creating fake DAG executions for work that never started.

---

## Runtime Queue Visibility

The runtime now exposes immutable snapshots for local queue visibility.

### Run state snapshot

`AiRuntimePipelineRunState` exposes:

- `RunId`
- `ExecutionId`
- `PipelineKey`
- `PipelineName`
- `RuntimeInstanceId`
- `Status`
- `IsQueued`
- `IsRunning`
- `CancellationRequested`
- timestamps when available
- failure reason when available

### Queue state snapshot

`AiRuntimePipelineQueueState` exposes:

- `RuntimeInstanceId`
- `IsPaused`
- `QueuedRunCount`
- `RunningRunCount`
- `ActiveRunCount`
- `QueueCapacity`
- `MaxConcurrentRuns`
- `AvailableRunSlots`
- `CanAcceptRun`
- `WorkerCount`
- `ActiveWorkerCount`
- `AvailableWorkerCount`
- `MaxLocalWorkersPerExecution`
- `SnapshotAtUtc`

These snapshots are intended for:

- dashboard
- HTTP API
- MCP server
- CLI
- diagnostics
- shared admission
- Kubernetes visibility

---

## Queue Pause and Resume

Queue pause prevents new queued runs from starting.

It does not pause already-running executions.

```text
PauseQueueAsync
        ↓
local queue state = paused
        ↓
queued runs remain queued
        ↓
already-running executions continue
```

Queue resume allows queued runs to start again.

```text
ResumeQueueAsync
        ↓
local queue state = active
        ↓
queued runs become eligible to start
        ↓
runtime executions are created
```

Queue pause/resume must not be confused with execution pause/resume.

```text
PauseQueueAsync
= stop starting queued runs

PauseExecutionAsync
= stop claims for an existing ExecutionId
```

---

## Queue Pause / Resume Ledger Correlation

Queue pause/resume operations can be called externally.

External calls are not always executed inside the runtime execution async context.

Because the runtime correlation accessor is backed by async-flow context, it may not contain the active `ExecutionId` / `RunId` during external control-plane calls.

Therefore, queue pause/resume ledger correlation is resolved from controller state.

The controller checks:

```text
1. running runs with ExecutionId
2. queued runs with RunId
3. controller fallback identity
```

This preserves execution-correlated ledger events when a run is active.

It also avoids relying on `AsyncLocal` context where no execution scope exists.

---

## Control-Plane Discovery

The runtime control plane now includes Redis-backed control-plane discovery.

The purpose of discovery is to let runtime-only hosts resolve the active logical MCP/control-plane identity before registering runtime instances or publishing capacity.

```text
MCP Control Plane
    ↓
Redis Control-Plane Discovery Store
    ↓
ControlPlaneIdResolver
    ↓
RuntimeInstanceOnly Host
    ↓
Runtime Instance Registration
    ↓
Runtime Capacity Publication
```

This prevents runtime hosts from accidentally registering under a different logical control-plane id than the MCP server.

Important identities:

```text
ControlPlaneId
    logical shared Redis/control-plane scope

ControlPlaneHostId
    control-plane host or process publishing discovery

RuntimeInstanceId
    dispatchable runtime identity, often host-scoped

RuntimeId
    local runtime id inside a host or pool
```

Discovery is used during startup and registration.

Shutdown cleanup should not depend on rediscovery.

Once a runtime instance has registered or published capacity, cleanup should reuse the known resolved control-plane id for that runtime instance.

This is important because discovery descriptors, Redis dependencies, or logging providers may already be disposed during shutdown.

---

## Runtime Instance Capacity Store

Runtime capacity descriptors expose live scheduling and visibility information for runtime instances.

The runtime capacity store supports:

- publish capacity descriptor
- get capacity descriptor
- list capacity descriptors
- remove capacity descriptor on shutdown

Capacity descriptors include:

- runtime instance id
- role
- provider metadata
- worker count
- active worker count
- available worker count
- max local workers per execution
- queued run count
- running run count
- active run count
- queue capacity
- max concurrent runs
- available run slots
- queue paused state
- can accept run
- heartbeat / snapshot timestamp

Redis-backed capacity storage is part of the validated runtime control-plane foundation.

The capacity store is used by:

- admission
- shared queue pump readiness
- MCP runtime instance tools
- dashboard/API visibility
- provider routing
- future autoscaling decisions.

During shutdown, the capacity descriptor should be removed using the known control-plane id for the runtime instance.

It should not attempt to rediscover the control-plane id after discovery may have already been removed.


## Runtime Instance Registry

The runtime instance registry tracks visible runtime instances.

A runtime instance represents one runtime process.

In Kubernetes, a runtime instance usually maps to one pod / replica.

The registry supports:

- register/update runtime instance
- heartbeat runtime instance
- get runtime instance
- list runtime instances
- mark draining
- unregister / mark stopped

Runtime instance ids may be host-scoped in pooled provider scenarios.

Example:

```text
host-7ab6d623500844f88a6a2972d8c5a2e2:runtime-http-1
```

Registry cleanup should be idempotent and best-effort during shutdown.

The registry stores visibility data such as:

- runtime instance id
- host name
- process id
- Kubernetes namespace
- Kubernetes pod name
- Kubernetes node name
- worker count
- queued run count
- running run count
- active run count
- queue capacity
- max concurrent runs
- available run slots
- queue paused state
- can accept run
- runtime version
- metadata
- registered timestamp
- last heartbeat timestamp

Current implementations include:

- in-memory runtime instance registry
- Redis-backed runtime instance registry

The in-memory implementation remains useful for:

- local development
- unit tests
- single-process demos

The Redis-backed implementation is validated for shared-controller and pooled runtime scenarios.

It supports real multi-instance visibility across MCP/control-plane hosts and runtime-only hosts.

---

## Runtime Instance Status

Runtime instances can have the following statuses:

| Status | Meaning |
|---|---|
| Unknown | Registered but current health is unknown. |
| Ready | Alive and able to accept work. |
| Busy | Alive but under pressure. |
| Paused | Alive but local queue is paused. |
| Draining | Should not receive new runs. |
| Unhealthy | Heartbeat or health is invalid/stale. |
| Stopped | Explicitly unregistered. |

---

## Runtime Instance Control Plane

The runtime instance control plane exposes registry operations through an adapter-neutral facade.

It supports:

- publish control-plane discovery descriptor
- resolve control-plane id from discovery
- register runtime instance
- heartbeat runtime instance
- publish runtime capacity descriptor
- remove runtime capacity descriptor
- get runtime instance
- list runtime instances
- mark runtime instance as draining
- unregister runtime instance

This control plane is intended for:

- dashboard
- CLI
- MCP server
- HTTP API
- Kubernetes control-plane pod
- future shared runtime controller

It does not create Kubernetes pods.

It does not scale deployments directly.

It does not execute DAG steps.

It does not claim work.

It exposes visibility and control over registered runtime instances.

---

## Run Admission / Slot System V1

Run admission decides what should happen when a new run arrives.

Admission can return:

- assign to runtime instance
- queue globally
- request scale-out
- reject

Admission does not enqueue the run.

Admission does not modify local queues.

Admission does not create Kubernetes replicas.

Admission only produces a decision.

---

## Admission Reservations

The runtime control plane now includes a Redis-backed admission reservation foundation.

Admission capacity is based on visible runtime capacity descriptors.

In heavy dispatch scenarios, Redis-backed reservations protect selected runtime capacity during dispatch.

This reduces the risk of multiple dispatchers selecting the same visible capacity before heartbeat/capacity snapshots update.

Conceptual flow:

```text
Admission lists runtime capacity
    ↓
Select candidate runtime instance
    ↓
Reserve selected capacity
    ↓
Dispatch through provider
    ↓
If dispatch succeeds:
        local queue / heartbeat reflects real usage
    ↓
If dispatch fails:
        release or expire reservation
```

The current Redis admission reservation store is validated in heavy HTTP dispatch scenarios.

Lua-based reservation refinement can still be added later for stronger atomic slot and worker reservation semantics in production multi-control-plane scheduling.


## Admission Decision Flow

```text
New run request
        ↓
Run Admission Controller
        ↓
List registered runtime instances
        ↓
Filter eligible instances
        ↓
Find available capacity
        ↓
Decision:
    AssignToInstance
    QueueGlobally
    RequestScaleOut
    Reject
```

---

## Admission Decision Types

| Decision | Meaning |
|---|---|
| AssignToInstance | A runtime instance is available and selected. |
| QueueGlobally | No local instance is available, but future shared queue fallback is allowed. |
| RequestScaleOut | No local instance is available, and scale-out should be requested. |
| Reject | The run should be rejected by admission policy. |
| Unknown | No final admission decision could be produced. |

---

## Admission Policy Options

Run admission supports policy options such as:

- enabled / disabled
- maximum runtime instance count
- allow scale-out request
- allow global queue fallback
- reject when no capacity exists
- allow paused instances
- allow draining instances
- allow unhealthy instances
- prefer requested runtime instance
- duration measurement

This prepares the future shared runtime controller and Kubernetes scaler integration.

---

## Runtime Control Plane Observability

The control plane records operation events for supported facades.

Events include:

- operation started
- operation completed
- operation failed

Control-plane event fields include:

- event type
- area
- operation
- outcome
- correlation context
- duration
- message
- failure reason
- custom properties

Control-plane observability currently supports:

- no-op observer
- logged observer

Future observers can export to:

- metrics
- tracing
- decision ledger
- Kibana
- Grafana
- OpenSearch

The runtime core remains decoupled from specific dashboard tools.

---

## Dependency Injection

The control-plane service registration now includes:

- `IAiReplayControlPlane`
- `IAiExecutionControlPlane`
- `IAiRuntimeQueueControlPlane`
- `IAiRuntimeInstanceRegistry`
- `IAiRuntimeInstanceCapacityStore`
- `IAiControlPlaneDiscoveryStore`
- `IAiControlPlaneIdResolver`
- `IAiRuntimeInstanceControlPlane`
- `IAiRunAdmissionController`
- `IAiRuntimeAdmissionReservationStore`
- `IAiControlPlaneObserver`

Options are registered for:

- replay control
- execution control-plane
- runtime queue control-plane
- runtime instance control-plane
- run admission

A logging extension can replace the no-op observer with a logged observer.

---

## Validated Behavior

The implementation is validated by unit and integration tests covering:

- replay control-plane behavior
- execution control-plane behavior
- runtime queue control-plane behavior
- runtime instance registry behavior
- Redis runtime instance registry behavior
- runtime instance capacity store behavior
- Redis runtime instance capacity store behavior
- control-plane discovery store behavior
- control-plane id resolver behavior
- runtime instance control-plane behavior
- run admission decisions
- Redis admission reservation behavior
- DI registration
- queue pause/resume ledger correlation
- execution-correlated queue ledger visibility
- run-id correlated queue ledger visibility
- shared runtime controller behavior
- Redis shared run store behavior
- Redis shared queue behavior
- HTTP pooled runtime provider dispatch
- MCP manual drain and background pump dispatch
- replay/report/ledger/trace through MCP
- shutdown cleanup without late rediscovery dependency

---

## Current Capabilities

The runtime can now expose or support:

- replay execution
- audit execution
- pause execution
- resume execution
- cancel execution
- submit human input
- get execution control state
- enqueue local runtime run
- cancel local runtime run
- cancel queued local run
- pause local queue
- resume local queue
- get local run status
- get local queue status
- publish control-plane discovery descriptor
- resolve control-plane id from discovery
- register runtime instance
- heartbeat runtime instance
- publish runtime capacity descriptor
- remove runtime capacity descriptor
- get runtime instance
- list runtime instances
- mark runtime instance draining
- unregister runtime instance
- admit run
- assign run to runtime instance
- reserve selected runtime capacity
- queue globally
- request scale-out
- reject run
- enforce Shared/Dedicated/Hybrid runtime visibility
- persist tenant runtime settings in scale-out requests
- dispatch with restored tenant execution context
- create tenant-scoped local runtime instances through provider scale-out

---

## Kubernetes Preparation

This work prepares Kubernetes support by introducing:

- runtime instance identity
- runtime instance registration
- runtime instance heartbeat
- local queue visibility
- run capacity visibility
- admission decisions
- Redis-backed scale-out request persistence
- scale-out request watcher
- provider-based scale-out selection
- local runtime scale-out through the existing provider model
- fulfilled scale-out run requeue
- local queue control
- instance draining
- stopped/unregistered instances
- observability hooks
- structured event model

The next Kubernetes-related pieces can now be built on top:

- Shared Runtime Controller
- Shared Run Queue
- Redis-backed Runtime Instance Registry
- Redis-backed Runtime Instance Capacity Store
- Redis-backed Control-Plane Discovery Store
- Control-Plane Id Resolver
- Redis-backed admission reservation logic
- Scale-out requested events
- Redis scale-out request lifecycle
- Local runtime scale-out execution flow
- Kubernetes deployment scaler adapter
- MCP/API control-plane endpoints
- Live observability export to Kibana / Grafana / OpenSearch

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Replay ControlPlane | Exposes replay/audit operations. |
| Execution ControlPlane | Exposes `ExecutionId`-level control. |
| RuntimeQueue ControlPlane | Exposes local `RunId`/queue-level control. |
| RuntimeInstance Registry | Tracks runtime instance visibility and heartbeat. |
| RuntimeInstance ControlPlane | Exposes registry operations to adapters. |
| RunAdmission Controller | Decides assignment, global queue fallback, scale-out, or rejection. |
| Background Controller | Owns local queue lifecycle and `RunId` state. |
| DAG Runtime | Executes durable `ExecutionId` workflows. |
| Observability Layer | Records control-plane operation events. |

---

## What This Does Not Do Yet

The current foundation does not yet provide:

- Kubernetes pod scaling adapter
- actual Kubernetes scale-out execution
- Redis command queue dispatch
- gRPC runtime dispatch
- distributed shared controller election
- production dashboard UI
- production security model for external adapters
- database-backed tenant runtime settings management UI
- production tenant settings administration workflow

These are intentionally left for the next phases.

---

## Next Step

The Shared Runtime Controller, Redis/local scale-out lifecycle, and tenant-aware runtime isolation foundation are now implemented.

The next step is to continue hardening provider-based runtime instance administration, production multi-process coordination, and moving tenant settings from hardcoded provider-backed configuration toward a durable/configurable source.

Expected next work:

```text
Provider router hardening
Status provider capability
Control provider capability
Redis command queue provider
gRPC runtime provider
Kubernetes metadata provider
Kubernetes scaling provider
Production multi-control-plane reservation hardening
Tenant settings configuration/persistence
Dashboard/API/MCP operational polish
```

Future work should preserve the same control-plane boundaries:

- external adapters call control-plane facades
- shared queue coordinates global work
- providers deliver work to runtime instances
- local runtime queues own `RunId`
- DAG engine owns durable `ExecutionId`.

---


---

## Shared Runtime Controller V1

The Shared Runtime Controller is now implemented as the orchestration layer above admission, shared run persistence, shared queue coordination, local runtime dispatch, queue pumping, background queue consumption, and scale-out request publication.

It receives submitted runs and asks `IAiRunAdmissionController` what should happen next.

The controller currently handles all admission outcomes:

- `AssignToInstance`
- `QueueGlobally`
- `RequestScaleOut`
- `Reject`

For every submitted run, the controller creates a durable shared run record.

The shared run record must persist `ExecutionContextSnapshot` so tenant context survives queueing, scale-out, requeue, background dispatch, and provider delivery.

This makes admission decisions visible, queryable, auditable, and ready for external adapters such as:

- HTTP API
- MCP server
- CLI
- Dashboard
- Kubernetes control-plane pod

The shared controller does not execute DAG steps.

It does not directly claim shared queue work.

It does not create Kubernetes pods.

It coordinates runtime-control decisions through focused abstractions.

---

## Shared Run Store

The shared run store persists shared run records independently from local runtime queue state.

The shared run store abstraction is:

- `IAiSharedRunStore`

Current implementations:

- `InMemoryAiSharedRunStore`
- `RedisAiSharedRunStore`

The shared run store supports:

- create shared run
- get shared run
- list shared runs
- cancel shared run
- mark shared run as dispatched

Redis shared run storage uses:

- one Redis hash per shared run
- one Redis sorted set index for listing
- Lua atomic create
- Lua atomic cancel
- Lua atomic mark-dispatched
- Lua script SHA caching
- automatic NOSCRIPT reload

This allows shared run state to remain consistent under concurrent workers and multiple runtime instances.

---

## Shared Queue

The shared queue is the global queue used when admission decides that no current runtime instance can accept a run immediately, but the run should not be rejected.

The shared queue abstraction is:

- `IAiSharedQueue`

Current implementations:

- `InMemoryAiSharedQueue`
- `RedisAiSharedQueue`

The shared queue supports:

- enqueue pending shared run
- claim next pending shared run
- mark claimed item as dispatched
- requeue claimed item
- cancel queued item
- get queue item
- list queue items

Redis shared queue storage uses:

- one Redis hash per queue item
- pending sorted set
- all-items sorted set
- Lua atomic enqueue
- Lua atomic claim-next
- Lua atomic mark-dispatched
- Lua atomic requeue
- Lua atomic cancel
- Lua script SHA caching
- automatic NOSCRIPT reload

The Redis implementation prevents double dispatch by ensuring only one dispatcher can claim a pending queue item atomically.

---

## Runtime Provider Dispatch

The runtime control plane now supports provider-oriented dispatch.

Admission decides which runtime instance should receive a run.

The provider layer decides how to contact that runtime instance.

```text
Admission
    ↓
AssignedRuntimeInstanceId
    ↓
Capacity descriptor / provider metadata
    ↓
Runtime provider
    ↓
Target runtime local queue
```

Validated provider paths include:

- local runtime provider foundation
- HTTP runtime provider foundation
- pooled HTTP runtime hosting

The validated HTTP pooled model is:

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

The HTTP host identity is transport and hosting infrastructure.

The dispatchable runtime identities are the child runtime instances created by the runtime instance pool.

```text
HTTP host identity != dispatch target
runtime-http-* child instance == dispatch target
```


## Shared Runtime Controller Flow

```text
SubmitRun
  -> IAiRunAdmissionController

  -> AssignToInstance
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiSharedRunDispatcher.DispatchAsync(...)
      -> IAiSharedRunStore.MarkDispatchedAsync(...)
      -> SharedRun.Status = Dispatched

  -> QueueGlobally
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiSharedQueue.EnqueueAsync(...)
      -> SharedRun.Status = QueuedGlobally

  -> RequestScaleOut
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiRuntimeScaleOutRequestPublisher.PublishAsync(...)
      -> SharedRun.Status = ScaleOutRequested
      -> Redis scale-out request persisted
      -> watcher/provider/scaler fulfill capacity
      -> fulfilled shared run is requeued
      -> shared queue pump later dispatches it normally

  -> Reject
      -> IAiSharedRunStore.CreateAsync(...)
      -> SharedRun.Status = Rejected
```

This completes the first shared orchestration layer above local runtime queues.

---

## Direct Assigned Run Dispatch

When admission returns `AssignToInstance`, the shared controller dispatches the run directly to the selected runtime instance.

The dispatch abstraction is:

- `IAiSharedRunDispatcher`

Current implementation direction includes provider-capable dispatch for local and HTTP runtime scenarios.

The local dispatcher bridges the shared controller to the local runtime queue through:

- `IAiRuntimeQueueControlPlane`

The HTTP provider dispatches through a runtime HTTP command endpoint and ultimately enqueues into a selected child runtime local queue.

The assigned run dispatch flow is:

```text
AssignToInstance
  -> create shared run record
  -> dispatch through IAiSharedRunDispatcher
  -> enqueue into local runtime queue control plane
  -> receive LocalRunId
  -> receive ExecutionId when available
  -> mark shared run as Dispatched
```

This keeps the local queue model intact while making assigned dispatch visible at the shared controller level.

---

## Global Shared Queue Dispatch

When admission returns `QueueGlobally`, the shared controller creates a shared run record and enqueues a pending item into the shared queue.

Queued work is later consumed by the shared queue dispatcher.

The shared queue dispatcher abstraction is:

- `IAiSharedQueueDispatcher`

Runtime implementation:

- `AiSharedQueueDispatcher`

The queue dispatch flow is:

```text
IAiSharedQueueDispatcher
  -> IAiSharedQueue.ClaimNextAsync(...)
  -> IAiSharedRunStore.GetAsync(...)
  -> Dispatch-time admission
  -> Reserve selected capacity when required
  -> IAiSharedRunDispatcher.DispatchAsync(...)
  -> IAiSharedQueue.MarkDispatchedAsync(...)
  -> IAiSharedRunStore.MarkDispatchedAsync(...)
```

If the shared run record is missing, the queue item is requeued.

If dispatch fails, the queue item is requeued.

If dispatch succeeds, both the queue item and the shared run record are marked as dispatched.

---

## Shared Queue Pump

The shared queue pump runs controlled dispatch cycles over the shared queue.

The pump abstraction is:

- `IAiSharedQueuePump`

Runtime implementation:

- `AiSharedQueuePump`

The pump repeatedly calls `IAiSharedQueueDispatcher.DispatchNextAsync(...)` until:

- maximum dispatch count is reached
- no pending item is available
- a dispatch failure occurs and options require stopping
- cancellation is requested

Pump options include:

- enabled flag
- maximum dispatches per cycle
- default claim TTL
- stop when no item is available
- stop on dispatch failure
- worker id
- source label

The pump does not contain queue claim logic directly.

It coordinates dispatch cycles and delegates actual claim/dispatch behavior to the shared queue dispatcher.

---

## Shared Queue Background Service

The shared queue background service continuously runs shared queue pump cycles.

Hosted service:

- `AiSharedQueueBackgroundService`

Options:

- `AiSharedQueueBackgroundServiceOptions`

DI registration extension:

- `AddAiSharedQueueBackgroundService(...)`

The background service is intentionally thin.

It delegates business logic to:

- `IAiSharedQueuePump`

It handles:

- start / stop lifecycle
- runtime instance id resolution
- worker id resolution
- runtime readiness gate before dispatch
- pump cycle execution
- idle delay
- active delay
- error delay
- logging
- cancellation-aware shutdown

Background service flow:

```text
AiSharedQueueBackgroundService
  -> IAiSharedQueuePump.PumpOnceAsync(...)

IAiSharedQueuePump
  -> IAiSharedQueueDispatcher.DispatchNextAsync(...)
```

This allows runtime instances or MCP/control-plane hosts to automatically consume globally queued work.

The background service should wait for visible runtime capacity before dispatching.

```text
Background service startup
  -> resolve control-plane identity
  -> wait for registry/capacity visibility
  -> start pump cycles
```

---

## Runtime Scale-Out Lifecycle

When admission returns `RequestScaleOut`, the shared controller now persists a scale-out request instead of only acknowledging it.

Scale-out abstractions:

- `IAiRuntimeScaleOutRequestPublisher`
- `IAiRuntimeScaleOutRequestStore`
- `IAiRuntimeScaleOutProviderSelector`
- `IAiRuntimeScaleOutProvider`
- `IAiScaleOutFulfilledRunRequeueService`

Request/result models:

- `AiRuntimeScaleOutRequest`
- `AiRuntimeScaleOutRequestResult`
- `AiRuntimeScaleOutRequestRecord`
- `AiRuntimeScaleOutProviderRequest`
- `AiRuntimeScaleOutProviderResult`

Current validated implementations include:

- `StoreBackedAiRuntimeScaleOutRequestPublisher`
- `RedisAiRuntimeScaleOutRequestStore`
- `AiRuntimeScaleOutRequestWatcherHostedService`
- `AiRuntimeScaleOutProviderSelector`
- `LocalAiRuntimeInstanceProvider`
- `AiLocalRuntimeInstanceScaler`
- `AiScaleOutFulfilledRunRequeueService`

The scale-out provider model reuses the existing runtime instance provider router.

`IAiRuntimeScaleOutProvider` extends `IAiRuntimeInstanceProvider`.

This means scale-out is a provider capability, not a separate routing system.

Provider resolution uses:

```text
request.ProviderHint
    -> AiRuntimeInstanceRegistrationOptions.ProviderName
    -> local
```

The validated Redis/local scale-out flow is:

```text
SubmitRun
  -> IAiRunAdmissionController
  -> no runtime capacity available
  -> Decision = RequestScaleOut
  -> IAiSharedRunStore.CreateAsync(...)
  -> SharedRun.Status = ScaleOutRequested
  -> StoreBackedAiRuntimeScaleOutRequestPublisher.PublishAsync(...)
  -> RedisAiRuntimeScaleOutRequestStore creates pending request
  -> AiRuntimeScaleOutRequestWatcherHostedService observes pending request
  -> AiRuntimeScaleOutProviderSelector resolves local provider
  -> LocalAiRuntimeInstanceProvider requests local scale-out
  -> AiLocalRuntimeInstanceScaler creates local runtime instance
  -> runtime instance starts/registers/publishes capacity
  -> scale-out request is marked Fulfilled
  -> IAiScaleOutFulfilledRunRequeueService requeues the shared run
  -> IAiSharedQueuePump claims the requeued run
  -> dispatch-time admission sees new runtime capacity
  -> run is dispatched to the newly created local runtime instance
  -> local runtime creates ExecutionId
  -> runtime run completes
```

The watcher intentionally does not dispatch directly.

It only processes the scale-out request and requeues the shared run after fulfillment.

The normal shared queue pump remains responsible for claim, admission, dispatch ownership, and queue item lifecycle.

This keeps scale-out and dispatch responsibilities separated.

## Shared Controller Capabilities

The runtime can now expose or support:

- submit shared run
- get shared run
- list shared runs
- cancel shared run
- assign run to runtime instance
- dispatch assigned run locally
- queue run globally
- claim globally queued run
- dispatch globally queued run
- requeue failed dispatch
- mark shared run dispatched
- mark shared queue item dispatched
- publish scale-out request
- persist Redis-backed scale-out request
- observe pending scale-out requests
- fulfill scale-out through provider-capable local runtime scaler
- requeue fulfilled scale-out shared runs
- pump shared queue manually
- consume shared queue through hosted background service
- coordinate shared queue dispatch through Redis
- prevent double dispatch through Redis atomic claim

---

## Updated Dependency Injection

The control-plane service registration now also includes:

- `IAiSharedRunStore`
- `IAiSharedQueue`
- `IAiSharedRunDispatcher`
- `IAiSharedQueueDispatcher`
- `IAiSharedQueuePump`
- `IAiRuntimeScaleOutRequestPublisher`
- `IAiRuntimeScaleOutRequestStore`
- `IAiRuntimeScaleOutProviderSelector`
- `IAiRuntimeScaleOutProvider`
- `IAiScaleOutFulfilledRunRequeueService`
- `IAiLocalRuntimeInstanceScaler`
- `IAiSharedRuntimeController`
- `IAiRuntimeInstanceCapacityStore`
- `IAiControlPlaneDiscoveryStore`
- `IAiControlPlaneIdResolver`
- `IAiRuntimeAdmissionReservationStore`

Default implementations:

- `InMemoryAiSharedRunStore`
- `InMemoryAiSharedQueue`
- `LocalAiSharedRunDispatcher`
- `AiSharedQueueDispatcher`
- `AiSharedQueuePump`
- `NoopAiRuntimeScaleOutRequestPublisher`
- `StoreBackedAiRuntimeScaleOutRequestPublisher`
- `AiRuntimeScaleOutRequestWatcherHostedService`
- `AiRuntimeScaleOutProviderSelector`
- `LocalAiRuntimeInstanceProvider`
- `AiLocalRuntimeInstanceScaler`
- `AiScaleOutFulfilledRunRequeueService`
- `AiSharedRuntimeController`

Redis-backed validated implementations include:

- `RedisAiSharedRunStore`
- `RedisAiSharedQueue`
- `RedisAiRuntimeInstanceRegistry`
- `RedisAiRuntimeInstanceCapacityStore`
- `RedisAiRuntimeAdmissionReservationStore`
- `RedisAiRuntimeScaleOutRequestStore`

The hosted background service is opt-in through:

- `AddAiSharedQueueBackgroundService(...)`

---

## Updated Validated Behavior

The implementation is now validated by unit and integration tests covering:

- shared runtime controller behavior
- Redis shared runtime controller behavior
- shared run store behavior
- Redis shared run store behavior
- shared queue behavior
- Redis shared queue behavior
- direct assigned dispatch
- local shared run dispatcher
- shared queue dispatcher
- Redis shared queue dispatcher
- Redis atomic claim safety
- Redis concurrent dispatch safety
- missing shared run requeue
- dispatch failure requeue
- shared queue pump behavior
- shared queue background service lifecycle
- scale-out request publisher behavior
- Redis scale-out request store behavior
- store-backed scale-out request publisher behavior
- scale-out request watcher behavior
- scale-out provider selector behavior
- local runtime scale-out provider behavior
- local runtime instance scaler behavior
- fulfilled scale-out shared run requeue behavior
- MCP Redis local scale-out request fulfillment
- MCP Redis local scale-out requeue, dispatch, execution, and completion
- DI registrations
- Redis runtime instance registry cleanup
- Redis runtime capacity cleanup
- control-plane discovery resolution
- HTTP pooled runtime dispatch
- heavy HTTP dispatch across pooled child runtime instances
- MCP replay/report/ledger/trace scenarios
- tenant-aware shared/default runtime scale-out
- tenant-a Dedicated runtime scale-out
- tenant-b Hybrid runtime scale-out
- Hybrid fallback to shared runtime
- Dedicated no-fallback behavior
- Redis registry tenant visibility filtering
- Redis capacity tenant visibility filtering
- admission restricted to tenant-visible runtime capacity
- local scale-out scoped by runtime instance prefix
- shared queue dispatcher context restoration
- direct runtime execution requiring `ExecutionContextSnapshot`

---

## Updated Kubernetes Preparation

This work extends Kubernetes preparation by introducing:

- shared runtime controller
- shared run persistence
- Redis-backed shared run store
- Redis-backed shared queue
- atomic shared queue claim
- dispatch ownership
- queue pump
- background queue consumption
- scale-out request publisher abstraction
- Redis-backed scale-out request persistence
- scale-out request watcher
- provider-based scale-out selection
- local runtime scale-out provider capability
- local runtime instance scaler
- fulfilled scale-out shared run requeue
- runtime instance compatible dispatch path
- metadata propagation
- strong tenant runtime field propagation
- `ExecutionContextSnapshot` propagation
- source / requestedBy / reason / correlation propagation

The local scale-out lifecycle is now validated end-to-end.

This is not Kubernetes pod scaling yet, but it proves the full control-plane shape required by a future Kubernetes scaler:

```text
0 visible runtime capacity
  -> RequestScaleOut
  -> persisted scale-out request
  -> watcher fulfills capacity
  -> shared run requeued
  -> pump dispatches
  -> runtime executes
  -> completed
```

The next Kubernetes-related pieces can now be built on top:

- runtime instance heartbeat TTL / expiration
- runtime instance health visibility
- remote runtime dispatcher
- HTTP runtime dispatch adapter hardening
- gRPC runtime dispatch adapter
- Kubernetes scale-out adapter
- Kubernetes pod / deployment scaler
- dashboard / API / MCP control-plane endpoints
- real-time logs and observability export

---

## Updated What This Does Not Do Yet

The current foundation still does not yet provide:

- Kubernetes pod creation
- Redis command queue runtime dispatch
- gRPC runtime dispatch
- Kubernetes pod/deployment scaling
- dashboard UI
- HTTP API controller implementation
- distributed shared controller election
- production multi-control-plane leader election

These remain intentionally left for the next phases.

---

## Updated Next Step

The Shared Runtime Controller V1 and Redis/local scale-out execution flow are now complete.

The distributed runtime instance layer now has a validated foundation for:

- shared admission
- Redis-backed queue coordination
- provider-based dispatch
- provider-based local scale-out
- fulfilled scale-out run requeue
- end-to-end execution after dynamic capacity creation

Expected next work:

- heartbeat TTL / expiration hardening
- runtime instance health self-healing
- Redis command queue provider
- gRPC dispatch adapter
- provider status/control capabilities
- Kubernetes scaler adapter
- Kubernetes pod/deployment scaler
- control-plane API endpoints
- Kibana / Grafana / OpenSearch observability export
- Kubernetes production demo


## Current Validated Evidence

The current runtime control-plane foundation has been validated with:

```text
HTTP pooled QueueFirst dispatch:
    Runs = 50
    StepsPerRun = 100
    RuntimeInstances = runtime-http-1, runtime-http-2, runtime-http-3
    RedisAiSharedRunStore = validated
    RedisAiSharedQueue = validated
    RedisAiRuntimeAdmissionReservationStore = validated
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

Runtime lifecycle evidence:

```text
Redis runtime registry = validated
Redis runtime capacity store = validated
Control-plane discovery = validated
Runtime-only host identity resolution = validated
Shutdown cleanup without late rediscovery dependency = validated
```


Redis/local scale-out evidence:

```text
MCP submit = validated
Initial runtime capacity = 0
Admission decision = RequestScaleOut
SharedRunStatus = ScaleOutRequested -> Dispatched
ScaleOutRequestStatus = Fulfilled
ScaleOutRuntimeInstanceId = host-*:runtime-instance-1
QueueStatus = Dispatched
LocalRunId = available
ExecutionId = available
RuntimeRunStatus = completed
ActiveLocalInstances = 1
```

Tenant isolation evidence:

```text
Default/shared tenant -> runtime-instance-1
Tenant-a Dedicated -> tenant-a-runtime-1
Tenant-b Hybrid -> tenant-b-runtime-1
Tenant-b Hybrid fallback -> runtime-instance-1 when shared fallback is allowed
Tenant-a Dedicated no-fallback -> shared runtime ignored
Redis registry visibility = tenant-filtered
Redis capacity visibility = tenant-filtered
Admission candidate selection = tenant-visible only
Scale-out request tenant fields = Redis persisted and round-tripped
Shared queue dispatcher = restores ExecutionContextSnapshot before admission
Local runtime queue = requires ExecutionContextSnapshot before execution
Test suite = 1036 tests green
```

Validated scenario:

```text
ControlPlaneWithLocalRuntimeInstances_With_No_Runtime_Capacity_Should_ScaleOut_Requeue_Dispatch_And_Execute_Run
```


## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Execution Control State](execution-control-state.md)
- [Distributed Execution](distributed-execution.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability and Tracing](observability-tracing.md)
- [Testing Strategy](testing-strategy.md)

---