# Shared Queue Pump and Worker Capacity

Status: Implemented foundation / validated for local runtime pools, HTTP pooled runtime scenarios, Redis-backed shared queue coordination, Redis scale-out request persistence, fulfilled-run requeue, tenant-aware runtime isolation, RBAC context propagation, and end-to-end MCP local scale-out execution.

This document describes the shared queue pump, queue-first submit mode, direct-dispatch scale-out mode, dispatch-time admission, pump identity separation, RBAC `ExecutionContextSnapshot` propagation, tenant-aware runtime visibility, runtime worker-capacity visibility, `MaxLocalWorkersPerExecution`, Redis-backed coordination, Redis-backed scale-out request lifecycle, fulfilled-run requeue, and the validated local/HTTP pooled runtime provider model.

It complements:

- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
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
- preserve RBAC and tenant context across background dispatch hops
- restore `ExecutionContextSnapshot` before dispatch-time admission
- enforce tenant-aware registry and capacity visibility
- expose runtime instance capacity and worker pressure
- wait for runtime readiness before background dispatch
- use Redis-backed shared queue and shared run stores
- use Redis-backed scale-out request persistence
- requeue fulfilled scale-out shared runs for normal pump dispatch
- use Redis-backed admission reservations during heavy dispatch scenarios
- support local, dynamically scaled local, HTTP pooled, and future Kubernetes-style runtime instance hosting

The core principle is:

```text
Shared queue coordinates global work.
ExecutionContextSnapshot carries the durable tenant boundary.
Local runtime queue owns executable work.
DAG engine owns durable execution.
```

The shared queue pump must not replace the runtime engine.

It must not execute DAG steps.

It must not mutate DAG state directly.

It only claims shared queue items and dispatches shared runs into selected runtime instance local queues.

---

## Core Design Rules

The shared queue pump has several non-negotiable design rules.

```text
1. Shared queue work is global control-plane work.
2. Local runtime queues remain the executable ownership boundary.
3. Pump identity is not necessarily the dispatch target.
4. Dispatch-time admission selects the target runtime instance.
5. Tenant isolation is enforced before admission and dispatch.
6. Background dispatch must restore the durable execution context.
7. Runtime registry and capacity listing must be tenant-visible.
8. Scale-out fulfillment must requeue, not dispatch directly.
9. DAG state is only created after local runtime enqueue/start.
10. ExecutionId belongs to the DAG engine, not the shared queue.
```

The tenant-specific rule is especially important:

```text
ContextKey = RBAC / correlation / debug context
ExecutionContextSnapshot.TenantId = durable tenant boundary
Metadata = observability duplicate only
```

Do not use `ContextKey` or metadata as the runtime partition key.

---

## Runtime Readiness and Control-Plane Discovery

The shared queue pump depends on runtime visibility.

A background pump should not start dispatching until at least one runtime instance is visible, ready, tenant-visible, and able to accept work.

In the current validated model, the MCP control plane can publish its logical control-plane identity through the Redis control-plane discovery store.

Runtime-only hosts that require discovery resolve that identity before registering their child runtime instances or publishing capacity.

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
    ↓
Shared Queue Pump Readiness Gate
```

This prevents the pump from draining queued work before runtime capacity is visible.

Important rules:

```text
Discovery resolves the logical control-plane id.
Registry exposes runtime instance identity and status.
Capacity descriptors expose dispatch readiness.
Tenant visibility filters registry and capacity results.
The pump waits for ready capacity before dispatching.
```

Shutdown cleanup should reuse the already resolved control-plane identity for known runtime instances.

Registry unregister and capacity descriptor removal should not depend on rediscovery during shutdown, because the discovery descriptor or Redis dependencies may already be disposed.

---

## RBAC and ExecutionContextSnapshot Propagation

The shared queue pump runs in background or manual-drain contexts where the original MCP request `AsyncLocal` context may no longer exist.

Because of that, the pump must not rely on the current ambient RBAC context inherited from the caller.

The durable source of truth is the `ExecutionContextSnapshot` persisted on the shared run.

```text
MCP request
    ↓
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
AiSharedRuntimeControllerRequest.RunRequest.ExecutionContextSnapshot
    ↓
AiSharedRunRecord.ExecutionContextSnapshot
    ↓
AiSharedQueueDispatcher restores snapshot before admission
    ↓
Tenant-aware registry/capacity filtering
    ↓
Dispatch to selected runtime instance
    ↓
Local runtime queue receives request with same snapshot
    ↓
Background controller restores snapshot before DAG execution
```

The snapshot contains:

```text
ContextKey
Project
UserId
TenantId
TenantGroupId
CurrentNamespace
Namespaces / TRNs
```

The important boundary is:

```text
ExecutionContextSnapshot.TenantId
```

This is the durable tenant partition for control-plane and runtime operations.

`ContextKey` is still useful for RBAC, correlation, and diagnostics, but it is not the durable tenant boundary.

---

## Shared Queue Dispatcher Context Restore

`AiSharedQueueDispatcher` must restore the shared run execution context before it calls admission.

Correct dispatch-time flow:

```text
IAiSharedQueueDispatcher.DispatchNextAsync
    ↓
IAiSharedQueue.ClaimNextAsync(...)
    ↓
IAiSharedRunStore.GetAsync(...)
    ↓
Read sharedRun.ExecutionContextSnapshot
    ↓
Restore RBAC ExecutionContext from snapshot
    ↓
IAiRunAdmissionController.AdmitAsync(...)
    ↓
Tenant-visible registry / capacity listing
    ↓
Reserve selected capacity when required
    ↓
IAiSharedRunDispatcher.DispatchAsync(...)
    ↓
IAiSharedQueue.MarkDispatchedAsync(...)
    ↓
IAiSharedRunStore.MarkDispatchedAsync(...)
    ↓
Restore previous context or clear current context
```

The previous context restore step matters because a manual MCP tool call or hosted background service may already have an ambient context.

The dispatcher should not leak one shared run tenant context into the next dispatch cycle.

Required behavior:

```text
If previous context existed:
    restore previous context after dispatch attempt

If no previous context existed:
    clear context after dispatch attempt
```

This prevents cross-run and cross-tenant context leakage.

---

## Tenant Visibility Rules During Pump Dispatch

Dispatch-time admission must only see runtime instances and capacity descriptors visible to the current tenant.

The visibility evaluator applies the following rules.

```text
Shared runtime instance:
    visible to Shared/default tenants
    visible to Hybrid/Dedicated tenants only when tenant settings allow shared fallback

Dedicated runtime instance:
    visible only when TenantId or TenantGroupId matches

Hybrid runtime instance:
    visible only when TenantId or TenantGroupId matches
    AllowSharedFallback does not make an unowned Hybrid runtime visible
```

The last rule prevents a dangerous leak:

```text
Hybrid runtime without owner + AllowSharedFallback = true
    must not become visible to every hybrid tenant
```

Hybrid fallback means:

```text
A Hybrid tenant may fall back to a Shared runtime instance.
```

It does not mean:

```text
A Hybrid runtime instance with no matching owner is globally visible.
```

---

## Tenant Runtime Settings Used by Admission

The current foundation uses provider-backed/hardcoded tenant runtime settings.

Validated examples:

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
```

```text
tenant-b
    IsolationMode = Hybrid
    PreferDedicatedCapacity = true
    AllowSharedFallback = true
    MaxRuntimeInstances = 2
    RuntimeInstanceIdPrefix = tenant-b-runtime
    WorkerCountPerInstance = 5
    MaxConcurrentRunsPerInstance = 3
    LocalQueueCapacity = 250
```

```text
default / unknown / test-tenant
    IsolationMode = Shared
    PreferDedicatedCapacity = false
    AllowSharedFallback = true
    MaxRuntimeInstances = 1
    RuntimeInstanceIdPrefix = runtime-instance
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 3
```

The pump does not own these settings.

The pump restores context and calls admission.

Admission resolves the tenant settings and evaluates registry/capacity visibility.

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
    - backed by Redis in validated multi-runtime scenarios
    - carries shared run identity, not executable DAG state

Local runtime queue
    - owned by one runtime instance
    - stores executable local runs
    - creates RunId
    - starts ExecutionId
    - receives the ExecutionContextSnapshot
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
ExecutionContextSnapshot = persisted on shared run
```

This is useful when the system should persist and observe queued work before runtime assignment.

Use queue-first mode for:

- Kubernetes-style work distribution
- MCP demos
- manual operator-controlled drain
- background pump validation
- queue persistence validation
- HTTP/runtime-provider dispatch tests
- HTTP pooled runtime dispatch tests
- Redis shared queue validation
- heavy dispatch validation
- no-double-dispatch shared queue tests

Configuration:

```text
AiSharedRuntimeController:SubmitMode = QueueFirst
```

Queue-first intentionally forces the shared run into `QueuedGlobally`.

It is the correct mode for queue-first demos and shared queue validation.

It is not the correct mode for admission-driven scale-out at submit time.

For a submitted run to become `ScaleOutRequested`, the controller must run admission directly.

Use direct dispatch mode for that path.

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
- admission should be allowed to return `RequestScaleOut`
- the system should create runtime capacity on demand before requeueing and dispatching the run

Direct dispatch does not mean every run is immediately dispatched.

It means the controller preserves the real admission decision.

When no runtime capacity is available and scale-out is enabled, direct dispatch can produce:

```text
SubmitRunAsync
    ↓
Admission
    ↓
RequestScaleOut
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Redis scale-out request persisted
```

For tenant-aware direct dispatch, the controller request and run request must carry tenant context:

```text
AiSharedRuntimeControllerRequest.TenantId
AiRuntimePipelineRunRequest.ExecutionContextSnapshot.TenantId
```

The durable source used by background hops is the snapshot.

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

Before the background pump starts automatic dispatch, it should pass a readiness gate.

The readiness gate ensures that runtime capacity has been published and at least one target runtime instance can accept work.

```text
Background Pump Startup
    ↓
Resolve control-plane identity
    ↓
Wait for runtime registry/capacity visibility
    ↓
Apply tenant visibility during admission
    ↓
Find at least one ready runtime instance
    ↓
Start pump loop
```

This avoids dispatch attempts against an empty or not-yet-discovered runtime pool.

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
Restore ExecutionContextSnapshot
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

## Scale-Out Fulfilled Requeue

Scale-out fulfillment now feeds back into the shared queue pump instead of dispatching directly from the watcher.

This preserves the shared queue as the dispatch ownership boundary.

Validated flow:

```text
SubmitRunAsync
    ↓
DirectDispatch mode
    ↓
Admission returns RequestScaleOut
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Redis scale-out request is created
    ↓
AiRuntimeScaleOutRequestWatcherHostedService observes pending request
    ↓
Provider selector resolves scale-out-capable provider
    ↓
Local provider delegates to AiLocalRuntimeInstanceScaler
    ↓
New local runtime instance starts and publishes capacity
    ↓
Scale-out request is marked Fulfilled
    ↓
AiScaleOutFulfilledRunRequeueService enqueues the shared run
    ↓
SharedQueueItem.Status = Pending
    ↓
Shared queue pump claims the item
    ↓
Dispatcher restores ExecutionContextSnapshot
    ↓
Dispatch-time admission sees tenant-visible runtime capacity
    ↓
Run is dispatched to the newly created runtime instance
    ↓
LocalRunId and ExecutionId become visible
    ↓
Runtime run completes
```

Important responsibility split:

```text
Scale-out watcher
    = observes request and creates capacity

Fulfilled run requeue service
    = places the shared run back into the shared queue

Shared queue pump
    = owns claim, dispatch-time admission, provider dispatch, and state updates
```

The watcher must not bypass the shared queue.

This keeps the same dispatch guarantees for scale-out runs as for queue-first runs.

---

## Tenant-Aware Scale-Out Requeue

Scale-out requests persist tenant runtime settings so the provider creates capacity in the correct tenant scope.

Important fields persisted in the scale-out request record:

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

Examples:

```text
default/test-tenant Shared
    RuntimeInstanceIdPrefix = runtime-instance
    FulfilledRuntimeInstanceId contains :runtime-instance-1
```

```text
tenant-a Dedicated
    RuntimeInstanceIdPrefix = tenant-a-runtime
    FulfilledRuntimeInstanceId contains :tenant-a-runtime-1
    Shared fallback disabled
```

```text
tenant-b Hybrid
    RuntimeInstanceIdPrefix = tenant-b-runtime
    FulfilledRuntimeInstanceId contains :tenant-b-runtime-1
    Shared fallback enabled when shared capacity is visible
```

The local scaler must count existing hosts by tenant/runtime prefix, not by global host count.

Correct rule:

```text
Scope local scale-out by RuntimeInstanceIdPrefix.
Do not use total active local host count as tenant capacity.
```

This prevents a shared runtime instance from satisfying a dedicated tenant capacity request.

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
Claim shared queue item
    ↓
Restore shared run ExecutionContextSnapshot
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

Manual drain through MCP also relies on RBAC authorization for the MCP tool itself.

Once authorized, the dispatch-time tenant boundary still comes from the shared run snapshot, not from arbitrary metadata.

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
    = claim + context restore + admission + dispatch + state update
```

This separation keeps the pump usable outside hosted service scenarios.

The MCP host can use the same pump in two modes:

```text
Manual drain
    AiMcpHost:EnableSharedQueuePump = false
    AiSharedQueueBackgroundService:Enabled = false

Background dispatch
    AiMcpHost:EnableSharedQueuePump = true
    AiSharedQueueBackgroundService:Enabled = true
```

When background dispatch is enabled, the hosted service must wait for runtime readiness before the first dispatch cycle.

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

Tenant identity is separate from both pump identity and assigned runtime identity.

```text
Tenant identity
    = ExecutionContextSnapshot.TenantId

Pump identity
    = who is draining

Assigned runtime identity
    = who will receive the run
```

---

## Dispatch-Time Admission

Shared queue dispatch now re-evaluates admission at drain time.

This is intentional.

At submit time, the run may have been queued.

At drain time, the system may have new capacity information.

Dispatch-time admission allows the selected runtime instance to be based on the latest visible capacity.

Tenant-aware flow:

```text
Pending shared queue item
    ↓
Pump claims item
    ↓
Dispatcher loads shared run
    ↓
Dispatcher restores ExecutionContextSnapshot
    ↓
Admission is called
    ↓
Registry/capacity are filtered by tenant visibility
    ↓
Admission returns AssignToInstance
    ↓
AssignedRuntimeInstanceId selected
    ↓
Dispatcher sends run to selected runtime instance
```

Benefits:

- runtime assignment can use current capacity
- tenant visibility is applied at dispatch time
- queue-first submit remains stable
- pump identity is decoupled from dispatch target
- local and HTTP provider paths use the same shared queue model
- future Kubernetes control-plane/runtime-pod split is supported
- scale-out fulfilled runs can be requeued and dispatched using the same logic as normal queued runs

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
Restore ExecutionContextSnapshot
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

A tenant context restored for a failed dispatch must be cleared/restored afterward.
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

Tenant filtering is applied after the item is claimed and the shared run is loaded.

If tenant-visible admission cannot assign capacity, the item should not be incorrectly marked dispatched.

---

## Runtime Instance Provider Dispatch

After admission selects an assigned runtime instance, dispatch should go through a runtime provider path.

Current provider-oriented foundations include:

- local runtime instance provider
- HTTP runtime provider foundation
- pooled HTTP runtime instance hosting
- runtime instance provider metadata
- Redis runtime instance registry visibility
- Redis runtime capacity descriptors
- Redis admission reservation store
- tenant-aware runtime visibility
- tenant-aware scale-out request records

Provider principle:

```text
Admission decides WHO receives the run.
Provider router decides HOW to contact that runtime instance.
Provider dispatches into the local runtime queue.
```

Providers must not execute DAG steps directly.

Providers must not mutate DAG state.

Providers must not bypass local runtime queues.

Providers must preserve the `ExecutionContextSnapshot` when dispatching the run request.

### Local Scale-Out Dispatch After Requeue

The local scale-out path validates that newly created runtime capacity can be consumed by the normal pump.

```text
Initial state
    ActiveLocalInstances = 0
    No executable runtime capacity visible

Submit shared run
    ↓
Admission = RequestScaleOut
    ↓
Scale-out watcher creates runtime-instance-1 or tenant-specific runtime
    ↓
Run is requeued
    ↓
Pump claims requeued item
    ↓
Dispatcher restores ExecutionContextSnapshot
    ↓
Admission selects tenant-visible runtime instance
    ↓
Local provider dispatches into that runtime local queue
    ↓
RunId is created
    ↓
ExecutionId is created
    ↓
Runtime run completes
```

This proves that scale-out creates capacity and that the shared queue pump can consume the original run afterward.

### HTTP Pooled Runtime Dispatch

The HTTP provider has been validated with a pooled runtime model.

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

The HTTP host is transport and hosting infrastructure.

The dispatchable runtime identities are the child runtime instances created by the local runtime instance pool.

```text
HTTP host identity != dispatch target
runtime-http-* child instance == dispatch target
```

Shared queue dispatch should assert the assigned child runtime instance, not the parent HTTP host identity.

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

## Runtime Queue Snapshot Requirement

The selected runtime instance local queue must receive the `ExecutionContextSnapshot`.

Local queue execution now requires the snapshot before the background controller starts execution.

Correct local queue flow:

```text
IAiSharedRunDispatcher.DispatchAsync
    ↓
AiRuntimePipelineRunRequest
    ↓
ExecutionContextSnapshot attached
    ↓
IAiRuntimeQueueControlPlane.EnqueueRunAsync
    ↓
AiRuntimePipelineBackgroundController local queue
    ↓
RestoreExecutionContextFromSnapshot
    ↓
Create DAG execution
```

If the snapshot is missing, the runtime should fail fast instead of executing in an unknown tenant context.

This protects direct runtime tests and non-MCP paths too.

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
IAiRuntimeInstanceCapacityStore
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
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
RuntimeInstanceIdPrefix
```

This allows operators and tests to see whether an instance is:

- idle
- busy
- saturated
- queue-limited
- worker-limited
- paused
- available for new runs
- shared
- dedicated to a tenant
- hybrid for a tenant
- visible to the current tenant

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

For tenant-aware admission, `CanAcceptRun = true` is still not enough.

The runtime must also be visible to the current tenant.

```text
EligibleForDispatch = CanAcceptRun + TenantVisible + ProviderReachable
```

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

Tenant settings may also define `WorkerCountPerInstance`, `MaxConcurrentRunsPerInstance`, and `LocalQueueCapacity` for dynamically created runtime instances.

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

Tenant visibility must also be considered for future cross-instance assistance.

A helper runtime instance must not assist an execution belonging to another tenant unless the tenant/group visibility rules explicitly allow it.

---

## Ledger and Assistance Metadata

Runtime run ledger metadata should include:

```text
max.local.workers.per.execution
effective.worker.count.per.execution
tenant.id
tenant.groupId
runtime.isolation.mode
runtime.instance.id
assigned.runtime.instance.id
```

Execution assistance candidate metadata should use the effective worker count instead of raw distributed worker count.

This prevents over-reporting capacity when `MaxLocalWorkersPerExecution` caps actual local worker participation.

Tenant metadata is useful for observability, but authorization and isolation must still use strong fields and `ExecutionContextSnapshot`.

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
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
ProviderName
ProviderEndpoint
```

This makes MCP useful as a temporary operational dashboard before a full UI exists.

MCP tools must also use RBAC capability checks before exposing operational commands.

After RBAC authorizes the tool call, tenant data visibility still comes from the active execution context and the stored snapshots/indexes.

---

## Runtime Run Index and Queue Status Isolation

Runtime queue status and run status operations are `RunId`-level operations.

A local runtime controller does not know tenant ownership by itself.

Therefore, tenant-aware runtime status access must be authorized before touching the local controller.

Correct pattern:

```text
RuntimeQueue.GetRunStatus(runId)
    ↓
Tenant-aware runtime run execution index lookup
    ↓
If not visible:
        return empty result
        do not call local controller
    ↓
If visible:
        call local controller for state
```

Same rule for cancellation:

```text
CancelRun(runId)
    ↓
Tenant-aware runtime run execution index lookup
    ↓
If not visible:
        return empty result
        do not call local controller
    ↓
If visible:
        call local controller cancellation
```

This prevents cross-tenant leaks by `RunId`.

The runtime run execution index stores:

```text
RunId
ExecutionId
RuntimeInstanceId
Status
FailureReason
CreatedAtUtc
StartedAtUtc
CompletedAtUtc
ExecutionContextSnapshot
Metadata
```

Redis tenant indexes include tenant-specific listing keys.

---

## Testing Strategy

The following behavior is validated or should remain covered by tests.

### Queue-First and Manual Drain

```text
Submit queue-first run
    -> SharedRun.Status = QueuedGlobally
    -> SharedQueueItem.Status = Pending
    -> ExecutionContextSnapshot persisted
    -> no LocalRunId
    -> no ExecutionId

Manual drain
    -> dispatcher restores ExecutionContextSnapshot
    -> dispatch-time admission runs under tenant context
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

### Scale-Out Requeue and Execute

Tests should prove:

```text
DirectDispatch + no runtime capacity + scale-out enabled
    -> SharedRun.Status = ScaleOutRequested
    -> Redis scale-out request is created
    -> tenant runtime settings persisted in request
    -> scale-out watcher marks request Fulfilled
    -> local runtime instance is created dynamically in the correct tenant scope
    -> shared run is requeued into the shared queue
    -> pump restores ExecutionContextSnapshot
    -> pump dispatches the run to the new tenant-visible runtime instance
    -> LocalRunId exists
    -> ExecutionId exists
    -> runtime run reaches completed
```

Validated final evidence:

```text
SharedRunStatus = Dispatched
AssignedRuntimeInstanceId = host-...:runtime-instance-1 or tenant-specific prefix
LocalRunId = available
ExecutionId = available
RuntimeRunStatus = completed
QueueStatus = Dispatched
ScaleOutRequestStatus = Fulfilled
ActiveLocalInstances = 1
```

### Tenant-Aware Scale-Out Prefixes

Tests should prove:

```text
default/test-tenant Shared
    -> :runtime-instance-1

tenant-a Dedicated
    -> :tenant-a-runtime-1
    -> no fallback to shared runtime

tenant-b Hybrid
    -> :tenant-b-runtime-1 when dedicated/hybrid capacity is created
    -> may fall back to :runtime-instance-1 when shared capacity is visible and fallback is allowed
```

### Local and HTTP Runtime Providers

Tests should prove:

```text
Queue-first + manual drain + local provider
    -> dispatch
    -> completion

Queue-first + manual drain + HTTP provider
    -> dispatch to runtime-http-* child instance
    -> completion

Queue-first + background pump + HTTP provider
    -> readiness gate passes
    -> dispatch to runtime-http-* child instance
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
    -> restored tenant context does not leak into the next dispatch
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

Admission uses restored ExecutionContextSnapshot before registry/capacity listing.
```

### Tenant Visibility

Tests should prove:

```text
Shared runtime visible to shared/default tenants.
Shared runtime visible to hybrid tenant when fallback enabled.
Shared runtime not visible to dedicated tenant when fallback disabled.
Dedicated runtime visible only to matching tenant/group.
Hybrid runtime visible only to matching tenant/group.
Unowned Hybrid runtime is not globally visible.
```

### Heavy HTTP Queue-First Dispatch

Tests should prove:

```text
Queue-first + Redis shared queue + Redis shared run store
    -> 50 shared runs submitted
    -> 100 steps per run
    -> 3 pooled HTTP child runtime instances
    -> runs distributed across runtime-http-* instances
    -> no duplicate dispatch
    -> Redis admission reservation store is used
```

The expected evidence is not perfect round-robin distribution.

The expected evidence is that the dispatch target is selected from the pooled child runtime instances and that all submitted runs receive assigned runtime identities.

### Runtime Readiness Gate

Tests should prove:

```text
Background pump enabled
    -> waits for runtime registry/capacity visibility
    -> starts dispatch only after at least one runtime can accept work
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

### Runtime Run Status Tenant Isolation

Tests should prove:

```text
Tenant A submit/dispatch/run status is visible to Tenant A.
Tenant B cannot see Tenant A runtime run status.
Tenant B cannot cancel Tenant A runtime run.
Tenant B does not receive Tenant A RunState or ExecutionId.
Runtime queue control checks tenant-aware runtime run index before local controller access.
```

---

## Current Limitations

Implemented / validated:

```text
queue-first submit mode
shared queue pump
manual drain
background pump
background pump readiness gate
dispatch-time admission
pump identity / assigned runtime identity separation
RBAC ExecutionContextSnapshot propagation
shared queue dispatcher context restore
tenant-aware admission
tenant-aware registry visibility
tenant-aware capacity visibility
tenant-aware runtime run status isolation
tenant-aware scale-out request persistence
shared/dedicated/hybrid runtime isolation rules
local provider foundation
HTTP provider foundation
HTTP pooled runtime dispatch
Redis shared run store
Redis shared queue
Redis admission reservation store
Redis runtime instance registry
Redis runtime instance capacity store
Redis control-plane discovery store
control-plane id resolver
Redis scale-out request store
scale-out request watcher
provider-based scale-out selector
local runtime instance scaler
fulfilled scale-out shared run requeue
scale-out dispatch through shared queue pump
scale-out runtime execution completion
runtime worker capacity visibility
MaxLocalWorkersPerExecution
worker-aware CanAcceptRun
dispatch failure requeue
no-double-dispatch shared queue behavior
MCP replay/report/ledger/trace for completed shared runs
1036 tests green after tenant-aware isolation changes
```

Not implemented yet:

```text
Redis/Lua runtime slot reservation refinement
Redis command queue provider
gRPC runtime provider
Kubernetes provider
Kubernetes pod/deployment autoscaling
production dashboard UI
full provider capability negotiation
production multi-control-plane scheduling hardening
production tenant settings store
production tenant settings admin API
```

---

## Admission Reservation Future Work

Current admission uses visible capacity snapshots together with Redis-backed admission reservations in validated heavy dispatch scenarios.

That is enough for controlled tests and current HTTP pooled runtime dispatch validation.

Further hardening is still needed for perfect production scheduling under multiple fast control-plane dispatchers.

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

The current Redis admission reservation store provides the validated foundation.

Redis Lua can still be added later for stronger atomic slot and worker reservation semantics.

Reservation should protect:

- run slots
- possibly worker capacity
- runtime instance availability
- tenant-visible dispatch ownership
- shared run assignment consistency

---

## Kubernetes Direction

The current design prepares for Kubernetes without requiring Kubernetes in the core runtime.

Future topology:

```text
mcp-control-plane pod
    Role = ControlPlane
    hosts MCP/API/dashboard adapters
    drains or observes shared queue
    restores snapshots for dispatch-time admission

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

The validated local scale-out path should map directly to a Kubernetes provider later:

```text
RequestScaleOut
    ↓
Redis scale-out request
    ↓
Scale-out watcher
    ↓
Kubernetes scale-out provider
    ↓
runtime pod created or deployment scaled
    ↓
runtime pod registers and publishes tenant-aware capacity
    ↓
scale-out request fulfilled
    ↓
shared run requeued
    ↓
pump restores ExecutionContextSnapshot
    ↓
admission selects tenant-visible runtime pod
    ↓
pump dispatches normally
```

Kubernetes should not replace runtime queues or DAG execution ownership.

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Shared Runtime Controller | Creates shared runs, applies submit mode, queues globally, dispatches directly when admitted, and persists `ExecutionContextSnapshot`. |
| Shared Run Store | Persists shared run records, shared run status, run request, and durable execution context snapshot. |
| Shared Queue | Stores pending global queue items and protects claim ownership. |
| Control-Plane Discovery Store | Publishes and reads the MCP logical control-plane identity used by runtime-only hosts. |
| Runtime Instance Registry | Tracks runtime identities, roles, readiness, heartbeat, lifecycle state, and tenant visibility metadata. |
| Runtime Capacity Store | Publishes worker/run capacity descriptors used by admission and pump readiness. |
| Runtime Instance Visibility Evaluator | Applies Shared/Dedicated/Hybrid visibility rules for tenant-aware registry and capacity listing. |
| Shared Queue Pump | Executes one or more dispatch cycles. |
| Shared Queue Dispatcher | Claims shared queue items, restores `ExecutionContextSnapshot`, re-admits runs, dispatches selected targets, and updates queue/run state. |
| Scale-Out Request Store | Persists requested scale-out work and tracks pending, observed, fulfilled, or rejected status with tenant runtime settings. |
| Scale-Out Watcher | Observes pending scale-out requests and delegates capacity creation to a scale-out-capable provider. |
| Fulfilled Run Requeue Service | Requeues a shared run after scale-out fulfillment so the pump can dispatch normally. |
| Admission Controller | Selects whether to assign, queue globally, request scale-out, or reject using tenant-visible capacity. |
| Runtime Provider | Delivers dispatched work to the selected runtime instance local queue and preserves `ExecutionContextSnapshot`. |
| Runtime Queue Control Plane | Exposes one runtime instance local queue and checks tenant-aware runtime run index for `RunId` operations. |
| Runtime Run Execution Index | Maps `RunId` to `ExecutionId`, runtime instance, status, and `ExecutionContextSnapshot` for tenant-aware queue/status operations. |
| Background Controller | Owns local `RunId` lifecycle, restores snapshot, and starts DAG executions. |
| Worker Capacity Model | Tracks local workers, active workers, available workers, and per-execution caps. |
| DAG Engine | Owns durable `ExecutionId` execution and step state transitions. |
| MCP Server | Exposes shared queue, runtime instance, replay, control, and observability operations through RBAC-protected tools. |

---

## Validated Evidence

The current implementation has been validated with the following evidence:

```text
Redis local scale-out execution:
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

```text
Tenant-aware runtime isolation:
    default/test-tenant Shared -> :runtime-instance-1
    tenant-a Dedicated -> :tenant-a-runtime-1
    tenant-a does not fallback to shared when disabled
    tenant-b Hybrid -> :tenant-b-runtime-1
    tenant-b can fallback to shared when enabled
    registry ListAsync filters by tenant visibility
    capacity ListAsync/GetAsync filters by tenant visibility
    admission assigns only tenant-visible runtime capacity
```

```text
Runtime run status tenant isolation:
    Tenant A run status visible to Tenant A
    Tenant B cannot read Tenant A RunId status
    Tenant B cannot cancel Tenant A RunId
    Runtime queue control checks tenant-aware runtime run index before local controller access
```

```text
HTTP pooled QueueFirst dispatch:
    Runs = 50
    StepsPerRun = 100
    RuntimeInstances = runtime-http-1, runtime-http-2, runtime-http-3
    RedisAiSharedRunStore = validated
    RedisAiSharedQueue = validated
    RedisAiRuntimeAdmissionReservationStore = validated
```

Validated outcomes:

- all heavy HTTP dispatch tests pass
- HTTP provider scenarios pass against pooled child runtime instances
- background pump dispatch works after runtime readiness
- manual drain works through MCP
- replay/report/ledger/trace works for completed shared runs
- runtime registry and capacity cleanup no longer block shutdown
- discovery-based control-plane id resolution works for runtime-only hosts
- runtime identity assertions target child runtime instances instead of parent HTTP hosts
- Redis local scale-out request fulfillment works
- fulfilled scale-out runs are requeued
- pump dispatches scale-out requeued runs
- dynamically created local runtime instances execute the run to completion
- tenant-aware shared queue dispatch restores execution context before admission
- runtime queue run status isolation protects `RunId` access
- 1036 tests are green after the multi-tenant control-plane isolation changes

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
- RBAC `ExecutionContextSnapshot` propagation
- tenant-aware registry and capacity visibility
- shared/dedicated/hybrid runtime isolation
- tenant-aware scale-out request persistence
- Redis-backed shared queue coordination
- Redis-backed scale-out request lifecycle
- fulfilled scale-out run requeue
- Redis-backed admission reservation foundation
- control-plane discovery and runtime readiness
- no-double-dispatch shared queue behavior
- runtime worker capacity visibility
- worker-aware `CanAcceptRun`
- local worker cap per execution
- runtime run status tenant isolation

The result is a cleaner path toward Kubernetes-style runtime hosting:

```text
MCP/RBAC authorizes the operation.
ExecutionContextSnapshot carries the durable tenant boundary.
Shared queue coordinates work globally.
Pump restores tenant context before admission.
Admission selects the tenant-visible target.
Provider dispatches to the runtime instance.
Local queue owns RunId.
DAG engine owns ExecutionId.
Workers execute deterministically.
```

## Documentation Rule

Do not shorten this document by replacing it with `shared-controller-usage.md` content.

This document is specifically about:

```text
shared queue pump
background/manual drain
worker capacity
pump identity
context restore
scale-out fulfilled requeue
tenant-aware dispatch-time admission
runtime queue handoff
```

Detailed shared controller setup examples belong in `shared-controller-usage.md`.
