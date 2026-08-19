# Architecture Overview
Status: Implemented architecture foundation / validated with shared controller, MCP, Redis coordination, tenant-aware HTTP/gRPC Process and Kubernetes hosting, ProcessHostPool and KubernetesPool warm capacity, hierarchical child and full-boundary recovery, shared durable failure authority, lifecycle history, replay, ledger, trace, forensics, and multi-tenant isolation.
Status: Implemented architecture foundation / validated with shared controller, MCP, Redis coordination, Redis-backed scale-out request persistence, local runtime pools, local runtime scale-out, fulfilled-run requeue, HTTP pooled and process-host runtime provisioning, gRPC process-host runtime provisioning, Kubernetes Runtime Host Manager provisioning through Fake and Kubernetes SDK clients, Kubernetes Pod/Service readiness, HTTP/gRPC transport preservation, tenant-aware runtime isolation, end-to-end scale-out execution, real process and Kubernetes Pod crash recovery, tenant-isolated recovery reconciliation, runtime recovery forensics, control-plane ledger causal chain evidence, and replay / ledger / trace validation after recovery.

This document provides a high-level overview of the **Deterministic AI Runtime** architecture.

It also reflects the current control-plane evolution: shared queue pump, queue-first submit mode, direct-dispatch scale-out mode, dispatch-time admission, tenant-aware admission, runtime instance providers, MCP control-plane integration, Redis discovery/registry/capacity coordination, tenant-filtered runtime visibility, Redis-backed scale-out request coordination, admission reservations, local runtime scale-out, fulfilled-run requeue, HTTP pooled runtime hosting, HTTP process-host runtime provisioning, gRPC runtime provider dispatch, gRPC process-host runtime provisioning, runtime instance health reconciliation, execution recovery reconciliation, runtime recovery forensics, control-plane ledger tracing, replay / ledger / trace recovery proof, safe-tenant non-impact validation, and worker-capacity visibility.

---

## Purpose

The Deterministic AI Runtime is designed to execute production-grade AI workflows as controlled, observable, recoverable, and distributed execution systems.

It is not only a framework for calling AI providers.

It is an execution runtime responsible for:

- orchestrating DAG-based workflows
- coordinating distributed workers
- managing execution state
- resolving execution and step context
- enforcing retry and recovery rules
- controlling memory growth
- applying concurrency and throttling policies
- supporting replay and audit foundations
- exposing observability and tracing foundations
- exposing runtime control-plane operations
- coordinating shared queue dispatch
- supporting queue-first shared run submission
- managing runtime instance visibility
- publishing and resolving control-plane discovery
- publishing runtime capacity descriptors
- reserving runtime capacity during dispatch
- exposing runtime worker capacity
- persisting and processing scale-out requests
- creating local runtime capacity dynamically through provider-based scale-out
- requeueing fulfilled scale-out runs for normal shared queue dispatch
- supporting provider-based local, local scale-out, HTTP pooled runtime dispatch, and gRPC runtime dispatch
- carrying durable tenant context across MCP, shared queue, scale-out, dispatch, and runtime execution
- enforcing shared, dedicated, and hybrid runtime isolation during admission and dispatch
- detecting unsafe runtime instances through heartbeat/health reconciliation
- recovering assigned work from failed runtime instances without making the transport provider own recovery
- resuming in-flight DAG executions with the same durable `ExecutionId`
- redispatching volatile local-queued work through durable `SharedRunId` state
- proving safe-tenant non-impact during multi-tenant runtime crashes
- emitting runtime recovery forensics, control-plane ledger evidence, trace evidence, and replay proof after recovery across HTTP and gRPC transport providers

The core idea is simple:

> AI orchestration becomes a distributed systems problem once AI moves to production.

---

## High-Level Architecture

At a high level, the runtime is composed of the following layers:

```text
Client / API / MCP Layer
        ↓
RBAC / Execution Context Snapshot Layer
        ↓
Control Plane and Shared Queue Layer
        ↓
Scale-Out Request and Requeue Layer
        ↓
Runtime Health and Execution Recovery Layer
        ↓
Discovery / Registry / Capacity Layer
        ↓
Runtime Provider Dispatch / Scale-Out Layer
        ↓
Runtime Orchestration Layer
        ↓
Pipeline Resolution Layer
        ↓
Context Resolution and Helper Layer
        ↓
DAG Execution Engine
        ↓
Runtime Instance and Worker Capacity Layer
        ↓
Distributed Coordination Layer
        ↓
Step Execution / Policy / Resolver Layer
        ↓
Persistence / Retention / Observability
```

Each layer has a specific responsibility and is intentionally separated from the others.

This separation keeps the runtime modular, testable, extensible, and deterministic.

---

## The Context Resolution Layer Is Central

The context resolution and helper layer is the connective tissue of the runtime.

It transforms declarative pipeline configuration and runtime execution state into the concrete contexts required by:

- step executors
- RAG providers
- input bindings
- payload resolvers
- retry policies
- retention policies
- concurrency policies
- distributed throttling
- replay validation
- observability

Without this layer, every engine, runner, plugin, policy, and provider would need to manually reconstruct context from raw execution state.

The core model is:

```text
Pipeline definition
        +
runtime execution state
        +
payload references
        +
step configuration
        ↓
Context Resolution and Helpers
        ↓
resolved inputs
step execution context
provider/model/operation context
policy context
concurrency context
retention context
RAG retrieval context
replay-safe context
```

This layer allows the runtime to stay clean:

```text
DAG engine coordinates execution.
Context helpers prepare runtime context.
Step plugins execute domain behavior.
Policies decide runtime behavior.
Redis/Mongo persist state safely.
```

---

## Runtime Pool Hosting Layer

The architecture includes explicit ProcessHostPool and KubernetesPool hosting. Both preserve independent runtime identity inside a larger physical failure boundary.

```text
PoolId
    HostId / ProcessHost or Pod incarnation
        RuntimeInstanceId A1
        RuntimeInstanceId A2
        RuntimeInstanceId A3
```

The key invariant is:

```text
physical failure boundary != execution identity
```

A child runtime can fail and be replaced while its parent and healthy siblings survive. A complete ProcessHost or Pod can also fail, in which case only the exact failed membership is suppressed and recovered.

Failure facts are durable through the Runtime Pool Failure Journal; infrastructure history is append-only through the Runtime Lifecycle Journal. Recovery continues to preserve durable `ExecutionId` and `SharedRunId` ownership rather than replaying the entire pool.

The same hierarchical contract is validated across:

```text
gRPC + ProcessHostPool   PASS
HTTP + ProcessHostPool   PASS
gRPC + KubernetesPool    PASS
HTTP + KubernetesPool    PASS
```

See:

- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)
- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)

---

## Main Runtime Layers

### 0. RBAC / Execution Context Snapshot Layer

The runtime now treats tenant context as durable execution input, not as volatile request-only metadata.

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

`ContextKey` remains useful for RBAC, correlation, debugging, and request scoping, but it is not the durable partition key for runtime execution.

Metadata may duplicate tenant values for observability, but runtime isolation must use strong fields and the execution context snapshot.

The execution context snapshot is carried through:

```text
MCP request
        ↓
RBAC execution context
        ↓
ExecutionContextSnapshot
        ↓
SharedRunRecord
        ↓
SharedQueueDispatcher context restore
        ↓
Admission and capacity filtering
        ↓
Runtime instance dispatch
        ↓
Runtime local queue
        ↓
Background controller context restore
        ↓
DAG execution
```

Every asynchronous, background, or distributed hop must either carry the snapshot or restore it before performing tenant-sensitive work.

### 1. Client / API Layer

The client or API layer is the external entry point into the runtime.

It is responsible for:

- submitting pipeline execution requests
- passing input state
- receiving execution handles
- querying execution status
- retrieving final results

This layer should not contain orchestration logic.

It delegates execution to the runtime.

---

### 2. Control Plane and Shared Queue Layer

The control-plane layer exposes operational runtime capabilities without replacing the runtime engine.

It is responsible for:

- shared run submission
- shared run visibility
- durable execution context snapshot propagation
- tenant-aware shared run admission
- queue-first submit mode
- global shared queue coordination
- shared queue pump and manual drain
- dispatch-time admission
- runtime instance registry visibility
- runtime capacity descriptors
- tenant-filtered runtime registry and capacity visibility
- Redis control-plane discovery
- control-plane id resolution
- admission reservations
- Redis-backed scale-out requests
- provider-based scale-out
- fulfilled scale-out shared run requeue
- provider-based local, HTTP, and gRPC dispatch
- runtime queue control
- execution control
- replay and observability adapters
- MCP server tool exposure
- runtime instance health visibility and unsafe-capacity suppression
- execution recovery reconciliation for work assigned to unsafe runtimes
- runtime recovery forensics and incident correlation
- control-plane ledger causal-chain evidence for scale-out, host creation, recovery, and redispatch

This layer operates above local runtime queues.

It does not execute DAG steps directly.

The shared queue provides global coordination before a run is assigned to a runtime instance.

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
Capacity Reservation / Provider Selection
        ↓
Runtime Instance Dispatch
        ↓
Local Runtime Queue
```

Queue-first mode uses this layer to persist a shared run and place it in the global queue before selecting a runtime instance.

Direct-dispatch mode can preserve an admission `RequestScaleOut` decision when no runtime capacity is available.

The validated scale-out control-plane path is:

```text
Shared Runtime Controller
        ↓
Run Admission = RequestScaleOut
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
Runtime instance registered / capacity published
        ↓
AiScaleOutFulfilledRunRequeueService
        ↓
Shared Queue
        ↓
Shared Queue Pump
        ↓
Dispatch-Time Admission
        ↓
Provider Dispatch
        ↓
Local Runtime Queue
```

The watcher does not dispatch directly.

It creates capacity and requeues the shared run.

The same scale-out and requeue control loop is now validated for real gRPC process-host runtime instances. In the gRPC path, the control plane selects the gRPC runtime provider, delegates runtime host creation through the Runtime Host Manager, starts a real `RuntimeInstanceOnly` process, waits for registry / capacity visibility, and then dispatches over gRPC into the selected runtime local queue.

The pump remains responsible for claim ownership, dispatch-time admission, provider dispatch, and queue/run state updates.

Tenant-aware dispatch adds an additional invariant:

```text
background dispatch must not depend on the ambient AsyncLocal context
```

The shared run persists `ExecutionContextSnapshot`, and `AiSharedQueueDispatcher` restores that snapshot before admission, reservation, and dispatch.

This ensures that Redis registry and capacity queries are evaluated under the correct tenant context even when work is processed by a background pump, manual drain, or future remote control-plane worker.

The control plane also separates runtime health from execution recovery.

```text
RuntimeInstanceHealthReconciler
        = detects stale / unsafe / draining runtime capacity and prevents unsafe routing

Runtime execution recovery reconciler
        = enumerates work assigned to unsafe runtime instances and recovers it

HTTP provider
        = reports transport and endpoint failure signals; it does not own recovery
```

This boundary is validated by real process-host crash recovery scenarios. When a runtime process stops heartbeating, the control plane marks the runtime unsafe, suppresses unsafe capacity from admission, reconciles assigned work, selects or creates replacement tenant-visible capacity, resumes in-flight executions, and redispatches local-queued work through durable shared-run state.

The provider may report failures such as `http-circuit-open`, `http-dispatch-timeout`, `http-provider-unavailable`, or `grpc-circuit-open`, but the provider must not directly kill, restart, or recover runtime instances. Runtime replacement belongs to the lifecycle owner, and assigned-work recovery belongs to the execution recovery reconciler.

The current validated control-plane model also includes Redis-backed coordination components:

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
Shared Queue Pump Readiness
        ↓
Provider Dispatch
```

The MCP control plane publishes the logical control-plane identity.

Runtime-only hosts resolve that identity before registration and capacity publication.

This ensures that MCP, shared queues, runtime registry entries, and capacity descriptors all use the same logical Redis/control-plane scope.


---

### 3. Runtime Health and Execution Recovery Layer

Runtime health and runtime execution recovery are related but separate responsibilities.

The health layer answers:

```text
Is this runtime instance safe to route new work to?
```

The execution recovery layer answers:

```text
What work was already assigned to an unsafe runtime, and how must it be recovered?
```

A runtime instance can become unsafe when heartbeat or endpoint health is lost. The control plane must then stop using that capacity for new admission decisions. That does not automatically recover assigned work. Assigned work must be reconciled from durable control-plane state.

The validated recovery model separates assigned work into two categories.

```text
InFlightExecution
    = DAG execution already exists
    = durable ExecutionId already exists
    = recovery must resume the same ExecutionId on replacement capacity

LocalQueued
    = shared run was dispatched to a runtime local queue
    = DAG execution has not started yet
    = no durable ExecutionId exists yet
    = recovery must redispatch the durable SharedRunId without duplicating submission
```

The local runtime queue is intentionally not treated as durable truth. If the process dies, its local queue dies with it. Recovery reconstructs the correct state from durable control-plane records:

```text
SharedRunStore
SharedQueue
RuntimeRunExecutionIndex
DAG execution store
Runtime registry / capacity state
Ledger / trace / forensics evidence
```

Real process-host recovery scenarios validate the following flow:

```text
real RuntimeInstanceOnly process stops heartbeating
        ↓
runtime instance becomes unsafe
        ↓
unsafe capacity is suppressed from admission
        ↓
execution recovery reconciler enumerates assigned work
        ↓
in-flight executions are requeued for resume
        ↓
local queued shared runs are requeued for redispatch
        ↓
replacement tenant-visible runtime capacity is selected or created
        ↓
new LocalRun is registered on replacement runtime
        ↓
resume context is seeded when ExecutionId already exists
        ↓
DAG resumes and completes
        ↓
forensics / ledger / trace / replay evidence is validated
```

In multi-tenant crash scenarios, this layer must recover only the impacted tenants' assigned work. A safe tenant in the same control plane must continue normally, with zero recovered work, zero recovery forensics, and zero recovery contamination visible through tenant-scoped ledger queries.

---

### 4. Runtime Orchestration Layer

The orchestration layer turns an external request into a runtime execution.

It is responsible for:

- creating execution records
- assigning execution identity
- initializing execution state
- resolving pipeline definitions
- selecting the correct runtime mode
- starting execution through the DAG engine or background controller

This layer separates external lifecycle concerns from internal execution logic.

---

### 5. Pipeline Resolution Layer

Pipeline definitions describe the workflow declaratively.

Before execution, the runtime resolves the pipeline into an executable structure.

This includes:

- validating step names
- validating dependencies
- resolving step keys
- preparing input bindings
- building the DAG dependency graph
- attaching configuration such as retry, retention, and concurrency

The pipeline describes intent.

The runtime controls execution.

---

### 6. Context Resolution and Helper Layer

The context resolution layer transforms configuration and state into concrete runtime context.

It is responsible for:

- resolving input bindings
- reading values from execution state
- resolving previous step outputs
- rehydrating compacted or externalized payloads
- extracting provider/model/operation metadata
- building step execution context
- building retry policy context
- building retention policy context
- building concurrency context
- building RAG retrieval, merge, and compose context
- supporting replay-safe fingerprints and comparison helpers
- keeping orchestration classes smaller and more testable

This layer is central because almost every other runtime component depends on correctly resolved context.

A step should not manually scan raw DAG state.

A policy should not manually reconstruct provider metadata.

A RAG provider should not manually resolve upstream payloads.

The helper layer provides the resolved context.

---

### 7. DAG Execution Engine

The DAG execution engine is the core execution coordinator.

It is responsible for:

- evaluating dependency completion
- identifying ready steps
- coordinating step claims
- enforcing deterministic convergence
- handling retry-aware execution state
- finalizing execution status

The engine does not rely on a fixed execution order.

It evaluates state and advances only the steps that are eligible.

The DAG engine should remain orchestration-focused.

Context-building logic belongs in context helpers.

---

### 7A. Durable Child DAG Composition — Experimental

The DAG engine now includes a native durable Child DAG composition path for delegating work to another DAG execution without introducing a second orchestration engine.

```text
Parent ExecutionId
    ↓
execution.child-dag
    ↓
ChildExecutionId
    ↓
parent step = WaitingForExternal
    ↓
child completes durably
    ↓
deterministic continuation
    ↓
same Parent ExecutionId resumes
```

The composition path reuses the existing execution store, DAG state, Policy Engine, shared queue, dispatch, recovery, replay, Ledger, tracing, and Forensics infrastructure. The waiting parent releases its claim, concurrency lease, and runtime capacity instead of holding a physical worker while the child executes.

The capability is currently labeled **Experimental**. The full `ChildDepth = 1` gRPC Kubernetes Runtime Pool warm-reuse production proof is green, but promotion beyond Experimental is intentionally blocked on complete engine lifecycle observation and deeper nested closure. Lifecycle Events, durable Ledger evidence, and Forensics must expose the same child-completion / continuation / parent-resume transitions with the same correlation identities.

See [Durable Child DAG Composition](child-dag-composition.md).

---

### 8. Runtime Instance and Worker Capacity Layer

Runtime instances are the execution participants that own local queues and workers.

A runtime instance may be local, HTTP-backed through a pooled runtime host, HTTP/gRPC-backed through a child process, or HTTP/gRPC-backed through a Kubernetes `RuntimeInstanceOnly` Pod. Redis command queues remain a separate future transport option.

Each runtime instance publishes visibility and capacity.

In the current HTTP pooled model, the parent HTTP host is transport and hosting infrastructure.

The dispatchable runtime identities are child runtime instances created by the local runtime instance pool:

```text
RuntimeInstanceOnly HTTP Host
    ↓
Local Runtime Instance Pool
    ↓
runtime-http-1
runtime-http-2
runtime-http-3
```

```text
HTTP host identity != dispatch target
runtime-http-* child instance == dispatch target
```

The local runtime instance infrastructure also supports dynamic scale-out.

A control-plane host can start with zero executable local runtime instances when the local pool startup is disabled.

Admission can then request scale-out, and the local provider/scaler can create a runtime instance on demand.

Validated local scale-out shape:

```text
MCP Control Plane
    Runtime capacity at start = 0
    ↓
Submit shared run
    ↓
Admission = RequestScaleOut
    ↓
Redis scale-out request
    ↓
Local runtime scaler
    ↓
host-...:runtime-instance-1
    ↓
Shared run requeued
    ↓
Shared queue pump dispatches
    ↓
Runtime run completed
```
Validated tenant-aware local scale-out identities:

```text
default / shared tenant
    → host-...:runtime-instance-1

tenant-a dedicated tenant
    → host-...:tenant-a-runtime-1

tenant-b hybrid tenant
    → host-...:tenant-b-runtime-1
    → or shared fallback to host-...:runtime-instance-1 when allowed
```

The local scaler must count matching runtime hosts by `RuntimeInstanceIdPrefix`, not by the global number of local hosts.

This prevents a shared runtime instance from satisfying a dedicated tenant scale-out request.


Important capacity fields include:

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

Runtime visibility is evaluated before admission can assign work:

```text
Shared runtime:
- visible to shared/default tenants
- visible to dedicated or hybrid tenants only when their tenant settings allow shared fallback

Dedicated runtime:
- visible only when TenantId or TenantGroupId matches

Hybrid runtime:
- visible only when TenantId or TenantGroupId matches
- AllowSharedFallback does not make an unowned hybrid runtime visible
```

This layer allows the control plane, MCP tools, and future dashboards to see:

- which runtime instances exist
- which runtime instances are executable
- which queues are paused
- how many run slots are available
- how many workers are active or free
- whether a runtime instance can accept another run

`MaxLocalWorkersPerExecution` limits how many local workers from one runtime instance can work on a single execution.

This prevents one execution from consuming the whole local worker pool unless explicitly configured.

---

### 9. Distributed Coordination Layer

The runtime uses Redis as the hot coordination layer.

Redis is used for:

- active execution state
- step state
- atomic step claims
- claim ownership
- retry scheduling
- recovery coordination
- distributed concurrency leases
- execution control state
- shared run store
- shared queue
- runtime instance registry
- runtime capacity store
- control-plane discovery store
- admission reservation store
- scale-out request store
- tenant-aware runtime visibility descriptors
- tenant-aware runtime settings propagated through scale-out requests

Critical transitions are protected by Redis Lua scripts.

This allows multiple workers or runtime instances to coordinate safely without duplicate step ownership.

---

### 10. Step Execution Layer

Steps are executed by registered step executors.

Each step is identified by a `stepKey`.

Examples include:

- RAG retrieval steps
- RAG merge steps
- RAG compose steps
- LLM or prompt steps
- tool/action steps
- decision steps

The DAG engine does not hardcode step behavior.

It coordinates execution, while step plugins provide the domain-specific logic.

#### Content-Agnostic Execution Boundary

The runtime owns execution semantics, not the semantic meaning of the result.

A step may execute RAG, an LLM call, vector search, an external MCP tool, an internal network service, a database operation, human approval, or code implemented in any language behind a supported adapter.

```text
step implementation
    = domain behavior and result semantics

runtime engine
    = admission, ownership, dispatch, retry, retention,
      eviction, recovery, replay, and observability
```

Real AI operations can have different latency, streaming, side-effect, retry, cost, and cancellation characteristics. Those differences affect policy and capacity configuration, but they do not redesign the runtime's durable ownership and recovery model.

> The runtime does not need to understand the answer. It needs to guarantee what happens to the execution that produced it.

Step executors receive resolved context from the context resolution layer.

---

### 11. Policy and Governance Layer

Policies provide reusable runtime decision logic.

Policy-driven behavior applies to:

- retry decisions
- retention decisions
- concurrency admission
- distributed throttling

The policy layer depends on context helpers to build correct policy context.

Examples:

```text
Retry policy context
= failure reason + retry count + step metadata + provider/model/operation

Retention policy context
= payload size + completed step count + replay requirements

Concurrency context
= pipeline + step + provider + model + operation + runtime instance
```

Policies decide.

The runtime applies state transitions safely.

---

### 12. Persistence Layer

The persistence layer stores durable execution data.

It is used for:

- large payload storage
- terminal snapshots
- replay foundations
- audit foundations
- historical inspection

MongoDB is used as the durable storage layer for large execution payloads and snapshots.

Redis remains the hot state layer.

MongoDB acts as the cold durable layer.

---

### 13. Retention and Compaction Layer

AI workflows can generate large intermediate payloads.

The retention and compaction layer keeps hot state bounded.

It supports:

- payload externalization
- compaction
- eviction
- hybrid retention
- resolver-backed rehydration

This prevents Redis from becoming an unbounded memory store.

The context resolver is what allows downstream steps to continue working even when payloads were compacted or evicted from hot state.

---

### 14. Replay and Audit Foundations

The runtime includes foundations for replay and auditability.

Current replay foundations include:

- terminal snapshots
- snapshot restoration
- deterministic replay validation
- execution fingerprints
- restored execution comparison
- MCP replay report retrieval
- MCP replay ledger retrieval
- MCP replay trace retrieval
- replay validation after process-boundary execution
- replay validation after runtime crash recovery

Replay depends on context helpers to avoid relying on volatile runtime fields such as claim tokens, leases, or worker-local state.

Replay-safe comparison should use stable execution state, payload references, and deterministic fingerprints.

The production recovery scenarios validate replay after recovery, not only replay after normal completion. A recovered execution is not considered fully proven only because the DAG completed. The system also validates ledger evidence, trace evidence, completion evidence, step completion evidence, replay report readability, replay ledger readability, replay trace readability, and strict replay validation.

---

### 15. Observability Layer

Observability is built into the runtime.

The runtime tracks:

- execution lifecycle
- step execution
- retry decisions
- recovery events
- retention actions
- resolver behavior
- context resolution failures
- distributed concurrency admission
- queue and control-plane activity
- shared queue pump activity
- runtime instance capacity
- worker capacity
- max local workers per execution
- effective worker count per execution
- scale-out request lifecycle
- fulfilled scale-out run requeue
- scale-out dispatch and completion
- runtime process crash detection evidence
- execution recovery reconciliation evidence
- runtime recovery forensics records
- runtime failure incident correlation
- control-plane causal chain ledger entries
- tenant-scoped recovery evidence and safe-tenant non-impact proof
- replay / ledger / trace proof after recovery

This allows the runtime to be inspected, tested, and eventually monitored through dashboards.

---

## Runtime Data Flow

A simplified runtime data flow is:

```text
Client / API / MCP submits pipeline run
        ↓
RBAC context is mapped to ExecutionContextSnapshot
        ↓
Control-plane identity is resolved / published when required
        ↓
Runtime registry and capacity descriptors are visible
        ↓
Shared controller may create shared run with durable ExecutionContextSnapshot
        ↓
Queue-first mode may enqueue in shared queue
        ↓
Direct-dispatch admission may request scale-out
        ↓
Scale-out watcher/provider/scaler may create runtime capacity
        ↓
Fulfilled scale-out run may be requeued
        ↓
Shared queue pump or manual drain restores ExecutionContextSnapshot
        ↓
Tenant-aware admission selects a runtime instance
        ↓
Reservation/provider dispatch path is used
        ↓
Runtime instance local queue receives run with ExecutionContextSnapshot
        ↓
If runtime process stays alive: normal local queue execution continues
        ↓
If runtime process becomes unsafe: execution recovery reconciler recovers assigned work
        ↓
Runtime background controller restores ExecutionContextSnapshot
        ↓
Runtime creates execution
        ↓
Pipeline definition is resolved
        ↓
Context helpers prepare execution and step context
        ↓
DAG engine evaluates ready steps
        ↓
Control and concurrency gates are checked
        ↓
Redis Lua claims step ownership
        ↓
Step executor receives resolved context
        ↓
Step returns result or failure
        ↓
Runtime persists transition
        ↓
Retention may compact/externalize payloads
        ↓
Resolver can rehydrate payloads later
        ↓
Finalization creates terminal state / snapshot
```

This flow keeps configuration, context, execution, distributed coordination, and persistence separated.

---

## Runtime Crash Recovery Data Flow

A simplified runtime crash recovery flow is:

```text
Tenant-scoped runs are submitted
        ↓
Runs are dispatched to tenant-visible process-host runtime instances
        ↓
Runtime A and Runtime B processes are killed or stop heartbeating
        ↓
RuntimeInstanceHealthReconciler marks those runtime instances unsafe
        ↓
Admission stops selecting unsafe capacity
        ↓
Execution recovery reconciler lists assigned work for each unsafe runtime
        ↓
InFlightExecution work is requeued for resume with the same ExecutionId
        ↓
LocalQueued work is requeued through durable SharedRunId redispatch
        ↓
Replacement tenant-visible capacity is selected or created
        ↓
Recovered work is dispatched to replacement runtime instances
        ↓
Recovered DAG executions complete
        ↓
Forensics records are closed with per-work-item timelines
        ↓
Ledger / trace / replay evidence is queried through MCP
        ↓
Safe tenant evidence proves zero recovery contamination
```

Important identity rules:

```text
ExecutionId
    durable DAG execution identity; must not change during in-flight resume

SharedRunId
    durable shared submission identity; used to redispatch local queued work

LocalRunId
    runtime-local attempt identity; may change after recovery

RuntimeInstanceId
    process/runtime capacity identity; failed and replacement runtime ids are distinct
```

This recovery flow is part of the architecture, not a fixture-only test harness. It is validated with real `RuntimeInstanceOnly` OS processes and tenant-scoped process-host runtime instances.

---

## Control Plane

The runtime includes a control-plane layer for long-running workflows.

This includes three related but separate control scopes:

```text
SharedRunId-level control
RunId-level control
ExecutionId-level control
```

`SharedRunId` belongs to the shared runtime controller and shared/global queue.

`RunId` belongs to one runtime instance local queue.

`ExecutionId` belongs to the durable DAG execution.

### SharedRunId-Level Control

SharedRunId-level control belongs to the shared runtime controller.

It manages:

- shared run records
- persisted execution context snapshots
- queue-first submit mode
- global shared queue state
- shared queue item lifecycle
- shared queue pump/manual drain
- dispatch-time tenant-aware admission
- runtime capacity reservation
- provider-based dispatch
- scale-out request persistence
- fulfilled scale-out run requeue
- assigned runtime instance id
- LocalRunId visibility after dispatch
- ExecutionId visibility after local execution starts

A shared run can exist before a local `RunId` exists.

A local `RunId` appears only after dispatch into a selected runtime instance local queue.

### Pump Identity vs Assigned Runtime Identity

The shared queue pump uses explicit pump identity:

```text
PumpRuntimeInstanceId
PumpWorkerId
```

These identify who is draining the shared queue.

They do not necessarily identify who receives the run.

```text
PumpRuntimeInstanceId
    = runtime instance executing the pump cycle

AssignedRuntimeInstanceId
    = runtime instance selected by admission for dispatch
```

This separation is required for provider-based runtime hosting, MCP manual drain, HTTP/gRPC runtime instances, and the implemented Kubernetes control-plane/runtime-Pod separation.

### RunId-Level Control

RunId-level control belongs to the background controller.

It manages:

- queued runs
- running controller jobs
- queue pause/resume
- queued run cancellation
- hot enqueue
- bridge cancellation to execution control

### ExecutionId-Level Control

ExecutionId-level control belongs to the durable runtime execution.

It manages:

- pause
- resume
- cancel
- waiting for human input
- submit human input
- claim blocking
- cancellation finalization override

This separation prevents controller lifecycle state from being mixed with durable DAG execution state.

---

## Distributed Execution Model

The runtime supports distributed execution through shared state and atomic ownership.

The model is:

```text
Multiple workers
        ↓
Read shared execution state
        ↓
Resolve execution/step context
        ↓
Attempt atomic claim
        ↓
One worker owns one step
        ↓
Execute step
        ↓
Persist result through controlled transition
```

This model enables:

- safe multi-worker execution
- no duplicate step ownership
- recovery after worker crashes
- deterministic convergence under concurrency
- runtime-local worker capacity control
- cross-instance execution assistance foundations

---

## Deterministic Convergence

A central runtime guarantee is deterministic convergence.

For the same pipeline definition and input state, the execution should converge to the same terminal state regardless of:

- worker count
- execution timing
- parallel scheduling
- retry timing
- recovery events

This is achieved through:

- explicit DAG dependencies
- state-driven scheduling
- deterministic context resolution
- Redis Lua atomic transitions
- claim ownership
- retry state
- finalization rules

---

## Configuration and Policy Model

The runtime is both config-driven and policy-driven.

Configuration defines runtime behavior through declarative sections such as:

- `config.retry`
- `config.retention`
- `config.concurrency`
- provider configuration
- model configuration
- operation configuration
- step-specific configuration

Policies provide reusable decision logic for runtime governance.

Policy-driven behavior currently applies to:

- retry decisions
- retention decisions
- concurrency admission
- distributed throttling

Context helpers connect configuration to policy execution by building the correct runtime context for each policy engine.

This allows the runtime to evolve by adding policies instead of hardcoding behavior inside the engine.

---

## Runtime Instance Provider Model

Runtime instance dispatch is moving toward a provider-based model.

Admission decides which runtime instance should receive work, under the current tenant visibility rules.

Providers decide how to contact that runtime instance.

When admission requests scale-out, the scale-out provider selector decides which provider can create or request capacity inside the required tenant runtime scope.

```text
Admission
    decides WHO or WHETHER SCALE-OUT IS NEEDED

Provider Router
    decides HOW

Provider
    performs transport-specific dispatch/control/status/scale-out operation
```

Current provider-oriented foundations include:

- local runtime instance provider
- HTTP runtime provider foundation
- HTTP pooled runtime instance hosting
- gRPC runtime provider foundation
- gRPC process-host runtime instance hosting
- Kubernetes runtime host lifecycle through Fake and Kubernetes SDK clients
- Kubernetes Pod/Service creation, readiness, publication, and termination
- optional per-runtime port-forward and shared Gateway API transport exposure
- provider-based scale-out capability
- gRPC scale-out provider capability
- tenant-aware scale-out request fields
- local runtime instance scaler scoped by runtime instance prefix
- Redis scale-out request store
- fulfilled scale-out run requeue
- runtime instance provider metadata
- Redis runtime instance registry visibility
- Redis capacity descriptor visibility
- Redis control-plane discovery
- ControlPlaneIdResolver
- Redis admission reservation store
- MCP control-plane scenarios

Validated tenant-aware local scale-out provider shape:

```text
MCP Control Plane
    ↓
Admission = RequestScaleOut
    ↓
Redis scale-out request
    ↓
Scale-out watcher
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
LocalAiRuntimeInstanceProvider
    ↓
AiLocalRuntimeInstanceScaler
    ↓
host-...:runtime-instance-1
    ↓
Shared run requeued
    ↓
Shared queue pump
    ↓
Local runtime queue
    ↓
DAG execution completed
```

Validated HTTP process-host / pooled provider shape:

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

Shared queue dispatch should validate assignment to the pooled child runtime instance, not the parent HTTP host identity.

The HTTP provider also participates in process-host scale-out through the Runtime Host Manager:

```text
MCP Control Plane
    ↓
Redis scale-out request / recovery request
    ↓
HTTP Runtime Provider
    ↓
AiHttpRuntimeScaleOutProvisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
real RuntimeInstanceOnly process
    ↓
runtime self-registration / heartbeat / capacity
    ↓
HTTP dispatch / recovery dispatch
```

The gRPC provider now validates the same process-host architecture through a typed gRPC dispatch transport:

```text
MCP Control Plane
    ↓
Redis scale-out request / recovery request
    ↓
gRPC Runtime Provider
    ↓
AiGrpcRuntimeScaleOutProvisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
real RuntimeInstanceOnly process
    ↓
runtime self-registration / heartbeat / capacity
    ↓
gRPC dispatch / recovery dispatch
```

The gRPC process-host path uses `ControlPlaneWithGrpcRuntimeInstances` for the parent control plane and `RuntimeInstanceOnly` for the child process. The child runtime publishes `provider.name = grpc` and `transport.name = grpc`, exposes the gRPC runtime command service, and requires HTTP/2 for the plaintext test process-host endpoint.

The Kubernetes host path preserves the same provider boundary:

```text
MCP Control Plane
    ↓
HTTP or gRPC Runtime Provider
    ↓
IAiRuntimeHostManager
    ↓
KubernetesAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly Pod + per-runtime Service
    ↓
Pod readiness + transport endpoint readiness
    ↓
KubernetesAiRuntimeInstancePublisher
    ↓
registry / capacity visibility
    ↓
normal HTTP or gRPC dispatch
```

Kubernetes owns Pod/Service/Gateway lifecycle. HTTP or gRPC continues to own command dispatch. The provider does not own runtime execution recovery.

Future providers may include:

- Redis command queue provider
- external scheduler or remote host-manager providers

Providers must not replace local runtime queues.

They must deliver work into the selected runtime instance local queue.

The DAG engine and workers remain the only layer responsible for durable execution.

---

## Extension Model

The runtime is extensible through step plugins.

A step plugin is typically connected through:

- a `stepKey`
- a registered executor
- class-level step metadata
- assembly-based discovery
- provider abstractions
- operation-specific configuration

This allows new runtime behavior to be added without changing the DAG engine core.

The engine remains responsible for orchestration.

Context helpers remain responsible for resolved inputs and execution context.

Plugins remain responsible for domain-specific execution.

---

## Current Architecture Status

| Area | Status |
|---|---|
| DAG execution | Implemented |
| Durable Child DAG composition / `ExecuteChildDag` | **Experimental** — implemented; full `ChildDepth = 1` warm-reuse production proof green; complete engine lifecycle observation and deeper nested closure pending |
| Redis hot state | Implemented |
| Redis Lua atomic coordination | Implemented |
| Distributed workers | Implemented |
| Distributed multi-runtime-instance execution | Implemented / validated foundations |
| Context resolution and helper layer | Implemented / foundation available |
| Input binding resolution | Implemented / foundation available |
| Payload resolver and rehydration | Implemented / validated foundations |
| Provider/model/operation context | Implemented / validated |
| Retry and recovery | Implemented |
| Retention and compaction | Implemented |
| Distributed concurrency and throttling | Implemented |
| Execution control state | Implemented |
| Runtime queue control | Implemented |
| Shared runtime controller | Implemented / validated foundations |
| Shared queue pump | Implemented / validated |
| Queue-first submit mode | Implemented / validated |
| Manual shared queue drain | Implemented / validated |
| Dispatch-time admission | Implemented / validated |
| Runtime instance provider hosting | Implemented foundations / validated local, local scale-out, and HTTP pooled scenarios |
| Local runtime instance provider | Implemented / validated |
| HTTP runtime provider foundation | Implemented / validated with pooled runtime instances |
| HTTP pooled runtime dispatch | Implemented / validated |
| Redis control-plane discovery store | Implemented / validated |
| Control-plane id resolver | Implemented / validated |
| Redis runtime instance registry | Implemented / validated |
| Redis runtime capacity store | Implemented / validated |
| Redis admission reservation store | Implemented / validated |
| Redis scale-out request store | Implemented / validated |
| Store-backed scale-out request publisher | Implemented / validated |
| Scale-out request watcher | Implemented / validated |
| Scale-out provider selector | Implemented / validated |
| Local runtime instance scaler | Implemented / validated |
| Fulfilled scale-out run requeue | Implemented / validated |
| Tenant runtime settings provider | Implemented foundation / hardcoded provider validated |
| Shared / dedicated / hybrid runtime isolation | Implemented / validated |
| Tenant-aware admission | Implemented / validated |
| Tenant-filtered runtime registry visibility | Implemented / validated |
| Tenant-filtered runtime capacity visibility | Implemented / validated |
| Tenant-aware scale-out request persistence | Implemented / validated |
| Shared queue dispatcher execution context restore | Implemented / validated |
| Direct runtime queue execution context requirement | Implemented / validated |
| MCP Redis local scale-out execution | Implemented / validated |
| Shared queue pump readiness gate | Implemented / validated |
| Runtime worker capacity visibility | Implemented / validated |
| Max local workers per execution | Implemented / validated |
| Human-in-the-loop foundations | Implemented |
| Replay and snapshot foundations | Implemented / validated foundations |
| Decision ledger foundation | Implemented foundations / validated through replay ledger scenarios |
| Replay report / ledger / trace through MCP | Implemented / validated |
| HTTP process-host runtime provisioning | Implemented / validated |
| gRPC runtime provider foundation | Implemented / validated |
| gRPC process-host runtime provisioning | Implemented / validated |
| Real runtime process crash recovery | Implemented / validated over HTTP and gRPC process-host providers |
| Runtime instance health reconciliation boundary | Implemented / validated |
| Execution recovery reconciliation boundary | Implemented / validated |
| In-flight DAG resume with same `ExecutionId` | Implemented / validated |
| Local queued redispatch through `SharedRunId` | Implemented / validated |
| Runtime recovery forensics | Implemented / validated |
| Control-plane ledger causal chain | Implemented / validated |
| Multi-tenant crash isolation / safe tenant non-impact | Implemented / validated over HTTP and gRPC process-host providers |
| Durable decision ledger hardening | Implemented / validated for current recovery/replay scenarios; ongoing for broader audit API |
| Observability dashboard | Planned |
| Kubernetes runtime host provider | Implemented / validated for Host Manager lifecycle, Kubernetes SDK Pod/Service creation, layered readiness, HTTP/gRPC transport preservation, and Pod crash-recovery scenarios |
| Full Kubernetes deployment packaging and cluster operations | Ongoing |
| Public SDK polish | Planned |
| Process-host Runtime Pool Manager | Implemented / validated |
| Independent `PoolId` / `HostId` / `RuntimeInstanceId` identity | Implemented / validated |
| Stable HTTP and gRPC pool routing | Implemented / validated |
| Immutable route incarnation and forwarding leases | Implemented / validated |
| Shared durable Runtime Pool failure journal and exact capacity suppression | Implemented / validated |
| Deterministic assigned-work claim and exact child/full-boundary recovery | Implemented / validated |
| Kubernetes Runtime Pool Pod with multiple independent child runtimes | Implemented / validated over HTTP and gRPC |

---

## Current Validated Evidence

The current architecture has been validated through MCP, Redis, local runtime pool, local scale-out, HTTP pooled and process-host scenarios, gRPC process-host scenarios, Kubernetes Host Manager scale-out, Kubernetes SDK Pod/Service readiness, real process and Pod crash recovery, runtime recovery forensics, control-plane ledger causal chain validation, and tenant-isolated recovery proof.

Tenant-aware runtime isolation evidence:

```text
tenant-a Dedicated
    IsolationMode = Dedicated
    RuntimeInstanceIdPrefix = tenant-a-runtime
    Shared fallback = disabled
    Scale-out creates tenant-a-runtime-1
    Shared runtime capacity is not used

tenant-b Hybrid
    IsolationMode = Hybrid
    RuntimeInstanceIdPrefix = tenant-b-runtime
    Shared fallback = enabled
    Scale-out can create tenant-b-runtime-1
    Shared runtime fallback is allowed only through Shared runtime visibility

default / test tenant
    IsolationMode = Shared
    RuntimeInstanceIdPrefix = runtime-instance
    Scale-out creates runtime-instance-1
```

Visibility evidence:

```text
tenant-a sees tenant-a dedicated capacity only
tenant-b sees tenant-b hybrid capacity and shared fallback capacity
default/shared tenants see shared capacity only
unowned hybrid runtime instances are not visible
```

Context propagation evidence:

```text
SharedRunRecord persists ExecutionContextSnapshot
AiSharedQueueDispatcher restores ExecutionContextSnapshot before admission and dispatch
Runtime local queue requires ExecutionContextSnapshot before background execution
Direct runtime tests now provide ExecutionContextSnapshot explicitly
```

Redis local scale-out execution evidence:

```text
Initial ActiveLocalInstances = 0
Admission = RequestScaleOut
SharedRun.Status = ScaleOutRequested
ScaleOutRequest.Status = Fulfilled
ScaleOutRuntimeInstanceId = host-...:runtime-instance-1
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

gRPC process-host evidence:

```text
ControlPlaneWithGrpcRuntimeInstances = validated
gRPC provider scale-out = validated
gRPC provider dispatch = validated
Real RuntimeInstanceOnly child process = validated
Child runtime provider.name = grpc
Child runtime transport.name = grpc
Kestrel HTTP/2 process-host endpoint = validated
gRPC single-tenant process kill recovery = validated
gRPC two-tenant crash recovery = validated
gRPC safe-tenant non-impact scenario = validated
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
Redis control-plane discovery = validated
ControlPlaneIdResolver = validated
Runtime-only host identity resolution = validated
Shutdown cleanup without late rediscovery dependency = validated
```

These validations prove the current architecture can:

- submit shared runs through MCP
- queue work globally
- drain work manually or through the background pump
- wait for runtime readiness before background dispatch
- resolve MCP/control-plane identity through Redis discovery
- dispatch through local and HTTP providers
- request scale-out through the provider model
- preserve tenant runtime settings through Redis-backed scale-out requests
- dynamically create local runtime capacity from zero executable instances
- requeue fulfilled scale-out shared runs
- dispatch scale-out requeued runs through the shared queue pump
- restore tenant execution context during background dispatch
- execute scale-out-triggered runs to completion
- assign runs to pooled child runtime instances
- expose runtime run status and execution ids
- replay completed executions
- inspect ledger and trace output
- enforce shared, dedicated, and hybrid runtime isolation through tests.


Runtime process-host crash recovery evidence over HTTP and gRPC:

```text
Real external RuntimeInstanceOnly processes = validated
Tenant A unsafe runtime process killed = validated
Tenant B unsafe runtime process killed = validated
Tenant C / safe tenant process not killed = validated
Impacted in-flight execution resumes same ExecutionId = validated
Impacted local queued work redispatched through SharedRunId = validated
Recovered work = impacted tenants only
Safe tenant recovered work = 0
Safe tenant recovery forensics = 0
Cross-tenant ledger leak = false
Safe tenant recovery leak = false
Strict replay validation after recovery = 9/9 in the full safe-tenant scenario
```

Control-plane causal chain evidence:

```text
Scale-out request persisted
Scale-out watcher observed request
Provider selected
Runtime Host Manager created host
Process runtime host started
Runtime capacity became visible
Runtime registry/capacity lookup validated
Execution recovery reconciled assigned work
Recovered work redispatched
```

Recovery forensics evidence:

```text
In-flight recovery timeline includes:
execution.recovery.candidate.detected
shared.run.requeued.for.resume
failed.local.run.marked.requeued.for.recovery
replacement.runtime.selected
replacement.local.run.registered
resume.context.seeded
dag.resume.started
dag.resume.completed
execution.recovery.completed

Local queued recovery timeline includes:
SharedRunRequeuedForLocalQueuedRecovery
failed.local.run.marked.requeued.for.recovery
replacement.runtime.selected
replacement.local.run.registered
resume.context.seeded
```

These validations prove that recovery is not treated as a global panic button. The control plane recovers assigned work for impacted unsafe runtime instances while unrelated tenant runtime capacity continues normally.


## Experimental Child DAG Validation Boundary

Native durable Child DAG composition is implemented on top of the existing runtime primitives. The current validated high-water mark is a full `ChildDepth = 1` gRPC Kubernetes Runtime Pool warm-reuse scenario with 5 Pods × 5 runtime processes, 2 cycles, 100 parent DAGs, 5,100 parent logical steps, one exact in-Pod runtime failure/recovery per cycle, one distinct busy Pod failure/recovery per cycle, replay/Ledger/trace/Forensics proof, warm reuse, bounded capacity, and deterministic cleanup.

Focused `ChildDepth = 2` scenarios validate nominal nesting and several failure boundaries, but the complete bounded warm-reuse closure is not yet green. For that reason, and because the full child-completion → continuation → parent-resume lifecycle is not yet exposed coherently across Lifecycle Events, Ledger, and Forensics, the capability remains **Experimental**.

See [Durable Child DAG Composition](child-dag-composition.md).

---

## Concurrency Hardening and Adversarial Validation

The process-host validation campaign now includes an explicit adversarial concurrency proof model.

The local harness intentionally concentrates control planes, tenants, queues, runtime processes, Redis, MongoDB, scale-out, process kills, recovery, ledger, trace, and forensics on one machine.

This is intentionally harsher than the expected production topology.

Validated boundaries include:

- exact pre-crash inventory: one in-flight execution and two local-queued runs;
- durable crash-gate state instead of elapsed-time process termination;
- stable single-flight recovery scale-out identity;
- readiness as registration, capacity, endpoint, and dispatchability;
- failed runtime capacity suppression;
- in-flight resume with the same `ExecutionId`;
- local-queued redispatch through the durable `SharedRunId`;
- safe-tenant non-impact;
- HTTP and gRPC process-host P35 completion;
- local saturation classification separated from protocol defects.

The full reference is:

- [Concurrency Hardening and Adversarial Validation](concurrency-hardening-and-adversarial-validation.md)


## Related Documents

- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [Context Resolution and Helpers](context-resolution-and-helpers.md)
- [Distributed Execution](distributed-execution.md)
- [Execution Control State](execution-control-state.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Kubernetes Runtime Host Provider](kubernetes-runtime-host-provider.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Concurrency Hardening and Adversarial Validation](concurrency-hardening-and-adversarial-validation.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability](observability.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Step Plugins](step-plugins.md)
- [RAG Pipelines](rag-pipelines.md)

