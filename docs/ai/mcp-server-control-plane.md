# MCP Server as Runtime Control Plane

Status: Active foundation.

This document describes how the **MCP Server** acts as a concrete runtime control-plane adapter for the Deterministic AI Runtime.

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
- drain shared queues
- list runtime instances
- inspect runtime capacity
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

---

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

The control-plane host can submit runs, list instances, request dispatch, and inspect replay/observability, but it should not be selected as a runtime execution target.

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

The local pool is a simulation of what future Kubernetes runtime pods will provide as separate processes.

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

The dispatch layer is currently local/in-memory for local pool mode.

The next architecture step is provider-based dispatch.

---

## Runtime Capacity Descriptors

Runtime instances publish capacity descriptors.

Capacity descriptors allow the control plane to know:

- which runtime instances exist
- which are ready
- which can accept runs
- how many run slots are available
- how many workers exist
- how much queue pressure exists
- whether a queue is paused
- when the last heartbeat was published

Example:

```text
mcp-runtime-1
    WorkerCount = 10
    MaxRunSlots = 5
    AvailableRunSlots = 5
    CanAcceptRun = true
```

Capacity descriptors are the foundation for capacity-aware admission and future provider-based dispatch.

---

## MCP Tool Groups

The MCP server exposes runtime operations through focused tool groups.

Tool group names may evolve, but the current intent is stable.

---

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

The next step is to make runtime dispatch provider-based.

---

## Provider-Based Dispatch Direction

Dispatch should become provider-based.

The shared controller should not need to know whether a runtime instance is:

- local in-memory
- reachable through Redis command queue
- reachable through HTTP
- reachable through gRPC
- represented by a Kubernetes pod
- represented by another future provider

The runtime instance descriptor should declare how it can be contacted.

The provider router should resolve the correct provider.

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
- runtime instance role separation
- runtime capacity descriptor publication
- Redis registry usage
- Redis capacity store usage
- shared run submission
- assigned runtime dispatch
- local queue enqueue
- local run status polling
- execution replay
- replay report retrieval
- observability ledger retrieval
- observability trace retrieval
- idempotent runtime unregister
- idempotent local pool shutdown

Validated example:

```text
mcp-control-plane
    Role = ControlPlane

mcp-runtime-1
    Role = Runtime
    WorkerCount = 10
    MaxRunSlots = 5

mcp-runtime-2
    Role = Runtime
    WorkerCount = 10
    MaxRunSlots = 5

mcp-runtime-3
    Role = Runtime
    WorkerCount = 10
    MaxRunSlots = 5
```

---

## Current Limitations

The current MCP server/control-plane adapter does not yet provide:

- provider-based runtime dispatch
- cross-pod dispatch through Redis command queues
- HTTP/gRPC runtime dispatch
- Kubernetes scale-out implementation
- distributed shared controller leader election
- production dashboard UI
- OpenTelemetry exporter polish
- production security model for MCP access
- tenant-aware operational authorization
- provider capability negotiation
- Redis/Lua slot reservation for multi-control-plane dispatch safety

---

## Next Steps

The next implementation step is provider-based runtime instance administration.

Planned first layer:

```text
AiRuntimeInstanceProviderAttribute
IAiRuntimeInstanceProvider
IAiRuntimeInstanceDispatchProvider
IAiRuntimeInstanceStatusProvider
IAiRuntimeInstanceControlProvider
IAiRuntimeInstanceProviderRouter
LocalAiRuntimeInstanceProvider
```

The first provider should preserve existing behavior:

```text
Local provider
    uses IAiSharedRuntimeInstanceRegistry
    dispatches to LocalAiSharedRuntimeInstance
    enqueues into existing local runtime queue
```

After that, future providers can be added without changing the shared controller architecture.

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
| Provider Router | Resolves how to contact a selected runtime instance. |
| Provider | Performs transport-specific operations such as dispatch, status, control, capacity, or scaling. |
| Runtime Queue Control Plane | Exposes one runtime instance local queue. |
| Local Runtime Queue | Owned by one runtime instance and unchanged by shared queue. |
| Workers | Execute DAG steps after local queue starts an execution. |
| Replay Control Plane | Exposes replay and audit operations. |
| Observability | Exposes ledger, trace, metrics, and diagnostic information. |

---

## Related Documents

- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Distributed Execution](distributed-execution.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability and Tracing](observability-tracing.md)
- [Runtime Metrics](runtime-metrics.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction for MCP server control-plane architecture.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
