# Managed Hosting Model

## Deterministic AI Runtime Platform

This document describes the managed hosting model direction for the Deterministic AI Runtime Platform.

The managed hosting model is not a disconnected business idea. It comes directly from the technical foundations already present in the platform:

- multiple runtime instances;
- multiple workers per runtime instance;
- shared queue direction;
- local queue per runtime instance;
- admission control direction;
- capacity-aware dispatch direction;
- runtime instance registry direction;
- provider-based runtime hosting;
- dynamic provider direction for communication between control plane, runtime instances, and MCP;
- MCP control-plane foundation;
- replay and audit foundation;
- decision ledger foundation;
- observability direction;
- retention, eviction, and compaction foundation;
- policy-driven concurrency and throttling.

Because the runtime is already structured around runtime instances, workers, queues, runs, executions, providers, and MCP control operations, the hosting model can naturally evolve around execution capacity.

The platform can become not only a deterministic runtime, but also a managed execution infrastructure for production AI workflows.

---

## Purpose

The purpose of the managed hosting model is to define how the platform can evolve from a runtime engine into an operational service.

A production AI workflow platform must eventually answer questions such as:

- Where does execution run?
- How many runtime instances are available?
- How many workers are available?
- Which runtime instance accepted a run?
- Which worker executed a step?
- How is work admitted?
- How is queue pressure handled?
- How is capacity measured?
- How are executions replayed and audited?
- How does the control plane communicate with runtime instances?
- How does MCP expose operational control?
- How can customers scale execution capacity?
- How can capacity be isolated by tenant, project, or deployment?
- How can hosting eventually be priced or metered?

The managed hosting model gives a product direction to the runtime architecture.

---

## Current Foundation

The platform already has important foundations for managed hosting.

These include:

- runtime instance identity;
- worker identity;
- local queue direction;
- shared queue direction;
- shared run direction;
- run/execution separation;
- admission and dispatch direction;
- runtime instance registry direction;
- provider-based hosting direction;
- HTTP runtime provider direction;
- runtime-instance-only host mode direction;
- control-plane with runtime instances direction;
- MCP server and control-plane direction;
- shared queue pump direction;
- policy-driven concurrency and throttling direction;
- decision ledger foundation;
- replay and audit foundation;
- observability direction;
- retention, eviction, and compaction foundation.

This means the managed hosting model is already supported by the architecture.

The roadmap is to harden, expose, document, productize, observe, and eventually commercialize this foundation.

---

## Core Idea

The managed hosting model is based on a simple execution-capacity concept:

```text
Customer workload
  -> Control Plane / MCP
      -> Shared Queue
          -> Runtime Instance
              -> Local Queue
                  -> Worker
                      -> Execution Step
```

This model is powerful because it separates:

```text
Control plane = submit, inspect, replay, pause, resume, cancel, diagnose
Execution plane = runtime instances and workers executing steps
Coordination plane = shared queue, local queues, admission, dispatch, registry
Audit plane = replay, decision ledger, observability, retention
```

This separation is what allows the platform to evolve toward managed hosting.

---

## Why Managed Hosting Fits This Runtime

Managed hosting fits this architecture because the runtime already has natural capacity units.

The main units are:

| Unit | Meaning |
|---|---|
| Runtime Instance | A process, container, pod, or managed execution unit. |
| Worker | A local execution slot inside a runtime instance. |
| Shared Queue | Global queue of submitted runs waiting for dispatch. |
| Local Queue | Queue of work assigned to a runtime instance. |
| Run | Control-plane identity for submitted work. |
| Execution | Durable workflow execution identity. |
| Step | Smallest runtime execution unit. |
| Provider | Communication or hosting abstraction for executing work. |
| MCP Control Plane | Operational tool surface for controlling and inspecting runtime behavior. |

This makes hosting measurable.

A managed service can eventually reason about:

- number of runtime instances;
- workers per runtime instance;
- max concurrent runs;
- queue capacity;
- execution volume;
- replay/audit retention;
- observability level;
- storage usage;
- dedicated environment needs.

---

## Runtime Pool as a Managed Hosting Unit

The implemented process-host Runtime Pool introduces a reusable capacity unit above individual runtime instances.

```text
PoolId
    HostId
        stable HTTP/gRPC endpoint
        RuntimeInstanceId A1
        RuntimeInstanceId A2
        RuntimeInstanceId A3
```

This model supports:

- warm reusable capacity;
- independently selectable runtime instances;
- exact transport routing;
- targeted child replacement;
- graceful draining;
- child-local failure isolation;
- deterministic recovery claims.

A customer or tenant is not permanently mapped to one process.

The existing Kubernetes mode remains one runtime per Pod. A future Kubernetes Runtime Pool mode will place several independently registered runtimes inside one Pod and use the Pod UID as the immutable `HostId`.

Commercial packaging can eventually meter:

- runtime pools;
- warm runtime capacity;
- workers per runtime;
- execution volume;
- recovery and audit retention;
- dedicated Pool or Pod isolation.

The transport router remains separate from tenant admission, scheduling, and commercial quota policy.

See [`runtime-pool-roadmap.md`](runtime-pool-roadmap.md).

---

## Runtime Instance as Hosting Unit

A runtime instance is the primary hosting unit.

A runtime instance can represent:

- a local process;
- a background hosted service;
- a runtime-only process;
- a container;
- a Kubernetes pod;
- a dedicated customer runtime;
- a managed execution unit.

A runtime instance can expose:

- RuntimeInstanceId;
- worker count;
- local queue capacity;
- max concurrent runs;
- heartbeat;
- current assigned runs;
- current executing steps;
- health status;
- queue depth;
- capacity status;
- observability metadata.

This is the natural building block for managed hosting.

---

## Worker as Capacity Unit

Workers are the execution slots inside a runtime instance.

A worker can execute claimed steps.

Worker-based capacity allows the platform to reason about:

- how much work can run concurrently;
- how many steps can execute at the same time;
- which worker owns a step;
- which worker failed;
- which worker is saturated;
- how many workers should be provisioned;
- how runtime capacity should be scaled.

This creates a direct path toward hosting by worker capacity.

A future managed hosting tier can be based on:

```text
runtime instances x workers per instance
```

instead of vague infrastructure limits.

---

## Shared Queue as Admission Layer

The shared queue is the global admission and dispatch layer.

It allows the platform to accept work before it is assigned to a runtime instance.

The shared queue can support:

- queued runs;
- run ordering direction;
- queue pressure visibility;
- admission decisions;
- dispatch decisions;
- cancellation of queued runs;
- runtime instance selection;
- capacity-aware dispatch;
- policy-driven throttling;
- fair scheduling direction;
- tenant-aware queue direction.

The shared queue is critical for managed hosting because it separates incoming workload from execution capacity.

This is the layer that can decide:

```text
Can this work run now?
Should it wait?
Which runtime instance should receive it?
Should the run be throttled?
Should capacity be increased?
```

---

## Local Queue per Runtime Instance

Each runtime instance can maintain its own local queue.

The local queue receives work assigned to that runtime instance.

This preserves the single-instance model while allowing distributed execution.

The architectural rule remains:

> Shared scheduling is added above local queues. Local queues remain valid.

This is important because:

- single-instance execution remains simple;
- local runtime behavior remains stable;
- multi-instance execution can be added above it;
- runtime instances can manage their own workers;
- Kubernetes-style pods can operate independently;
- the control plane can still dispatch work globally.

The local queue is what makes each runtime instance autonomous enough to execute work safely.

---

## Admission Control

Admission control is the decision layer that decides whether work can enter the system.

Admission can be based on:

- global capacity;
- tenant capacity;
- project capacity;
- pipeline capacity;
- runtime instance capacity;
- worker capacity;
- shared queue depth;
- local queue depth;
- provider/model limits;
- operation limits;
- concurrency policy;
- throttling policy;
- cost or usage limits direction;
- maintenance mode direction;
- pause/resume state direction.

Admission control is critical for managed hosting.

A managed runtime should not accept unlimited work blindly.

It should make controlled decisions and record those decisions.

Admission decisions should be visible through:

- decision ledger;
- metrics;
- traces;
- MCP diagnostics;
- dashboard views;
- queue status.

---

## Policy-Driven Admission

Admission should be policy-driven.

The policy engine can evaluate:

- can this tenant submit more runs?
- is this pipeline allowed to execute?
- is the queue under pressure?
- is the provider/model capacity available?
- are runtime instances healthy?
- is there enough worker capacity?
- should the run be accepted, queued, delayed, throttled, or rejected?

Policy-driven admission makes the hosting model governable.

It prevents the platform from becoming a simple unbounded queue.

---

## Dynamic Provider-Based Runtime Hosting

Provider-based runtime hosting is a major foundation for managed hosting.

The runtime should be able to communicate with execution environments through providers.

Provider-based hosting can support:

- local runtime provider;
- in-memory provider direction;
- HTTP runtime provider direction;
- runtime-instance-only provider direction;
- control-plane provider direction;
- shared queue provider;
- runtime instance registry provider;
- decision ledger provider;
- replay provider;
- observability provider.

This provider-driven approach allows communication between:

```text
MCP Control Plane
Control Plane Host
Shared Queue
Runtime Instance
Remote Runtime Provider
Local Runtime Provider
HTTP Runtime Provider
Decision Ledger
Replay Store
Observability Layer
```

The goal is to avoid hardcoding one hosting model.

The runtime can evolve from local execution to distributed execution without rewriting the core engine.

---

## Dynamic Communication Between Runtime Instances and MCP

The managed hosting model depends on communication between MCP, the control plane, and runtime instances.

The dynamic provider direction supports communication patterns such as:

```text
MCP tool call
  -> Control-plane operation
      -> Shared run / shared queue
          -> Runtime provider
              -> Runtime instance
                  -> Local queue
                      -> Worker
```

It also supports inspection patterns such as:

```text
MCP diagnostics
  -> Runtime instance registry
      -> Runtime instance status
      -> Worker status
      -> Queue status
      -> Ledger events
      -> Replay report
```

This means MCP does not need to execute everything directly.

MCP can act as the structured control surface over the distributed runtime.

---

## Runtime Provider Responsibilities

A runtime provider can be responsible for:

- submitting work to a runtime instance;
- dispatching assigned runs;
- communicating over HTTP or another transport direction;
- returning runtime status;
- returning health status;
- returning queue status;
- exposing execution results direction;
- exposing diagnostics direction;
- supporting cancellation direction;
- supporting replay/control-plane integration direction.

This makes runtime communication pluggable.

A local provider can be used for development.  
An HTTP provider can be used for remote runtime instances.  
A future provider can support other transport models.

---

## Control Plane Responsibilities

The control plane is responsible for operating the runtime system.

It can handle:

- run submission;
- admission;
- shared queue interaction;
- runtime instance selection;
- dispatch;
- cancellation;
- replay;
- diagnostics;
- ledger inspection;
- runtime instance inspection;
- worker inspection;
- observability summary;
- policy evaluation direction.

The control plane does not need to execute every step itself.

It coordinates execution across runtime instances.

This separation is essential for managed hosting.

---

## MCP Responsibilities

MCP exposes control-plane operations as structured tools.

MCP can support:

- submit run;
- inspect run;
- inspect execution;
- pause execution;
- resume execution;
- cancel execution;
- replay execution;
- inspect shared queue;
- inspect runtime instances;
- inspect workers;
- inspect decision ledger;
- inspect policy decisions;
- inspect retention decisions;
- inspect diagnostics;
- inspect observability summaries.

MCP makes the managed runtime operable.

It can be used by developers, agents, dashboards, and future automation.

---

## Hosting Modes

The platform can support several hosting modes.

## 1. Local Development Mode

Everything runs locally.

Useful for:

- development;
- tests;
- demos;
- debugging;
- simple sample workflows.

This mode can use in-memory or local infrastructure.

---

## 2. Single Runtime Instance Mode

A single runtime instance runs the workflow execution.

Useful for:

- small deployments;
- early production usage;
- local demos;
- simple self-hosted environments.

Local queues remain valid in this mode.

---

## 3. Control Plane With Local Runtime Instances

The control plane can run with local runtime instances in the same host process.

Useful for:

- integration tests;
- local multi-instance simulation;
- demos;
- validating shared queue dispatch;
- validating worker capacity.

This mode is very useful before full Kubernetes deployment.

---

## 4. Runtime Instance Only Mode

A host can run only as a runtime instance.

It receives work assigned from a control plane.

Useful for:

- distributed deployments;
- remote runtime processes;
- Kubernetes pods;
- worker services;
- dedicated execution nodes.

This is one of the most important modes for managed hosting.

---

## 5. Control Plane With Remote Runtime Instances

The control plane can coordinate runtime instances that run in separate processes or services.

Useful for:

- distributed execution;
- remote workers;
- horizontal scale-out;
- control-plane/worker separation;
- Kubernetes-style deployments;
- managed hosting.

This mode depends on provider-based communication.

---

## 6. HTTP Runtime Provider Mode

HTTP runtime provider direction allows communication between control plane and runtime instances over HTTP.

Useful for:

- remote runtime instances;
- process separation;
- container communication;
- integration testing;
- managed runtime direction;
- future cloud deployment.

The HTTP provider is an important step toward distributed hosting.

---

## 7. Kubernetes-Style Mode

In Kubernetes-style mode:

```text
MCP / Control Plane Pod
  -> Shared Queue
      -> Runtime Instance Pods
          -> Local Queues
              -> Workers
```

Useful for:

- multi-instance execution;
- scale-out demos;
- runtime instance isolation;
- worker capacity scaling;
- observability demonstration;
- managed hosting direction.

Kubernetes is a natural deployment target because runtime instances map cleanly to pods.

---

## 8. Dedicated Enterprise Runtime Mode

A customer may eventually have dedicated runtime instances or a dedicated cluster.

Useful for:

- regulated environments;
- high-volume customers;
- isolation requirements;
- custom retention policies;
- custom observability exports;
- dedicated support;
- data residency requirements.

This is a long-term hosting direction.

---

## Hosting Capacity Model

The managed hosting model can be based on measurable capacity.

Potential capacity dimensions include:

| Dimension | Meaning |
|---|---|
| Runtime instances | Number of runtime hosts available. |
| Workers per instance | Number of local execution slots. |
| Max concurrent runs | Maximum active runs per instance or tenant. |
| Local queue capacity | Per-instance queue depth. |
| Shared queue capacity | Global or tenant-level queue capacity. |
| Execution volume | Number of executions over time. |
| Step volume | Number of executed steps over time. |
| Replay volume | Number of replay operations. |
| Ledger volume | Number of decision events. |
| Retention size | Amount of retained execution/audit data. |
| Observability level | Logs, metrics, traces, exports, dashboards. |
| Dedicated capacity | Reserved runtime instances or clusters. |

This model is technically aligned with the architecture.

---

## Capacity-Based Product Direction

A future managed product can offer capacity based on:

- small runtime capacity;
- standard runtime capacity;
- high-throughput runtime capacity;
- dedicated runtime capacity;
- regulated workload capacity;
- long-retention capacity;
- high-observability capacity.

This does not need to be implemented immediately.

But the architecture already supports the idea because runtime capacity is measurable.

---

## Scaling Model

The scaling model can evolve progressively.

## Vertical Scaling

Increase capacity inside one runtime instance:

- more workers;
- larger local queue;
- higher max concurrent runs;
- stronger machine/container resources.

## Horizontal Scaling

Add more runtime instances:

- more runtime services;
- more containers;
- more pods;
- more dedicated execution nodes.

## Shared Queue Scaling

Use shared queue and dispatch logic to distribute runs across runtime instances.

## Tenant-Aware Scaling

Allocate capacity by tenant, project, or pipeline direction.

## Dedicated Scaling

Allocate reserved runtime instances or clusters for high-value or regulated customers.

This scaling model aligns with managed hosting.

---

## Autoscaling Direction

Autoscaling can eventually be based on runtime signals.

Potential signals include:

- shared queue depth;
- local queue depth;
- average queue wait time;
- worker utilization;
- runtime instance saturation;
- dispatch failures;
- retry rate;
- execution duration;
- tenant workload;
- provider/model throttling;
- observability metrics.

Autoscaling direction can support:

- adding runtime instances;
- increasing workers per instance;
- routing work differently;
- throttling incoming runs;
- reserving capacity for specific tenants.

For now, autoscaling should be treated as a direction, not a completed product claim.

---

## Managed Hosting and Decision Ledger

The Decision Ledger is important for managed hosting.

It can record:

- admission decisions;
- queue decisions;
- dispatch decisions;
- runtime instance selection;
- capacity decisions;
- throttling decisions;
- cancellation decisions;
- replay decisions;
- retention decisions;
- scaling decisions direction;
- failed dispatch attempts.

This helps explain hosting behavior.

For example:

- why a run waited;
- why a runtime instance was selected;
- why a tenant was throttled;
- why capacity was exhausted;
- why a run was cancelled;
- why a replay was allowed;
- why data was retained or compacted.

Managed hosting requires this level of explanation.

---

## Managed Hosting and Observability

Observability is essential for managed hosting.

The hosting model should expose:

- runtime instance health;
- worker utilization;
- shared queue depth;
- local queue depth;
- queue wait time;
- execution throughput;
- failure rate;
- retry rate;
- cancellation rate;
- dispatch success/failure;
- ledger event volume;
- replay volume;
- retention activity;
- storage pressure;
- tenant usage direction.

This helps operate the platform and support customers.

---

## Managed Hosting and Replay / Audit

Managed hosting should include replay and audit capabilities.

Customers need to understand what happened inside their AI workflows.

Replay and audit can support:

- customer support;
- incident investigation;
- debugging;
- execution validation;
- policy review;
- regulated-market technical controls;
- internal governance;
- audit reports direction.

Replay and audit are not optional in a managed AI execution service.

They are part of the value proposition.

---

## Managed Hosting and Retention

Managed hosting must include retention strategy.

Retention affects:

- storage cost;
- replay availability;
- audit history;
- compliance-support direction;
- customer expectations;
- sensitive data handling;
- archive cost;
- dashboard visibility.

Possible retention dimensions:

- short retention;
- standard retention;
- long retention;
- audit retention;
- replay report retention;
- ledger retention;
- trace retention;
- payload retention direction;
- encrypted retention archive direction.

Retention can become a hosting configuration and eventually a product tier dimension.

---

## Managed Hosting and Security

Managed hosting requires security hardening.

Future directions include:

- tenant-aware isolation;
- access-controlled dashboard;
- access-controlled MCP tools;
- RBAC-aware execution;
- ARN-inspired resource scopes;
- provider credential isolation;
- secret reference direction;
- redacted logs/replay direction;
- encrypted ledger payload direction;
- encrypted retention archive direction;
- audit of sensitive access.

Security must be part of hosting from the beginning.

---

## Managed Hosting and Multi-Tenant Readiness

Managed hosting and multi-tenant readiness are connected.

Multi-tenant hosting requires:

- tenant-aware execution context;
- tenant-aware policies;
- tenant-aware queues direction;
- tenant-aware runtime capacity;
- tenant-aware ledger;
- tenant-aware replay;
- tenant-aware observability;
- tenant-aware retention;
- tenant-aware dashboard views;
- tenant-aware MCP access.

The current RBAC/context/policy foundation supports this direction.

---

## Managed Hosting and MCP

MCP can become the operational interface for managed hosting.

MCP tools can expose:

- submit run;
- inspect run;
- inspect queue;
- inspect runtime instance;
- inspect worker capacity;
- inspect replay;
- inspect ledger;
- inspect diagnostics;
- inspect retention;
- pause/resume/cancel;
- runtime health summaries.

This makes MCP a natural control interface for hosted execution.

---

## Managed Hosting and Dashboard

The dashboard is the visual control layer for managed hosting.

Dashboard views can include:

- tenant workload direction;
- runtime instance capacity;
- worker utilization;
- queue pressure;
- run status;
- execution status;
- replay/audit;
- decision ledger;
- retention usage;
- observability signals;
- diagnostics.

The dashboard helps customers understand what they are paying for and how their workloads are behaving.

---

## Deployment Models

The platform can support several deployment models over time.

| Deployment Model | Description |
|---|---|
| Local Development | Single machine or local Docker setup. |
| Self-Hosted | Customer deploys platform in their own infrastructure. |
| Managed Cloud | Platform is hosted as a managed service. |
| Dedicated Runtime Instances | Customer receives reserved runtime instances. |
| Dedicated Enterprise Cluster | Customer receives isolated cluster direction. |
| Private Cloud Deployment | Customer deploys in private cloud or controlled environment. |
| Kubernetes Deployment | Runtime instances run as pods with shared queue and control plane. |

The same runtime concepts should apply across deployment models.

---

## Commercial Model Direction

This document is public GitHub documentation, so it should avoid private pricing details.

However, the technical architecture naturally supports future commercial models such as:

- self-hosted enterprise license direction;
- managed runtime hosting direction;
- dedicated runtime capacity direction;
- usage-based execution direction;
- instance/worker capacity direction;
- replay/audit retention direction;
- observability level direction;
- support/SLA direction.

The important point is that the commercial direction is aligned with the architecture.

The system already has measurable execution capacity concepts.

---

## Single-Developer Roadmap

Because the project is currently built and maintained by one developer, managed hosting should be approached progressively.

The first goal should be to prove the hosting architecture, not to run a full commercial cloud service immediately.

## Suggested Stages

### Stage 1 — Local Multi-Instance Demo

- multiple runtime instances;
- multiple workers;
- shared queue dispatch;
- local queues;
- run/execution mapping;
- replay;
- ledger;
- observability logs.

### Stage 2 — Runtime Instance Registry Visibility

- list runtime instances;
- heartbeat;
- capacity;
- assigned runs;
- worker count;
- local queue status.

### Stage 3 — Provider-Based Remote Dispatch

- HTTP runtime provider direction;
- runtime-instance-only host mode;
- control plane dispatch to remote runtime instances;
- diagnostics through MCP.

### Stage 4 — Dashboard Visibility

- runtime instance dashboard;
- queue dashboard;
- worker dashboard;
- replay/audit dashboard;
- ledger dashboard.

### Stage 5 — Kubernetes-Style Demo

- control plane;
- shared queue;
- multiple runtime instance pods;
- worker capacity;
- queue pressure;
- distributed observability.

### Stage 6 — Tenant-Aware Hosting Direction

- tenant/project context;
- quota direction;
- reserved capacity direction;
- tenant-aware ledger/replay/retention direction.

This staged approach keeps the roadmap realistic.

---

## Current Foundation Summary

| Area | Status |
|---|---|
| Runtime instance identity | Foundation exists |
| Worker identity | Foundation exists |
| Multiple workers per instance | Foundation exists |
| Multiple runtime instances | Foundation exists / active direction |
| Local queue per runtime instance | Foundation exists |
| Shared queue direction | Foundation exists |
| Shared run direction | Foundation exists |
| Run/execution separation | Foundation exists |
| Admission control direction | Foundation exists / active direction |
| Capacity-aware dispatch direction | Foundation exists / active direction |
| Policy-driven concurrency/throttling | Foundation exists |
| Runtime instance registry direction | Foundation exists |
| Provider-based hosting | Foundation exists |
| Dynamic runtime provider direction | Foundation exists |
| HTTP runtime provider direction | Foundation exists |
| Runtime-instance-only mode | Foundation exists |
| Control plane with runtime instances | Foundation exists |
| MCP control-plane foundation | Foundation exists |
| Shared queue pump direction | Foundation exists |
| Replay/audit foundation | Foundation exists |
| Decision ledger foundation | Foundation exists |
| Observability direction | Foundation exists |
| Retention/eviction/compaction foundation | Foundation exists |
| Kubernetes-style execution | Active direction |
| Managed cloud service | Long-term productization target |
| Dedicated enterprise cluster | Long-term productization target |
| Autoscaling | Future direction |

---

## Planned Improvements

The managed hosting model should continue through staged productization:

- runtime instance registry hardening;
- worker capacity visibility;
- shared queue diagnostics;
- admission decision visibility;
- provider-based communication hardening;
- HTTP runtime provider hardening;
- runtime-instance-only deployment examples;
- MCP diagnostics for hosting;
- dashboard views for runtime capacity;
- Kubernetes-style demo;
- tenant-aware capacity direction;
- usage metering direction;
- retention usage visibility;
- observability export;
- security hardening;
- dedicated runtime capacity direction.

These are productization and hardening steps.

They build on the existing multiple-instance, multiple-worker, admission, provider, queue, MCP, replay, ledger, and observability foundations.

---

## Final Statement

The managed hosting model is a natural extension of the Deterministic AI Runtime Platform.

The architecture already contains the main ingredients:

- multiple runtime instances;
- multiple workers;
- local queues;
- shared queue;
- run/execution separation;
- admission direction;
- capacity-aware dispatch direction;
- policy-driven concurrency and throttling;
- provider-based hosting;
- dynamic runtime provider direction;
- MCP control plane;
- replay and audit;
- decision ledger;
- observability;
- retention, eviction, and compaction.

The long-term goal is to make AI execution capacity manageable as a product.

A customer should eventually be able to run deterministic AI workflows on managed runtime capacity, observe what is happening, control execution through MCP or dashboard, replay and audit results, and scale by runtime instance and worker capacity.
