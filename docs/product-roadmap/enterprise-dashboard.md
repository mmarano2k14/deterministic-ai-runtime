# Enterprise Dashboard

## Deterministic AI Runtime Platform

This document describes the Enterprise Dashboard direction for the Deterministic AI Runtime Platform.

The dashboard is the visual operational layer of the platform. It is not the runtime itself, but it is the product layer that makes the runtime understandable, inspectable, controllable, and demonstrable.

The project already has several runtime foundations that can power an enterprise dashboard:

- deterministic execution state;
- run and queue lifecycle;
- runtime instance identity;
- worker identity;
- replay and audit foundation;
- decision ledger foundation;
- policy-driven decision events;
- MCP control-plane foundation;
- observability direction;
- retention, eviction, and compaction foundation;
- distributed runtime and shared queue direction.

The dashboard roadmap is therefore not about inventing runtime visibility from zero.

It is about exposing, organizing, visualizing, and productizing the runtime evidence that the platform already produces or is already architected to produce.

---

## Purpose

The purpose of the Enterprise Dashboard is to turn the runtime into an operational product.

A deterministic AI runtime can be powerful internally, but enterprise users need to see and operate it.

The dashboard should help users answer questions such as:

- What executions are running?
- What runs are queued?
- Which runtime instance accepted a run?
- Which worker executed a step?
- Which step failed?
- Which policy allowed or denied an operation?
- Which retry was scheduled?
- Which execution was cancelled?
- Which replay report exists?
- What does the decision ledger say?
- Is the shared queue under pressure?
- Are runtime instances healthy?
- Are workers saturated?
- Was hot state evicted safely?
- Was execution history compacted?
- Can this execution be audited?

The dashboard should make distributed AI execution visible.

---

## Product Positioning

The dashboard is not only an admin UI.

It is the enterprise control and observability layer for production AI execution.

It should help transform the platform from:

```text
Runtime library
```

into:

```text
Operational AI execution platform
```

The dashboard should expose the runtime as a product that users can understand without reading code, tests, logs, or raw database records.

---

## Current Foundation

The platform already includes the foundations needed to build dashboard views.

Existing foundations include:

- execution identity;
- run identity;
- runtime instance identity;
- worker identity;
- execution state;
- step state;
- queue and run lifecycle;
- shared queue direction;
- runtime instance registry direction;
- replay and audit foundation;
- replay reports direction;
- decision ledger foundation;
- policy decision events;
- retry and cancellation state;
- retention, eviction, and compaction decisions;
- observability direction;
- MCP control-plane direction;
- correlation identifiers.

The dashboard should be built on top of those foundations.

It should not create a separate operational model.

---

## Dashboard Design Principle

The dashboard should follow one principle:

> Show the execution as the runtime understands it.

That means the dashboard should not be a decorative UI layer disconnected from the engine.

It should visualize the same concepts used by the runtime:

- ExecutionId;
- RunId;
- StepId;
- StepKey;
- RuntimeInstanceId;
- WorkerId;
- ClaimToken;
- CorrelationId;
- tenant/project/pipeline direction;
- policy decisions;
- ledger events;
- replay reports;
- retention decisions;
- queue state;
- runtime instance health.

This keeps the UI aligned with the architecture.

---

# Dashboard Modules

The enterprise dashboard can be organized into several modules.

| Module | Purpose |
|---|---|
| Execution Dashboard | Inspect executions, status, timeline, steps, failures, retries, and finalization. |
| Run Dashboard | Inspect submitted runs, queue lifecycle, assignment, dispatch, and execution mapping. |
| Queue Dashboard | Inspect shared queue, local queues, pressure, blocked runs, dispatch state, and queue health. |
| Runtime Instance Dashboard | Inspect runtime instances, heartbeat, capacity, workers, assigned runs, and health. |
| Worker Dashboard | Inspect workers, activity, utilization, errors, and current work direction. |
| Replay and Audit Dashboard | Inspect replay reports, replay issues, validation, timeline, and audit summaries. |
| Decision Ledger Dashboard | Inspect structured decisions, policy outcomes, retries, claims, queue events, finalization, and retention decisions. |
| Policy and Governance Dashboard | Inspect policy decisions, allowed/denied operations, throttling, concurrency, and context. |
| Observability Dashboard | Inspect logs, metrics, traces, runtime health, queue pressure, and distributed execution signals. |
| Retention / Eviction / Compaction Dashboard | Inspect execution data lifecycle decisions, hot-state cleanup, archives, and compaction status. |
| MCP Control Dashboard | Trigger or inspect MCP-based operations such as replay, pause, resume, cancel, and diagnostics. |
| Diagnostics Dashboard | Summarize execution, queue, runtime, replay, policy, ledger, and observability issues. |

---

# 1. Execution Dashboard

The Execution Dashboard is the primary view for workflow execution.

It should show:

- execution list;
- execution status;
- execution start time;
- execution end time;
- duration;
- current lifecycle state;
- final status;
- finalization reason;
- step count;
- completed steps;
- failed steps;
- skipped steps;
- cancelled steps;
- waiting-for-retry steps;
- waiting-for-input steps;
- linked RunId;
- linked CorrelationId.

The goal is to make an execution understandable at a glance.

---

## Execution Details

An execution detail view should show:

- execution metadata;
- workflow/pipeline reference;
- status summary;
- step list;
- step timeline;
- retry history;
- cancellation history;
- replay availability;
- decision ledger link;
- trace correlation;
- retention/compaction status;
- diagnostics.

This view should be the main entry point for debugging an AI workflow.

---

## Step View

The step view should show:

- StepId;
- StepKey;
- step name;
- current status;
- dependency status;
- claim status;
- worker identity;
- runtime instance identity;
- retry count;
- error summary;
- input/output metadata direction;
- policy decisions;
- ledger events;
- trace links;
- replay evidence.

Step-level visibility is essential because production AI failures usually happen at step level.

---

# 2. Run Dashboard

The Run Dashboard should expose the control-plane lifecycle of submitted work.

A run represents submitted work before or around durable execution.

The run dashboard should show:

- RunId;
- submitted time;
- current run status;
- queued state;
- assigned runtime instance;
- dispatch status;
- linked ExecutionId;
- cancellation status;
- queue position direction;
- error or rejection reason;
- decision ledger links.

This is important because a run may be queued, assigned, cancelled, or delayed before execution is visible.

---

## Run-to-Execution Mapping

The dashboard should make the difference between `RunId` and `ExecutionId` clear.

```text
RunId        = submitted/control-plane work identity
ExecutionId  = durable workflow execution identity
```

This mapping is important for:

- shared queue operations;
- cancellation before execution starts;
- runtime instance assignment;
- distributed execution;
- support investigations;
- replay and audit.

---

# 3. Queue Dashboard

The Queue Dashboard should show work waiting to be executed.

It should expose both shared queue and local queue concepts.

## Shared Queue View

The shared queue view should show:

- queued run count;
- assigned run count;
- running run count;
- failed dispatch count;
- cancelled queued runs;
- queue pressure;
- queue pause/resume state direction;
- oldest queued run;
- average queue wait direction;
- dispatch attempts;
- dispatch failures;
- runtime instance availability.

The shared queue sits above local queues.

It helps distribute submitted runs across runtime instances.

---

## Local Queue View

The local queue view should show:

- runtime instance identity;
- local queue depth;
- local queue capacity;
- assigned runs;
- active workers;
- blocked work;
- completed local dispatch direction;
- local pressure.

This is useful because even if the shared queue is healthy, a local runtime instance may be overloaded.

---

## Queue Diagnostics

Queue diagnostics should answer:

- Is work waiting?
- Why is work waiting?
- Are runtime instances available?
- Are workers available?
- Is dispatch failing?
- Is queue pressure increasing?
- Is one runtime instance overloaded?
- Are queue decisions recorded in the ledger?

This makes the queue understandable instead of invisible background infrastructure.

---

# 4. Runtime Instance Dashboard

The Runtime Instance Dashboard should show the distributed execution environment.

A runtime instance can map to:

- a local process;
- a background host;
- a runtime service;
- a container;
- a Kubernetes pod;
- a managed execution unit.

The dashboard should show:

- RuntimeInstanceId;
- status;
- heartbeat freshness;
- worker count;
- active workers;
- available worker capacity;
- max concurrent runs;
- current assigned runs;
- local queue depth;
- local queue capacity;
- health status;
- last activity;
- error summary;
- linked logs/traces direction.

---

## Runtime Instance Health

Runtime instance health should include:

- healthy;
- warning;
- unhealthy;
- offline direction;
- heartbeat stale;
- capacity exhausted;
- queue overloaded;
- worker failures detected;
- dispatch unavailable.

This is important for distributed runtime operations and Kubernetes-style demos.

---

## Runtime Instance Capacity

Capacity views should show:

- configured workers;
- active workers;
- available workers;
- run slots;
- local queue capacity;
- current assigned runs;
- recent completions;
- recent failures;
- utilization trend direction.

This helps the platform demonstrate managed hosting by runtime instance and worker capacity.

---

# 5. Worker Dashboard

The Worker Dashboard should show worker-level activity.

Workers are the execution slots that process workflow steps.

Worker views can show:

- WorkerId;
- RuntimeInstanceId;
- current status;
- current step direction;
- recent completed steps;
- recent failed steps;
- retry activity;
- cancellation activity;
- utilization;
- error count;
- average execution time direction;
- correlation links.

Worker visibility helps explain how work is processed across a distributed runtime.

---

# 6. Replay and Audit Dashboard

The Replay and Audit Dashboard should expose replay reports and audit evidence.

It should show:

- replay reports;
- replay status;
- replay mode;
- audit-only replay status;
- deterministic validation result;
- replay issues;
- execution timeline;
- step timeline;
- retry history;
- cancellation history;
- policy decision summary;
- retention/compaction status;
- ledger correlation;
- trace correlation;
- audit report export direction.

Replay and audit are major product differentiators.

The dashboard should make them visible and useful.

---

## Replay Issue View

Replay issue views should show:

- missing execution state;
- missing step state;
- missing ledger event;
- inconsistent final status;
- missing finalization event;
- missing worker identity;
- stale claim evidence;
- compacted history warning;
- evicted hot-state warning;
- retained-history reference missing;
- replay validation warnings.

Each issue should have a severity:

- info;
- warning;
- error;
- critical.

This helps users understand replay quality.

---

# 7. Decision Ledger Dashboard

The Decision Ledger Dashboard should expose structured runtime decisions.

It should allow users to inspect:

- execution lifecycle decisions;
- run lifecycle decisions;
- queue decisions;
- dispatch decisions;
- claim decisions;
- worker decisions;
- policy decisions;
- retry decisions;
- cancellation decisions;
- replay decisions;
- finalization decisions;
- retention decisions;
- eviction decisions;
- compaction decisions;
- archive decisions.

The dashboard should make the ledger searchable and filterable.

---

## Ledger Filters

Useful filters include:

- ExecutionId;
- RunId;
- StepId;
- StepKey;
- RuntimeInstanceId;
- WorkerId;
- CorrelationId;
- event type;
- event group;
- severity;
- decision result;
- policy result;
- time range;
- tenant/project/pipeline direction.

The ledger view should be one of the main investigation tools.

---

## Ledger Timeline

A ledger timeline should present decision events in sequence.

This can show:

- run submitted;
- run queued;
- runtime instance selected;
- execution created;
- step claimed;
- policy allowed;
- step completed;
- retry scheduled;
- cancellation requested;
- replay started;
- retention decision made;
- execution finalized.

This turns runtime decisions into a readable story.

---

# 8. Policy and Governance Dashboard

The Policy and Governance Dashboard should expose policy-driven runtime behavior.

It should show:

- policy evaluations;
- allowed decisions;
- denied decisions;
- throttled direction;
- delayed direction;
- failed policy checks;
- provider/model access decisions;
- tool access decisions;
- concurrency decisions;
- tenant quota direction;
- operation limits direction;
- RBAC context direction.

This is important because the platform is not only deterministic. It is also configuration-driven, context-driven, policy-driven, and provider-driven.

A policy dashboard helps explain runtime governance.

---

## Policy Decision Detail

A policy decision detail view should include:

- policy name or type;
- decision result;
- reason;
- context;
- tenant/project/pipeline direction;
- user/RBAC context direction;
- provider/model/operation context;
- affected execution/run/step;
- linked ledger event;
- correlation ID.

This makes governance auditable.

---

# 9. Retention, Eviction, and Compaction Dashboard

The dashboard should expose execution data lifecycle decisions.

Retention, eviction, and compaction are part of the runtime foundation.

A dedicated view can show:

- retention policies direction;
- retained execution records;
- hot-state eviction events;
- stale claim cleanup;
- compaction events;
- archived payload references;
- compacted history warnings;
- replay after compaction status;
- retention errors;
- retention skipped decisions;
- encrypted retention archive direction.

This is important because cleanup decisions can affect replay, audit, cost, and compliance-support direction.

Retention should not be invisible.

It should be visible, auditable, and explainable.

---

# 10. Observability Dashboard

The Observability Dashboard should expose runtime health and operational signals.

It should include:

- logs direction;
- metrics direction;
- traces direction;
- execution throughput;
- failure rate;
- retry rate;
- cancellation rate;
- replay activity;
- queue pressure;
- worker utilization;
- runtime instance health;
- ledger event volume;
- policy decision volume;
- retention activity;
- storage pressure direction;
- correlation summaries.

Observability helps teams operate the runtime in real time.

---

## External Observability Exports

The platform should support export direction toward:

- Grafana;
- Kibana;
- OpenSearch;
- SIEM-style systems;
- cloud observability platforms.

The dashboard should not replace external observability systems.

It should provide runtime-native visibility and integrate with broader observability stacks.

---

# 11. MCP Control Dashboard

The MCP Control Dashboard should align with the MCP Control Interface.

The dashboard can expose buttons or workflows for:

- submit run;
- inspect run;
- inspect execution;
- pause execution;
- resume execution;
- cancel execution;
- replay execution;
- inspect queue;
- inspect runtime instance;
- inspect ledger;
- run diagnostics.

The dashboard and MCP tools should use the same runtime concepts.

This avoids building two disconnected control models.

---

# 12. Diagnostics Dashboard

Diagnostics should provide high-level summaries.

Diagnostics views can include:

- execution diagnostics;
- replay diagnostics;
- queue diagnostics;
- runtime instance diagnostics;
- worker diagnostics;
- policy diagnostics;
- ledger diagnostics;
- retention diagnostics;
- observability diagnostics;
- distributed runtime diagnostics.

Diagnostics are important because users may not always know where to look first.

A good diagnostic view can guide users from summary to detailed investigation.

---

# Dashboard User Journeys

The dashboard should support practical investigation workflows.

## Journey 1 — Debug a Failed Execution

A user should be able to:

1. Open the execution.
2. See final status.
3. Identify failed step.
4. Inspect retry history.
5. Inspect worker and runtime instance.
6. Inspect decision ledger.
7. Inspect replay report.
8. Inspect traces/logs.
9. Understand why the execution failed.

---

## Journey 2 — Investigate Queue Pressure

A user should be able to:

1. Open queue dashboard.
2. See shared queue length.
3. See runtime instance availability.
4. See local queue pressure.
5. Identify overloaded instance.
6. Inspect worker capacity.
7. Inspect dispatch decisions.
8. Inspect throttling or policy decisions.
9. Decide whether more runtime capacity is needed.

---

## Journey 3 — Audit a Sensitive Execution

A user should be able to:

1. Open execution detail.
2. Inspect decision ledger.
3. Inspect policy decisions.
4. Inspect replay report.
5. Inspect retention status.
6. Inspect audit summary.
7. Export audit report direction.
8. Verify execution evidence.

---

## Journey 4 — Inspect Distributed Runtime Health

A user should be able to:

1. Open runtime instance dashboard.
2. See all runtime instances.
3. Check heartbeat freshness.
4. Check worker utilization.
5. Check assigned runs.
6. Check local queues.
7. Check failures and retries.
8. Check observability signals.
9. Identify unhealthy or overloaded instances.

---

## Journey 5 — Review Retention and Compaction

A user should be able to:

1. Open retention dashboard.
2. See retained executions.
3. See hot-state eviction activity.
4. See compacted history.
5. Check archive references.
6. Inspect retention decision ledger events.
7. Confirm replay remains possible after compaction.
8. Identify retention errors or skipped decisions.

---

# Dashboard Data Sources

The dashboard can be powered by existing and planned runtime data sources.

| Data Source | Dashboard Use |
|---|---|
| Execution state | Execution dashboard, step view, replay view. |
| Run records | Run dashboard, queue dashboard, assignment tracking. |
| Shared queue state | Queue dashboard, dispatch diagnostics. |
| Runtime instance registry | Runtime instance dashboard, capacity view. |
| Worker identity/activity | Worker dashboard, execution detail, metrics. |
| Decision ledger | Ledger dashboard, replay, audit, policy, retention views. |
| Replay reports | Replay dashboard, audit dashboard. |
| Observability data | Logs, metrics, traces, health views. |
| Retention/compaction records | Retention dashboard, replay transparency. |
| MCP tool responses | Control dashboard and operational workflows. |

The dashboard should not duplicate state unnecessarily.

It should read from runtime-authoritative sources.

---

# Dashboard and Single-Developer Roadmap

Because the project is currently built and maintained by one developer, the dashboard should be built in stages.

The first goal should not be a perfect enterprise UI.

The first goal should be a useful operational console that proves the runtime concepts visually.

## Suggested Stages

### Stage 1 — Read-Only Runtime Visibility

- execution list;
- run list;
- queue status;
- runtime instance list;
- worker status;
- basic ledger list;
- basic replay report view.

### Stage 2 — Investigation Views

- execution timeline;
- step details;
- retry/cancellation history;
- ledger timeline;
- replay issues;
- runtime instance diagnostics.

### Stage 3 — Control Actions

- pause;
- resume;
- cancel;
- replay;
- diagnostics trigger;
- queue inspection actions.

### Stage 4 — Observability and Retention Views

- metrics panels;
- trace links;
- queue pressure;
- worker utilization;
- retention/eviction/compaction activity.

### Stage 5 — Enterprise Hardening

- tenant/project filters;
- RBAC direction;
- redacted sensitive payloads;
- access-controlled views;
- audit export direction.

This staged approach keeps the roadmap realistic.

---

# Dashboard Security Direction

The dashboard can expose sensitive runtime data.

Future hardening should include:

- authentication direction;
- authorization direction;
- tenant-aware visibility;
- RBAC direction;
- redacted sensitive payloads;
- secure replay access;
- secure ledger access;
- audit of dashboard actions;
- audit of replay access;
- access-controlled retention views.

The dashboard should not expose sensitive prompts, model outputs, tool data, or policy context without controls.

---

# Dashboard Productization Roadmap

## Step 1 — Runtime Visibility Foundation

Build read-only views for:

- executions;
- runs;
- queues;
- runtime instances;
- workers;
- decision ledger;
- replay reports.

## Step 2 — Investigation and Diagnostics

Add:

- execution timeline;
- step details;
- retry history;
- cancellation history;
- replay issues;
- ledger timeline;
- queue diagnostics;
- runtime instance diagnostics.

## Step 3 — MCP-Controlled Operations

Add controlled actions:

- pause execution;
- resume execution;
- cancel execution;
- replay execution;
- inspect ledger;
- inspect diagnostics.

## Step 4 — Observability Integration

Add:

- logs direction;
- metrics direction;
- traces direction;
- queue pressure;
- worker utilization;
- runtime instance health;
- external export links direction.

## Step 5 — Retention and Audit Views

Add:

- retention activity;
- hot-state eviction;
- compaction activity;
- retained-history references;
- audit report export direction;
- replay after compaction visibility.

## Step 6 — Enterprise and Multi-Tenant Readiness

Add:

- tenant/project filters;
- RBAC direction;
- access-controlled views;
- redaction direction;
- compliance profile direction;
- dedicated deployment views direction.

---

# Current Foundation Summary

| Area | Status |
|---|---|
| Execution dashboard data | Foundation exists |
| Run dashboard data | Foundation exists |
| Queue dashboard data | Foundation exists / active direction |
| Runtime instance dashboard data | Foundation exists / active direction |
| Worker dashboard data | Foundation exists |
| Replay dashboard data | Foundation exists |
| Decision ledger dashboard data | Foundation exists |
| Policy decision dashboard data | Foundation exists |
| Observability dashboard data | Foundation exists / active direction |
| Retention/eviction/compaction dashboard data | Foundation exists / active direction |
| MCP control dashboard alignment | Foundation exists |
| Diagnostics dashboard data | Foundation exists / active direction |
| Dashboard UI implementation | Productization target |
| Dashboard control actions | Productization target |
| Dashboard security hardening | Planned hardening direction |
| Tenant-aware dashboard views | Planned hardening direction |

---

# Planned Improvements

The Enterprise Dashboard should continue through staged productization:

- read-only execution dashboard;
- run dashboard;
- queue dashboard;
- runtime instance dashboard;
- worker dashboard;
- replay/audit dashboard;
- decision ledger dashboard;
- policy/governance dashboard;
- retention/eviction/compaction dashboard;
- observability dashboard;
- MCP control actions;
- diagnostics views;
- tenant/project filters;
- redaction direction;
- access-control direction;
- audit export direction.

These are productization steps.

They expose existing runtime foundations through a user interface that makes the platform easier to understand, operate, demonstrate, and trust.

---

# Final Statement

The Enterprise Dashboard is the visual operational layer of the Deterministic AI Runtime Platform.

It should expose the runtime as a product.

It connects:

- executions;
- runs;
- queues;
- workers;
- runtime instances;
- replay;
- audit;
- decision ledger;
- policy decisions;
- observability;
- diagnostics;
- retention, eviction, and compaction;
- MCP control operations.

The long-term goal is to make distributed AI execution visible, understandable, controllable, replayable, auditable, and operationally trustworthy.

A production AI runtime should not only run workflows.

It should show what it is doing.
