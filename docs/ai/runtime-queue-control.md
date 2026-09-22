# Runtime Queue Control

Status: Documentation split in progress.

This document describes the **RunId-level background controller queue control** used by the Deterministic AI Runtime.

It also clarifies how local runtime queue control relates to the shared/global queue, queue-first submit mode, shared queue pump/manual drain, and runtime worker-capacity visibility.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

The runtime is not only responsible for executing DAG workflows.

It also needs a controller layer capable of managing work before and while runtime executions are created.

Production systems often need to:

- enqueue multiple pipeline runs
- pause the local runtime queue
- resume the local runtime queue
- cancel queued work before execution starts
- cancel a running controller run
- add new work while the controller is already active
- accept work while the queue is paused
- complete waiting callers when queued work is cancelled
- expose queue capacity to the control plane
- expose worker capacity to the control plane
- preserve strict separation between queue lifecycle and execution lifecycle

This is handled by the runtime queue control layer.

Queue control operates at the `RunId` level.

Execution control operates at the `ExecutionId` level.

---

## Two-Layer Control Model

The runtime separates control into two layers.

```text
Layer 1: Controller / Queue / Run Control
        RunId
        background controller queue
        queued runs
        running controller runs
        hot enqueue
        queue pause / resume
        queued run cancellation
        running run cancellation bridge

Layer 2: Execution Control
        ExecutionId
        DAG execution control state
        pause / resume
        cancellation
        waiting for human input
        submit human input
        claim blocking
        finalization override
```

This distinction prevents controller lifecycle state from being mixed with durable DAG execution state.

---

## RunId vs ExecutionId

The runtime separates two identities:

```text
RunId
= background controller / queued job lifecycle id

ExecutionId
= authoritative runtime DAG execution id
```

This separation is critical.

A queued run can exist before an execution exists.

A controller run may be cancelled before any DAG state is created.

Once execution starts, the controller receives or tracks the created `ExecutionId`.

The `ExecutionId` then becomes the durable runtime execution identity.

---

## Local Runtime Queue vs Shared Queue

The runtime now has two queue layers.

```text
Shared / global queue
= control-plane queue for shared runs
= SharedRunId / shared queue item lifecycle
= consumed by shared queue pump or manual drain

Local runtime queue
= runtime-instance queue for executable pipeline runs
= RunId lifecycle
= owned by IAiRuntimePipelineBackgroundController
```

The shared queue does not execute DAG steps.

The local runtime queue does not coordinate global dispatch ownership.

The flow is:

```text
QueueFirst shared submit
    ↓
SharedRun.Status = QueuedGlobally
    ↓
Shared queue item = Pending
    ↓
Shared queue pump / manual drain
    ↓
Dispatch-time admission selects runtime instance
    ↓
IAiSharedRunDispatcher sends work to selected runtime instance
    ↓
Selected runtime instance enqueues local runtime RunId
    ↓
Local background controller creates ExecutionId
    ↓
DAG workers execute the durable execution
```

This separation is important for MCP, HTTP runtime providers, Kubernetes-style runtime instances, and no-double-dispatch guarantees.

---

## Why the Separation Matters

Without separating `RunId` and `ExecutionId`, the runtime risks mixing:

- queue lifecycle
- controller lifecycle
- DAG execution lifecycle
- replay identity
- snapshot identity
- cancellation behavior
- cleanup behavior
- completion task behavior

The runtime must know whether an operation targets:

```text
A queued controller job
        or
A durable runtime execution
```

This is why:

```text
RunId != ExecutionId
```

The two namespaces must not overlap.

---

## Queue-First Submit and Manual Drain

Queue-first mode affects the shared controller before a local `RunId` exists.

In queue-first mode:

```text
SubmitRunAsync
    ↓
SharedRun is persisted
    ↓
SharedRun.Status = QueuedGlobally
    ↓
SharedQueueItem.Status = Pending
```

At this point:

```text
No local RunId exists yet.
No ExecutionId exists yet.
No DAG state exists yet.
```

A local `RunId` appears only after a shared queue pump or manual drain dispatches the shared run to a selected runtime instance.

### Durable placement across queue-first handoff

If queue-first submission carries required runtime placement, the placement must survive the durable shared-run handoff. The shared run persists the placement and the dispatcher restores it before dispatch-time admission.

```text
QueueFirst + required placement
    ↓
AiSharedRunRecord persists placement
    ↓
shared queue dispatcher restores placement
    ↓
exact runtime dispatch
```

Recovery redispatch is different. If the original placement points to failed runtime or host capacity, recovery clears that placement and reuses the durable `SharedRunId` so admission can select healthy replacement capacity.

This is why placement is durable initial-dispatch intent without becoming a permanent recovery pin.

Manual drain can be enabled without enabling the background pump.

Recommended controlled-drain configuration:

```text
AiSharedQueuePump:Enabled = true
AiMcpHost:EnableSharedQueuePump = false
AiSharedQueueBackgroundService:Enabled = false
```

This means:

```text
Manual drain works.
Automatic background pump does not run.
Queued shared runs remain queued until an operator or test drains them.
```

This is useful for MCP demos, controlled test scenarios, and proving that the demo path does not depend on the automatic background pump.

---

## Queue Control Scope

Queue control applies to background controller state.

It manages:

- queued local runtime runs
- currently running controller jobs
- queue pause and resume
- queued run cancellation
- running run cancellation bridge
- hot enqueue behavior
- completion task behavior
- handle status updates
- queue shutdown behavior
- local queue visibility
- local run slot visibility
- local worker capacity visibility

It does not directly mutate DAG step state.

Once a runtime execution exists, execution-level control must be delegated to the execution control service.

---

## Runtime Queue State and Capacity Visibility

The local runtime queue exposes a visibility snapshot through `AiRuntimePipelineQueueState`.

The snapshot is intended for:

- control plane APIs
- MCP tools
- dashboards
- diagnostics
- admission visibility
- future Kubernetes autoscaling

Important fields include:

```text
RuntimeInstanceId
IsPaused
QueuedRunCount
RunningRunCount
ActiveRunCount
QueueCapacity
MaxConcurrentRuns
AvailableRunSlots
WorkerCount
ActiveWorkerCount
AvailableWorkerCount
MaxLocalWorkersPerExecution
CanAcceptRun
SnapshotAtUtc
```

`CanAcceptRun` is now worker-aware.

A runtime instance can accept a new local run only when it has queue capacity, available run slots, and available worker capacity.

```text
CanAcceptRun = queue not paused
            + queue capacity available
            + run slot available
            + worker available
```

This visibility is published through runtime instance registration and projected into `AiRuntimeInstanceSnapshot`.

---

## Max Local Workers Per Execution

`MaxLocalWorkersPerExecution` limits how many local workers from one runtime instance may work on a single execution.

It belongs to `AiRuntimePipelineBackgroundControllerOptions`.

Example:

```text
Distributed.WorkerCount = 10
MaxLocalWorkersPerExecution = 4
```

Result:

```text
The runtime instance owns 10 workers.
One execution can reserve up to 4 workers.
Remaining workers can stay available for other executions.
```

The effective worker count per execution is resolved from:

```text
min(
  Distributed.WorkerCount,
  MaxLocalWorkersPerExecution,
  AvailableWorkerCount
)
```

If no worker capacity is available, the controller waits for worker capacity instead of immediately failing the run.

This setting is local to one runtime instance. It is not the same as cross-instance execution assistance.

---

## Background Controller

The background controller accepts runtime pipeline requests and returns a run handle.

A submitted request receives a `RunId`.

After execution is created, the handle also receives an `ExecutionId`.

Example:

```csharp
var controller = serviceProvider
    .GetRequiredService<IAiRuntimePipelineBackgroundController>();

await controller.StartAsync();

var handle = await controller.EnqueueAsync(
    new AiRuntimePipelineRunRequest
    {
        PipelineName = pipelineName,
        PipelineDefinition = pipelineDefinition,
        Input = new
        {
            candidateId = "candidate-001",
            source = "background-controller"
        }
    });

var final = await handle.Completion;
```

The handle tracks:

```text
handle.RunId
handle.ExecutionId
handle.Status
handle.Completion
```

---

## Queue Pause

Queue pause prevents new queued runs from starting.

It does not pause already-running executions.

The flow is:

```text
PauseQueueAsync
        ↓
queue state = paused
        ↓
queued runs remain Queued
        ↓
no ExecutionId is created for paused queued runs
        ↓
already-running executions continue
```

This allows operators to temporarily stop new work from starting without interrupting active executions.

Queue pause is therefore different from execution pause.

```text
PauseQueueAsync
= stop starting queued runs

PauseExecutionAsync
= stop new claims for an existing ExecutionId
```

---

## Queue Resume

Queue resume allows queued runs to start again.

The flow is:

```text
ResumeQueueAsync
        ↓
queue state = active
        ↓
queued runs become eligible to start
        ↓
controller creates runtime executions
        ↓
ExecutionId is assigned per started run
```

Queue resume does not replay old work manually.

It simply allows the background controller to continue draining the queue.

---

## Queued Run Cancellation

A queued run can be cancelled before execution creation.

This is a controller-level cancellation.

The flow is:

```text
Run is queued
        ↓
CancelQueuedRunAsync(runId)
        ↓
run status = Cancelled
        ↓
completion returns Cancelled
        ↓
ExecutionId remains empty
        ↓
no DAG state is created
```

This is safe because the runtime execution does not exist yet.

No execution control state is required.

This behavior is important because it prevents fake or empty DAG executions from being created only to represent cancelled queue work.

---

## Unknown Queued Run Cancellation

The queue controller should handle unknown queued run cancellation safely.

If a caller requests cancellation for a `RunId` that is not known as a queued run, the operation should not corrupt queue state.

Depending on the API contract, the result may be:

```text
false
```

or a safe no-op result.

The important guarantee is:

```text
Unknown queued run cancellation must not create execution state.
```

---

## Running Run Cancellation

A running controller run has both:

```text
RunId
ExecutionId
```

When a running run is cancelled through the controller, the controller must bridge to execution control.

The flow is:

```text
CancelRunAsync(runId)
        ↓
find running run by RunId
        ↓
read ExecutionId from handle
        ↓
IAiExecutionControlService.CancelExecutionAsync(executionId)
        ↓
execution control handles deterministic cancellation
        ↓
final execution status = Cancelled
```

This avoids duplicating cancellation logic in the controller.

The controller owns the `RunId`.

The execution control service owns the `ExecutionId`.

---

## Hot Enqueue

Hot enqueue means work can be added while the background controller is already active.

The runtime supports enqueueing new runs while:

- the controller is running
- another run is currently executing
- the queue is paused
- earlier runs are waiting
- later runs should be picked up automatically

Example flow:

```text
Run A is running
        ↓
Run B is enqueued dynamically
        ↓
Run B waits in queue
        ↓
Run A completes
        ↓
Run B starts automatically
```

Hot enqueue makes the controller usable as a real runtime work queue instead of a static startup batch.

---

## Hot Enqueue While Queue Is Paused

The queue can accept work while paused.

The flow is:

```text
Queue paused
        ↓
Run A enqueued
Run B enqueued
        ↓
both remain Queued
        ↓
no ExecutionId is created
        ↓
queue resumed
        ↓
runs start normally
```

This allows operators or APIs to collect work while preventing execution from starting immediately.

---

## Queue Control API

The controller exposes operations such as:

```csharp
await controller.PauseQueueAsync(
    reason: "maintenance window",
    requestedBy: "operator");

await controller.ResumeQueueAsync(
    requestedBy: "operator");

await controller.CancelQueuedRunAsync(
    runId,
    reason: "cancel before start",
    requestedBy: "operator");

await controller.CancelRunAsync(
    runId,
    reason: "cancel running run",
    requestedBy: "operator");
```

These operations belong to the queue/controller layer.

They should not be confused with `ExecutionId`-level operations such as `PauseExecutionAsync`, `ResumeExecutionAsync`, or `CancelExecutionAsync`.

---

## Example: Pause Queue, Enqueue, Resume

```csharp
var controller = serviceProvider
    .GetRequiredService<IAiRuntimePipelineBackgroundController>();

await controller.StartAsync();

await controller.PauseQueueAsync(
    reason: "operator pause",
    requestedBy: "admin");

var handle = await controller.EnqueueAsync(
    new AiRuntimePipelineRunRequest
    {
        PipelineName = "approval-pipeline",
        PipelineDefinition = pipelineDefinition,
        Input = new
        {
            candidateId = "candidate-001"
        }
    });

// Still queued. No ExecutionId has been created yet.
Console.WriteLine(handle.Status);      // Queued
Console.WriteLine(handle.ExecutionId); // null / empty

await controller.ResumeQueueAsync(
    requestedBy: "admin");

var final = await handle.Completion;
```

Expected behavior:

```text
handle.Status = Queued while queue is paused
handle.ExecutionId = empty before execution starts
handle.Completion completes after queue resumes and execution finishes
```

---

## Example: Cancel Queued Run

```csharp
await controller.PauseQueueAsync();

var handle = await controller.EnqueueAsync(
    new AiRuntimePipelineRunRequest
    {
        PipelineName = "approval-pipeline",
        PipelineDefinition = pipelineDefinition
    });

var cancelled = await controller.CancelQueuedRunAsync(
    handle.RunId,
    reason: "user cancelled request",
    requestedBy: "api");

var final = await handle.Completion;
```

Expected behavior:

```text
cancelled = true
handle.Status = Cancelled
handle.ExecutionId = empty
final.Status = Cancelled
```

No DAG execution state should be created.

---

## Example: Cancel Running Run

```csharp
var handle = await controller.EnqueueAsync(
    new AiRuntimePipelineRunRequest
    {
        PipelineName = "long-running-pipeline",
        PipelineDefinition = pipelineDefinition
    });

await WaitUntilExecutionIdExists(handle);

await controller.CancelRunAsync(
    handle.RunId,
    reason: "operator cancellation",
    requestedBy: "admin");

var final = await handle.Completion;
```

Expected behavior:

```text
handle.RunId exists
handle.ExecutionId exists
CancelRunAsync delegates to execution control
final.Status = Cancelled
handle.Status = Cancelled
```

---

## Controller-Level State

The controller tracks the lifecycle of submitted runs.

Typical run states include:

- queued
- running
- completed
- failed
- cancelled

The controller must update handle status consistently so callers can observe queue state and completion behavior.

The controller should not use `ExecutionId` as the queue identity.

The queue identity is `RunId`.

---

## Completion Task Behavior

Each run handle exposes a completion task.

The completion task should complete when:

- the execution finishes successfully
- the execution fails
- the run is cancelled while queued
- the run is cancelled while running and execution finalizes as cancelled
- the controller stops and queued work is cancelled

This gives callers a unified way to wait for the result of a submitted run.

Queued run cancellation must call the completion source so callers do not wait forever.

---

## Queue Shutdown Behavior

When the controller stops, queued work may need to be cancelled before execution starts.

A safe shutdown should:

- prevent new queue advancement
- cancel queued runs that were not started
- mark queued handles as cancelled
- complete their completion tasks
- preserve already-created execution state
- avoid creating new `ExecutionId` values during shutdown

Running executions should be handled through execution-level control if cancellation is required.

---

## Interaction with Execution Control State

Queue control and execution control are connected but separate.

The rule is:

```text
If no ExecutionId exists:
    handle cancellation at RunId / queue level.

If ExecutionId exists:
    delegate cancellation to ExecutionId control state.
```

This keeps the behavior clean and avoids creating fake execution state for work that never started.

---

## Interaction with Distributed Execution

The background controller can submit work that is executed by the runtime engine.

Depending on configuration, execution may be:

- single runtime-instance execution
- distributed multi-worker execution
- distributed multi-runtime-instance execution

Queue control remains at the submission/controller layer.

Distributed execution remains at the `ExecutionId` layer.

---

## Interaction with Shared Queue Pump

The shared queue pump operates before the local runtime queue receives work.

The pump request contains pump identity:

```text
PumpRuntimeInstanceId
PumpWorkerId
```

These identify the runtime instance and worker executing the pump cycle.

They do not necessarily identify the runtime instance that will receive the run.

Dispatch-time admission selects the assigned runtime instance.

```text
PumpRuntimeInstanceId
    = who is draining / pumping

AssignedRuntimeInstanceId
    = who receives the run
```

After dispatch, the selected runtime instance receives the run through its local queue control plane and creates a local `RunId`.

Tests that expect pump-local dispatch should explicitly configure admission so the assigned runtime instance equals the pump runtime instance.

Production code should not assume those two identities are always the same.

---

## Interaction with Replay and Snapshots

Only runtime executions with an `ExecutionId` can produce DAG state, snapshots, and replayable records.

A cancelled queued run has no `ExecutionId`.

Therefore:

```text
Cancelled before start
        ↓
No DAG state
No terminal snapshot
No replay record
```

A cancelled running run has an `ExecutionId`.

Therefore:

```text
Cancelled after start
        ↓
Execution control state applies
DAG finalization applies
Terminal status = Cancelled
Snapshot / replay foundations may apply
```

---

## Validated Behavior

The queue-control implementation is validated by integration tests covering:

- queue pause
- queue resume
- queued run cancellation
- unknown queued run cancellation
- running run cancellation
- running run cancellation bridge to execution control
- hot enqueue while controller is running
- hot enqueue while queue is paused
- queued run remains without `ExecutionId`
- cancelled queued run completes its completion task
- running run cancellation finalizes through `ExecutionId` control
- queue-first shared submit
- manual shared queue drain with background pump disabled
- local runtime dispatch after manual drain
- HTTP runtime dispatch after manual drain
- worker capacity visibility
- worker saturation visibility
- `CanAcceptRun` becoming false when workers are saturated
- chaos scenarios with distributed execution

The broader execution-control and queue-control implementation is validated together through integration tests covering:

- Redis control state persistence
- optimistic Redis version updates
- execution pause
- execution resume
- execution cancellation
- waiting for human input
- human input submission
- control-based claim blocking
- `Pausing -> Paused`
- `Resuming -> Running`
- cancellation override during finalization
- queue pause
- queue resume
- queued run cancellation
- unknown queued run cancellation
- running run cancellation
- hot enqueue while controller is running
- hot enqueue while queue is paused
- chaos scenarios with distributed execution

---

## Why This Matters

This feature turns the runtime from an executor into a controllable execution platform.

It allows the system to answer production questions such as:

```text
Can I stop new queued runs from starting?
Can I resume queued work later?
Can I cancel work before it creates DAG state?
Can I cancel a running controller run?
Can I bridge RunId cancellation to ExecutionId cancellation?
Can I add work dynamically while the runtime is already active?
Can I accept new work while the queue is paused?
Can I keep queued cancellation separate from execution cancellation?
```

The answer is yes, with explicit state, durable transitions, completion handling, and deterministic behavior.

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Queue paused | New queued runs remain queued. |
| Queue resumed | Queued runs may start. |
| Run cancelled before start | No `ExecutionId` is created. |
| Unknown queued run cancelled | Cancellation returns false or no-op depending on API contract. |
| Running run cancelled | Controller delegates to execution control service. |
| Run enqueued while controller active | Run is accepted and processed later. |
| Run enqueued while queue paused | Run remains queued until resume. |
| Controller shutdown with queued runs | Queued runs can be cancelled before execution creation. |
| Running execution finishes after cancellation request | Execution finalization must respect cancellation state. |
| Caller waits on cancelled queued run | Completion task completes as cancelled. |

---

## Current Status

| Capability | Status |
|---|---|
| Background controller queue | Implemented / validated |
| RunId identity | Implemented / validated |
| ExecutionId identity separation | Implemented / validated |
| Queue pause | Implemented / validated |
| Queue resume | Implemented / validated |
| Queued run cancellation | Implemented / validated |
| Unknown queued run cancellation handling | Implemented / validated |
| Running run cancellation bridge | Implemented / validated |
| Hot enqueue while controller is running | Implemented / validated |
| Hot enqueue while queue is paused | Implemented / validated |
| Completion task per handle | Implemented / validated |
| Queue shutdown cancellation for queued runs | Implemented / validated |
| Distributed execution integration | Implemented / validated foundations |
| Shared queue-first submit path | Implemented / validated |
| Manual shared queue drain | Implemented / validated |
| Background shared queue pump | Implemented / validated |
| Pump identity / assigned runtime identity separation | Implemented / validated |
| Runtime worker capacity visibility | Implemented / validated |
| Max local workers per execution | Implemented / validated |
| Worker-aware CanAcceptRun | Implemented / validated |
| Rich queue audit history | Planned |
| Public controller API polish | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Background controller | Owns queue lifecycle and `RunId` state. |
| Run handle | Tracks `RunId`, optional `ExecutionId`, status, and completion. |
| Queue state | Determines whether queued runs may start. |
| Runtime queue state snapshot | Exposes run slots, queue depth, worker usage, and accept capacity. |
| Shared queue pump | Claims shared queue items and requests dispatch into selected runtime instances. |
| Dispatch-time admission | Selects the assigned runtime instance during shared queue drain. |
| Execution control service | Handles cancellation once an `ExecutionId` exists. |
| DAG runtime | Executes the durable `ExecutionId` once started. |
| Completion source | Notifies callers when queued/running work completes, fails, or is cancelled. |
| Observability layer | Records queue state transitions and cancellation behavior. |

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Execution Control State](execution-control-state.md)
- [Distributed Execution](distributed-execution.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
