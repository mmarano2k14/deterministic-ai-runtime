# Architecture Overview

Status: Implemented architecture foundation / validated with shared controller, MCP, Redis coordination, Redis-backed scale-out request persistence, local runtime pools, local runtime scale-out, fulfilled-run requeue, HTTP pooled runtime provider scenarios, and end-to-end MCP scale-out execution.

This document provides a high-level overview of the **Deterministic AI Runtime** architecture.

It also reflects the current control-plane evolution: shared queue pump, queue-first submit mode, direct-dispatch scale-out mode, dispatch-time admission, runtime instance providers, MCP control-plane integration, Redis discovery/registry/capacity coordination, Redis-backed scale-out request coordination, admission reservations, local runtime scale-out, fulfilled-run requeue, HTTP pooled runtime hosting, and worker-capacity visibility.

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
- supporting provider-based local, local scale-out, and HTTP pooled runtime dispatch

The core idea is simple:

> AI orchestration becomes a distributed systems problem once AI moves to production.

---

## High-Level Architecture

At a high level, the runtime is composed of the following layers:

```text
Client / API / MCP Layer
        ↓
Control Plane and Shared Queue Layer
        ↓
Scale-Out Request and Requeue Layer
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

## Main Runtime Layers

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
- queue-first submit mode
- global shared queue coordination
- shared queue pump and manual drain
- dispatch-time admission
- runtime instance registry visibility
- runtime capacity descriptors
- Redis control-plane discovery
- control-plane id resolution
- admission reservations
- Redis-backed scale-out requests
- provider-based scale-out
- fulfilled scale-out shared run requeue
- provider-based local and HTTP dispatch
- runtime queue control
- execution control
- replay and observability adapters
- MCP server tool exposure

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

The pump remains responsible for claim ownership, dispatch-time admission, provider dispatch, and queue/run state updates.

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

### 3. Runtime Orchestration Layer

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

### 4. Pipeline Resolution Layer

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

### 5. Context Resolution and Helper Layer

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

### 6. DAG Execution Engine

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

### 7. Runtime Instance and Worker Capacity Layer

Runtime instances are the execution participants that own local queues and workers.

A runtime instance may be local, HTTP-backed through a pooled runtime host, or later connected through Redis command queues, gRPC, or Kubernetes provider transports.

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
host-...:mcp-scaleout-runtime-1
    ↓
Shared run requeued
    ↓
Shared queue pump dispatches
    ↓
Runtime run completed
```

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

### 7. Distributed Coordination Layer

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

Critical transitions are protected by Redis Lua scripts.

This allows multiple workers or runtime instances to coordinate safely without duplicate step ownership.

---

### 8. Step Execution Layer

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

Step executors receive resolved context from the context resolution layer.

---

### 9. Policy and Governance Layer

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

### 10. Persistence Layer

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

### 11. Retention and Compaction Layer

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

### 12. Replay and Audit Foundations

The runtime includes foundations for replay and auditability.

Current replay foundations include:

- terminal snapshots
- snapshot restoration
- deterministic replay validation
- execution fingerprints
- restored execution comparison

Replay depends on context helpers to avoid relying on volatile runtime fields such as claim tokens, leases, or worker-local state.

Replay-safe comparison should use stable execution state, payload references, and deterministic fingerprints.

This creates the basis for future official replay APIs and durable decision ledger support.

---

### 13. Observability Layer

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

This allows the runtime to be inspected, tested, and eventually monitored through dashboards.

---

## Runtime Data Flow

A simplified runtime data flow is:

```text
Client / API / MCP submits pipeline run
        ↓
Control-plane identity is resolved / published when required
        ↓
Runtime registry and capacity descriptors are visible
        ↓
Shared controller may create shared run
        ↓
Queue-first mode may enqueue in shared queue
        ↓
Direct-dispatch admission may request scale-out
        ↓
Scale-out watcher/provider/scaler may create runtime capacity
        ↓
Fulfilled scale-out run may be requeued
        ↓
Shared queue pump or manual drain may dispatch
        ↓
Admission selects a runtime instance
        ↓
Reservation/provider dispatch path is used
        ↓
Runtime instance local queue receives run
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
- queue-first submit mode
- global shared queue state
- shared queue item lifecycle
- shared queue pump/manual drain
- dispatch-time admission
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

This separation is required for provider-based runtime hosting, MCP manual drain, HTTP runtime instances, and future Kubernetes control-plane/runtime-pod separation.

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

Admission decides which runtime instance should receive work.

Providers decide how to contact that runtime instance.

When admission requests scale-out, the scale-out provider selector decides which provider can create or request capacity.

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
- provider-based scale-out capability
- local runtime instance scaler
- Redis scale-out request store
- fulfilled scale-out run requeue
- runtime instance provider metadata
- Redis runtime instance registry visibility
- Redis capacity descriptor visibility
- Redis control-plane discovery
- ControlPlaneIdResolver
- Redis admission reservation store
- MCP control-plane scenarios

Validated local scale-out provider shape:

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
host-...:mcp-scaleout-runtime-1
    ↓
Shared run requeued
    ↓
Shared queue pump
    ↓
Local runtime queue
    ↓
DAG execution completed
```

Validated HTTP pooled provider shape:

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

Future providers may include:

- Redis command queue provider
- gRPC provider
- Kubernetes metadata provider
- Kubernetes scaling provider

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
| MCP Redis local scale-out execution | Implemented / validated |
| Shared queue pump readiness gate | Implemented / validated |
| Runtime worker capacity visibility | Implemented / validated |
| Max local workers per execution | Implemented / validated |
| Human-in-the-loop foundations | Implemented |
| Replay and snapshot foundations | Implemented / validated foundations |
| Decision ledger foundation | Implemented foundations / validated through replay ledger scenarios |
| Durable decision ledger hardening | Planned |
| Observability dashboard | Planned |
| Kubernetes deployment | Planned |
| Public SDK polish | Planned |

---

## Current Validated Evidence

The current architecture has been validated through MCP, Redis, local runtime pool, local scale-out, and HTTP pooled runtime provider scenarios.

Redis local scale-out execution evidence:

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

Heavy HTTP dispatch evidence:

```text
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
- dynamically create local runtime capacity from zero executable instances
- requeue fulfilled scale-out shared runs
- dispatch scale-out requeued runs through the shared queue pump
- execute scale-out-triggered runs to completion
- assign runs to pooled child runtime instances
- expose runtime run status and execution ids
- replay completed executions
- inspect ledger and trace output.


## Related Documents

- [Context Resolution and Helpers](context-resolution-and-helpers.md)
- [Distributed Execution](distributed-execution.md)
- [Execution Control State](execution-control-state.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability](observability.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Step Plugins](step-plugins.md)
- [RAG Pipelines](rag-pipelines.md)

