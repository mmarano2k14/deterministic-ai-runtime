# Deterministic AI Runtime

A deterministic AI execution runtime for production-grade AI workloads.

This repository provides a reference implementation of a distributed, state-driven runtime for executing AI workflows with deterministic DAG orchestration, context resolution, Redis Lua coordination, retry/recovery, retention/compaction, distributed concurrency control, execution control state, replay validation, correlated metrics and tracing, execution-correlated decision ledger, shared runtime control-plane orchestration, Redis-backed shared queue coordination, queue-first submission, shared queue pumping/manual drain, dispatch-time admission, runtime instance provider hosting, runtime worker-capacity visibility, scale-out request publication, and executable enterprise demo scenarios.

The current runtime foundations are intentionally designed as the base for a broader AI execution and MLOps-oriented platform.

[![Version](https://img.shields.io/badge/Version-1.0.6.0-blue)](./CHANGELOG.md)
[![Changelog](https://img.shields.io/badge/Changelog-view-lightgrey)](./CHANGELOG.md)
![AI Runtime](https://img.shields.io/badge/AI-Deterministic%20Execution-purple)
![Runtime](https://img.shields.io/badge/Runtime-distributed-brightgreen)
![Redis](https://img.shields.io/badge/Redis-required-red?logo=redis)
![MongoDB](https://img.shields.io/badge/MongoDB-required-green?logo=mongodb)
![Status](https://img.shields.io/badge/Status-active%20development-orange)

---

## Latest Updates

The latest major updates focused on turning the runtime from a DAG executor into a controllable, observable, distributed execution platform.

| Area | Summary |
|---|---|
| Distributed multi-runtime-instance execution | Added foundations for multiple runtime instances and workers to coordinate through shared Redis-backed execution state. |
| Runtime control plane foundation | Added adapter-neutral control-plane foundations for replay, execution control, local runtime queue control, runtime instance registry/control, run admission decisions, Shared Runtime Controller V1, shared queue dispatch, queue pump/background service, and scale-out request publication. |
| Shared Runtime Controller V1 | Added shared run persistence, Redis-backed shared run store, Redis-backed shared queue, assigned-run dispatch, global shared queue dispatch, queue pump, hosted background queue consumption, queue-first submit mode, dispatch-time admission, and scale-out request publication. |
| Shared queue pump and manual drain | Added queue-first submission, manual shared queue drain, background pump validation, pump identity separation, and local/HTTP runtime provider dispatch validation. |
| Runtime capacity descriptors | Added Redis-backed runtime instance capacity descriptors, runtime capacity publication, worker identity propagation, active/free worker visibility, worker-aware `CanAcceptRun`, and idempotent runtime shutdown foundations. |
| MCP Server as control plane | Added MCP server documentation as a concrete runtime control-plane adapter with ControlPlaneOnly, ControlPlaneWithLocalRuntimeInstances, ControlPlaneWithHttpRuntimeInstances, and RuntimeInstanceOnly host modes. |
| Runtime instance provider model | Added provider-based runtime instance administration direction and foundations for local and HTTP runtime instance providers, with future Redis command queue, gRPC, and Kubernetes providers. |
| Runtime worker capacity control | Added `MaxLocalWorkersPerExecution`, effective worker reservation, worker saturation visibility, and runtime instance snapshots exposing active/free worker capacity. |
| Distributed concurrency and throttling demo | Added an executable `throttling-100` enterprise demo scenario with provider-level concurrency control, realtime throttling visibility, Redis lease-based admission control, randomized provider distribution, and bounded provider capacity under worker pressure. |
| Execution control state | Added durable `ExecutionId`-level pause, resume, cancel, waiting-for-input, and human input submission. |
| Runtime queue control | Added `RunId`-level queue pause/resume, queued cancellation, running cancellation bridge, and hot enqueue support. |
| Execution replay validation | Added Replay API foundations that load persisted snapshots by `ExecutionId`, validate deterministic fingerprints, validate dependency graph and step state, optionally restore runtime state, and expose replay metadata, ledger events, and timeline events. |
| Execution-correlated decision ledger | Added durable execution-correlated ledger events for execution lifecycle, run lifecycle, queue control, claim acquisition, retry, recovery, concurrency, policy evaluation, snapshot persistence, replay lifecycle, and runtime control observability. |
| Correlated metrics and tracing storage modes | Added runtime correlation for metrics and traces with configurable `Disabled`, `Memory`, `Mongo`, and `MemoryAndMongo` storage modes. |
| Context resolution and helpers | Added a dedicated helper layer for input bindings, previous step outputs, payload rehydration, provider/model/operation context, policy context, RAG context, and replay-safe helpers. |
| Documentation restructure | Completed Phase 0 V1 with a shorter README, preserved runtime internals, documentation index, roadmap, enterprise readiness matrix, ecosystem comparison, and focused runtime documentation under `docs/ai/`. |
| Long-term platform direction | Added a dedicated road-to-MLOps direction document to describe how the runtime foundations can evolve toward AI execution infrastructure and MLOps-oriented runtime operations. |

For detailed changes, see [`CHANGELOG.md`](./CHANGELOG.md), [`docs/index.md`](docs/index.md), and [`docs/road-to-mlops.md`](docs/road-to-mlops.md).

## Overview

Deterministic AI Runtime is a .NET runtime for executing complex AI workflows as controlled, observable, recoverable, replayable, and auditable distributed executions.

It is designed for workloads such as:

- LLM orchestration
- RAG pipelines
- tool execution
- decision workflows
- long-running AI processes
- multi-step distributed execution

The runtime treats AI orchestration as a systems problem, not only as a prompt engineering problem.

It provides a state-driven execution layer where:

- workflows are modeled as DAGs
- workers are stateless
- Redis stores hot execution state
- Redis Lua scripts enforce atomic coordination
- MongoDB stores durable payloads and snapshots
- context helpers resolve inputs, payloads, provider metadata, and policy context
- policies control retry, retention, and concurrency
- metrics, traces, and ledger events share runtime correlation
- executions can be replay-validated from persisted snapshots
- execution can be paused, resumed, cancelled, or blocked for human input
- submitted runs can be admitted, assigned, globally queued, queue-first submitted, pumped manually or by background service, dispatched, scale-out requested, rejected, listed, retrieved, or cancelled through the shared runtime controller
- runtime instances expose run capacity, worker capacity, queue pressure, and worker-aware `CanAcceptRun`
- local and HTTP provider hosting foundations validate the path toward Kubernetes-style runtime instances

The project should be read as an AI execution infrastructure foundation. The runtime core is already substantial, while the longer-term direction is to evolve toward a broader AI operations and MLOps-oriented platform.

---

## Why This Exists

Most AI projects focus on prompts, agents, RAG, embeddings, or model calls.

That is enough for prototypes.

Once AI moves to production, the hard problem becomes execution:

- How do you coordinate multiple workers?
- How do you avoid duplicate execution?
- How do you recover after crashes?
- How do you replay an execution?
- How do you control retries?
- How do you throttle providers and models?
- How do you keep memory bounded?
- How do you resolve context safely across steps, payloads, providers, and policies?
- How do you pause, resume, or cancel safely?
- How do you support human-in-the-loop workflows?
- How do you prove deterministic convergence?

This runtime exists to address those production execution concerns.

---

## What Problem This Runtime Fixes

Modern AI applications often start as simple prompts, agents, or RAG pipelines.

That works for prototypes, but production AI execution quickly becomes a distributed systems problem.

This runtime is designed to fix the operational problems that appear when AI workflows must run reliably across multiple steps, workers, runtime instances, providers, queues, and control-plane operations.

| Problem | What the Runtime Provides |
|---|---|
| AI workflows are hard to reproduce | Deterministic DAG execution, persisted execution state, replay metadata, fingerprints, snapshots, and replay validation. |
| Workers can crash mid-execution | Redis-backed execution state, stale running-step recovery, retry state, and deterministic convergence. |
| Multiple workers can duplicate work | Redis Lua atomic claims, claim tokens, lease validation, and single-owner step execution. |
| Retry behavior becomes unpredictable | Step-scoped retry state, retry scheduling, recovery separation, and policy-driven retry behavior. |
| Provider and model calls can overload external services | Distributed concurrency gates, Redis ZSET leases, provider/model/operation limits, and throttling policies. |
| Runtime memory can grow without bounds | Retention, compaction, eviction, payload externalization, and payload rehydration. |
| Context can become corrupted or inconsistent | Dedicated context resolution helpers for inputs, previous step outputs, payloads, provider metadata, policy context, and RAG context. |
| Queued work is hard to control | RunId-level queue control, queue pause/resume, queued cancellation, running cancellation bridge, shared run records, and shared queue coordination. |
| Live executions are hard to control | ExecutionId-level pause, resume, cancel, waiting-for-input, and human input submission. |
| Runtime behavior is hard to audit | Execution-correlated decision ledger, runtime lifecycle events, claim/retry/recovery/concurrency events, and replay lifecycle events. |
| Distributed runtime instances are hard to observe | Runtime instance registration, heartbeat, Redis-backed capacity descriptors, queue pressure, available run slots, worker identity propagation, active worker count, available worker count, and worker-aware `CanAcceptRun`. |
| Queued global work needs controlled dispatch | Queue-first submit mode, shared queue pump, manual drain, dispatch-time admission, and no-double-dispatch shared queue coordination. |
| One execution can consume too many local workers | `MaxLocalWorkersPerExecution`, effective worker reservation, active/free worker visibility, and worker-aware runtime capacity snapshots. |
| Control-plane operations become coupled to internals | Adapter-neutral control-plane facades for MCP, HTTP API, CLI, dashboards, and Kubernetes-oriented orchestration. |
| Dispatch and scaling can become transport-specific | Provider-based runtime instance model and foundations for local and HTTP runtime providers, with future Redis command queue, gRPC, and Kubernetes providers. |
| Kubernetes scaling needs runtime visibility | Runtime roles, runtime capacity descriptors, shared queue coordination, scale-out request publication, and provider-based administration direction. |

The main goal is to make production AI execution controllable, observable, recoverable, auditable, replayable, and eventually scalable across runtime instances without turning the runtime core into a transport-specific scheduler.

The runtime treats AI execution as infrastructure:

```text
Prompt / Tool / RAG call
    ↓
Workflow step
    ↓
Deterministic DAG state
    ↓
Distributed worker coordination
    ↓
Runtime control plane
    ↓
Replay, audit, metrics, tracing, and capacity visibility
```

This is the difference between a prototype agent and an operational AI execution platform.

---

## The Production AI Execution Problem

Production AI workloads are no longer single prompts running in isolation.

They become distributed execution systems with:

- multiple pipeline steps
- parallel branches
- dependencies
- retries
- external providers
- large payloads
- compacted or externalized state
- context resolution across previous step outputs
- failure recovery
- operational controls
- audit and replay requirements
- multiple workers or runtime instances

Without a real execution runtime, these systems often become fragile:

- hidden retry loops
- duplicated work
- lost state
- corrupted execution progress
- unbounded memory growth
- unclear ownership
- inconsistent input/context reconstruction
- poor or fragmented observability
- impossible replay

This project explores what an AI execution runtime should look like when reliability, determinism, context resolution, and distributed coordination are treated as first-class design requirements.

---

## Core Capabilities

| Capability | Status | Summary |
|---|---:|---|
| Deterministic DAG execution | Implemented | Workflows execute through dependency-aware DAG state. |
| Redis hot state | Implemented | Active execution state is stored in Redis. |
| Redis Lua atomic coordination | Implemented | Critical transitions use Lua-backed atomic operations. |
| Distributed workers | Implemented | Workers can claim and execute steps safely. |
| Multi-runtime-instance execution foundations | Implemented | Runtime instances can coordinate through shared Redis-backed execution state. |
| Context resolution and helpers | Foundation available | Input bindings, previous step outputs, provider metadata, policy context, and payload rehydration are resolved through helper layers. |
| Deterministic convergence | Implemented | Final state is derived from state transitions, not worker ordering. |
| Retry and recovery | Implemented | Retry state, waiting windows, and stale running-step recovery are separated. |
| Retention and compaction | Implemented | Hot state can be compacted/evicted while payloads remain resolvable. |
| Distributed concurrency and throttling | Implemented | Redis ZSET leases enforce global, pipeline, step, execution, instance, provider, model, and operation limits. |
| Enterprise throttling scenario | Implemented | `throttling-100` demonstrates provider-level distributed throttling with realtime visibility and deterministic convergence. |
| Policy-driven execution | Implemented | Retry, retention, and concurrency use configurable policy definitions. |
| Execution control state | Implemented | ExecutionId-level pause, resume, cancel, waiting-for-input, and human input submission. |
| Runtime queue control | Implemented | RunId-level queue pause/resume, queued cancellation, running cancellation bridge, and hot enqueue. |
| Runtime control plane foundation | Implemented | Adapter-neutral facades expose replay, execution control, local queue control, runtime instance control, admission, shared runtime controller, shared queue dispatch, queue pump/manual drain, background service, and scale-out publication for API/MCP/CLI/dashboard/Kubernetes adapters. |
| Runtime instance registry and control | Implemented | Runtime instances can register, heartbeat, expose queue capacity, expose worker capacity, be listed, marked draining, or unregistered. |
| Run admission / slot decisions | Implemented | Admission can assign runs to an available runtime instance, request scale-out, queue globally, or reject according to policy. |
| Shared Runtime Controller V1 | Implemented | Shared controller handles assigned dispatch, queue-first submission, global queue enqueue, Redis-backed shared queue dispatch, dispatch-time admission, queue pump/background consumption, scale-out request publication, and shared run visibility. |
| Shared queue pump and manual drain | Implemented / validated | Queue-first runs can remain globally queued, be dispatched by a background pump, or be manually drained later through MCP/control-plane tools. |
| Redis runtime capacity descriptors | Foundation available | Runtime instances publish Redis-backed capacity descriptors for worker count, active/free workers, max workers per execution, run slots, queue pressure, heartbeat, and future capacity-aware admission. |
| Runtime worker capacity visibility | Implemented / validated | Runtime snapshots expose `WorkerCount`, `ActiveWorkerCount`, `AvailableWorkerCount`, `MaxLocalWorkersPerExecution`, and worker-aware `CanAcceptRun`. |
| MCP server control-plane adapter | Foundation available | MCP tools expose shared run, shared queue, manual drain, runtime instance, runtime queue, replay, execution control, observability, and worker-capacity visibility operations. |
| Runtime instance provider model | Implemented foundations / ongoing | Provider-based runtime administration has local and HTTP foundations, with future Redis command queue, gRPC, and Kubernetes providers. |
| RunId vs ExecutionId separation | Implemented | Controller lifecycle identity is separated from durable DAG execution identity. |
| Snapshot and Replay API foundations | Implemented | Terminal snapshots, replay metadata, deterministic fingerprint validation, audit-only replay, restore replay, ledger loading, and timeline loading are available. |
| Execution-correlated decision ledger | Implemented | Durable correlated ledger events exist for execution lifecycle, run lifecycle, queue control, claims, steps, retry, recovery, policy evaluation, concurrency, execution control, human input, snapshots, storage failures, replay lifecycle, retention, compaction, and finalization. |
| Observability, metrics, and tracing | Foundation available | Runtime metrics, trace recording, realtime events, correlated trace timelines, and configurable Memory/Mongo/MemoryAndMongo persistence exist; production-grade OpenTelemetry integration and dashboarding remain planned. |
| MLOps-oriented platform evolution | Direction defined | Long-term platform direction is documented in `docs/road-to-mlops.md`. |
| Durable decision ledger | Foundation available | Execution-correlated runtime ledger foundations are implemented and aligned with runtime correlation, including replay lifecycle visibility. |
| Public API / SDK polish | Planned | Future work for cleaner external developer experience. |

---

## Architecture at a Glance

```text
Client / API / MCP / Controller
        |
        v
Runtime Orchestration / Shared Control Plane Layer
        |
        +--> Shared Run Store
        +--> Shared Queue
        +--> Queue Pump / Manual Drain
        +--> Dispatch-Time Admission
        |
        v
Runtime Instance Provider / Local Queue Layer
        |
        v
Pipeline Definition + DAG Resolution
        |
        v
Context Resolution and Helper Layer
        |
        +--> Input binding resolution
        +--> Previous step output resolution
        +--> Payload rehydration
        +--> Provider / model / operation context
        +--> Policy context
        +--> Concurrency context
        +--> RAG retrieval / merge / compose context
        |
        v
DAG Execution Engine
        |
        +--> Execution Control Gate
        |
        +--> Concurrency / Throttling Engine
        |
        v
Redis Hot State + Redis Lua Coordination
        |
        +--> Atomic step claims
        +--> Retry scheduling
        +--> Worker recovery
        +--> Control state
        +--> Distributed leases
        +--> Shared run records
        +--> Shared queue claims
        |
        v
Runtime Instances / Local Queues / Stateless Workers
        |
        v
Step Executors
        |
        +--> LLM
        +--> RAG
        +--> Tools
        +--> Decisions
        |
        v
MongoDB Payloads / Snapshots / Replay Validation
        |
        v
Execution-Correlated Decision Ledger
        |
        v
Correlated Observability / Metrics / Tracing Foundations
```

The runtime is intentionally split into layers:

- orchestration starts and manages executions
- DAG state determines what can run
- context helpers resolve inputs, payloads, metadata, and policy context
- Redis coordinates distributed workers
- policies control runtime behavior
- workers execute claimed steps
- persistence stores large payloads and snapshots
- replay validates deterministic reconstruction from persisted snapshots
- shared controller coordinates run admission, shared run persistence, queue-first submission, global queue dispatch, queue pumping/manual drain, dispatch-time admission, and scale-out publication
- runtime instance providers route selected runs to local or HTTP-hosted runtime instances while preserving local queues
- runtime capacity snapshots expose run slots, worker pressure, and worker-aware `CanAcceptRun`
- correlated observability records runtime behavior across ledger, metrics, traces, workers, and executions

---

## Enterprise Readiness

The project is designed around production questions that enterprise AI systems must answer.

| Enterprise Question | Runtime Direction |
|---|---|
| What happens if a worker crashes? | Running steps can be recovered through stale-claim detection and Redis-backed recovery. |
| How do you prevent duplicate executions? | Atomic Redis Lua claims and claim tokens enforce single step ownership. |
| How do you replay a workflow? | Terminal snapshots, replay metadata, deterministic fingerprint validation, audit-only replay, and restore replay provide replay foundations. |
| How do you audit an AI decision? | Execution-correlated decision ledger events, correlated metrics/traces, execution state, step results, retry metadata, recovery state, snapshots, replay reports, and observability provide audit foundations. |
| How do you limit concurrency? | Distributed Redis ZSET leases and policy-driven throttling enforce limits. |
| How do you resolve execution context safely? | Context helpers resolve inputs, step outputs, payload references, provider metadata, and policy context consistently. |
| How do you pause/resume/cancel safely? | Execution control state blocks new claims and coordinates deterministic finalization. |
| How do you control human-in-the-loop? | WaitingForInput and SubmitHumanInput are supported through durable control state. |
| How do you keep memory/state bounded? | Retention, compaction, eviction, and payload externalization control hot state size. |
| How do you coordinate multiple runtime instances? | Shared Redis state, Lua coordination, leases, shared run records, Redis-backed shared queue claims, queue pump/background consumption, dispatch-time admission, provider-hosting foundations, and deterministic convergence enable coordination. |
| How do you prevent one execution from consuming all workers? | `MaxLocalWorkersPerExecution`, active/free worker tracking, effective worker reservation, and worker-aware runtime capacity snapshots. |
| How do you dispatch queued work without an automatic background pump? | Queue-first submit mode plus manual shared queue drain through MCP/control-plane tooling. |
| How do you prove deterministic convergence? | Integration tests and enterprise demo scenarios validate completion, replay fingerprints, distributed execution, throttling, recovery behavior, atomic retention, compaction consistency, ledger visibility, and trace timeline visibility. |
| How does this evolve toward AI operations and MLOps? | Runtime foundations are designed to support future AI execution control planes, governance, observability, replay, and operational workflows. |

For the detailed enterprise matrix, see [`docs/enterprise-readiness.md`](docs/enterprise-readiness.md).

---

## Runtime Control Plane

The runtime includes an adapter-neutral control-plane foundation for operational runtime control.

The control plane currently exposes foundations for:

- replay and audit control
- execution control
- local runtime queue control
- runtime instance registration and heartbeat
- runtime instance visibility and draining
- run admission / slot decisions
- Shared Runtime Controller V1
- Redis-backed runtime instance registry and capacity descriptors
- MCP server control-plane adapter documentation
- runtime instance provider model direction
- shared run persistence
- Redis-backed shared run store
- Redis-backed shared queue
- direct assigned-run dispatch
- queue-first submit mode
- global shared queue dispatch
- dispatch-time admission
- shared queue pump
- manual shared queue drain
- hosted shared queue background consumption
- runtime worker-capacity visibility
- scale-out request publication
- future API, MCP, CLI, dashboard, and Kubernetes adapters

The control plane separates three identity levels.

```text
SharedRunId
= shared controller / global queue lifecycle id

RunId
= local runtime queue / background controller lifecycle id

ExecutionId
= authoritative durable DAG execution id
```

This separation allows the runtime to manage global queue work, local queue work, and durable execution state without mixing their lifecycles.

### ExecutionId-Level Control

Implemented execution control capabilities include:

- pause execution
- resume execution
- cancel execution
- wait for human input
- submit human input
- block new claims based on control state
- cancellation finalization override

### RunId-Level Queue Control

Implemented controller queue capabilities include:

- pause queue
- resume queue
- cancel queued run
- cancel running run by bridging to ExecutionId cancellation
- hot enqueue while controller is running
- hot enqueue while queue is paused

This makes the runtime controllable, not only executable.

### Runtime Instance Control

Implemented runtime instance capabilities include:

- register runtime instance
- heartbeat runtime instance
- get runtime instance status
- list runtime instances
- expose local queue pressure
- expose available run slots
- expose active worker count
- expose available worker count
- expose max local workers per execution
- expose worker-aware CanAcceptRun
- mark runtime instance as draining
- unregister runtime instance

These foundations prepare the runtime for Kubernetes pod visibility, shared admission, dashboards, and future autoscaling.

### Run Admission / Slot Decisions

Implemented admission capabilities include:

- assign a run to an available runtime instance
- prefer a requested runtime instance when available
- select the least-loaded available instance
- request scale-out when no instance has capacity
- queue globally later when shared queue fallback is enabled
- reject when no capacity or fallback exists

Admission does not enqueue runs directly.

It only decides what should happen next. The shared runtime controller now applies that decision by dispatching assigned runs, enqueueing globally queued runs, publishing scale-out requests, or recording rejected runs.

### Shared Runtime Controller V1

Implemented shared controller capabilities include:

- submit shared run
- submit shared run in queue-first mode
- get shared run
- list shared runs
- cancel shared run
- assign run to runtime instance
- dispatch assigned run locally
- queue run globally
- claim globally queued run
- dispatch globally queued run
- re-admit queued work at dispatch time
- separate pump identity from assigned runtime identity
- requeue failed dispatch
- mark shared run dispatched
- mark shared queue item dispatched
- publish scale-out request
- pump shared queue manually
- consume shared queue through hosted background service
- coordinate shared queue dispatch through Redis
- prevent double dispatch through Redis atomic claim

The current shared controller flow is:

```text
SubmitRun
  -> IAiRunAdmissionController

  -> AssignToInstance
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiSharedRunDispatcher.DispatchAsync(...)
      -> IAiSharedRunStore.MarkDispatchedAsync(...)
      -> SharedRun.Status = Dispatched

  -> QueueGlobally / QueueFirst
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiSharedQueue.EnqueueAsync(...)
      -> SharedRun.Status = QueuedGlobally
      -> SharedQueueItem.Status = Pending

  -> SharedQueuePump / ManualDrain
      -> IAiSharedQueue.ClaimNextAsync(...)
      -> IAiRunAdmissionController.AdmitAsync(...)
      -> IAiSharedRunDispatcher.DispatchAsync(...)
      -> IAiSharedQueue.MarkDispatchedAsync(...)
      -> IAiSharedRunStore.MarkDispatchedAsync(...)

  -> RequestScaleOut
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiRuntimeScaleOutRequestPublisher.PublishAsync(...)
      -> SharedRun.Status = ScaleOutRequested

  -> Reject
      -> IAiSharedRunStore.CreateAsync(...)
      -> SharedRun.Status = Rejected
```

For details, see [`docs/ai/runtime-control-plane.md`](docs/ai/runtime-control-plane.md).

---

## Distributed Execution and Coordination

The runtime uses Redis as a hot state and coordination layer.

Redis is not only used as a cache. It is used as the active distributed execution state.

Critical operations include:

- creating execution state
- claiming ready DAG steps
- validating claim ownership
- completing steps
- failing steps
- scheduling retries
- recovering stale running steps
- enforcing distributed concurrency leases
- storing execution control state

Redis Lua scripts are used for atomic transitions where race conditions must be avoided.

This allows multiple workers or runtime instances to cooperate safely without direct worker-to-worker communication.

---

## Context Resolution and Runtime Helpers

The runtime includes a helper layer that connects declarative configuration to concrete execution behavior.

This layer resolves:

- input bindings from execution state
- previous step outputs
- compacted or externalized payloads
- provider, providerKey, model, and operation metadata
- retry policy context
- retention policy context
- concurrency context
- RAG retrieval, merge, and compose context
- replay-safe comparison data

This keeps the DAG engine focused on orchestration and prevents plugins, policies, and providers from manually reconstructing raw execution state.

For details, see [`docs/ai/context-resolution-and-helpers.md`](docs/ai/context-resolution-and-helpers.md).

---

## Observability, Replay, and Audit Foundations

The runtime includes foundations for production visibility and replayability:

- execution lifecycle metrics
- retry and recovery metrics
- retention metrics
- resolver metrics
- storage metrics
- context resolution diagnostics
- concurrency admission diagnostics
- realtime runtime events
- readable console runtime events
- execution-correlated decision ledger
- durable runtime lifecycle audit events
- claim, concurrency, retry, and recovery audit visibility
- queue and execution control audit visibility
- atomic retention and compaction auditability
- shared runtime controller foundations
- Redis-backed shared queue coordination
- queue pump and background consumption
- queue-first and manual drain visibility
- dispatch-time admission visibility
- runtime worker capacity visibility
- scale-out request publication
- snapshot persistence audit events
- replay lifecycle ledger events
- replay metadata
- replay report generation
- replay snapshot loading
- replay deterministic fingerprint validation
- replay dependency graph validation
- replay step state validation
- replay payload reference validation
- replay ledger event loading
- replay trace timeline loading
- replay diagnostic output for ledger and timeline inspection
- correlated trace recording foundations
- correlated metric and trace storage modes
- in-memory, MongoDB, and MemoryAndMongo observability persistence
- terminal snapshots
- replay restoration
- deterministic replay fingerprint validation

Runtime metrics and traces can be configured as `Disabled`, `Memory`, `Mongo`, or `MemoryAndMongo`. This allows local diagnostics, durable MongoDB-backed inspection, or both at the same time while keeping the execution runtime independent from observability storage choices.

Replay can validate persisted executions without re-running LLMs, tools, external providers, or side effects. It can expose replay metadata, decision ledger events, and trace timeline events when requested.

OpenTelemetry-style distributed tracing, richer dashboards, HTTP replay APIs, replay audit tooling, and advanced decision lineage remain roadmap items.

---

## Current Status

This project is under active development.

It should be treated as an advanced reference implementation and evolving AI infrastructure project, not as a polished commercial product.

The strongest areas today are:

- deterministic DAG execution
- Redis-backed distributed state
- Redis Lua atomic coordination
- context resolution and helper foundations
- retry/recovery semantics
- retention/compaction
- distributed concurrency and throttling
- executable distributed throttling scenario
- execution control state
- runtime queue control
- runtime control-plane foundations
- Shared Runtime Controller V1
- Redis-backed runtime instance registry and capacity descriptors
- MCP server control-plane adapter documentation
- runtime instance provider model direction
- Redis-backed shared run store and shared queue
- shared queue dispatcher, pump, manual drain, and background service
- queue-first submit mode
- dispatch-time admission
- runtime worker capacity visibility
- local and HTTP runtime provider hosting foundations
- scale-out request publication
- runtime instance registry and control
- run admission / slot decisioning
- queue and execution control observability
- replay/snapshot validation foundations
- replay metadata, ledger, and trace timeline diagnostics
- correlated observability, tracing, metrics, and realtime logging foundations
- integration-test-driven validation

Areas still evolving include:

- public API/SDK polish
- remote runtime instance dispatch hardening
- provider-based runtime instance administration beyond local/HTTP foundations
- automatic Kubernetes scaling adapter
- HTTP replay/control-plane APIs and controller abstractions
- OpenTelemetry/exporter polish for tracing and metrics
- operational dashboarding
- Kubernetes deployment assets
- real enterprise sample workflows
- MLOps-oriented platform capabilities
- production documentation split

---

## Enterprise Runtime Demo

A local enterprise-oriented demo is available in [`demo/enterprise-runtime/`](demo/enterprise-runtime/README.md).

The demo is designed to prove that the runtime behaves like distributed AI execution infrastructure, not a toy agent framework.

It currently includes:

- Docker Compose infrastructure for Redis and MongoDB
- scenario documentation for enterprise runtime behaviors
- an external sample step plugin under `Samples/Multiplexed.Sample.External.Plugins.Steps`
- a JSON pipeline at `demo/enterprise-runtime/pipelines/enterprise-demo-pipeline.json`
- interactive console scenario selection
- distributed runtime worker participation
- realtime readable runtime logs
- live progress output
- execution pause, resume, and cancel controls
- retry recovery summaries
- retention and hot-state summaries
- replay validation for supported scenarios
- replay metadata, ledger, and timeline diagnostics in integration tests
- distributed provider throttling through the `throttling-100` scenario
- `RunId` and `ExecutionId` separation
- terminal completion through the controller path
- execution-correlated runtime ledger visibility
- correlated metrics and tracing diagnostics
- MemoryAndMongo observability validation
- queue and execution control audit events
- retry, recovery, and concurrency ledger validation
- atomic retention and compaction auditability
- shared runtime controller foundations
- Redis-backed shared queue coordination
- queue pump and background consumption
- queue-first and manual drain visibility
- dispatch-time admission visibility
- runtime worker capacity visibility
- scale-out request publication

The current executable console scenarios are:

```text
json
chaos-100
chaos-500
throttling-100
```

The `throttling-100` scenario demonstrates:

- distributed provider throttling
- Redis lease-based concurrency admission
- realtime `[THROTTLED]` visibility
- randomized provider distribution with OpenAI as the throttled target
- bounded provider capacity under worker pressure
- deterministic convergence despite throttling delays

The demo validates the controller execution path, distributed worker participation, runtime controls, realtime logging, correlated observability, and terminal completion behavior. It is intended to show distributed AI execution infrastructure, not only a simple batch or in-memory execution path.

Future demo work will expand further into crash recovery, human-in-the-loop, advanced replay workflows, Kubernetes deployment assets, real enterprise sample workflows, and broader AI operations/MLOps-oriented runtime capabilities.

---

## Roadmap

The roadmap is organized into phases.

| Phase | Focus | Status |
|---|---|---|
| Completed | Core runtime foundations already implemented | Implemented / validated by tests |
| Phase 0 | README review and documentation restructure | Completed (V1) |
| Phase 1 | Enterprise demo | Completed (V1) - controller demo, distributed workers, runtime controls, chaos scenarios, retention/replay, and throttling scenario validated |
| Phase 2 | Real enterprise sample | Planned |
| Phase 3 | Correlated observability, tracing, and metrics | Foundations available / active polish |
| Phase 4 | Kubernetes deployment demo | Planned - shared controller V1 and Redis shared queue foundation completed |
| Phase 5 | Public API / SDK polish | Planned |
| Phase 6 | Deterministic Replay Engine and Audit Foundations | Completed (V1) |
| Phase 7 | Replay Controller, HTTP APIs, Dashboard, and Operational Tooling | Planned |
| Phase 8 | Cost and Provider Governance | Planned |
| Phase 9 | Articles and public positioning | Planned |

The roadmap above tracks the current runtime and enterprise demo evolution.

The longer-term platform direction is tracked separately in [`docs/road-to-mlops.md`](docs/road-to-mlops.md), which describes how these deterministic execution foundations can evolve toward broader AI execution infrastructure, governance, observability, replay, and MLOps-oriented runtime operations.

For the detailed roadmap, see [`docs/roadmap.md`](docs/roadmap.md).

---

## Documentation

The full documentation map is available here:

- [`docs/index.md`](docs/index.md) — Documentation index and reading guide.
- [`docs/runtime-internals.md`](docs/runtime-internals.md) — Complete technical reference preserved from the original README.
- [`docs/enterprise-readiness.md`](docs/enterprise-readiness.md) — Enterprise readiness matrix.
- [`docs/comparison-existing-tools.md`](docs/comparison-existing-tools.md) — Ecosystem positioning and comparison with existing tools.
- [`docs/roadmap.md`](docs/roadmap.md) — Project roadmap.
- [`docs/road-to-mlops.md`](docs/road-to-mlops.md) — Long-term evolution from deterministic AI runtime foundations toward a broader AI execution and MLOps-oriented platform.
- [`docs/ai/runtime-control-plane.md`](docs/ai/runtime-control-plane.md) — Runtime control-plane foundation for replay, execution control, runtime queue control, runtime instance visibility/control, run admission, Shared Runtime Controller V1, Redis shared queue coordination, queue pump/background service, and scale-out request publication.
- [`docs/ai/mcp-server-control-plane.md`](docs/ai/mcp-server-control-plane.md) — MCP server as a runtime control-plane adapter, including host modes, tool groups, runtime role separation, local runtime pool behavior, shared queue dispatch flow, and Kubernetes direction.
- [`docs/ai/runtime-instance-provider-model.md`](docs/ai/runtime-instance-provider-model.md) — Provider-based runtime instance administration model for local, Redis command queue, HTTP, gRPC, and Kubernetes providers.
- [`docs/ai/shared-queue-pump-and-worker-capacity.md`](docs/ai/shared-queue-pump-and-worker-capacity.md) — Shared queue pump, queue-first submit mode, manual drain, dispatch-time admission, pump identity separation, runtime worker capacity visibility, and `MaxLocalWorkersPerExecution`.
- [`demo/enterprise-runtime/README.md`](demo/enterprise-runtime/README.md) — Local enterprise runtime demo using Docker Compose, Redis, MongoDB, external demo steps, controller execution, distributed workers, and scenario documentation.
- [`demo/enterprise-runtime/scenarios/06-distributed-throttling.md`](demo/enterprise-runtime/scenarios/06-distributed-throttling.md) — Executable distributed throttling scenario documentation.
- [`demo/enterprise-runtime/scenarios/08-deterministic-convergence.md`](demo/enterprise-runtime/scenarios/08-deterministic-convergence.md) — Deterministic convergence scenario documentation.

Focused AI runtime documentation:

- [`docs/ai/architecture-overview.md`](docs/ai/architecture-overview.md)
- [`docs/ai/distributed-execution.md`](docs/ai/distributed-execution.md)
- [`docs/ai/execution-control-state.md`](docs/ai/execution-control-state.md)
- [`docs/ai/runtime-queue-control.md`](docs/ai/runtime-queue-control.md)
- [`docs/ai/runtime-control-plane.md`](docs/ai/runtime-control-plane.md)
- [`docs/ai/mcp-server-control-plane.md`](docs/ai/mcp-server-control-plane.md)
- [`docs/ai/runtime-instance-provider-model.md`](docs/ai/runtime-instance-provider-model.md)
- [`docs/ai/shared-queue-pump-and-worker-capacity.md`](docs/ai/shared-queue-pump-and-worker-capacity.md)
- [`docs/ai/retry-and-recovery.md`](docs/ai/retry-and-recovery.md)
- [`docs/ai/retention-and-compaction.md`](docs/ai/retention-and-compaction.md)
- [`docs/ai/distributed-concurrency-throttling.md`](docs/ai/distributed-concurrency-throttling.md)
- [`docs/ai/replay-and-audit.md`](docs/ai/replay-and-audit.md)
- [`docs/ai/observability.md`](docs/ai/observability.md)
- [`docs/ai/observability-tracing.md`](docs/ai/observability-tracing.md)
- [`docs/ai/runtime-metrics.md`](docs/ai/runtime-metrics.md)
- [`docs/ai/execution-correlated-ledger.md`](docs/ai/execution-correlated-ledger.md)
- [`docs/ai/testing-strategy.md`](docs/ai/testing-strategy.md)
- [`docs/ai/config-driven-runtime.md`](docs/ai/config-driven-runtime.md)
- [`docs/ai/policy-driven-execution.md`](docs/ai/policy-driven-execution.md)
- [`docs/ai/context-resolution-and-helpers.md`](docs/ai/context-resolution-and-helpers.md)
- [`docs/ai/step-plugins.md`](docs/ai/step-plugins.md)
- [`docs/ai/rag-pipelines.md`](docs/ai/rag-pipelines.md)

These files were extracted progressively from `docs/runtime-internals.md`.

---

## License

This project is licensed under the **Business Source License 1.1 (BSL)**.

- Free for development, testing, and internal use
- Commercial production use requires a license
- Automatically converts to Apache 2.0 on 2029-01-01

See the repository license file for full terms.
