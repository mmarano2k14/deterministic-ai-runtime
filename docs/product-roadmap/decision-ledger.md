# Decision Ledger

## Deterministic AI Runtime Platform

This document describes the Decision Ledger of the Deterministic AI Runtime Platform.

The Decision Ledger is one of the core foundations of the platform. It exists because production AI execution needs more than logs, traces, and final outputs.

A production AI workflow should be able to explain:

- what happened;
- why it happened;
- which decision was made;
- which context was used;
- which policy was evaluated;
- which worker or runtime instance was involved;
- which run and execution were affected;
- whether retry, cancellation, replay, retention, eviction, or compaction decisions occurred.

The Decision Ledger is the structured runtime memory that makes those explanations possible.

---

## Purpose

The purpose of the Decision Ledger is to record meaningful runtime decisions in a structured, queryable, auditable way.

Logs usually answer:

> What message was written by the system?

The Decision Ledger should answer:

> What decision did the runtime make, under which context, and why does it matter?

This is a major distinction.

The platform is designed to operate AI workflows as production workloads. For that, important runtime decisions must not disappear inside logs or orchestration code.

They must be recorded as first-class events.

---

## Current Foundation

The project already includes a Decision Ledger foundation.

The current foundation covers the idea of structured runtime decision history across several runtime areas:

- execution lifecycle decisions;
- run lifecycle decisions;
- queue decisions;
- dispatch decisions;
- step claim decisions;
- worker decisions;
- policy decisions;
- retry decisions;
- cancellation decisions;
- replay decisions;
- finalization decisions;
- retention decisions;
- eviction decisions;
- compaction decisions;
- archive decisions;
- observability correlation direction.

The roadmap is not to invent a ledger later.

The roadmap is to harden, expose, productize, search, visualize, export, secure, and correlate the existing ledger foundation.

---

## Why a Decision Ledger Is Needed

AI workflows are different from traditional deterministic business processes because they often involve:

- model calls;
- tool calls;
- external providers;
- retrieval;
- dynamic context;
- policies;
- human-in-the-loop decisions;
- retries;
- cancellations;
- distributed workers;
- state transitions;
- queueing;
- replay;
- retention;
- audit requirements.

Without a decision ledger, a team may know that a workflow completed or failed, but not understand the decision path that led there.

The Decision Ledger helps answer questions such as:

- Why was this run accepted?
- Why was this step allowed to execute?
- Why was this provider or model used?
- Why was this retry scheduled?
- Why was this execution cancelled?
- Why did the runtime finalize the execution?
- Why was this replay started?
- Why was this data retained, evicted, or compacted?
- Which worker claimed the step?
- Which runtime instance processed the run?
- Which policy decision affected the execution?

This is critical for production operations, debugging, replay, audit, and enterprise trust.

---

## Ledger vs Logs

The Decision Ledger is not a replacement for logs.

It complements logs.

| Area | Logs | Decision Ledger |
|---|---|---|
| Purpose | Operational messages | Structured runtime decision history |
| Format | Often text-oriented | Event-oriented and queryable |
| Meaning | Describes system activity | Explains meaningful runtime decisions |
| Replay value | Useful but noisy | Directly useful for replay and audit |
| Audit value | Partial | Stronger and more structured |
| Correlation | Possible | First-class requirement |
| Dashboard usage | Useful | Core product feature |
| Compliance support | Limited alone | Strong foundation for audit-oriented controls |

Logs help engineers debug.

The Decision Ledger helps the runtime explain itself.

---

## Ledger vs Traces

Traces are useful for following execution across services.

The Decision Ledger is different.

Traces can show:

- call duration;
- service boundaries;
- spans;
- latency;
- errors;
- distributed flow.

The Decision Ledger should show:

- policy was evaluated;
- execution was admitted;
- step was claimed;
- retry was scheduled;
- cancellation was requested;
- replay was started;
- retention policy was applied;
- compaction was skipped because state was unsafe;
- execution was finalized for a specific reason.

Traces show flow.

The Decision Ledger explains decisions.

Both are needed.

---

## Ledger Scope

The Decision Ledger should cover the runtime domains that matter for production AI execution.

## 1. Execution Decisions

Execution decisions describe the lifecycle of a workflow execution.

Examples:

- execution created;
- execution started;
- execution paused;
- execution resumed;
- execution cancelled;
- execution failed;
- execution completed;
- execution finalized;
- execution archived direction;
- execution retained direction.

These events help explain the main execution lifecycle.

---

## 2. Run Decisions

Run decisions describe control-plane and queue-level work.

Examples:

- run submitted;
- run accepted;
- run rejected;
- run queued;
- run assigned;
- run dispatched;
- run started;
- run completed;
- run failed;
- run cancelled.

This is important because `RunId` and `ExecutionId` are separate concepts.

A run is the submitted/control-plane identity.  
An execution is the durable workflow identity.

The ledger should help connect both.

---

## 3. Queue and Dispatch Decisions

Queue and dispatch decisions describe how work moves through the runtime.

Examples:

- run admitted to queue;
- run rejected from queue;
- queue paused direction;
- queue resumed direction;
- shared queue dispatch accepted;
- shared queue dispatch skipped;
- runtime instance selected;
- runtime instance unavailable;
- local queue capacity reached;
- queue pressure detected;
- dispatch delayed;
- dispatch failed.

These decisions are essential for distributed execution and Kubernetes-style runtime behavior.

---

## 4. Step Claim Decisions

Claim decisions describe how steps are safely assigned to workers.

Examples:

- step ready;
- step selected;
- step claim attempted;
- step claim accepted;
- step claim rejected;
- claim expired;
- stale claim detected;
- stale claim cleanup executed;
- worker ownership recorded;
- claim token recorded.

Claim decisions are critical because distributed AI execution must prevent duplicate work.

If two workers see the same executable step, only one should be able to own it safely.

The ledger helps explain step ownership after the fact.

---

## 5. Worker and Runtime Instance Decisions

Worker and runtime instance decisions describe where execution happened.

Examples:

- runtime instance registered;
- runtime instance heartbeat received;
- runtime instance capacity updated;
- runtime instance selected for run;
- runtime instance rejected due to capacity;
- worker started;
- worker acquired work;
- worker completed step;
- worker failed step;
- worker released capacity;
- worker became unavailable.

These events help answer:

- Which runtime instance hosted the run?
- Which worker executed the step?
- Was the runtime instance overloaded?
- Was capacity available?
- Did the worker complete or fail?

This is important for observability, replay, dashboard, and managed hosting direction.

---

## 6. Policy Decisions

Policy decisions are one of the most important ledger categories.

Policy-driven execution is already part of the platform foundation.

Policy decisions may apply to:

- execution admission;
- run admission;
- queue admission;
- step execution;
- model/provider usage;
- tool usage;
- operation limits;
- concurrency limits;
- throttling;
- retry behavior;
- cancellation rules;
- replay access;
- ledger access;
- retention behavior;
- sensitive data access;
- tenant quotas;
- runtime instance capacity;
- worker capacity.

Examples:

- policy evaluated;
- policy allowed;
- policy denied;
- policy failed;
- policy throttled direction;
- policy delayed direction;
- policy requires approval direction;
- policy retry later direction.

The ledger should preserve policy decision context.

That context may include:

- tenant;
- project;
- pipeline;
- execution;
- run;
- step;
- user;
- RBAC context;
- provider;
- model;
- operation;
- runtime instance;
- worker;
- correlation ID.

Policy decisions make runtime governance explainable.

---

## 7. Retry Decisions

Retry decisions describe how failures are handled.

Examples:

- step failed;
- retry policy evaluated;
- retry allowed;
- retry denied;
- retry scheduled;
- retry delay recorded;
- retry count incremented;
- max retry reached;
- execution failed after retry exhaustion;
- retry succeeded.

Retry decisions are important because production AI workflows often fail due to transient external issues.

The ledger should help distinguish:

- transient failure;
- terminal failure;
- retryable failure;
- policy-denied retry;
- retry exhaustion.

This makes retry behavior auditable and replayable.

---

## 8. Cancellation Decisions

Cancellation decisions describe how a workflow or run was stopped.

Examples:

- cancellation requested;
- cancellation accepted;
- cancellation rejected;
- queued run cancelled;
- running execution cancellation requested;
- cancellation propagated to worker;
- cancellation observed by step;
- execution finalized as cancelled;
- cancellation failed;
- cancellation completed.

Cancellation decisions are important because production workflows must be controllable.

A system that can cancel but cannot explain cancellation is not reliable enough for enterprise operations.

---

## 9. Replay Decisions

Replay decisions describe replay lifecycle and replay behavior.

Examples:

- replay requested;
- replay started;
- replay mode selected;
- audit-only replay started;
- replay validation started;
- replay issue detected;
- replay report generated;
- replay completed;
- replay failed;
- replay access denied;
- replay used compacted history;
- replay used retained archive reference.

Replay decisions connect the ledger to the Replay and Audit Layer.

They help show when an execution was inspected and what replay evidence was used.

---

## 10. Finalization Decisions

Finalization decisions describe how an execution reached its final state.

Examples:

- finalization evaluated;
- finalization skipped because steps still running;
- finalization skipped because retry pending;
- finalization accepted;
- execution finalized as completed;
- execution finalized as failed;
- execution finalized as cancelled;
- finalization conflict detected;
- duplicate finalization prevented.

Finalization decisions are important because distributed execution can otherwise produce ambiguous results.

The ledger should show why the runtime considered an execution complete.

---

## 11. Retention, Eviction, and Compaction Decisions

Retention, eviction, and compaction decisions are part of the ledger foundation.

They are important because execution history must remain useful without allowing hot state and historical data to grow without control.

Examples:

- retention policy evaluated;
- record retained;
- retention skipped;
- hot state evicted;
- stale claim removed;
- completed coordination record removed;
- execution compacted;
- payload archived;
- archive created;
- archive skipped;
- compaction skipped because execution still active;
- eviction skipped because state unsafe to remove;
- retention failed;
- retention completed.

These decisions matter because retention can affect replayability and auditability.

The ledger should preserve enough evidence to explain:

- what was retained;
- what was evicted;
- what was compacted;
- why a cleanup decision was safe;
- whether replay value was preserved;
- whether archive references exist.

Retention should not be invisible cleanup.

It should be an auditable runtime decision.

---

## Event Structure

A decision ledger event should be structured enough to support replay, audit, observability, and dashboard views.

A typical ledger event can include:

| Field | Purpose |
|---|---|
| EventId | Unique event identity. |
| EventType | Stable event type or category. |
| TimestampUtc | When the event occurred. |
| ExecutionId | Durable workflow execution identity. |
| RunId | Control-plane or queue-level submitted work identity. |
| StepId | Step identity when applicable. |
| StepKey | Stable logical step key when applicable. |
| RuntimeInstanceId | Runtime instance involved in the decision. |
| WorkerId | Worker involved in the decision. |
| ClaimToken | Claim ownership token when applicable. |
| CorrelationId | Cross-system correlation identifier. |
| TenantId direction | Tenant context when applicable. |
| ProjectId direction | Project context when applicable. |
| PipelineId direction | Pipeline context when applicable. |
| UserId direction | User or actor context when applicable. |
| Provider direction | Provider context when applicable. |
| Model direction | Model context when applicable. |
| Operation direction | Operation context when applicable. |
| Decision | Decision result or outcome. |
| Reason | Human-readable or machine-readable reason. |
| Metadata | Structured additional data. |
| Fingerprint direction | Integrity or reproducibility metadata direction. |

This structure makes the ledger usable by systems, not only humans.

---

## Event Naming Direction

Stable event names are important.

Event names should be grouped by runtime domain.

Possible event groups include:

```text
execution.*
run.*
queue.*
dispatch.*
step.*
claim.*
worker.*
runtime_instance.*
policy.*
retry.*
cancellation.*
replay.*
finalization.*
retention.*
eviction.*
compaction.*
archive.*
observability.*
```

Examples:

```text
execution.created
execution.started
execution.finalized

run.submitted
run.queued
run.dispatched

step.claimed
step.completed
step.failed

policy.evaluated
policy.allowed
policy.denied

retry.scheduled
retry.exhausted

cancellation.requested
cancellation.completed

replay.started
replay.completed

retention.evaluated
eviction.hot_state_removed
compaction.completed
archive.created
```

Stable event names make the ledger easier to query, test, display, and export.

---

## Ledger Write Modes

The platform can support different ledger write modes.

This is important because some environments may prefer best-effort logging while others require strict audit behavior.

## Best-Effort Mode

In best-effort mode, the runtime attempts to write ledger events but does not necessarily fail execution if the ledger write fails.

This may be useful for:

- local development;
- tests;
- non-critical workflows;
- early demos;
- performance-sensitive scenarios.

## Strict Mode

In strict mode, important ledger writes can be treated as required.

This may be useful for:

- regulated workflows;
- audit-sensitive environments;
- critical business processes;
- production workflows that require strong traceability.

The correct mode may depend on tenant, pipeline, operation, or compliance profile direction.

---

## Ledger and Configuration-Driven Runtime

The ledger should work with configuration-driven runtime behavior.

Configuration can influence:

- ledger provider;
- write mode;
- retention policy;
- event categories;
- payload redaction;
- export behavior;
- storage backend;
- observability integration;
- dashboard visibility;
- replay behavior.

This allows the same runtime to behave differently in development, test, enterprise, or managed hosting environments.

---

## Ledger and Context-Driven Execution

The ledger should preserve important execution context.

Context may include:

- tenant;
- project;
- pipeline;
- execution;
- run;
- step;
- user;
- RBAC context;
- provider;
- model;
- operation;
- runtime instance;
- worker;
- correlation.

This is important because the same runtime decision can have different meaning depending on context.

For example:

- a policy denial for one tenant may be expected;
- a policy denial for another tenant may indicate misconfiguration;
- a provider throttling decision may depend on model or operation;
- a retention decision may depend on tenant or compliance profile direction.

Context makes ledger events meaningful.

---

## Ledger and Policy Engine

The policy engine and decision ledger should work together.

The policy engine evaluates decisions.  
The ledger records the outcome.

This combination enables:

- explainable runtime governance;
- replayable policy decisions;
- audit-friendly policy history;
- dashboard visibility;
- incident investigation;
- future compliance reporting.

Policy events should include enough information to explain:

- what policy was evaluated;
- what context was used;
- what decision was returned;
- why the decision was made;
- what operation was affected.

Policy decisions should not remain hidden inside code.

They should become part of runtime history.

---

## Ledger and Replay

Replay depends on the ledger.

The ledger gives replay the decision history behind execution state.

Replay can use ledger events to explain:

- why a step executed;
- why a retry was scheduled;
- why a policy denied an operation;
- why cancellation occurred;
- why finalization happened;
- why data was retained, evicted, or compacted.

Without the ledger, replay may show state but not reasoning.

With the ledger, replay can explain the execution path.

---

## Ledger and Observability

The ledger is also part of observability.

Observability usually includes:

- logs;
- metrics;
- traces.

The Decision Ledger adds:

- structured decisions;
- runtime context;
- policy outcomes;
- replayable events;
- audit-oriented history.

Ledger events should be correlated with logs, metrics, and traces.

This allows a dashboard or external observability system to connect:

- what happened;
- where it happened;
- how long it took;
- which decision caused it;
- which execution/run/step/worker/runtime instance was involved.

---

## Ledger and Dashboard

The dashboard should make ledger events accessible.

Potential dashboard views include:

- execution ledger view;
- run ledger view;
- step ledger view;
- policy decision view;
- retry decision view;
- cancellation view;
- replay decision view;
- retention/eviction/compaction view;
- runtime instance decision view;
- correlation timeline.

A ledger dashboard helps users investigate workflow behavior without reading raw logs or querying the database manually.

---

## Ledger and MCP

MCP tools can expose ledger information.

Possible MCP operations include:

- inspect ledger for execution;
- inspect ledger for run;
- inspect ledger for step;
- inspect policy decisions;
- inspect retry decisions;
- inspect cancellation decisions;
- inspect replay decisions;
- inspect retention decisions;
- inspect runtime instance decisions;
- query ledger by correlation ID;
- summarize ledger timeline.

This makes ledger history accessible through a control-plane interface.

---

## Ledger and Retention

The ledger itself also needs retention strategy.

Important questions:

- How long should ledger events be kept?
- Which events are required for replay?
- Which events are required for audit?
- Which events can be compacted?
- Which event payloads should be archived?
- Which events contain sensitive data?
- Which events require encryption hardening?
- Which events should remain searchable?
- Which events should be exported before deletion?

Ledger retention must be aligned with:

- replay;
- audit;
- tenant policy;
- storage cost;
- observability;
- compliance-support direction.

The ledger should also record retention decisions that affect execution evidence.

---

## Ledger and Security

Ledger data can be sensitive.

It may include:

- user context;
- RBAC context;
- policy context;
- provider/model context;
- operation context;
- execution metadata;
- payload references;
- failure reasons;
- tool metadata;
- audit information.

The platform direction should support:

- metadata/payload separation;
- redaction direction;
- encrypted ledger payload direction;
- access-controlled ledger views;
- audit of ledger access direction;
- tenant-aware ledger isolation direction;
- encrypted retention archive direction.

A ledger should not become a place where sensitive data is copied without control.

It should be structured and protected.

---

## Ledger and Multi-Tenant Readiness

The ledger is central to multi-tenant readiness.

Future tenant-aware ledger behavior may include:

- tenant-specific event isolation;
- project-specific event filtering;
- pipeline-specific event filtering;
- tenant-aware retention;
- tenant-aware encryption boundary direction;
- tenant-aware access control;
- tenant-aware observability export;
- tenant-aware replay reports.

This allows the platform to support self-hosted enterprise deployment, managed SaaS, dedicated clusters, and regulated environments.

---

## Ledger and Distributed Runtime

Distributed execution makes the Decision Ledger even more important.

When work runs across multiple runtime instances and workers, the ledger can explain:

- which runtime instance accepted the run;
- which worker claimed each step;
- whether a dispatch decision was made;
- whether capacity was available;
- whether a claim was rejected;
- whether a stale claim was detected;
- whether finalization was safe;
- whether cancellation propagated correctly.

The ledger becomes the distributed runtime explanation layer.

It helps make a multi-instance runtime understandable.

---

## Ledger and Managed Hosting

The ledger can also support managed hosting direction.

In managed hosting, users may need visibility into:

- execution volume;
- queue pressure;
- runtime capacity;
- worker utilization;
- replay/audit retention;
- policy decisions;
- throttling decisions;
- usage metering direction;
- support investigations;
- incident reports.

The ledger provides part of the evidence needed to support these operational and commercial models.

---

## Regulated-Market Readiness

The Decision Ledger is relevant for audit-sensitive and regulated environments.

It supports technical controls such as:

- execution history;
- policy decision history;
- replayable audit evidence;
- retry history;
- cancellation history;
- operator action history;
- retention history;
- finalization evidence;
- correlation across logs, traces, and state;
- export direction;
- access-control direction;
- encryption hardening direction.

The platform does not claim automatic legal compliance.

The correct positioning is:

> The Decision Ledger is designed to provide structured technical evidence that can support compliance implementation per customer, sector, and jurisdiction.

---

## Productization Roadmap

The Decision Ledger already exists as a foundation. Productization should make it easier to use.

## Milestone 1 — Event Taxonomy Hardening

Improve:

- stable event names;
- event categories;
- event severity;
- event metadata;
- event versioning direction;
- standard correlation fields.

## Milestone 2 — Ledger Query and Inspection

Improve:

- query by execution;
- query by run;
- query by step;
- query by worker;
- query by runtime instance;
- query by correlation ID;
- query by event type;
- query by policy decision;
- query by time range.

## Milestone 3 — Replay and Audit Integration

Improve:

- replay timeline generation;
- audit report summaries;
- policy decision replay;
- retry replay;
- cancellation replay;
- retention-aware replay;
- ledger-to-trace correlation.

## Milestone 4 — MCP and API Exposure

Improve:

- MCP ledger inspection tools;
- ledger API;
- summary API;
- timeline API;
- diagnostics API;
- event filtering;
- export direction.

## Milestone 5 — Dashboard Views

Add:

- ledger timeline;
- policy decision view;
- retry decision view;
- queue decision view;
- replay decision view;
- retention/eviction/compaction view;
- runtime instance decision view;
- correlation explorer.

## Milestone 6 — Security and Retention Hardening

Improve:

- access-controlled ledger views;
- redacted ledger payloads;
- encrypted payload direction;
- tenant-aware ledger retention;
- ledger archive direction;
- audit of ledger access direction.

---

## Current Foundation Summary

| Area | Status |
|---|---|
| Decision ledger foundation | Foundation exists |
| Execution lifecycle events | Foundation exists |
| Run lifecycle events | Foundation exists |
| Queue decision events | Foundation exists |
| Claim decision events | Foundation exists |
| Policy decision events | Foundation exists |
| Retry decision events | Foundation exists |
| Cancellation decision events | Foundation exists |
| Replay decision events | Foundation exists |
| Finalization decision events | Foundation exists |
| Retention decision events | Foundation exists |
| Eviction decision events | Foundation exists / active direction |
| Compaction decision events | Foundation exists / active direction |
| Archive decision events | Foundation exists / active direction |
| Correlation identifiers | Foundation exists |
| Ledger write modes | Foundation exists / direction exists |
| Replay integration | Foundation exists |
| Observability integration | Foundation exists |
| MCP ledger access | Productization target |
| Dashboard ledger views | Productization target |
| Access-controlled ledger views | Planned hardening direction |
| Encrypted ledger payloads | Planned hardening direction |
| Tenant-aware ledger isolation | Planned hardening direction |

---

## Planned Improvements

The Decision Ledger should continue improving in the following areas:

- event taxonomy hardening;
- event versioning direction;
- stronger correlation identifiers;
- replay integration;
- audit report integration;
- policy decision visibility;
- retry/cancellation visibility;
- queue and dispatch visibility;
- retention/eviction/compaction visibility;
- ledger query API;
- MCP ledger tools;
- dashboard ledger views;
- export direction;
- access-control direction;
- redaction direction;
- encrypted payload direction;
- tenant-aware ledger isolation;
- ledger retention and archive direction.

These are productization and hardening steps.

They make the existing ledger foundation easier to inspect, easier to trust, easier to replay, easier to audit, and easier to operate.

---

## Final Statement

The Decision Ledger is one of the strongest foundations of the Deterministic AI Runtime Platform.

It turns runtime behavior into structured history.

It connects:

- execution lifecycle;
- run lifecycle;
- step lifecycle;
- claims;
- workers;
- runtime instances;
- policies;
- retries;
- cancellations;
- replay;
- finalization;
- retention;
- eviction;
- compaction;
- archive direction;
- observability;
- audit.

The long-term goal is to make AI workflow execution explainable not only by reading logs, but by inspecting the structured decisions that shaped the execution path.

A production AI runtime should be able to explain itself.

The Decision Ledger is the foundation for that.
