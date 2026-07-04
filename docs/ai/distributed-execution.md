# Distributed Execution

Status: Implemented / validated foundation with Redis-backed DAG hot state, Redis Lua atomic ownership, distributed workers, multi-runtime-instance execution foundations, tenant-aware shared queue dispatch, durable `ExecutionContextSnapshot` propagation, provider-based local / HTTP / gRPC runtime dispatch, Runtime Host Manager process-host provisioning, real `RuntimeInstanceOnly` process boundaries, runtime health separation, in-flight DAG recovery, local-queued shared-run redispatch, replay / ledger / trace validation, recovery forensics, and multi-tenant safe-tenant non-impact proof.

This document describes how the **Deterministic AI Runtime** coordinates distributed workers, runtime instances, shared queues, providers, and tenant-scoped control-plane recovery safely.

This document is one of the core architecture documents. It is not only about worker-level distributed execution. It also describes the distributed identity model that allows the runtime to remain deterministic across:

- multiple workers;
- multiple runtime instances;
- shared queue dispatch;
- provider transports;
- process-host runtime boundaries;
- tenant-scoped recovery;
- replay / ledger / trace proof after failures.

Related documents:

- [Architecture Overview](architecture-overview.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Testing Strategy](testing-strategy.md)

---

## Purpose

Distributed execution is the force of the runtime.

The goal is to allow many workers, many runtime instances, multiple process hosts, and multiple tenants to advance AI workflow execution safely without:

- duplicate step execution;
- duplicate shared run dispatch;
- broken DAG dependency ordering;
- stale ownership corruption;
- uncontrolled parallelism;
- cross-tenant runtime leakage;
- unsafe fallback to another tenant's runtime;
- lost retry state;
- corrupted terminal lifecycle;
- non-deterministic final results;
- recovery contamination of safe tenants;
- transport providers becoming hidden recovery owners.

The runtime treats AI workflow execution as a distributed systems problem.

Workers do not own the workflow.

Runtime processes do not own the workflow.

Transport providers do not own the workflow.

The durable execution state and control-plane records own the workflow.

```text
Execution state owns DAG progress.
SharedRun state owns shared submission lifecycle.
RuntimeRunExecutionIndex owns assignment visibility.
Registry/capacity owns runtime routing visibility.
Ledger/trace/forensics own audit evidence.
```

This is what makes process failure survivable and tenant isolation provable.

---

## Core Principle

The core rule is:

```text
Distributed execution is state-driven, not process-driven.
```

A worker, process, or provider may disappear.

The workflow must still be explainable and recoverable from durable state.

That requires strict separation between:

```text
SharedRunId
    durable shared/control-plane submission identity

LocalRunId
    runtime-local queue attempt identity

ExecutionId
    durable DAG execution identity

RuntimeInstanceId
    runtime capacity / process identity

TenantId
    durable tenant boundary from ExecutionContextSnapshot
```

These identities must never be collapsed.

A runtime process can die and be replaced.

A local run attempt can be abandoned.

A provider transport can change from HTTP to gRPC.

But a recovered in-flight DAG execution must keep the same `ExecutionId`.

A recovered local-queued shared run must keep the same `SharedRunId`.

---

## Distributed Execution Layers

Distributed execution spans several layers.

```text
Client / MCP / API
    ↓
RBAC / ExecutionContextSnapshot
    ↓
Shared Runtime Controller
    ↓
SharedRunStore / SharedQueue
    ↓
Dispatch-time admission
    ↓
Tenant-visible registry / capacity
    ↓
Provider router
    ↓
Local / HTTP / gRPC runtime provider
    ↓
Runtime local queue
    ↓
Runtime background controller
    ↓
DAG execution engine
    ↓
Redis Lua atomic step ownership
    ↓
Step executors
    ↓
Retention / replay / ledger / trace
```

The important design point is that the distributed execution guarantee is not limited to Redis step claiming.

Redis step claiming protects the DAG.

Shared queue claiming protects shared dispatch.

Tenant-aware admission protects runtime selection.

Provider metadata protects transport routing.

Runtime health reconciliation protects unsafe capacity suppression.

Execution recovery reconciliation protects work already assigned to a failed runtime.

Replay / ledger / trace / forensics prove the result after convergence.

---

## Execution Model

The runtime executes workflows as DAGs.

Each execution is represented by:

- an `ExecutionId`;
- an execution record;
- a DAG state;
- step states;
- dependency information;
- retry state;
- claim ownership metadata;
- result and payload references;
- terminal snapshot and replay metadata when applicable.

Workers advance the execution by reading shared state and attempting atomic transitions.

The basic model is:

```text
Execution state exists
    ↓
Workers inspect state
    ↓
Ready steps are identified
    ↓
Workers attempt atomic claims
    ↓
Only one worker wins ownership of each step
    ↓
Step executes
    ↓
Result is persisted through controlled transition
    ↓
Runtime evaluates convergence
```

Execution is therefore state-driven, not process-driven.

---

## Redis as Distributed Hot State

Redis is used as the active distributed coordination layer.

It stores the hot execution state required to coordinate workers.

This includes:

- active execution records;
- step states;
- claim tokens;
- claim ownership metadata;
- retry scheduling metadata;
- stale running recovery metadata;
- dependency information;
- distributed concurrency leases;
- execution control state;
- shared run store;
- shared queue;
- runtime run execution index;
- runtime registry;
- runtime capacity store;
- control-plane discovery store;
- scale-out request store;
- admission reservation store.

Redis is not used only as a cache.

It acts as the shared coordination substrate for active execution and control-plane coordination.

---

## Hot State vs Durable Truth

Redis is the hot state layer.

MongoDB / payload storage / replay artifacts are durable storage layers.

```text
Redis
    hot coordination state
    active DAG state
    queue ownership
    runtime visibility
    admission reservations
    scale-out lifecycle

MongoDB / payload store / replay artifacts
    large payloads
    terminal snapshots
    archived step data
    replay foundations
    long-term audit data
```

For runtime crash recovery, there is another important rule:

```text
Runtime local queue is volatile.
Shared/control-plane records are durable truth.
```

If a runtime process dies, its local queue dies with it.

Recovery must not depend on reading the dead local queue.

Recovery reconstructs assigned work from:

```text
SharedRunStore
SharedQueue
RuntimeRunExecutionIndex
DAG execution store
Runtime registry
Runtime capacity store
Ledger
Trace
Runtime recovery forensics
Replay artifacts
```

---

## Atomic Coordination with Redis Lua

Critical state transitions are protected by Redis Lua scripts.

Lua is used because distributed workers or dispatchers may attempt the same operation at the same time.

Atomic Redis Lua transitions protect operations such as:

- creating shared runs;
- claiming shared queue items;
- marking shared queue items dispatched;
- requeueing shared queue items;
- claiming a ready step;
- completing an owned step;
- failing an owned step;
- moving a failed step to `WaitingForRetry`;
- recovering stale running steps;
- applying terminal finalization;
- enforcing distributed concurrency lease acquisition;
- creating / checking / releasing admission reservations.

This ensures that multiple workers, runtime instances, pumps, or control-plane loops can compete safely without corrupting state.

---

## Atomic Step Claiming

Step claiming is one of the most important distributed operations.

A worker can execute a step only if it successfully claims it.

A claim operation must verify:

- the execution exists;
- the step exists;
- the step is eligible;
- all dependencies are completed;
- the step is not already owned;
- retry timing allows execution;
- execution control state does not block claims;
- concurrency admission has succeeded;
- distributed capacity is available when required.

If the claim succeeds:

- the step moves to `Running`;
- the worker receives ownership;
- a claim token is stored;
- the claim timestamp is recorded.

Only the owning worker can complete or fail that step.

---

## Claim Tokens and Ownership

Each claimed step is protected by a claim token.

The claim token must be provided when the worker attempts to:

- complete the step;
- fail the step;
- schedule retry;
- persist terminal step transition.

If the token does not match, the update is rejected.

Example:

```text
Worker A claims step
    ↓
Worker A becomes slow or crashes
    ↓
Recovery releases the stale running step
    ↓
Worker B claims and completes the step
    ↓
Worker A later wakes up and tries to complete
    ↓
Claim token mismatch
    ↓
Update rejected
```

This protects the execution from stale ownership corruption.

---

## Worker Crash Recovery

Distributed execution must assume workers can crash.

When a worker claims a step, the step is associated with claim timing metadata.

If the worker crashes while the step is `Running`, the step may remain in running state.

Recovery logic detects stale running work.

A stale running step can be moved back to `Ready` when its ownership window expires.

Important distinction:

```text
Retry
    step logic failed

Recovery
    worker ownership disappeared or became stale
```

Recovery must not consume retry budget.

A typical worker crash flow is:

```text
Step is Ready
    ↓
Worker A claims step
    ↓
Step becomes Running
    ↓
Worker A crashes before completion
    ↓
Step remains Running
    ↓
Recovery detects stale ownership
    ↓
Step returns to Ready
    ↓
Worker B claims step
    ↓
Worker B completes step
```

The runtime guarantee is:

- the step does not remain stuck forever;
- no invalid completion is accepted;
- retry count is not consumed by crash recovery;
- the execution can continue.

---

## Runtime Process Crash Recovery Is Different

A worker crash and a runtime process crash are not the same failure.

Worker crash recovery handles stale ownership inside an existing execution.

Runtime process crash recovery handles all work assigned to a runtime instance that became unsafe.

A runtime process crash may include:

```text
1 InFlightExecution
    DAG execution already exists
    ExecutionId exists
    recovery must resume same ExecutionId

2 LocalQueued
    shared run was dispatched to the runtime local queue
    DAG execution has not started
    no ExecutionId exists yet
    recovery must redispatch existing SharedRunId
```

The runtime process crash recovery flow is:

```text
RuntimeInstanceOnly process stops heartbeating / is killed
    ↓
RuntimeInstanceHealthReconciler marks capacity unsafe
    ↓
Admission stops selecting unsafe runtime capacity
    ↓
Execution recovery reconciler enumerates assigned work
    ↓
InFlightExecution work is requeued for resume
    ↓
LocalQueued shared runs are requeued for redispatch
    ↓
Replacement tenant-visible runtime capacity is selected or created
    ↓
Recovered work is dispatched to replacement runtime
    ↓
Replay / ledger / trace / forensics proof is validated
```

This is validated with real process-host runtime processes, not only in-memory simulation.

---

## Shared Queue Distributed Dispatch

Distributed execution starts before the DAG exists.

A shared run can be submitted and queued before any local `RunId` or durable `ExecutionId` exists.

Shared queue dispatch protects the distributed assignment boundary.

```text
SharedRunStore
    owns durable shared submission

SharedQueue
    owns pending / claimed / dispatched shared queue item

SharedQueuePump
    claims queue items

Dispatch-time admission
    selects tenant-visible runtime capacity

Runtime provider
    delivers the run to the selected runtime local queue

Runtime local queue
    creates LocalRunId and later ExecutionId
```

Important invariant:

```text
A shared queue item must not be marked Dispatched unless provider dispatch succeeded.
```

If provider dispatch fails, the shared queue item can be requeued according to policy.

This protects the system from losing runs during distributed dispatch.

---

## Pump Identity vs Assigned Runtime Identity

The shared queue pump has its own identity.

```text
PumpRuntimeInstanceId
PumpWorkerId
```

These identify who is draining the shared queue.

They do not necessarily identify who receives the run.

```text
PumpRuntimeInstanceId
    the participant executing the pump cycle

AssignedRuntimeInstanceId
    the runtime instance selected by admission as the dispatch target
```

This is required for:

- MCP manual drain;
- control-plane-only hosts;
- local runtime providers;
- HTTP runtime providers;
- gRPC runtime providers;
- future Kubernetes control-plane/runtime-pod separation.

The control-plane host must not become the dispatch target simply because it executed the pump.

---

## Durable Tenant Context

Distributed execution is multi-tenant.

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

Not:

```text
ContextKey
metadata["tenant"]
runtime instance prefix alone
```

Every asynchronous hop must carry or restore the snapshot before tenant-sensitive operations.

This includes:

- shared run creation;
- shared run Redis persistence;
- shared queue dispatch;
- scale-out request creation;
- scale-out watcher handling;
- runtime host creation request;
- runtime registry filtering;
- runtime capacity filtering;
- provider dispatch;
- runtime local queue enqueue;
- background controller execution;
- replay / ledger / trace / forensics queries.

The shared queue dispatcher must restore the persisted `ExecutionContextSnapshot` before dispatch-time admission.

This prevents background pumps from using an ambient or missing tenant context.

---

## Multi-Tenant Runtime Visibility

Distributed execution must select runtime capacity through tenant-aware visibility.

The validated visibility rules are:

```text
Shared runtime:
    visible to Shared tenants
    visible to Hybrid/Dedicated tenants only when shared fallback is allowed

Dedicated runtime:
    visible only when TenantId or TenantGroupId matches

Hybrid runtime:
    visible only when TenantId or TenantGroupId matches
    AllowSharedFallback does not make an unowned Hybrid runtime visible
```

A safe distributed system must not route tenant A work to tenant B capacity.

This applies to normal dispatch and recovery dispatch.

```text
Normal dispatch
    must select tenant-visible capacity

Recovery dispatch
    must select tenant-visible replacement capacity
```

---

## Runtime Instance Provider Model

Admission decides whether capacity exists and which runtime should receive the run.

The provider decides how to contact that runtime.

```text
Admission
    decides WHO receives work
    or whether SCALE-OUT is required

Provider router
    decides HOW to contact selected runtime

Provider
    performs transport-specific dispatch/control/status/scale-out

Runtime local queue
    receives the run

DAG engine
    executes durable steps
```

Current validated providers include:

```text
local
http
grpc
```

Provider metadata should include stable keys such as:

```text
provider.name = local | http | grpc
transport.name = in-memory | http | grpc
transport.endpoint = ...
```

Provider transport must not replace tenant isolation.

Provider transport must not replace durable execution.

Provider transport must deliver work into the selected runtime instance local queue.

---

## HTTP and gRPC Distributed Dispatch

HTTP and gRPC are transport providers.

They are not DAG engines.

They are not recovery owners.

They deliver control-plane commands to a runtime instance.

Validated provider shapes:

```text
Control Plane
    ↓
HTTP Runtime Provider
    ↓
RuntimeInstanceOnly process / HTTP command endpoint
    ↓
Runtime local queue
    ↓
DAG execution
```

```text
Control Plane
    ↓
gRPC Runtime Provider
    ↓
RuntimeInstanceOnly process / gRPC command service over HTTP/2
    ↓
Runtime local queue
    ↓
DAG execution
```

The same shared queue and tenant-aware dispatch model works for both.

The process-host recovery scenarios now use a provider-agnostic base and validate the same recovery contract over HTTP and gRPC.

---

## Runtime Host Manager Boundary

Provider scale-out can create real runtime process capacity through the Runtime Host Manager.

```text
Scale-out watcher
    ↓
Scale-out provider selector
    ↓
HTTP or gRPC runtime provider
    ↓
Provider-specific scale-out provisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
real RuntimeInstanceOnly child process
    ↓
runtime self-registers
    ↓
runtime publishes heartbeat / capacity
```

The provider participates in dispatch and scale-out.

The Host Manager owns process creation or attachment mechanics.

The runtime self-registers.

The registry and capacity stores decide when capacity is visible.

Execution recovery is owned by the recovery reconciler, not by the provider or Host Manager.

---

## Scale-Out and Distributed Dispatch

When no tenant-visible runtime capacity exists, admission can request scale-out.

```text
Submit run
    ↓
Admission = RequestScaleOut
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Scale-out request persisted in Redis
    ↓
Scale-out watcher observes request
    ↓
Provider selector resolves providerHint
    ↓
Provider creates or requests capacity
    ↓
Runtime registers and publishes capacity
    ↓
Scale-out request marked Fulfilled
    ↓
Original SharedRun is requeued
    ↓
Shared queue pump dispatches through normal path
```

Important rule:

```text
The scale-out watcher does not dispatch the run directly.
```

Fulfillment creates capacity.

Dispatch remains owned by the shared queue pump and dispatch-time admission.

This prevents scale-out from bypassing queue ownership, tenant context restore, provider dispatch safety, and admission reservations.

---

## Provider-Agnostic Process-Host Recovery

The real process-host crash recovery scenarios are now provider-agnostic.

The shared recovery test base validates:

```text
real RuntimeInstanceOnly process starts
    ↓
run is dispatched to the process through selected provider
    ↓
DAG reaches crash threshold
    ↓
runtime process is killed
    ↓
unsafe capacity is suppressed
    ↓
assigned work is recovered
    ↓
replacement runtime capacity is selected or created
    ↓
same ExecutionId resumes for in-flight work
    ↓
local queued work is redispatched through SharedRunId
    ↓
ledger / trace / replay / forensics proof is validated
```

Validated wrappers include:

```text
Http_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

The provider can change.

The recovery invariant must not change.

---

## Safe Tenant Non-Impact

The strongest distributed execution proof is multi-tenant crash isolation.

The validated scenario shape is:

```text
One shared control plane
Three tenant-scoped real RuntimeInstanceOnly processes
Tenant A runtime process killed
Tenant B runtime process killed
Tenant C runtime process not killed
```

At crash time, each impacted tenant has:

```text
1 InFlightExecution
2 LocalQueued shared runs
```

The safe tenant remains alive.

Expected proof:

```text
Tenant A recovered work = 3
Tenant B recovered work = 3
Tenant C recovered work = 0

Tenant A recovery forensics > 0
Tenant B recovery forensics > 0
Tenant C recovery forensics = 0

SafeTenantNonImpactValidated = true
SafeTenantRecoveryLeakDetected = false
CrossTenantLedgerLeakDetected = false
```

This proves recovery is not a global panic button.

It is scoped to unsafe runtime instances and their assigned work.

---

## Multi-Worker Execution

Multiple workers can safely process the same execution because they coordinate through shared state.

Workers do not communicate directly with each other.

They coordinate through Redis.

```text
Worker 1 ─┐
Worker 2 ─┼── Redis hot state / Lua coordination ── Execution state
Worker 3 ─┘
```

This reduces coupling and allows workers to remain stateless.

---

## RunId, SharedRunId, LocalRunId, and ExecutionId

The runtime separates control-plane, local queue, and durable DAG identities.

```text
SharedRunId
    shared/control-plane submission identity

LocalRunId
    runtime-local queue attempt identity

RunId
    local controller/job lifecycle identity where applicable

ExecutionId
    authoritative durable DAG execution identity
```

This separation is critical.

A `SharedRunId` can exist before a local run exists.

A `LocalRunId` can exist before an `ExecutionId` exists.

An `ExecutionId` is the durable DAG execution identity.

In-flight recovery preserves `ExecutionId`.

Local-queued recovery preserves `SharedRunId` and creates a new local attempt.

---

## Distributed Execution Modes

The runtime supports two important DAG execution models.

```text
Model 1:
multiple isolated executions
unique ExecutionId per run
safe snapshot/replay per execution
```

```text
Model 2:
one ExecutionId
multiple runtime workers or runtime instances
shared distributed DAG execution
workers competing safely for the same execution's steps
```

The distributed shared-execution model relies on:

- Redis-backed DAG state;
- atomic step claiming;
- lease-based ownership;
- deterministic convergence;
- bounded batch execution;
- idempotent terminal lifecycle handling;
- archive-backed resolver reconstruction after retention.

---

## Multi-Runtime-Instance Execution

In distributed multi-runtime-instance execution, multiple runtime workers can safely advance the same `ExecutionId` through Redis-backed DAG coordination.

Current validated behavior includes:

- strict `RunId` / `ExecutionId` separation;
- strict `SharedRunId` / `LocalRunId` / `ExecutionId` separation;
- isolated execution state per independent `ExecutionId`;
- shared execution mode for one `ExecutionId`;
- Redis-backed DAG coordination;
- atomic ownership through Lua;
- bounded batch execution;
- retry-safe multi-worker execution;
- idempotent terminal lifecycle processing;
- terminal snapshot support;
- retention and replay compatibility;
- archive-backed resolver reconstruction after retention;
- process-boundary replay / ledger / trace validation;
- tenant-scoped process-host crash recovery.

---

## Retry-Aware Distributed Execution

Retry behavior is coordinated through runtime state.

When a step fails and retry is allowed:

- retry count is updated;
- next retry time is calculated;
- step moves to `WaitingForRetry`;
- the step cannot be claimed again until retry time opens.

Workers must respect retry state.

A retry-ready step becomes eligible only when:

```text
UtcNow >= NextRetryAtUtc
```

Retry is business/execution failure handling.

Runtime crash recovery is infrastructure failure handling.

They must not be confused.

---

## Concurrency Before Claim

Concurrency admission is enforced before DAG step ownership is claimed.

A safe distributed flow is:

```text
Resolve pipeline + step config.concurrency
    ↓
Create AiConcurrencyContext
    ↓
Apply matching concurrency.throttle rules
    ↓
Evaluate configured admission policies
    ↓
Acquire Redis concurrency lease
    ↓
Attempt DAG claim
```

Important rules:

- if policy admission is denied, no Redis concurrency lease is acquired;
- if Redis admission is denied, no DAG claim is attempted;
- if Redis admission succeeds but the DAG claim fails, the concurrency lease is released immediately;
- if the DAG claim succeeds, the lease remains owned until step execution finishes.

This prevents leaked distributed capacity.

---

## Dispatch-Time Admission and Reservation

Shared queue dispatch re-evaluates admission at drain time.

```text
Shared queue item claimed
    ↓
Shared run loaded
    ↓
ExecutionContextSnapshot restored
    ↓
Tenant-visible registry/capacity listed
    ↓
Admission selects runtime target
    ↓
Admission reservation may protect selected capacity
    ↓
Provider dispatch attempted
    ↓
Reservation released or allowed to expire if dispatch fails
```

This model supports:

- local provider dispatch;
- HTTP provider dispatch;
- gRPC provider dispatch;
- process-host runtime dispatch;
- future Kubernetes runtime pod dispatch;
- multi-control-plane hardening.

Admission reservations protect selected runtime capacity during heavy distributed dispatch.

---

## Control-State-Aware Execution

Distributed workers must respect execution control state.

The control gate may block claims for statuses such as:

- `Pausing`;
- `Paused`;
- `WaitingForInput`;
- `Cancelling`;
- `Cancelled`.

This means pause, cancel, and human-in-the-loop control can stop new claims without corrupting DAG state.

Already claimed work may drain safely depending on the control scenario.

---

## Terminal Finalization

Distributed finalization must be idempotent.

More than one worker may observe that an execution appears terminal.

Finalization must ensure:

- terminal state is persisted once;
- snapshots are created consistently;
- cancellation override is respected;
- cleanup does not remove required replay or resolver data too early;
- final status remains deterministic.

This is especially important when cancellation races with natural DAG completion.

---

## Deterministic Convergence Under Concurrency

The distributed execution model is designed to converge deterministically.

The final execution result should not depend on:

- which worker claimed a step;
- which worker finished first;
- how many workers were active;
- which runtime process hosted the local queue;
- whether dispatch used local, HTTP, or gRPC;
- whether a stale worker was recovered;
- whether an entire runtime process was recovered;
- whether retry timing created a different scheduling order;
- whether retention compacted or externalized completed payloads.

The final result is derived from state and valid transitions.

This is the core difference between simply running distributed work and building deterministic execution infrastructure.

---

## Distributed Execution Flow

A simplified distributed execution flow is:

```text
Start execution
    ↓
Create DAG state
    ↓
Workers poll or receive execution work
    ↓
Recover stale running steps
    ↓
Find eligible ready steps
    ↓
Apply execution control gate
    ↓
Resolve concurrency configuration
    ↓
Create concurrency context
    ↓
Apply throttle rules
    ↓
Evaluate policy admission
    ↓
Acquire distributed concurrency lease
    ↓
Claim step atomically
    ↓
Execute step
    ↓
Complete / fail / schedule retry atomically
    ↓
Release concurrency lease
    ↓
Apply retention if required
    ↓
Evaluate convergence
    ↓
Finalize execution if terminal
```

A simplified distributed shared-run flow is:

```text
Submit shared run
    ↓
Persist ExecutionContextSnapshot
    ↓
Queue or request scale-out
    ↓
Shared queue item claimed
    ↓
Tenant context restored
    ↓
Admission selects tenant-visible runtime
    ↓
Provider dispatches to selected runtime
    ↓
Runtime local queue creates LocalRunId
    ↓
DAG execution creates ExecutionId
    ↓
Distributed workers advance steps safely
```

A simplified process crash recovery flow is:

```text
Runtime process dies
    ↓
Heartbeat/capacity becomes unsafe
    ↓
Admission stops selecting that runtime
    ↓
Assigned work is enumerated
    ↓
In-flight work resumes same ExecutionId
    ↓
Local-queued work redispatches same SharedRunId
    ↓
Replacement tenant-visible capacity handles recovered work
    ↓
Replay / ledger / trace / forensics prove recovery
```

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Shared runtime controller | Owns shared submission lifecycle and initial admission outcome. |
| Shared run store | Persists durable shared run identity and `ExecutionContextSnapshot`. |
| Shared queue | Owns distributed pending / claimed / dispatched shared queue state. |
| Shared queue pump | Claims shared queue work and runs dispatch cycles. |
| Shared queue dispatcher | Restores context, re-admits, reserves capacity, and dispatches through provider. |
| Admission controller | Selects tenant-visible runtime capacity or requests scale-out. |
| Admission reservation store | Protects selected capacity during distributed dispatch. |
| Scale-out request store | Persists provider-capable capacity requests. |
| Scale-out watcher | Observes requests and delegates capacity creation to providers. |
| Runtime provider selector | Resolves local / HTTP / gRPC provider paths. |
| Runtime provider | Delivers work to selected runtime instance and reports transport failure. |
| Runtime Host Manager | Creates or attaches runtime hosts for process-host scale-out. |
| Runtime instance registry | Tracks runtime identity, role, status, heartbeat, and tenant metadata. |
| Runtime capacity store | Tracks live dispatch capacity and worker/run availability. |
| Runtime health reconciler | Suppresses unsafe runtime capacity from new routing. |
| Execution recovery reconciler | Recovers work already assigned to unsafe runtime instances. |
| DAG execution engine | Evaluates dependencies and convergence. |
| Redis DAG store | Stores active execution state and applies atomic transitions. |
| Redis Lua scripts | Protect critical distributed state changes. |
| Worker / runner | Executes claimed steps and reports results. |
| Retry engine | Decides retry behavior from config and policies. |
| Concurrency engine | Applies admission and distributed capacity limits. |
| Redis concurrency gate | Enforces distributed concurrency through lease-based scopes. |
| Execution control gate | Blocks or allows advancement based on control state. |
| Retention coordinator | Keeps hot state bounded while preserving required data. |
| Resolver | Reconstructs compacted or evicted data when needed. |
| Finalization service | Persists terminal state and snapshots safely. |
| Ledger / trace / replay / forensics | Proves execution and recovery after convergence. |

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Worker crashes while running a step | Recovery returns stale running step to eligible state. |
| Multiple workers claim same step | Redis Lua allows only one winner. |
| Stale worker completes late | Claim token mismatch rejects update. |
| Step fails transiently | Retry state moves step to `WaitingForRetry`. |
| Retry window not open | Step is not claimable yet. |
| Execution is paused | New claims are blocked. |
| Execution is waiting for input | New claims are blocked until input is submitted. |
| Execution is cancelled | Finalization resolves to `Cancelled`. |
| Concurrency capacity is denied | No DAG claim is attempted. |
| Concurrency lease acquired but claim fails | Lease is released immediately. |
| Shared queue item is claimed concurrently | Only one dispatcher wins the claim. |
| Provider dispatch fails | Queue item is not marked dispatched and can be requeued. |
| Runtime process dies before DAG starts | Existing `SharedRunId` is redispatched. |
| Runtime process dies during DAG execution | Existing `ExecutionId` is resumed. |
| Runtime process dies for tenant A | Tenant B/C capacity remains isolated. |
| Safe tenant runs during impacted tenant recovery | Safe tenant completes normally with zero recovery records. |
| Large payload is evicted | Resolver reconstructs from persistent storage. |
| Multiple workers observe terminal state | Finalization remains idempotent. |

---

## Validated Evidence

Validated evidence includes:

```text
Redis Lua atomic step claim
Redis Lua atomic shared queue claim
Redis shared run store
Redis shared queue
Redis admission reservation store
Redis scale-out request store
Runtime registry and capacity visibility
Tenant-aware registry filtering
Tenant-aware capacity filtering
Dispatch-time ExecutionContextSnapshot restore
Local runtime scale-out from zero executable capacity
HTTP pooled runtime dispatch
HTTP process-host dispatch
gRPC process-host dispatch
Runtime Host Manager process launch
real RuntimeInstanceOnly process registration
real process-host runtime crash recovery
provider-agnostic HTTP/gRPC recovery scenarios
safe tenant non-impact proof
replay / ledger / trace proof after recovery
runtime recovery forensics proof
```

Representative recovery evidence:

```text
InFlightExecution resumes the same ExecutionId.
LocalQueued work is recovered through SharedRunId redispatch.
Recovered work count matches impacted work.
Safe tenant recovered work = 0.
Safe tenant recovery forensics = 0.
Cross-tenant ledger leak = false.
Strict replay validation passes after recovery.
```

---

## Current Status

| Capability | Status |
|---|---|
| Redis-backed hot execution state | Implemented / validated |
| Redis Lua atomic step claiming | Implemented / validated |
| Claim token ownership | Implemented / validated |
| Retry-aware distributed state | Implemented / validated |
| Worker crash recovery | Implemented / validated |
| Bounded batch execution | Implemented / validated |
| Distributed workers | Implemented / validated |
| RunId / ExecutionId separation | Implemented / validated |
| SharedRunId / LocalRunId / ExecutionId separation | Implemented / validated |
| Runtime queue control integration | Implemented / validated |
| Execution control gate integration | Implemented / validated |
| Distributed multi-runtime-instance execution | Implemented / validated foundations |
| Redis shared run store | Implemented / validated |
| Redis shared queue | Implemented / validated |
| Dispatch-time admission | Implemented / validated |
| Dispatch-time ExecutionContextSnapshot restore | Implemented / validated |
| Tenant-aware runtime visibility | Implemented / validated |
| Redis admission reservation | Implemented / validated |
| Provider-based dispatch | Implemented / validated |
| Local runtime provider | Implemented / validated |
| HTTP runtime provider | Implemented / validated |
| gRPC runtime provider | Implemented / validated |
| HTTP process-host runtime dispatch | Implemented / validated |
| gRPC process-host runtime dispatch | Implemented / validated |
| Runtime Host Manager process launch | Implemented / validated |
| Runtime health vs execution recovery boundary | Implemented / validated |
| Runtime process crash recovery | Implemented / validated |
| In-flight DAG resume with same ExecutionId | Implemented / validated |
| Local-queued SharedRunId redispatch | Implemented / validated |
| Multi-tenant safe-tenant non-impact | Implemented / validated |
| Replay / ledger / trace after recovery | Implemented / validated |
| Runtime recovery forensics | Implemented / validated |
| Retention and replay compatibility | Implemented / validated foundations |
| Archive-backed resolver reconstruction after retention | Foundation available |
| Kubernetes deployment scenario | Planned |
| Production multi-control-plane leadership | Planned |
| Public dashboard / operational UI | Planned |

---

## Current Limitations

The current validated model does not yet claim:

```text
Kubernetes pod crash recovery
Kubernetes autoscaling implementation
production multi-control-plane leader election
Byzantine runtime protection
durable-store corruption recovery
full Redis command queue provider
global throughput guarantees
public dashboard UX
```

gRPC is no longer a future-only provider in this document.

The validated gRPC process-host path exists, but provider-aware gRPC readiness should still be hardened beyond the temporary process-host scenario configuration.

---

## Design Rules

### Do

```text
Use ExecutionContextSnapshot.TenantId as durable tenant boundary.
Restore tenant context before admission, provider dispatch, recovery, and observability queries.
Keep SharedRunId, LocalRunId, RunId, ExecutionId, and RuntimeInstanceId separate.
Use Redis Lua for critical distributed transitions.
Use admission and tenant-visible capacity before provider dispatch.
Use provider metadata only for transport routing, not tenant isolation.
Treat runtime local queues as volatile.
Recover in-flight work by preserving ExecutionId.
Recover local-queued work by preserving SharedRunId.
Validate replay / ledger / trace after recovery.
Validate safe tenants have normal execution evidence and zero recovery evidence.
```

### Do Not

```text
Do not let workers own workflow truth.
Do not let runtime processes own durable workflow truth.
Do not treat the local queue as durable recovery state.
Do not collapse SharedRunId and ExecutionId.
Do not create a new ExecutionId for recovered in-flight DAGs.
Do not mark shared queue items dispatched before provider dispatch succeeds.
Do not let transport providers own execution recovery.
Do not route recovery globally across tenants.
Do not use ContextKey as durable tenant partition.
Do not describe gRPC provider as future-only.
```

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Execution Control State](execution-control-state.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

Do not reduce distributed execution to only worker-level Redis step claiming.

The validated distributed execution model includes:

```text
distributed workers
+ distributed shared queue dispatch
+ tenant-aware runtime visibility
+ provider-based transport
+ process-host runtime boundaries
+ runtime health suppression
+ assigned-work recovery
+ replay / ledger / trace / forensics proof
+ safe-tenant non-impact validation
```

This is the real strength of the multi-tenant runtime engine.
