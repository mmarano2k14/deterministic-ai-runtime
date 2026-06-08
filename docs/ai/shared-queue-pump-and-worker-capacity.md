# Shared Queue Pump and Worker Capacity

Status: Implemented foundation / actively validated.

This document describes the shared queue pump, queue-first submit mode, dispatch-time admission, pump identity separation, runtime worker-capacity visibility, and `MaxLocalWorkersPerExecution`.

It complements:

- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Distributed Execution](distributed-execution.md)
- [Testing Strategy](testing-strategy.md)

---

## Purpose

The shared queue pump is the bridge between globally queued shared runs and runtime-instance local queues.

It allows the runtime control plane to:

- submit work globally
- keep work queued until a runtime instance is selected
- drain the shared queue manually or automatically
- select the dispatch target at drain time
- preserve no-double-dispatch guarantees
- keep local runtime queues unchanged
- expose runtime instance capacity and worker pressure
- support local, HTTP, and future Kubernetes-style runtime instance hosting

The core principle is:

```text
Shared queue coordinates global work.
Local runtime queue owns executable work.
DAG engine owns durable execution.
```

The shared queue pump must not replace the runtime engine.

It must not execute DAG steps.

It must not mutate DAG state directly.

It only claims shared queue items and dispatches shared runs into selected runtime instance local queues.

---

## Core Concepts

### SharedRunId

`SharedRunId` identifies a shared control-plane run.

It exists before any local runtime run exists.

A shared run can be:

```text
Submitted
QueuedGlobally
Dispatched
ScaleOutRequested
Rejected
Cancelled
Failed
```

### RunId

`RunId` identifies a local runtime queue run.

It belongs to one runtime instance local queue.

A `RunId` exists only after a shared run has been dispatched into a selected runtime instance.

### ExecutionId

`ExecutionId` identifies the durable DAG execution.

It exists only after the local runtime background controller starts execution.

### Identity Progression

```text
SharedRunId
    exists at shared controller submission

RunId
    exists after dispatch into one runtime instance local queue

ExecutionId
    exists after the runtime instance starts the DAG execution
```

The three identities must remain separate.

---

## Queue Layers

The runtime has two queue layers.

```text
Shared / global queue
    - shared control-plane queue
    - stores shared queue items
    - coordinates dispatch ownership
    - consumed by pump/manual drain

Local runtime queue
    - owned by one runtime instance
    - stores executable local runs
    - creates RunId
    - starts ExecutionId
```

Flow:

```text
Shared Runtime Controller
    ↓
Shared Run Store
    ↓
Shared Queue
    ↓
Shared Queue Pump / Manual Drain
    ↓
Dispatch-Time Admission
    ↓
Shared Run Dispatcher
    ↓
Selected Runtime Instance Local Queue
    ↓
Background Controller
    ↓
DAG Execution Engine
```

---

## Queue-First Submit Mode

Queue-first mode forces submitted shared runs to enter the global shared queue first.

It does not immediately dispatch the run to a runtime instance.

```text
SubmitRunAsync
    ↓
Create shared run record
    ↓
Enqueue shared queue item
    ↓
SharedRun.Status = QueuedGlobally
    ↓
SharedQueueItem.Status = Pending
```

At this stage:

```text
LocalRunId = null
ExecutionId = null
DAG state = not created
```

This is useful when the system should persist and observe queued work before runtime assignment.

Use queue-first mode for:

- Kubernetes-style work distribution
- MCP demos
- manual operator-controlled drain
- background pump validation
- queue persistence validation
- HTTP/runtime-provider dispatch tests
- no-double-dispatch shared queue tests

Configuration:

```text
AiSharedRuntimeController:SubmitMode = QueueFirst
```

---

## Direct Dispatch Mode

Direct dispatch mode keeps the classic behavior.

Admission can immediately select a runtime instance and dispatch without first waiting in the global queue.

```text
SubmitRunAsync
    ↓
Admission
    ↓
AssignToInstance
    ↓
Dispatch to runtime instance
    ↓
LocalRunId created
```

Use direct dispatch when:

- immediate scheduling is desired
- the runtime instance is available at submit time
- the caller does not need to observe a globally queued phase
- a single local runtime process is enough

---

## Shared Queue Pump

The shared queue pump executes a controlled dispatch cycle.

It repeatedly asks the shared queue dispatcher to dispatch pending queue items until one of the following happens:

- maximum dispatch count is reached
- no pending item is available
- dispatch fails and options require stopping
- cancellation is requested

The pump is not a background service by itself.

It can be called by:

- MCP tool
- API endpoint
- CLI command
- hosted background service
- runtime instance loop
- integration test
- future Kubernetes control-plane process

Cycle shape:

```text
AiSharedQueuePump.PumpOnceAsync
    ↓
AiSharedQueueDispatcher.DispatchNextAsync
    ↓
Claim pending shared queue item
    ↓
Load shared run
    ↓
Re-admit at dispatch time
    ↓
Dispatch to selected runtime instance
    ↓
Mark queue item dispatched
    ↓
Mark shared run dispatched
```

---

## Manual Drain

Manual drain is an explicit pump operation.

It is useful when the hosted background pump should not run automatically.

Recommended controlled-drain configuration:

```text
AiSharedQueuePump:Enabled = true
AiMcpHost:EnableSharedQueuePump = false
AiSharedQueueBackgroundService:Enabled = false
```

Meaning:

```text
Manual drain is allowed.
Background pump is disabled.
Queued work remains pending until manually drained.
```

Manual drain flow:

```text
Submit queue-first run
    ↓
SharedRun.Status = QueuedGlobally
    ↓
SharedQueueItem.Status = Pending
    ↓
Wait / inspect / validate
    ↓
Manual queue.drain
    ↓
Dispatch-time admission
    ↓
Runtime instance selected
    ↓
Local queue receives run
    ↓
Execution starts
```

This is validated for both local and HTTP runtime instance scenarios.

---

## Background Shared Queue Service

The background shared queue service runs the pump continuously.

```text
AiSharedQueueBackgroundService
    ↓
IAiSharedQueuePump.PumpOnceAsync
```

The hosted service owns scheduling delay and lifecycle.

The pump owns one dispatch cycle.

The dispatcher owns claim, admission, dispatch, and state updates.

```text
Background service
    = loop / delay / hosted lifecycle

Pump
    = dispatch cycle

Dispatcher
    = claim + admission + dispatch + state update
```

This separation keeps the pump usable outside hosted service scenarios.

---

## Pump Identity vs Assigned Runtime Identity

The pump request has explicit pump identity:

```text
PumpRuntimeInstanceId
PumpWorkerId
```

These fields identify who is executing the pump cycle.

They do not necessarily identify who receives the run.

```text
PumpRuntimeInstanceId
    = runtime instance executing the pump cycle

AssignedRuntimeInstanceId
    = runtime instance selected by admission for dispatch
```

This distinction is important.

A control-plane process may drain the queue and dispatch to a remote runtime instance.

A runtime instance may drain work and admission may still select another instance.

A future Kubernetes control-plane pod may drain the queue and dispatch to runtime pods.

Tests that expect pump-local dispatch must explicitly configure admission so:

```text
AssignedRuntimeInstanceId = PumpRuntimeInstanceId
```

Production code should not assume this equality.

---

## Dispatch-Time Admission

Shared queue dispatch now re-evaluates admission at drain time.

This is intentional.

At submit time, the run may have been queued.

At drain time, the system may have new capacity information.

Dispatch-time admission allows the selected runtime instance to be based on the latest visible capacity.

Flow:

```text
Pending shared queue item
    ↓
Pump claims item
    ↓
Dispatcher loads shared run
    ↓
Admission is called
    ↓
Admission returns AssignToInstance
    ↓
AssignedRuntimeInstanceId selected
    ↓
Dispatcher sends run to selected runtime instance
```

Benefits:

- runtime assignment can use current capacity
- queue-first submit remains stable
- pump identity is decoupled from dispatch target
- local and HTTP provider paths use the same shared queue model
- future Kubernetes control-plane/runtime-pod split is supported

---

## Dispatch Failure and Requeue

A shared queue item must only be marked dispatched after runtime dispatch succeeds.

If dispatch fails, the item should be requeued according to policy.

Failure path:

```text
Claim queue item
    ↓
Load shared run
    ↓
Admission selects target
    ↓
Runtime dispatch fails
    ↓
Queue item returns to Pending
    ↓
SharedRun remains QueuedGlobally
    ↓
LocalRunId remains null
    ↓
ExecutionId remains null
```

Important guarantees:

```text
A failed dispatch must not mark the queue item Dispatched.

A failed dispatch must not mark the shared run Dispatched.

A failed dispatch must not create a fake ExecutionId.

A missing shared run must requeue or fail safely without corrupting queue state.
```

---

## No-Double-Dispatch Guarantee

The shared queue protects pending work with atomic claim semantics.

Only one dispatcher should be able to claim a pending item.

Redis shared queue implementation uses atomic transitions to protect:

- enqueue
- claim-next
- mark-dispatched
- requeue
- cancel

Expected behavior:

```text
Multiple pumps running concurrently
    ↓
same pending shared queue
    ↓
each item claimed once
    ↓
no duplicate dispatch for same SharedRunId
```

No-double-dispatch must hold even when many runtime instances drain the same shared queue.

---

## Runtime Instance Provider Dispatch

After admission selects an assigned runtime instance, dispatch should go through a runtime provider path.

Current provider-oriented foundations include:

- local runtime instance provider
- HTTP runtime provider foundation
- runtime instance provider metadata
- runtime instance registry visibility
- runtime capacity descriptors

Provider principle:

```text
Admission decides WHO receives the run.
Provider router decides HOW to contact that runtime instance.
Provider dispatches into the local runtime queue.
```

Providers must not execute DAG steps directly.

Providers must not mutate DAG state.

Providers must not bypass local runtime queues.

Correct flow:

```text
AssignedRuntimeInstanceId
    ↓
Descriptor / capacity lookup
    ↓
Provider metadata
    ↓
Provider route
    ↓
Target runtime local queue
    ↓
RunId
    ↓
ExecutionId
```

---

## Runtime Worker Capacity Visibility

Runtime worker capacity is now visible to the control plane.

The visibility path is:

```text
AiRuntimePipelineBackgroundController
    ↓
AiRuntimePipelineQueueState
    ↓
AiRuntimeInstanceRegistrationHostedService
    ↓
AiRuntimeInstanceCapacityDescriptor
    ↓
IAiRuntimeInstanceRegistry
    ↓
RuntimeInstanceEntry
    ↓
AiRuntimeInstanceSnapshot
    ↓
MCP / control-plane list instances
```

Visible fields include:

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
SnapshotAtUtc
```

This allows operators and tests to see whether an instance is:

- idle
- busy
- saturated
- queue-limited
- worker-limited
- paused
- available for new runs

---

## Worker-Aware CanAcceptRun

`CanAcceptRun` is a combined readiness signal.

It should reflect both run capacity and worker capacity.

Conceptual rule:

```text
CanAcceptRun = queue not paused
            + queue capacity available
            + run slot available
            + worker available
```

If all workers are reserved, a runtime instance should report:

```text
AvailableWorkerCount = 0
CanAcceptRun = false
```

Even if the local queue itself still has room.

This makes admission and dashboards more accurate.

---

## MaxLocalWorkersPerExecution

`MaxLocalWorkersPerExecution` limits how many workers from one runtime instance may participate in one execution.

Example:

```text
Distributed.WorkerCount = 30
MaxLocalWorkersPerExecution = 4
```

Result:

```text
Runtime instance owns 30 workers.
One execution can reserve at most 4 workers.
The remaining workers can stay available for other executions.
```

Effective worker count per execution:

```text
min(
  Distributed.WorkerCount,
  MaxLocalWorkersPerExecution,
  AvailableWorkerCount
)
```

If no workers are currently available, the local background controller waits for worker capacity instead of immediately failing the run.

This makes worker capacity a real scheduling constraint.

---

## Local Worker Capacity vs Execution Assistance

`MaxLocalWorkersPerExecution` is local to one runtime instance.

Execution assistance is cross-instance.

They solve different problems.

```text
Local worker capacity
    limits workers from one runtime instance
    for one execution

Execution assistance
    allows helper runtime instances
    to assist an existing execution
    through assistance leases
```

They should not be merged.

A runtime instance may limit local worker usage while still allowing other runtime instances to assist under controlled leases.

---

## Ledger and Assistance Metadata

Runtime run ledger metadata should include:

```text
max.local.workers.per.execution
effective.worker.count.per.execution
```

Execution assistance candidate metadata should use the effective worker count instead of raw distributed worker count.

This prevents over-reporting capacity when `MaxLocalWorkersPerExecution` caps actual local worker participation.

---

## MCP Visibility

MCP runtime instance tools should expose worker capacity fields from runtime instance snapshots.

Useful MCP list output should include:

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
AvailableRunSlots
IsQueuePaused
CanAcceptRun
LastHeartbeatAtUtc
```

This makes MCP useful as a temporary operational dashboard before a full UI exists.

---

## Testing Strategy

The following behavior is validated or should remain covered by tests:

### Queue-First and Manual Drain

```text
Submit queue-first run
    -> SharedRun.Status = QueuedGlobally
    -> SharedQueueItem.Status = Pending
    -> no LocalRunId
    -> no ExecutionId

Manual drain
    -> dispatch succeeds
    -> SharedRun.Status = Dispatched
    -> SharedQueueItem.Status = Dispatched
    -> LocalRunId exists
    -> ExecutionId eventually exists
```

### Background Pump Disabled

```text
AiSharedQueuePump:Enabled = true
AiMcpHost:EnableSharedQueuePump = false
AiSharedQueueBackgroundService:Enabled = false
```

Expected:

```text
Queue-first submitted runs remain queued.
No automatic background dispatch occurs.
Manual drain can still dispatch.
```

### Local and HTTP Runtime Providers

Tests should prove:

```text
Queue-first + manual drain + local provider
    -> dispatch
    -> completion

Queue-first + manual drain + HTTP provider
    -> dispatch
    -> completion
```

### Dispatch Failure

Tests should prove:

```text
Dispatch failure
    -> result.Success = false
    -> queue item returns Pending
    -> shared run remains QueuedGlobally
    -> LocalRunId remains null
    -> ExecutionId remains null
```

### Multi-Instance No-Double-Dispatch

Tests should prove:

```text
Multiple pumps
    -> same shared queue
    -> each shared run dispatched once
    -> no duplicate SharedRunId dispatch
```

### Dispatch-Time Admission

Tests should prove:

```text
PumpRuntimeInstanceId does not automatically become AssignedRuntimeInstanceId.

AssignedRuntimeInstanceId comes from admission.

Tests expecting pump-local dispatch explicitly inject admission target.
```

### Worker Capacity

Tests should prove:

```text
WorkerCount is visible.
ActiveWorkerCount is visible.
AvailableWorkerCount is visible.
MaxLocalWorkersPerExecution is visible.
CanAcceptRun becomes false when workers are saturated.
MaxLocalWorkersPerExecution caps worker participation.
```

---

## Current Limitations

Implemented:

```text
queue-first submit mode
shared queue pump
manual drain
background pump
dispatch-time admission
pump identity / assigned runtime identity separation
local provider foundation
HTTP provider foundation
runtime worker capacity visibility
MaxLocalWorkersPerExecution
worker-aware CanAcceptRun
dispatch failure requeue
no-double-dispatch shared queue behavior
```

Not implemented yet:

```text
atomic admission capacity reservation
Redis/Lua runtime slot reservation
Redis command queue provider
gRPC runtime provider
Kubernetes provider
production autoscaling
production dashboard UI
full provider capability negotiation
```

---

## Admission Reservation Future Work

Current admission uses visible capacity snapshots.

That is enough for controlled tests and basic runtime dispatch.

It is not enough for perfect production scheduling under multiple fast control-plane dispatchers.

Problem:

```text
Dispatcher A reads runtime-1 available.
Dispatcher B reads runtime-1 available.
Both choose runtime-1 before heartbeat updates.
```

Future solution:

```text
Admission selects candidate
    ↓
TryReserveCapacity(runtimeInstanceId, sharedRunId, ttl)
    ↓
If reservation succeeds:
        dispatch
    ↓
If dispatch fails:
        release reservation
    ↓
If reservation expires:
        capacity becomes available again
```

This should likely be implemented with Redis Lua.

Reservation should protect:

- run slots
- possibly worker capacity
- runtime instance availability
- dispatch ownership

---

## Kubernetes Direction

The current design prepares for Kubernetes without requiring Kubernetes in the core runtime.

Future topology:

```text
mcp-control-plane pod
    Role = ControlPlane
    hosts MCP/API/dashboard adapters
    drains or observes shared queue

runtime-instance pod 1
    Role = Runtime
    owns local queue
    owns workers
    publishes capacity

runtime-instance pod 2
    Role = Runtime
    owns local queue
    owns workers
    publishes capacity
```

Shared queue remains global.

Local runtime queue remains inside each runtime pod.

Runtime pods publish capacity.

Control plane dispatches through provider transports.

Possible provider transports:

```text
local
http
grpc
redis-command-queue
kubernetes-aware provider
```

Kubernetes should provide:

- pod lifecycle
- labels
- service discovery
- readiness/liveness
- scaling

Kubernetes should not replace runtime queues or DAG execution ownership.

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Shared Runtime Controller | Creates shared runs, applies submit mode, queues globally, dispatches directly when admitted. |
| Shared Run Store | Persists shared run records and shared run status. |
| Shared Queue | Stores pending global queue items and protects claim ownership. |
| Shared Queue Pump | Executes one or more dispatch cycles. |
| Shared Queue Dispatcher | Claims shared queue items, re-admits runs, dispatches selected targets, updates queue/run state. |
| Admission Controller | Selects whether to assign, queue globally, request scale-out, or reject. |
| Runtime Provider | Delivers dispatched work to the selected runtime instance local queue. |
| Runtime Queue Control Plane | Exposes one runtime instance local queue. |
| Background Controller | Owns local RunId lifecycle and starts DAG executions. |
| Worker Capacity Model | Tracks local workers, active workers, available workers, and per-execution caps. |
| DAG Engine | Owns durable ExecutionId execution and step state transitions. |
| MCP Server | Exposes shared queue, runtime instance, replay, control, and observability operations through tools. |

---

## Summary

The shared queue pump and worker capacity model adds the missing operational bridge between shared control-plane scheduling and local runtime execution.

It provides:

- queue-first submission
- manual drain
- background pump
- dispatch-time admission
- pump identity separation
- provider-friendly runtime dispatch
- no-double-dispatch shared queue behavior
- runtime worker capacity visibility
- worker-aware `CanAcceptRun`
- local worker cap per execution

The result is a cleaner path toward Kubernetes-style runtime hosting:

```text
Shared queue coordinates work globally.
Admission selects the target.
Provider dispatches to the runtime instance.
Local queue owns RunId.
DAG engine owns ExecutionId.
Workers execute deterministically.
```
