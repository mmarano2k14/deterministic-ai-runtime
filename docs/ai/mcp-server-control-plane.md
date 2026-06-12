# MCP Server as Runtime Control Plane

Status: Active foundation validated for local runtime pools, HTTP pooled runtime scenarios, Redis-backed shared queue coordination, Redis scale-out request persistence, local runtime scale-out, fulfilled-run requeue, and end-to-end MCP scale-out execution.

This document describes how the **MCP Server** acts as a concrete runtime control-plane adapter for the Deterministic AI Runtime.

It also reflects the current shared queue pump, queue-first submit mode, direct-dispatch admission scale-out mode, dispatch-time admission, runtime instance provider hosting, Redis-backed discovery/registry/capacity coordination, Redis-backed scale-out request coordination, HTTP pooled runtime hosting, local runtime scale-out, fulfilled-run requeue, and worker-capacity visibility work.

The MCP server does not replace the runtime engine.

It does not own DAG execution.

It does not replace local runtime queues.

It exposes runtime-control operations through MCP tools so operators, tests, dashboards, agents, and future automation layers can interact with the runtime safely.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)
- [runtime-control-plane.md](runtime-control-plane.md)

---

## Purpose

The Deterministic AI Runtime now has a runtime control-plane foundation.

The MCP server is one concrete adapter over that control plane.

Its purpose is to expose operational runtime commands such as:

- submit shared runs
- inspect shared runs
- submit queue-first runs
- submit direct-dispatch runs that may request scale-out
- observe Redis-backed scale-out requests
- validate local runtime scale-out from zero runtime capacity
- drain shared queues manually
- run or disable the background shared queue pump
- wait for runtime readiness before automatic dispatch
- publish control-plane discovery
- resolve runtime-only hosts against the MCP control-plane identity
- list runtime instances
- inspect runtime capacity
- inspect worker capacity
- inspect local queue pressure
- control runtime queues
- pause, resume, or cancel executions
- replay executions
- inspect decision ledger events
- inspect trace timelines

The MCP server is especially useful for:

- local control-plane demos
- integration tests
- Kubernetes demo preparation
- AI-assisted runtime operations
- future dashboard and operational automation
- proving that the runtime can be controlled externally
- validating provider-based dispatch before Kubernetes deployment
- proving the scale-out lifecycle before Kubernetes pod scaling exists

---

## MCP Control-Plane Discovery

The MCP server can act as the publisher of the active logical control-plane identity.

This identity is published through the Redis control-plane discovery store and resolved by runtime-only hosts through the control-plane id resolver.

```text
MCP Server / Control Plane Host
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

This allows runtime-only hosts to join the same logical control-plane scope without hardcoding the MCP control-plane id.

The MCP server identity is important for:

- shared Redis store scoping
- runtime instance registration
- runtime capacity publication
- shared queue pump readiness
- provider dispatch target discovery
- shutdown cleanup ownership.

The key rule is:

```text
MCP publishes the logical control-plane identity.
Runtime-only hosts resolve it before registration.
Registry and capacity descriptors use the resolved identity.
Shutdown cleanup reuses the known resolved identity.
```

This prevents runtime hosts from registering under a different control-plane id than the MCP server.


## What the MCP Server Is

The MCP server is an operational adapter.

```text
MCP Client / Tool Caller
        ↓
MCP Server
        ↓
Runtime Control Plane
        ↓
Shared Runtime Controller
        ↓
Admission / Dispatch / Queue Control / Replay / Observability
        ↓
Runtime Instances
        ↓
Local Queues
        ↓
Workers
        ↓
DAG Execution Engine
```

The MCP server sits above the control plane.

It calls existing control-plane abstractions.

It should not directly manipulate low-level runtime internals.

---

## What the MCP Server Is Not

The MCP server is not:

- the DAG execution engine
- a worker process by itself
- a replacement for local runtime queues
- a distributed scheduler by itself
- a Kubernetes controller by itself
- a database persistence layer
- a side-effect replay engine
- a dashboard UI

It is an adapter and command surface.

The runtime core remains protected behind focused abstractions.

---

## MCP Host Modes

The MCP host supports multiple runtime operating modes.

These modes define what the host process is responsible for.

---

## ControlPlaneOnly Mode

`ControlPlaneOnly` means the MCP host acts only as a control-plane process.

```text
MCP Host
    Role = ControlPlane
    Executes runs = false
    Owns local runtime instances = false
    Hosts MCP tools = true
    Can submit / inspect / control = true
```

This mode is intended for future setups where runtime instances are external.

Example future Kubernetes shape:

```text
mcp-control-plane pod
    ↓
Redis registry / shared queue / capacity descriptors
    ↓
runtime pod 1
runtime pod 2
runtime pod 3
```

The control-plane host can submit runs, list instances, request dispatch, publish discovery, and inspect replay/observability, but it should not be selected as a runtime execution target.

---

## ControlPlaneWithLocalRuntimeInstances Mode

`ControlPlaneWithLocalRuntimeInstances` is the current demo and integration-test mode.

```text
MCP Host
    Role = ControlPlane
    Hosts MCP tools = true
    Hosts local runtime instance pool = true
```

The process contains:

```text
mcp-control-plane
    Role = ControlPlane
    CanAcceptRun = false

mcp-runtime-1
    Role = Runtime
    CanAcceptRun = true

mcp-runtime-2
    Role = Runtime
    CanAcceptRun = true

mcp-runtime-3
    Role = Runtime
    CanAcceptRun = true
```

This mode is useful because it demonstrates the future Kubernetes architecture inside one process.

Each local runtime instance has its own:

- local queue
- queue state
- runtime identity descriptor
- worker pool
- run slots
- heartbeat
- capacity descriptor
- hosted registration service
- hosted pipeline background controller

The control-plane host remains separate from executable runtime instances.

---

## ControlPlaneWithHttpRuntimeInstances Mode

`ControlPlaneWithHttpRuntimeInstances` means the MCP host acts as a control-plane process while runtime instances are exposed through HTTP provider metadata.

```text
MCP Host
    Role = ControlPlane
    Hosts MCP tools = true
    Executes runs = false
    Dispatches through HTTP runtime provider metadata = true

HTTP Runtime Instance Host
    Role = Runtime host / transport host
    Owns local runtime instance pool = true
    Owns child runtime queues = true
    Owns child runtime workers = true
    Receives dispatch through HTTP provider path
```

This mode validates the provider-based hosting direction without requiring Kubernetes and is now validated through pooled child runtime instances.

It is useful for proving:

- control plane and runtime instance can be separated
- shared queue pump can dispatch to a remote-style runtime instance
- queue-first runs can remain queued until a background pump or manual drain dispatches them
- provider metadata can identify the runtime instance transport
- MCP tools can control and observe the distributed shape

Example provider metadata:

```text
provider.name = http
provider.transport = http
provider.endpoint = http://localhost
runtime.instance.id = runtime-http-1
```

Current validated shape:

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

---

## RuntimeInstanceOnly Mode

`RuntimeInstanceOnly` means the host process acts as a runtime participant.

```text
Runtime Host
    Role = Runtime
    Executes runs = true
    Hosts MCP control-plane tools = usually false
    Owns local queue = true
    Owns workers = true
```

This mode is intended for future multi-process or Kubernetes setups.

A runtime-only pod can:

- resolve the MCP-published control-plane identity when discovery is required
- register itself
- publish heartbeat
- publish capacity
- receive dispatch commands through a provider transport
- enqueue work into its local queue
- execute DAG workflows using local workers

This mode is the natural target for future Kubernetes runtime pods.

---

## Runtime Role Separation

The MCP control-plane architecture depends on role separation.

Runtime instance registrations include a role:

```text
AiRuntimeInstanceRole.ControlPlane
AiRuntimeInstanceRole.Runtime
```

The control-plane host is visible, but it is not dispatchable.

Runtime instances are visible and dispatchable.

```text
ControlPlane role
    Can expose tools
    Can orchestrate
    Can inspect
    Can request dispatch
    Should not execute assigned runs

Runtime role
    Can accept run dispatch
    Owns local queue
    Owns workers
    Executes DAG workflows
```

This prevents admission from accidentally assigning work to `mcp-control-plane`.

---

## Runtime Instance Identity

The runtime now uses descriptor-based runtime identity.

A local runtime instance may be configured as:

```text
mcp-runtime-1
```

The normalized runtime execution identity may appear as:

```text
MSI:mcp-runtime-1
```

Workers inside that runtime instance use numbered identities when distributed worker execution is enabled:

```text
MSI:mcp-runtime-1:worker:1
MSI:mcp-runtime-1:worker:2
MSI:mcp-runtime-1:worker:3
```

The default worker identity remains available as a fallback for direct DI usage:

```text
MSI:mcp-runtime-1:worker:default
```

Execution events should use numbered worker identities when distributed workers are active.

Controller-level events may still use controller or background service identity where appropriate.

In pooled runtime scenarios, runtime instance ids may be host-scoped.

Example:

```text
host-7ab6d623500844f88a6a2972d8c5a2e2:runtime-http-1
```

Tests and tools should validate the dispatchable child runtime identity rather than assuming a fixed parent host identity.


---

## Local Runtime Instance Pool

The local runtime instance pool creates multiple isolated local runtime hosts inside the same process.

Example configuration:

```json
{
  "AiLocalRuntimeInstancePool": {
    "Enabled": true,
    "InstanceCount": 3,
    "WorkerCountPerInstance": 10,
    "MaxConcurrentRunsPerInstance": 5,
    "RuntimeInstanceIdPrefix": "mcp-runtime"
  }
}
```

The pool creates runtime instances such as:

```text
mcp-runtime-1
mcp-runtime-2
mcp-runtime-3
```

Each runtime instance receives its own service provider and runtime identity descriptor.

Each runtime instance keeps its own queue and worker lifecycle.

The local runtime instance infrastructure can also be used dynamically by scale-out.

When the local pool startup option is disabled, the MCP host may start with zero executable local runtime instances.

A submitted run can then request scale-out through admission, persist a Redis scale-out request, and let the scale-out watcher create local runtime capacity on demand.

Validated dynamic local scale-out shape:

```text
MCP Host
    Role = ControlPlane
    Local pool startup = disabled
    Runtime capacity at start = 0

Submit shared run
    ↓
Admission = RequestScaleOut
    ↓
Redis scale-out request
    ↓
Scale-out watcher
    ↓
Local runtime scaler
    ↓
mcp-scaleout-runtime-1 created/registered/started
    ↓
Shared run requeued
    ↓
Shared queue pump dispatches
    ↓
Local runtime executes DAG
```

This validates the scale-out control loop before replacing the local scaler with a Kubernetes deployment/pod scaler.

---

## Local Queue Preservation Rule

The MCP server and shared queue must not replace local queues.

Local queues remain owned by runtime instances.

```text
Shared Queue
    ↓
Admission
    ↓
Runtime Instance Selection
    ↓
Dispatch Provider
    ↓
Local Runtime Queue
    ↓
Workers
    ↓
DAG Engine
```

This rule is important.

The local queue is the boundary where a selected runtime instance takes ownership of a run.

The shared queue is above the runtime instances.

The local runtime queue remains inside the runtime instance.

---

## Queue-First Submit Mode

The MCP control plane can submit runs in queue-first mode.

In queue-first mode, submission does not immediately dispatch to a runtime instance.

```text
MCP shared run submit
    ↓
IAiSharedRuntimeController.SubmitRunAsync
    ↓
SharedRun.Status = QueuedGlobally
    ↓
SharedQueueItem.Status = Pending
    ↓
Wait for background pump or manual drain
```

This mode is configured through the shared runtime controller:

```text
AiSharedRuntimeController:SubmitMode = QueueFirst
```

Queue-first mode is useful for:

- Kubernetes-style queue consumption
- manual operator-controlled drain
- MCP demo flows
- validating pump disabled behavior
- proving that shared queue persistence works independently from immediate dispatch

Queue-first intentionally bypasses the initial admission outcome and persists the run as `QueuedGlobally`.

For admission-driven scale-out tests and demos, use `DirectDispatch`.

```text
AiSharedRuntimeController:SubmitMode = DirectDispatch
```

Direct-dispatch mode allows the controller to preserve the real admission decision:

```text
AssignToInstance
QueueGlobally
RequestScaleOut
Reject
```

This distinction is important.

```text
QueueFirst
    -> SharedRun.Status = QueuedGlobally
    -> run waits for pump/manual drain

DirectDispatch + no runtime capacity + scale-out enabled
    -> SharedRun.Status = ScaleOutRequested
    -> Redis scale-out request is persisted
    -> watcher/scaler creates runtime capacity
    -> fulfilled run is requeued
    -> pump dispatches to new runtime instance
```

---

## Manual Drain and Background Pump Control

The MCP server can expose shared queue drain through MCP tooling.

Manual drain can be enabled while the automatic background pump is disabled.

Recommended controlled-drain configuration:

```text
AiSharedQueuePump:Enabled = true
AiMcpHost:EnableSharedQueuePump = false
AiSharedQueueBackgroundService:Enabled = false
```

This means:

```text
Manual queue.drain works.
Automatic background pump does not run.
Queued shared runs remain queued until manually drained.
```

When the background pump is enabled, the MCP host should wait for runtime readiness before automatic dispatch.

The readiness gate should ensure that at least one runtime instance is visible, ready, and able to accept work.

```text
MCP background pump startup
    ↓
resolve control-plane identity
    ↓
wait for runtime registry/capacity visibility
    ↓
start pump loop
```

This is important for tests and demos because it proves:

- queue-first submit persists work
- pump disabled does not break the runtime
- manual drain can dispatch later
- local and HTTP runtime providers can both complete runs after manual drain

---

## Shared Queue and Runtime Dispatch

The shared queue handles work that cannot immediately be assigned or needs global coordination.

The shared runtime controller handles:

- create shared run record
- ask admission for a decision
- assign directly to an instance
- queue globally
- request scale-out
- reject
- mark shared run state
- preserve visibility and auditability

Current assigned dispatch flow:

```text
MCP run.submit_many_runs
    ↓
IAiSharedRuntimeController.SubmitAsync
    ↓
IAiRunAdmissionController
    ↓
Decision = AssignToInstance
    ↓
IAiSharedRunDispatcher
    ↓
runtime instance local queue
    ↓
LocalRunId
    ↓
ExecutionId when execution starts
```

Current queue-first dispatch flow:

```text
MCP run.submit_many_runs
    ↓
IAiSharedRuntimeController.SubmitAsync
    ↓
SubmitMode = QueueFirst
    ↓
SharedRun.Status = QueuedGlobally
    ↓
SharedQueueItem.Status = Pending
    ↓
MCP queue.drain or background pump
    ↓
IAiSharedQueueDispatcher
    ↓
dispatch-time admission
    ↓
AssignedRuntimeInstanceId selected
    ↓
IAiSharedRunDispatcher
    ↓
selected runtime instance local queue
    ↓
LocalRunId
    ↓
ExecutionId when execution starts
```

Current Redis/local scale-out dispatch flow:

```text
MCP run.submit_many_runs
    ↓
IAiSharedRuntimeController.SubmitAsync
    ↓
SubmitMode = DirectDispatch
    ↓
IAiRunAdmissionController
    ↓
No runtime capacity available
    ↓
Decision = RequestScaleOut
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
StoreBackedAiRuntimeScaleOutRequestPublisher
    ↓
RedisAiRuntimeScaleOutRequestStore
    ↓
AiRuntimeScaleOutRequestWatcherHostedService
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
LocalAiRuntimeInstanceProvider
    ↓
AiLocalRuntimeInstanceScaler
    ↓
new local runtime instance is created/registered/started
    ↓
ScaleOutRequest.Status = Fulfilled
    ↓
AiScaleOutFulfilledRunRequeueService
    ↓
SharedQueueItem.Status = Pending
    ↓
background pump / shared queue pump
    ↓
dispatch-time admission sees new capacity
    ↓
AssignedRuntimeInstanceId = host-...:mcp-scaleout-runtime-1
    ↓
selected runtime instance local queue
    ↓
LocalRunId
    ↓
ExecutionId
    ↓
runtime run completed
```

The scale-out watcher does not dispatch directly.

It marks the scale-out request as fulfilled and requeues the shared run.

The normal shared queue pump remains responsible for claim ownership, dispatch-time admission, provider dispatch, and queue/run state transitions.

The dispatch layer now supports provider-oriented local and HTTP pooled runtime instance scenarios.

Provider-based dispatch remains the direction for Redis command queues, gRPC, and Kubernetes-native transports. The current Redis-backed shared run store, shared queue, registry, capacity store, discovery store, admission reservation store, and scale-out request store validate the coordination layer used by this direction.

---

## Pump Identity vs Assigned Runtime Identity

The shared queue pump uses explicit pump identity:

```text
PumpRuntimeInstanceId
PumpWorkerId
```

These fields identify the runtime instance and worker executing the pump cycle.

They do not necessarily identify the runtime instance selected for dispatch.

```text
PumpRuntimeInstanceId
    = who drains the shared queue

AssignedRuntimeInstanceId
    = who receives the shared run
```

Dispatch-time admission chooses the assigned runtime instance.

This separation allows:

- MCP control-plane host to drain shared work
- runtime instance pumps to drain shared work
- HTTP/runtime provider dispatch to target a different runtime instance
- future Kubernetes control-plane pods to dispatch to runtime pods

Tests that expect pump-local dispatch should explicitly configure admission so the assigned runtime instance equals the pump runtime instance.

Production code should not assume `PumpRuntimeInstanceId == AssignedRuntimeInstanceId`.

---

## Runtime Capacity Descriptors

Runtime instances publish capacity descriptors, backed by the runtime capacity store in Redis-enabled scenarios.

Capacity descriptors allow the control plane to know:

- which runtime instances exist
- which are ready
- which can accept runs
- how many run slots are available
- how many workers exist
- how many workers are active
- how many workers are available
- how many workers may be used by one execution
- how much queue pressure exists
- whether a queue is paused
- when the last heartbeat was published

Example:

```text
mcp-runtime-1
    WorkerCount = 10
    ActiveWorkerCount = 0
    AvailableWorkerCount = 10
    MaxLocalWorkersPerExecution = 4
    MaxRunSlots = 5
    AvailableRunSlots = 5
    CanAcceptRun = true
```

Capacity descriptors are the foundation for capacity-aware admission, MCP visibility, dashboard visibility, future autoscaling, provider-based dispatch, and pump readiness.

During shutdown, capacity descriptor cleanup should reuse the already resolved control-plane identity for the runtime instance.

Cleanup should not depend on rediscovery after the descriptor has already been published.

---

## MCP Tool Groups

The MCP server exposes runtime operations through focused tool groups.

Tool group names may evolve, but the current intent is stable.

---

## Discovery and Readiness Responsibilities

The MCP host is responsible for publishing the active discovery descriptor when configured as the control-plane owner.

Runtime-only hosts are responsible for resolving that descriptor before registering runtime instances.

The MCP server should make discovery and readiness visible through diagnostics and logs, even if discovery is not exposed as a public operator tool yet.

Useful diagnostics include:

```text
ControlPlaneId
ControlPlaneHostId
DiscoveryKey
Discovery owner
Discovery published
Runtime instances discovered
Runtime capacity visible
Pump readiness completed
```


## Shared Run Tools

Shared run tools expose shared controller operations.

Typical responsibilities:

- submit one or many runs
- get shared run
- list shared runs
- cancel shared run
- inspect shared run dispatch status
- view shared run metadata

These tools operate above local runtime queues.

They work with shared run records and shared controller decisions.

---

## Shared Queue Tools

Shared queue tools expose global shared queue operations.

Typical responsibilities:

- inspect shared queue items
- drain shared queue
- pump shared queue manually
- observe queued global work
- validate queue dispatch behavior

Shared queue tools are useful for testing and operational visibility.

---

## Scale-Out Visibility

Scale-out is now visible through the MCP integration test path and underlying control-plane stores.

Current scale-out responsibilities are:

- persist scale-out requests when admission returns `RequestScaleOut`
- expose request status as pending, observed, fulfilled, or rejected
- resolve a scale-out-capable provider through the existing runtime provider router
- create local runtime capacity through the local runtime scaler
- requeue the fulfilled shared run for normal shared queue dispatch
- prove execution completion through runtime queue status and `ExecutionId`

Validated local scale-out evidence:

```text
SharedRunStatus = Dispatched
AssignedRuntimeInstanceId = host-...:mcp-scaleout-runtime-1
LocalRunId = available
ExecutionId = available
RuntimeRunStatus = completed
QueueStatus = Dispatched
ScaleOutRequestStatus = Fulfilled
ActiveLocalInstances = 1
```

Future MCP tools can expose scale-out request listing and diagnostics directly.

Until then, tests and diagnostics validate the same control-plane path through Redis stores and MCP runtime status calls.

---

## Runtime Instance Tools

Runtime instance tools expose runtime registry and capacity visibility.

Typical responsibilities:

- list runtime instances
- get runtime instance status
- inspect runtime role
- inspect heartbeat data
- inspect runtime capacity
- inspect queue pressure
- mark instance draining when supported
- unregister or hide stopped instances when supported

These tools are the main visibility layer for local pools and future Kubernetes pods.

---

## Worker Capacity Visibility in Runtime Instance Tools

Runtime instance tools should expose worker capacity fields from `AiRuntimeInstanceSnapshot`.

Important fields:

```text
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
```

These fields are used to show whether a runtime instance is idle, saturated, queue-limited, worker-limited, or paused.

`CanAcceptRun` should be interpreted as a combined readiness signal.

```text
CanAcceptRun = queue not paused
            + queue capacity available
            + run slot available
            + worker available
```

This is especially useful for MCP demos before a dashboard UI exists.

---

## Runtime Queue Tools

Runtime queue tools expose local queue operations for a selected runtime instance.

Typical responsibilities:

- get queue state
- get local run status
- pause runtime queue
- resume runtime queue
- cancel queued local run
- bridge running cancellation to execution cancellation

Runtime queue tools operate at `RunId` level.

They must not be confused with `ExecutionId`-level execution control.

---

## Execution Control Tools

Execution control tools operate at the durable `ExecutionId` level.

Typical responsibilities:

- pause execution
- resume execution
- cancel execution
- get execution control state
- submit human input

Execution control affects durable DAG execution state.

It does not directly manage local queue scheduling.

---

## Replay Tools

Replay tools expose replay and audit behavior.

Typical responsibilities:

- replay execution
- get replay report
- validate deterministic fingerprints
- load replay metadata
- inspect replay issues
- inspect replay ledger events
- inspect replay trace timelines

Replay tools should remain safe and should not re-run external side effects unless explicitly supported later by isolated provider replay.

`ReExecuteAll` remains intentionally blocked until side-effect-safe provider replay exists.

---

## Observability Tools

Observability tools expose runtime diagnostics.

Typical responsibilities:

- get execution ledger events
- get trace timeline
- inspect runtime metrics
- inspect queue activity
- inspect shared queue activity
- inspect control-plane events

These tools are important for Kubernetes and dashboard demos.

The runtime core should remain decoupled from Kibana, Grafana, and OpenSearch.

Exporters can be added later without coupling the core runtime to dashboard tools.

---

## RunId vs ExecutionId in MCP Tools

MCP tools must respect the identity split.

```text
RunId
= queue/controller submitted job lifecycle

ExecutionId
= durable DAG execution lifecycle
```

A run may be queued before an execution exists.

A queued run can be cancelled before DAG state is created.

Once execution starts, the local run status exposes an `ExecutionId`.

From that point, execution-level operations should use the execution control tools.

---

## Current MCP Dispatch Flow

Current local pool dispatch flow:

```text
run.submit_many_runs
    ↓
Shared Runtime Controller
    ↓
Admission selects mcp-runtime-1
    ↓
Remote/shared dispatcher resolves local runtime instance
    ↓
LocalAiSharedRuntimeInstance
    ↓
IAiRuntimeQueueControlPlane.EnqueueRunAsync
    ↓
AiRuntimePipelineBackgroundController local queue
    ↓
Workers execute DAG
```

Despite the name `RemoteAiSharedRunDispatcher`, in local pool mode the dispatch target is still resolved in-process.

This is expected for the current local control-plane demo.

The current local scale-out dispatch flow is:

```text
run.submit_many_runs
    ↓
Shared Runtime Controller
    ↓
DirectDispatch admission
    ↓
No local runtime capacity
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Redis scale-out request
    ↓
Scale-out watcher
    ↓
Local provider scale-out capability
    ↓
Local runtime scaler creates mcp-scaleout-runtime-1
    ↓
Scale-out request fulfilled
    ↓
Shared run requeued
    ↓
Shared queue pump dispatches to mcp-scaleout-runtime-1
    ↓
Local queue starts execution
    ↓
ExecutionId created
    ↓
Runtime run completed
```

The current provider-based hosting work also validates HTTP pooled runtime instance scenarios.

Current HTTP pooled dispatch flow:

```text
run.submit_many_runs / run.submit
    ↓
Shared Runtime Controller
    ↓
QueueFirst shared run
    ↓
Manual drain or background pump
    ↓
Dispatch-time admission
    ↓
AssignedRuntimeInstanceId = host-...:runtime-http-*
    ↓
HTTP Runtime Provider
    ↓
RuntimeInstanceOnly HTTP Host
    ↓
Local Runtime Instance Pool
    ↓
Selected runtime-http-* local queue
    ↓
Workers execute DAG
```

The next step is to continue hardening provider routing, provider capabilities, and remote transports.

---

## Provider-Based Dispatch Direction

Dispatch is moving toward a provider-based model.

The shared controller should not need to know whether a runtime instance is:

- local in-memory
- reachable through Redis command queue
- reachable through HTTP
- reachable through gRPC
- represented by a Kubernetes pod
- represented by another future provider

The runtime instance descriptor should declare how it can be contacted.

The provider router should resolve the correct provider.

Scale-out uses the same provider model.

`IAiRuntimeScaleOutProvider` extends `IAiRuntimeInstanceProvider`, which makes scale-out a provider capability rather than a separate routing system.

The scale-out provider selector resolves provider name from:

```text
request.ProviderHint
    -> AiRuntimeInstanceRegistrationOptions.ProviderName
    -> local
```

The local provider currently supports:

```text
provider.name = local
    -> LocalAiRuntimeInstanceProvider
    -> AiLocalRuntimeInstanceScaler
    -> local runtime instance created on demand
```

```text
Runtime descriptor
    provider.name = local
        ↓
Provider router
        ↓
Local provider
        ↓
Local runtime queue
```

Future example:

```text
Runtime descriptor
    provider.name = redis-command-queue
    provider.commandQueueKey = ai:runtime:mcp-runtime-1:commands
        ↓
Provider router
        ↓
Redis command queue provider
        ↓
Remote runtime pod
        ↓
Local runtime queue
```

---

## Kubernetes Direction

The MCP server prepares the runtime for Kubernetes but does not directly require Kubernetes.

In Kubernetes, likely process roles are:

```text
mcp-control-plane pod
    Role = ControlPlane
    Hosts MCP / API / dashboard adapters

runtime-instance pod 1
    Role = Runtime
    Owns local queue and workers

runtime-instance pod 2
    Role = Runtime
    Owns local queue and workers

runtime-instance pod 3
    Role = Runtime
    Owns local queue and workers
```

The control-plane pod reads Redis registry and capacity descriptors.

Runtime pods publish heartbeats and capacity.

Dispatch can later use:

- Redis command queues
- HTTP
- gRPC
- another provider transport

Kubernetes itself should mainly provide:

- pod lifecycle
- deployment scaling
- service discovery
- labels and metadata
- readiness and liveness
- scaling operations

Kubernetes does not need to replace runtime queues.

---

## Current Validated Behavior

Current MCP integration tests validate:

- MCP host startup
- control-plane role registration
- local runtime instance pool startup
- HTTP runtime instance provider flow
- HTTP pooled runtime child instance dispatch
- runtime instance role separation
- runtime capacity descriptor publication
- runtime worker capacity visibility
- Redis registry usage
- Redis capacity store usage
- Redis control-plane discovery store usage
- control-plane id resolver usage
- Redis admission reservation store usage
- shared run submission
- queue-first shared run submission
- assigned runtime dispatch
- shared queue background pump dispatch
- shared queue pump readiness gate
- manual queue drain with background pump disabled
- Redis scale-out request persistence
- scale-out watcher processing
- scale-out provider selector resolution
- local runtime scale-out from zero runtime capacity
- fulfilled scale-out shared run requeue
- scale-out requeued run dispatch through the shared queue pump
- scale-out runtime execution completion
- local queue enqueue
- local run status polling
- execution replay
- replay report retrieval
- observability ledger retrieval
- observability trace retrieval
- idempotent runtime unregister
- idempotent capacity descriptor cleanup
- idempotent local pool shutdown
- cleanup without late rediscovery dependency

Validated example:

```text
mcp-control-plane
    Role = ControlPlane

mcp-runtime-1
    Role = Runtime
    WorkerCount = 10
    ActiveWorkerCount = 0
    AvailableWorkerCount = 10
    MaxLocalWorkersPerExecution = 4
    MaxRunSlots = 5

mcp-runtime-2
    Role = Runtime
    WorkerCount = 10
    ActiveWorkerCount = 0
    AvailableWorkerCount = 10
    MaxLocalWorkersPerExecution = 4
    MaxRunSlots = 5

mcp-runtime-3
    Role = Runtime
    WorkerCount = 10
    ActiveWorkerCount = 0
    AvailableWorkerCount = 10
    MaxLocalWorkersPerExecution = 4
    MaxRunSlots = 5
```

---

## Current Redis Local Scale-Out Validation Evidence

The current MCP scale-out test suite validates a complete Redis-backed local scale-out flow.

Validated scale-out evidence:

```text
Initial ActiveLocalInstances = 0
Admission = RequestScaleOut
SharedRun.Status = ScaleOutRequested
ScaleOutRequest.Status = Fulfilled
ScaleOutRuntimeInstanceId = host-...:mcp-scaleout-runtime-1
ActiveLocalInstances = 1
SharedRun.Status = Dispatched
QueueStatus = Dispatched
LocalRunId = available
ExecutionId = available
RuntimeRunStatus = completed
```

This proves that MCP can:

- submit a run when no runtime capacity exists
- trigger admission-driven scale-out
- persist a Redis scale-out request
- process the request through the watcher
- resolve the local scale-out-capable provider
- create a local runtime instance dynamically
- requeue the fulfilled shared run
- dispatch it through the normal shared queue pump
- execute the DAG to completion
- expose the final runtime status through MCP runtime queue tools.

---

## Current HTTP Pooled Validation Evidence

The current MCP/provider test suite validates the production-oriented HTTP provider shape.

Validated heavy dispatch evidence:

```text
Runs = 50
StepsPerRun = 100
RuntimeInstances = runtime-http-1, runtime-http-2, runtime-http-3
RedisAiSharedRunStore = validated
RedisAiSharedQueue = validated
RedisAiRuntimeAdmissionReservationStore = validated
```

Validated replay evidence:

```text
Replay = Success
Report = Success
Ledger = Success
Trace = Available
ReplayValid = True
FingerprintMatches = True
IssueCount = 0
```

This proves that MCP can:

- submit shared runs
- queue them globally
- drain them manually or through the background pump
- dispatch through the HTTP runtime provider
- assign work to pooled child runtime instances
- observe runtime status and execution ids
- replay completed executions
- inspect ledger and trace output.


## Current Limitations

The current MCP server/control-plane adapter does not yet provide:

- cross-pod dispatch through Redis command queues
- gRPC runtime dispatch
- Kubernetes pod/deployment scale-out implementation
- distributed shared controller leader election
- production dashboard UI
- OpenTelemetry exporter polish
- production security model for MCP access
- tenant-aware operational authorization
- full provider capability negotiation beyond the current local scale-out provider
- Redis/Lua slot reservation refinement for multi-control-plane dispatch safety
- production-grade admission reservation hardening

---

## Next Steps

The next implementation step is to continue hardening provider-based runtime instance administration.

Current first layer direction:

```text
IAiRuntimeInstanceProvider
IAiRuntimeInstanceDispatchProvider
IAiRuntimeInstanceStatusProvider
IAiRuntimeInstanceControlProvider
IAiRuntimeInstanceProviderRouter
LocalAiRuntimeInstanceProvider
HTTP runtime instance provider foundation
HTTP pooled runtime instance hosting
Redis control-plane discovery store
ControlPlaneIdResolver
Redis runtime instance registry
Redis runtime instance capacity store
Redis admission reservation store
```

The local provider preserves existing behavior:

```text
Local provider
    uses IAiSharedRuntimeInstanceRegistry
    dispatches to LocalAiSharedRuntimeInstance
    enqueues into existing local runtime queue
```

Next provider targets:

```text
Redis command queue provider
gRPC provider
Kubernetes metadata provider
Kubernetes scaling provider
```

The local scale-out provider is now validated as the first concrete scale-out capability.

The next Kubernetes scaling provider should reuse the same lifecycle:

```text
RequestScaleOut
  -> persist request
  -> watcher observes request
  -> provider selector resolves kubernetes provider
  -> Kubernetes scaler creates/expands runtime capacity
  -> request fulfilled
  -> shared run requeued
  -> pump dispatches to visible runtime capacity
```

Future providers should be added without changing the shared controller architecture.

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| MCP Server | Exposes operational tools over runtime control-plane abstractions. |
| MCP Host | Chooses process mode and registers services. |
| Shared Runtime Controller | Coordinates admission, shared run persistence, shared queue, dispatch, scale-out requests, and rejection. |
| Admission Controller | Decides what should happen to a submitted run. |
| Runtime Instance Registry | Tracks visible runtime instances and roles. |
| Runtime Capacity Store | Tracks real runtime capacity descriptors. |
| Control-Plane Discovery Store | Publishes and reads the MCP logical control-plane identity used by runtime-only hosts. |
| Control-Plane Id Resolver | Resolves the active logical control-plane id for runtime registration and capacity publication. |
| Admission Reservation Store | Protects selected runtime capacity during dispatch in Redis-backed scenarios. |
| Shared Queue Pump | Claims shared queue work and triggers dispatch-time admission. |
| Shared Queue Dispatcher | Re-admits queued runs, dispatches to selected runtime instances, and updates shared queue/run state. |
| Provider Router | Resolves how to contact a selected runtime instance. |
| Provider | Performs transport-specific operations such as dispatch, status, control, capacity, or scaling. |
| Scale-Out Request Store | Persists scale-out requests and their pending/observed/fulfilled/rejected lifecycle. |
| Scale-Out Watcher | Observes pending scale-out requests and delegates capacity creation to a scale-out-capable provider. |
| Scale-Out Provider Selector | Resolves the provider capable of handling a scale-out request using the existing runtime provider router. |
| Fulfilled Run Requeue Service | Requeues a shared run after scale-out has created capacity so the normal pump can dispatch it. |
| Runtime Queue Control Plane | Exposes one runtime instance local queue. |
| Runtime Instance Snapshot | Exposes role, heartbeat, queue pressure, run slots, and worker capacity. |
| Local Runtime Queue | Owned by one runtime instance and unchanged by shared queue. |
| Workers | Execute DAG steps after local queue starts an execution. |
| Replay Control Plane | Exposes replay and audit operations. |
| Observability | Exposes ledger, trace, metrics, and diagnostic information. |

---

## Related Documents

- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Distributed Execution](distributed-execution.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability and Tracing](observability-tracing.md)
- [Runtime Metrics](runtime-metrics.md)
- [Testing Strategy](testing-strategy.md)

