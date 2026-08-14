# MCP Control Interface

## Deterministic AI Runtime Platform

This document describes the MCP Control Interface of the Deterministic AI Runtime Platform.

The MCP control layer is not only a future idea. The project already has a meaningful MCP server and control-plane foundation. The roadmap is to harden, expose, document, extend, and productize this foundation into a complete operational interface for deterministic AI workflow execution.

MCP is important because the runtime should not only execute workflows. It should also be controllable, inspectable, replayable, diagnosable, and observable through a structured tool interface.

---

## Purpose

The purpose of the MCP Control Interface is to expose runtime operations through a structured control-plane surface.

A production AI runtime needs more than workflow execution.

It needs operations such as:

- submit a run;
- inspect a run;
- inspect an execution;
- pause execution;
- resume execution;
- cancel execution;
- replay execution;
- inspect queues;
- inspect shared queue state;
- inspect runtime instances;
- inspect workers;
- inspect decision ledger events;
- inspect observability signals;
- inspect replay reports;
- run diagnostics.

The MCP Control Interface turns the runtime into an operable platform.

---

## Current Foundation

The project already includes an MCP server and control-plane foundation.

The existing direction already covers several important areas:

- MCP server direction;
- MCP host direction;
- runtime control-plane direction;
- shared run direction;
- shared queue direction;
- runtime instance direction;
- replay tool direction;
- execution control direction;
- observability tool direction;
- diagnostics direction;
- integration testing direction;
- control-plane with runtime instances direction;
- runtime-instance-only hosting direction;
- HTTP runtime provider direction;
- shared queue pump direction;
- multi-instance runtime direction.

This means MCP is already part of the architecture.

The roadmap is not to invent MCP integration from scratch.

The roadmap is to make the existing MCP/control-plane foundation easier to use, more complete, more visible, more testable, and more product-ready.

---

## Why MCP Matters

AI workflows need a control surface.

Without a control surface, AI workflows become difficult to operate in production.

Operators, developers, and future dashboard interfaces need to ask questions such as:

- Which runs are queued?
- Which execution is running?
- Which runtime instance accepted the run?
- Which worker executed a step?
- Can this execution be paused?
- Can this execution be cancelled?
- Can this execution be replayed?
- What does the decision ledger say?
- What happened in the shared queue?
- Are runtime instances healthy?
- Are workers saturated?
- Are queues under pressure?
- Which policy decision blocked or allowed execution?
- Which retention or compaction decision happened?

MCP provides a structured way to expose these operations as tools.

---

## MCP as Runtime Control Plane

The MCP interface should be understood as part of the runtime control plane.

The control plane is responsible for operating the runtime.

It is different from the execution engine itself.

```text
Execution Engine = runs deterministic workflows
Control Plane    = observes, controls, replays, cancels, diagnoses, and manages runtime activity
MCP Interface    = exposes control-plane operations as structured tools
```

This separation is important.

The runtime engine should stay focused on execution semantics.

The MCP layer should expose safe operational commands.

---

## Core MCP Domains

The MCP control interface can be organized around several domains.

| Domain | Purpose |
|---|---|
| Shared Runs | Submit, inspect, cancel, and track submitted work. |
| Shared Queue | Inspect queue state, queue pressure, and dispatch direction. |
| Runtime Instances | Inspect registered runtime instances, capacity, health, and assignment direction. |
| Execution Control | Pause, resume, cancel, and inspect executions. |
| Replay | Replay executions, inspect replay reports, and expose replay diagnostics. |
| Decision Ledger | Inspect structured runtime decisions. |
| Observability | Expose logs, metrics, traces, and health direction. |
| Diagnostics | Provide runtime, queue, replay, and execution diagnostic summaries. |
| Provider Hosting | Support local, runtime-instance-only, control-plane, and HTTP provider modes. |

---

# 1. Shared Run Tools

Shared run tools expose control-plane work submission and tracking.

A run is different from an execution.

```text
RunId       = submitted/control-plane/queue identity
ExecutionId = durable workflow execution identity
```

Shared run tools can support:

- submit run;
- inspect run;
- list runs;
- inspect run status;
- inspect run assignment;
- inspect run execution ID;
- inspect run lifecycle;
- cancel queued run;
- cancel running run direction;
- inspect run diagnostics.

These tools are important because they allow users to interact with work before and after it becomes a durable execution.

---

## Shared Run States

The MCP interface should expose meaningful run states.

Possible run states include:

- submitted;
- queued;
- assigned;
- dispatched;
- running;
- completed;
- failed;
- cancelled;
- cancellation requested.

A user should be able to inspect where a run is in the control-plane lifecycle.

This is especially important for shared queue and multi-instance execution.

---

## Shared Run Diagnostics

Shared run diagnostics can answer:

- Was the run accepted?
- Was the run queued?
- Was it assigned to a runtime instance?
- Did it expose an ExecutionId?
- Was it cancelled before execution?
- Was it dispatched but not yet started?
- Did it fail during dispatch?
- Which ledger events are linked to it?
- Which runtime instance handled it?

This makes run-level operations visible.

---

# 2. Shared Queue Tools

The shared queue is a key part of distributed runtime direction.

The MCP interface should expose shared queue operations such as:

- inspect queue length;
- inspect queued runs;
- inspect queue pressure;
- inspect dispatch status;
- inspect dispatch failures;
- inspect paused/resumed queue state direction;
- inspect run ordering direction;
- inspect queue diagnostics;
- inspect queue metrics;
- inspect queue-related ledger events.

The shared queue should remain above local queues.

The architectural principle is:

> Shared scheduling is added above local queues. Local queues remain valid.

MCP should make that visible.

---

## Shared Queue Diagnostics

Shared queue diagnostics can answer:

- How many runs are waiting?
- Which runs are assigned?
- Which runs are blocked?
- Is the queue paused?
- Is the queue under pressure?
- Are runtime instances available?
- Are dispatch attempts failing?
- Is capacity exhausted?
- Are queue decisions recorded in the ledger?

This is useful for local demos, Kubernetes-style demos, and future managed hosting.

---

# 3. Runtime Instance Tools

Runtime instance tools expose the distributed execution environment.

A runtime instance can represent:

- a local process;
- a background host;
- a runtime service;
- a container;
- a Kubernetes pod;
- a managed execution unit.

MCP runtime instance tools can support:

- list runtime instances;
- inspect runtime instance;
- inspect heartbeat;
- inspect worker count;
- inspect available capacity;
- inspect local queue state;
- inspect assigned runs;
- inspect runtime health;
- inspect runtime diagnostics;
- inspect instance ledger events.

Runtime instance visibility is essential for distributed AI execution.

---

## Runtime Instance Capacity

The MCP interface should expose capacity information such as:

- configured worker count;
- active workers;
- available workers;
- max concurrent runs;
- current assigned runs;
- local queue depth;
- queue capacity;
- heartbeat freshness;
- health status.

This helps answer:

- Can this instance accept more work?
- Is this instance overloaded?
- Did this instance stop reporting heartbeat?
- Which runs are assigned to it?
- How many workers are active?

---

# 4. Worker Tools

Worker visibility is important because workers execute the actual steps.

MCP worker tools can expose:

- worker identity;
- worker status;
- current work direction;
- completed work direction;
- failed work direction;
- worker capacity;
- worker metrics;
- worker correlation identifiers;
- worker runtime instance relationship.

Worker tools help connect execution behavior to distributed runtime capacity.

---

# 5. Execution Control Tools

Execution control tools are central to the MCP interface.

Production AI workflows must be controllable.

MCP execution control tools can support:

- inspect execution;
- inspect execution state;
- inspect step states;
- pause execution;
- resume execution;
- cancel execution;
- inspect cancellation status;
- inspect waiting-for-input direction;
- inspect retry state;
- inspect finalization state;
- inspect execution diagnostics.

These operations should be safe and state-aware.

---

## Pause Execution

Pause should prevent new step execution while preserving execution state.

MCP pause tools should expose:

- execution ID;
- pause request result;
- current execution status;
- whether the execution was already paused;
- whether pause was rejected;
- related decision ledger events.

---

## Resume Execution

Resume should allow a paused execution to continue.

MCP resume tools should expose:

- execution ID;
- resume request result;
- current execution status;
- whether execution can continue;
- whether ready steps exist;
- related decision ledger events.

---

## Cancel Execution

Cancel should stop queued or running work where possible.

MCP cancel tools should expose:

- execution ID or run ID;
- cancellation result;
- whether the run was queued or running;
- whether running work received cancellation direction;
- final status direction;
- related decision ledger events.

Cancellation is a production control feature.

It must be visible and auditable.

---

# 6. Replay Tools

Replay tools are one of the strongest MCP domains.

The platform already has replay and audit foundation.

MCP replay tools can expose:

- replay execution;
- inspect replay report;
- inspect replay issues;
- inspect replay timeline;
- inspect deterministic validation result;
- inspect retry history;
- inspect cancellation history;
- inspect policy decisions;
- inspect retention/compaction status;
- inspect replay diagnostics.

Replay through MCP makes runtime investigation accessible through the control plane.

---

## Audit-Only Replay

MCP should support audit-only replay direction.

Audit-only replay means replaying for inspection without triggering side effects.

This is important because many AI workflows may include:

- external API calls;
- tool execution;
- database writes;
- notifications;
- business actions.

The safe default for replay should be inspection, not uncontrolled re-execution.

---

# 7. Decision Ledger Tools

The MCP interface should expose the Decision Ledger.

Decision ledger tools can support:

- inspect ledger by execution;
- inspect ledger by run;
- inspect ledger by step;
- inspect ledger by worker;
- inspect ledger by runtime instance;
- inspect ledger by correlation ID;
- inspect policy decisions;
- inspect retry decisions;
- inspect queue decisions;
- inspect replay decisions;
- inspect retention decisions;
- inspect finalization decisions;
- summarize ledger timeline.

This is important because the ledger explains why the runtime behaved the way it did.

MCP ledger tools make the decision history accessible.

---

# 8. Policy and Governance Tools

Because the runtime is configuration-driven, context-driven, policy-driven, and provider-driven, MCP can expose governance visibility.

Policy/governance tools can support:

- inspect policy decisions;
- inspect policy result;
- inspect policy context;
- inspect tenant/project/pipeline context direction;
- inspect provider/model/tool access decisions;
- inspect concurrency decisions;
- inspect throttling decisions;
- inspect denied operations;
- inspect policy-related ledger events.

This is important because enterprise runtime behavior must be explainable.

A control plane that can execute but cannot explain policy decisions is incomplete.

---

# 9. Retention, Eviction, and Compaction Tools

Retention, eviction, and compaction are part of the runtime foundation.

MCP tools can expose lifecycle visibility such as:

- inspect retention decisions;
- inspect eviction decisions;
- inspect compaction decisions;
- inspect archive decisions;
- inspect hot-state cleanup;
- inspect stale claim cleanup;
- inspect compacted history status;
- inspect retained-history references;
- inspect replay availability after compaction;
- inspect retention-related ledger events.

This matters because execution data lifecycle affects replay, audit, storage cost, and production safety.

Retention should not be invisible background cleanup.

It should be observable and auditable through the control plane.

---

# 10. Observability Tools

MCP observability tools can expose runtime health and operational signals.

These tools can include:

- inspect runtime health;
- inspect queue pressure;
- inspect worker utilization;
- inspect runtime instance health;
- inspect retry metrics;
- inspect failure metrics;
- inspect cancellation metrics;
- inspect replay metrics;
- inspect ledger metrics;
- inspect retention metrics;
- inspect traces direction;
- inspect logs direction;
- inspect correlation summaries.

Observability tools help turn MCP into a runtime operations interface.

---

# 11. Diagnostics Tools

Diagnostics tools provide summarized runtime insight.

MCP diagnostics can support:

- execution diagnostics;
- replay diagnostics;
- queue diagnostics;
- runtime instance diagnostics;
- worker diagnostics;
- ledger diagnostics;
- policy diagnostics;
- retention diagnostics;
- observability diagnostics;
- distributed runtime diagnostics.

Diagnostics are useful because users may not always know which low-level tool to call.

A diagnostic tool can provide a high-level summary and links to deeper details.

---

# 12. MCP Host Modes

The MCP host direction can support several modes.

Examples include:

| Mode | Purpose |
|---|---|
| Control Plane Only | MCP host exposes tools and coordinates external runtime instances. |
| Runtime Instance Only | Host runs as a runtime instance that receives assigned work. |
| Control Plane With Runtime Instances | Host exposes MCP tools and also starts local runtime instances. |
| Local Development Mode | Single-process local execution and diagnostics. |
| HTTP Runtime Provider Mode | Control plane communicates with runtime instances over HTTP direction. |
| Future Kubernetes Mode | MCP control plane manages or observes distributed runtime instances. |

This host-mode flexibility is important because the same architecture should support local development, integration tests, demos, and future distributed deployment.

---

# 13. MCP and Provider-Based Hosting

The MCP control plane fits naturally with provider-based runtime hosting.

Provider-based hosting allows the runtime to abstract execution across different host types.

MCP can interact with:

- local runtime provider;
- HTTP runtime provider direction;
- runtime-instance-only hosts;
- control-plane-managed runtime instances;
- shared queue provider;
- runtime instance registry provider;
- decision ledger provider;
- replay provider;
- observability provider.

This is what allows the control plane to remain stable while hosting modes evolve.

---

# 14. MCP and Shared Queue Pump

The shared queue pump is important for multi-instance runtime direction.

The pump can:

- observe shared queue;
- inspect available runtime instances;
- evaluate capacity;
- dispatch runs;
- push work toward runtime instances;
- update run assignment direction;
- expose queue pressure direction;
- produce structured diagnostics.

MCP should be able to inspect or trigger diagnostics around the shared queue pump.

This is important for Kubernetes-style demos and future managed hosting.

---

# 15. MCP and Kubernetes Direction

MCP can become the control surface for Kubernetes-style runtime execution.

In a Kubernetes-style deployment:

```text
MCP Control Plane
  -> Shared Queue
      -> Runtime Instance / Pod
          -> Local Queue
              -> Workers
```

MCP tools can expose:

- runtime instance registry;
- pod-like runtime identities;
- worker capacity;
- shared queue state;
- run assignment;
- execution state;
- replay report;
- decision ledger;
- observability summary.

This makes distributed execution visible and controllable.

---

# 16. MCP and Dashboard

The future dashboard can use the same conceptual control-plane operations exposed through MCP.

The dashboard can visualize:

- runs;
- executions;
- queues;
- runtime instances;
- workers;
- replay reports;
- decision ledger events;
- diagnostics;
- observability;
- policy decisions;
- retention activity.

MCP and dashboard should align around the same runtime concepts.

This avoids building two disconnected operational models.

---

# 17. MCP and Replay / Audit

Replay and audit are core MCP use cases.

MCP replay/audit operations can help users:

- replay execution;
- inspect replay report;
- inspect replay issues;
- inspect decision ledger;
- inspect policy decisions;
- inspect retry/cancellation history;
- inspect retention and compaction status;
- generate audit summary direction.

This makes MCP useful not only for starting work, but also for understanding work after it completes.

---

# 18. MCP and Security Boundaries

MCP tools must be designed with security boundaries in mind.

Some operations may expose sensitive data:

- prompts;
- model responses;
- tool inputs;
- tool outputs;
- RBAC context;
- tenant context;
- policy context;
- ledger payloads;
- replay reports;
- retained history.

Future hardening should include:

- access-controlled MCP tools;
- tenant-aware tool visibility;
- redacted outputs;
- sensitive payload filtering;
- audit of MCP tool access;
- encrypted payload direction;
- compliance profile direction.

MCP should not become an unrestricted backdoor into runtime internals.

It should be a controlled operational interface.

---

# 19. MCP Tool Response Design

MCP tool responses should be structured and predictable.

A good MCP response should include:

- status;
- result;
- errors;
- warnings;
- execution ID;
- run ID;
- correlation ID;
- runtime instance ID when applicable;
- worker ID when applicable;
- decision ledger references;
- replay report references;
- diagnostics summary;
- next suggested operation direction.

Structured responses make the tools easier to use by humans, agents, dashboards, and automated diagnostics.

---

# 20. MCP Error Handling

MCP error handling should be explicit.

Examples of error categories:

- execution not found;
- run not found;
- runtime instance not found;
- queue unavailable;
- replay report not found;
- ledger unavailable;
- operation denied by policy;
- operation not allowed in current state;
- execution already finalized;
- execution already cancelled;
- runtime instance unavailable;
- invalid input;
- storage unavailable;
- timeout.

Errors should be understandable and actionable.

---

# 21. MCP Observability

MCP operations themselves should be observable.

MCP activity can generate:

- structured logs;
- decision ledger events;
- metrics;
- traces;
- correlation IDs;
- diagnostics events.

This is important because MCP may become an operational entry point.

Users should be able to audit:

- who requested replay;
- who requested cancellation;
- who inspected ledger;
- who paused execution;
- which diagnostic tool was called;
- which MCP call failed.

---

# 22. Current Foundation Summary

| Area | Status |
|---|---|
| MCP server foundation | Foundation exists |
| MCP host direction | Foundation exists |
| Runtime control-plane direction | Foundation exists |
| Shared run tools direction | Foundation exists |
| Shared queue tools direction | Foundation exists |
| Runtime instance tools direction | Foundation exists |
| Replay tools direction | Foundation exists |
| Execution control tools direction | Foundation exists |
| Decision ledger tools direction | Foundation exists |
| Observability tools direction | Foundation exists |
| Diagnostics tools direction | Foundation exists |
| Provider-based hosting integration | Foundation exists |
| Runtime-instance-only mode | Foundation exists |
| Control-plane with runtime instances direction | Foundation exists |
| HTTP runtime provider direction | Foundation exists |
| Shared queue pump direction | Foundation exists |
| Kubernetes-style MCP direction | Foundation exists / active direction |
| Dashboard alignment | Productization target |
| Security boundaries | Planned hardening direction |
| Access-controlled MCP tools | Planned hardening direction |

---

# Productization Roadmap

The MCP Control Interface should continue improving through staged productization.

## Milestone 1 — Document Current MCP Tools

Improve:

- tool list;
- tool purpose;
- input schema documentation;
- output schema documentation;
- examples;
- error behavior;
- diagnostics behavior.

## Milestone 2 — Strengthen Runtime Control Tools

Improve:

- run inspection;
- execution inspection;
- pause;
- resume;
- cancel;
- replay;
- queue inspection;
- runtime instance inspection;
- diagnostics.

## Milestone 3 — Strengthen Replay / Ledger / Observability Tools

Improve:

- replay reports;
- replay issues;
- ledger inspection;
- policy decision inspection;
- retry/cancellation inspection;
- retention/compaction inspection;
- observability summaries.

## Milestone 4 — Align MCP With Dashboard

Prepare MCP operations and response models so dashboard views can align with the same runtime concepts.

## Milestone 5 — Harden Security and Access Control

Improve:

- tenant-aware access;
- permission-aware tools;
- sensitive payload redaction;
- tool access audit;
- secure replay access;
- secure ledger access.

---

# Planned Improvements

The MCP Control Interface should continue improving in the following areas:

- complete public tool documentation;
- response consistency;
- error consistency;
- replay tool coverage;
- ledger tool coverage;
- runtime instance tool coverage;
- shared queue tool coverage;
- execution control tool coverage;
- diagnostics tooling;
- policy decision visibility;
- retention/eviction/compaction visibility;
- observability summaries;
- dashboard alignment;
- access-control direction;
- tenant-aware tool visibility;
- security hardening.

These are productization and hardening steps.

They make the existing MCP foundation easier to use, easier to trust, easier to demonstrate, and easier to operate.

---

# Final Statement

The MCP Control Interface is already an important foundation of the Deterministic AI Runtime Platform.

It connects runtime execution to operational control.

It allows the platform to expose:

- run control;
- queue visibility;
- runtime instance visibility;
- worker visibility;
- execution control;
- replay;
- audit;
- decision ledger;
- diagnostics;
- observability;
- policy decisions;
- retention, eviction, and compaction activity.

The long-term goal is to make MCP the structured control surface for deterministic AI workflow execution.

A production AI runtime should not only run workflows.

It should expose the tools needed to operate them.
