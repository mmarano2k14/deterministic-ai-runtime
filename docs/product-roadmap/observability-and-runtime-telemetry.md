# Observability and Runtime Telemetry

## Deterministic AI Runtime Platform

This document describes the observability and runtime telemetry direction of the Deterministic AI Runtime Platform.

Observability is not an external afterthought in this architecture.

The runtime is designed so that executions, runs, queues, runtime instances, workers, policy decisions, replay reports, decision ledger events, retention operations, eviction decisions, compaction decisions, provider dispatch, transport behavior, and MCP control operations can become visible through structured telemetry.

The key idea is:

> A production AI runtime must be observable from the inside of the execution lifecycle, not only through external logs after something fails.

The platform should make AI workflow execution understandable in real time and after execution.

---

## Purpose

The purpose of observability and runtime telemetry is to make the platform operable.

A deterministic AI runtime must be able to answer questions such as:

- What is running?
- What is queued?
- What is blocked?
- What is retrying?
- What failed?
- Which runtime instance accepted the work?
- Which worker executed a step?
- Which policy allowed or denied an operation?
- Which provider or model was used?
- Which tool was called?
- Which replay report exists?
- Which ledger events explain the execution?
- Was hot state evicted?
- Was history compacted?
- Was a snapshot created?
- Is the shared queue under pressure?
- Are workers saturated?
- Is a runtime instance unhealthy?
- Is dispatch failing?
- Is a transport provider slow?
- Is a tenant consuming too much capacity?

Observability should make those answers accessible.

---

## Current Foundation

The platform already has important observability foundations.

These include:

- structured execution identifiers;
- ExecutionId;
- RunId;
- StepId / StepKey direction;
- RuntimeInstanceId;
- WorkerId;
- CorrelationId;
- decision ledger foundation;
- replay and audit foundation;
- policy decision events;
- queue and dispatch direction;
- runtime instance registry direction;
- worker identity;
- shared queue direction;
- local queue direction;
- provider-based runtime hosting;
- MCP control-plane foundation;
- retention, eviction, and compaction foundation;
- diagnostics direction;
- logs, metrics, and traces direction;
- Grafana / Kibana / OpenSearch direction.

The roadmap is not to invent observability from zero.

The roadmap is to harden, structure, export, visualize, and productize the observability foundation.

---

## Observability Principle

The observability model should follow this principle:

```text
Every important runtime decision should be visible through state, ledger, logs, metrics, traces, replay, diagnostics, or dashboard.
```

A production AI runtime should avoid hidden behavior.

If a run is queued, the system should show it.

If a policy denies execution, the system should show it.

If a worker claims a step, the system should show it.

If a retry is scheduled, the system should show it.

If hot state is evicted, the system should show it.

If compaction is skipped because execution is still active, the system should show it.

---

# 1. Observability Layers

Runtime observability can be organized into several layers.

| Layer | Purpose |
|---|---|
| Runtime State | Shows execution, run, step, queue, worker, and runtime instance state. |
| Decision Ledger | Records meaningful runtime decisions. |
| Logs | Provide structured operational messages. |
| Metrics | Provide counters, gauges, timings, rates, and capacity signals. |
| Traces | Connect distributed operations across runtime, providers, queues, and workers. |
| Replay Reports | Explain execution after the fact. |
| Diagnostics | Summarize runtime health and investigation results. |
| Dashboard | Makes observability human-readable. |
| External Exports | Sends telemetry to Grafana, Kibana, OpenSearch, SIEM, or cloud observability platforms. |

The platform should connect these layers through stable correlation identifiers.

---

# 2. Correlation Model

Correlation is the foundation of useful observability.

Important correlation identifiers include:

- ExecutionId;
- RunId;
- StepId;
- StepKey;
- RuntimeInstanceId;
- WorkerId;
- ClaimToken;
- CorrelationId;
- TenantId direction;
- ProjectId direction;
- PipelineId direction;
- Provider direction;
- Model direction;
- Operation direction.

These identifiers should appear consistently across:

- runtime state;
- Decision Ledger events;
- replay reports;
- logs;
- metrics;
- traces;
- MCP tool responses;
- dashboard views;
- diagnostics.

Without correlation, distributed runtime behavior becomes difficult to understand.

With correlation, an execution can be investigated from submission to finalization.

---

# 3. Execution Telemetry

Execution telemetry should expose workflow execution behavior.

Signals can include:

- executions started;
- executions completed;
- executions failed;
- executions cancelled;
- executions paused;
- executions resumed;
- executions finalized;
- execution duration;
- finalization reason;
- active executions;
- waiting executions;
- waiting-for-retry executions;
- waiting-for-input executions;
- execution failures by reason;
- execution status transitions.

Execution telemetry helps users understand system throughput and reliability.

---

# 4. Run and Queue Telemetry

Run and queue telemetry should expose control-plane workload.

Signals can include:

- runs submitted;
- runs accepted;
- runs rejected;
- runs queued;
- runs assigned;
- runs dispatched;
- runs completed;
- runs failed;
- runs cancelled;
- shared queue depth;
- local queue depth;
- queue wait time;
- dispatch latency;
- dispatch failure count;
- queue pressure;
- queue saturation;
- oldest queued run;
- admission denied count;
- admission throttled count.

This is important for distributed execution and managed hosting.

---

# 5. Runtime Instance Telemetry

Runtime instance telemetry should expose execution capacity.

Signals can include:

- runtime instance registered;
- runtime instance heartbeat;
- runtime instance healthy/unhealthy;
- heartbeat freshness;
- worker count;
- active workers;
- available workers;
- max concurrent runs;
- current assigned runs;
- local queue depth;
- local queue capacity;
- runtime instance saturation;
- instance dispatch failures;
- instance unavailable count;
- instance restart direction.

This is essential for Kubernetes-style execution and managed hosting.

---

# 6. Worker Telemetry

Worker telemetry should expose step execution capacity.

Signals can include:

- worker started;
- worker stopped direction;
- worker active;
- worker idle;
- worker busy;
- worker claimed step;
- worker completed step;
- worker failed step;
- worker cancelled step;
- worker execution duration;
- worker error count;
- worker utilization;
- worker saturation;
- worker collision prevention direction.

Worker telemetry connects actual execution to runtime capacity.

---

# 7. Step Telemetry

Step telemetry should expose the smallest execution unit.

Signals can include:

- step ready;
- step selected;
- step claimed;
- step started;
- step completed;
- step failed;
- step cancelled;
- step skipped;
- step waiting for retry;
- step waiting for input;
- step retry scheduled;
- step duration;
- step error reason;
- step policy result;
- step provider/model/tool context.

Step telemetry is critical because most AI workflow failures happen at step level.

---

# 8. Policy Telemetry

Policy telemetry should expose runtime governance.

Signals can include:

- policy evaluations;
- policy allowed count;
- policy denied count;
- policy failed count;
- policy throttled count;
- policy delayed count;
- policy blocked count;
- policy requires approval count;
- policy evaluation latency;
- policy decisions by tenant;
- policy decisions by pipeline;
- policy decisions by provider/model/tool;
- policy decisions by operation.

Policy telemetry is important because the policy engine is a core foundation.

It makes governance measurable.

---

# 9. Provider and Transport Telemetry

Provider and transport telemetry should expose communication between the control plane and runtime instances.

Signals can include:

- provider dispatch attempts;
- provider dispatch success;
- provider dispatch failure;
- provider latency;
- transport latency;
- HTTP runtime provider errors;
- runtime-instance-only dispatch status;
- provider timeout count;
- provider retry direction;
- provider unavailable count;
- cancellation propagation result;
- diagnostics request latency.

This is critical for distributed execution.

It helps show whether a failure is caused by the runtime, queue, provider, transport, or runtime instance.

---

# 10. Replay and Audit Telemetry

Replay and audit telemetry should expose inspection activity.

Signals can include:

- replay requested;
- replay started;
- replay completed;
- replay failed;
- audit-only replay count;
- replay duration;
- replay issue count;
- replay warnings;
- replay critical issues;
- replay using compacted history;
- replay using snapshot;
- replay using archive reference;
- audit report generated direction.

Replay telemetry makes audit activity visible.

---

# 11. Decision Ledger Telemetry

Decision Ledger telemetry should expose structured runtime decision volume and health.

Signals can include:

- ledger events written;
- ledger write failures;
- ledger write latency;
- ledger event volume by type;
- ledger event volume by execution;
- policy event count;
- replay event count;
- retention event count;
- queue event count;
- claim event count;
- finalization event count;
- ledger provider health;
- ledger strict/best-effort write mode direction.

The Decision Ledger is not only audit history.

It is also an observability source.

---

# 12. Retention, Eviction, Compaction, and Snapshot Telemetry

Lifecycle telemetry should expose execution data management.

Signals can include:

- retention policy evaluated;
- snapshot required;
- snapshot created;
- snapshot failed;
- hot-state eviction count;
- stale claim cleanup count;
- eviction skipped count;
- compaction started;
- compaction completed;
- compaction skipped;
- archive created;
- archive failed;
- retained data size direction;
- hot-state size direction;
- compacted history count;
- replay availability after compaction;
- lifecycle operation latency.

This is important because lifecycle operations affect replay, audit, memory, storage, and production stability.

---

# 13. MCP Telemetry

MCP telemetry should expose control-plane usage.

Signals can include:

- MCP tool call count;
- MCP tool latency;
- MCP tool failure count;
- MCP replay requests;
- MCP cancel requests;
- MCP pause/resume requests;
- MCP queue inspections;
- MCP runtime instance inspections;
- MCP ledger inspections;
- MCP diagnostics calls;
- denied MCP operations;
- MCP correlation IDs.

MCP is an operational control surface, so MCP activity must be observable.

---

# 14. Diagnostics

Diagnostics should summarize runtime health.

Diagnostics can include:

- execution diagnostics;
- replay diagnostics;
- queue diagnostics;
- runtime instance diagnostics;
- worker diagnostics;
- provider diagnostics;
- transport diagnostics;
- policy diagnostics;
- ledger diagnostics;
- retention diagnostics;
- observability diagnostics.

Diagnostics are useful because users may not know which low-level signal to inspect first.

A diagnostic summary should guide investigation.

---

# 15. Dashboard Integration

The Enterprise Dashboard should use runtime telemetry.

Dashboard views can include:

- execution dashboard;
- run dashboard;
- queue dashboard;
- runtime instance dashboard;
- worker dashboard;
- replay/audit dashboard;
- decision ledger dashboard;
- policy dashboard;
- provider/transport dashboard;
- retention/eviction/compaction dashboard;
- diagnostics dashboard.

The dashboard should make telemetry understandable.

---

# 16. External Export Direction

The platform should support external observability export direction.

Potential targets include:

- Grafana;
- Kibana;
- OpenSearch;
- SIEM-style systems;
- cloud observability platforms;
- log aggregation systems;
- metrics systems;
- tracing systems.

External export should preserve correlation identifiers.

This allows a user to move from:

```text
Dashboard execution view
  -> Decision Ledger event
      -> Trace
          -> Logs
              -> Runtime instance / worker / provider signal
```

This is important for production operations.

---

# 17. Kubernetes Demo Direction

Observability is critical for Kubernetes-style demos.

A Kubernetes demo should expose:

- MCP control plane activity;
- shared queue depth;
- runtime instance pods;
- worker capacity;
- dispatch decisions;
- provider communication;
- execution progress;
- replay reports;
- decision ledger events;
- logs;
- metrics;
- traces;
- runtime health;
- queue pressure.

The goal is to visually prove distributed AI execution.

---

# 18. Managed Hosting Direction

Managed hosting requires observability.

A hosted runtime should expose:

- customer workload;
- runtime capacity;
- worker utilization;
- queue pressure;
- execution volume;
- replay volume;
- ledger volume;
- retained data volume;
- policy decisions;
- throttling;
- failures;
- support diagnostics;
- SLA direction;
- usage metering direction.

Observability is part of the commercial foundation.

---

# 19. Security and Access Control Direction

Observability data can be sensitive.

It may expose:

- prompts direction;
- model outputs direction;
- tool metadata;
- user context;
- RBAC context;
- tenant context;
- policy decisions;
- provider/model usage;
- execution metadata;
- replay details;
- ledger payloads.

Future hardening should include:

- tenant-aware observability filtering;
- access-controlled dashboard views;
- access-controlled MCP diagnostics;
- redacted logs;
- redacted replay reports;
- encrypted ledger payload direction;
- encrypted retention archive direction;
- audit of sensitive observability access.

Observability must not become a data leak.

---

# Current Foundation Summary

| Area | Status |
|---|---|
| Execution identifiers | Foundation exists |
| Run identifiers | Foundation exists |
| Runtime instance identity | Foundation exists |
| Worker identity | Foundation exists |
| Correlation identifiers | Foundation exists |
| Decision Ledger observability | Foundation exists |
| Replay/audit observability | Foundation exists |
| Policy decision observability | Foundation exists |
| Queue and dispatch telemetry direction | Foundation exists / active direction |
| Runtime instance telemetry direction | Foundation exists / active direction |
| Worker telemetry direction | Foundation exists |
| Provider/transport telemetry direction | Foundation exists / active direction |
| Retention/eviction/compaction telemetry direction | Foundation exists |
| MCP diagnostics direction | Foundation exists |
| Logs, metrics, traces direction | Foundation exists |
| Dashboard telemetry views | Productization target |
| Grafana/Kibana/OpenSearch export | Productization target |
| Tenant-aware observability | Planned hardening direction |
| Access-controlled telemetry | Planned hardening direction |

---

# Productization Roadmap

## Milestone 1 — Standardize Telemetry Names

Improve:

- metric names;
- log event names;
- trace span names;
- ledger event names;
- provider event names;
- lifecycle event names.

## Milestone 2 — Strengthen Correlation

Ensure consistent propagation of:

- ExecutionId;
- RunId;
- StepId;
- RuntimeInstanceId;
- WorkerId;
- CorrelationId;
- provider/transport context;
- tenant/project/pipeline direction.

## Milestone 3 — Expose Diagnostics

Improve:

- execution diagnostics;
- queue diagnostics;
- runtime instance diagnostics;
- provider diagnostics;
- replay diagnostics;
- policy diagnostics;
- retention diagnostics.

## Milestone 4 — Add Dashboard Views

Add views for:

- execution telemetry;
- queue pressure;
- runtime instance health;
- worker utilization;
- provider/transport behavior;
- replay/audit activity;
- ledger events;
- policy decisions;
- retention lifecycle activity.

## Milestone 5 — Prepare External Exports

Prepare:

- structured logs for OpenSearch/Kibana;
- metrics for Grafana direction;
- traces direction;
- export configuration;
- dashboards examples;
- Kubernetes demo observability.

---

# Planned Improvements

The observability and telemetry layer should continue improving through:

- structured event taxonomy;
- consistent correlation;
- better logs;
- metrics exporter direction;
- tracing exporter direction;
- dashboard views;
- MCP diagnostics;
- provider/transport telemetry;
- retention lifecycle telemetry;
- replay and ledger telemetry;
- Kubernetes demo dashboards;
- tenant-aware observability filtering;
- access-controlled telemetry.

These are productization and hardening steps.

They build on the existing runtime observability foundation.

---

# Final Statement

Observability and runtime telemetry are central to the Deterministic AI Runtime Platform.

The runtime should not be a black box.

It should expose what is happening across:

- executions;
- runs;
- steps;
- queues;
- workers;
- runtime instances;
- providers;
- transports;
- policies;
- replay;
- ledger;
- retention;
- eviction;
- compaction;
- MCP operations.

The long-term goal is to make distributed AI execution visible enough for developers, operators, enterprise teams, managed hosting, and audit-sensitive environments.

A production AI runtime should not only execute workflows.

It should explain what it is doing while it is doing it.
