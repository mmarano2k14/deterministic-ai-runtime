# Execution Control and State Lifecycle

## Deterministic AI Runtime Platform

This document describes the execution control and state lifecycle direction of the Deterministic AI Runtime Platform.

Execution control is one of the core differences between a simple AI workflow runner and a production runtime.

A production AI workflow should not be a fire-and-forget black box.

It should be possible to:

- inspect it;
- pause it;
- resume it;
- cancel it;
- understand its current state;
- understand why it is waiting;
- understand why it failed;
- understand why it finalized;
- replay and audit it after completion.

The key idea is:

> AI workflow execution must be stateful, controllable, observable, replayable, and auditable throughout the full execution lifecycle.

---

## Purpose

The purpose of execution control and state lifecycle management is to make AI workflows operable in production.

A deterministic runtime must be able to answer questions such as:

- What is the current execution status?
- Which steps are pending?
- Which steps are ready?
- Which steps are running?
- Which steps completed?
- Which steps failed?
- Which steps are waiting for retry?
- Which steps are waiting for input?
- Is the execution paused?
- Has cancellation been requested?
- Can a worker still write the result?
- Can a claim still be trusted?
- Can the execution finalize?
- Can hot state be evicted?
- Can replay be generated?
- Can the run be mapped to an execution?

These questions require explicit state lifecycle management.

---

## Current Foundation

The platform already includes important foundations around execution control and state lifecycle.

These include:

- deterministic runtime execution;
- DAG-based workflow execution;
- execution state foundation;
- step lifecycle foundation;
- run/execution separation;
- retry direction;
- cancellation direction;
- pause/resume direction;
- finalization direction;
- claim ownership direction;
- worker identity;
- runtime instance identity;
- Decision Ledger foundation;
- replay and audit foundation;
- MCP control-plane foundation;
- shared queue and local queue direction;
- provider-based runtime hosting direction;
- retention, eviction, and compaction foundation;
- observability direction.

The roadmap is not to invent lifecycle control from zero.

The roadmap is to harden, expose, test, document, and productize the existing lifecycle foundation.

---

## Core Principle

The core principle is:

```text
Every execution must move through explicit states.
Every state transition must be controlled.
Every important control decision must be recorded.
Every final state must be replayable and auditable.
```

This is the foundation of deterministic execution.

---

# 1. Execution Lifecycle Overview

An execution can move through several lifecycle phases.

A conceptual lifecycle can look like:

```text
Created
  -> Queued / Submitted
      -> Running
          -> Paused
          -> WaitingForRetry
          -> WaitingForInput
          -> Cancelling
          -> Completed
          -> Failed
          -> Cancelled
              -> Finalized
                  -> Replay / Audit / Retention / Eviction / Compaction
```

The exact internal state names can evolve, but the lifecycle idea is critical.

A runtime should always know where an execution is and what can happen next.

---

## Execution State Responsibilities

Execution state should track:

- ExecutionId;
- linked RunId;
- current status;
- created time;
- started time;
- completed time direction;
- finalization time direction;
- cancellation requested flag;
- pause state;
- retry state direction;
- waiting-for-input direction;
- step states;
- error summary;
- finalization reason;
- correlation identifiers;
- runtime metadata;
- replay metadata;
- retention status direction.

Execution state is the durable source of truth for workflow progress.

---

# 2. Run Lifecycle

A run is the submitted/control-plane identity.

A run is not the same as an execution.

```text
RunId       = submitted work identity
ExecutionId = durable workflow execution identity
```

Run lifecycle states can include:

- submitted;
- accepted;
- rejected;
- queued;
- assigned;
- dispatched;
- running;
- completed;
- failed;
- cancelled;
- cancellation requested.

Run lifecycle is important because work may exist before a durable execution is created or exposed.

This matters for shared queue, MCP, dashboard, and cancellation.

---

## Run-to-Execution Mapping

The runtime should make the RunId to ExecutionId mapping visible.

This mapping is useful for:

- MCP inspection;
- dashboard views;
- shared queue diagnostics;
- cancellation;
- replay;
- Decision Ledger correlation;
- observability;
- support investigations.

A user should be able to start with a RunId and discover the ExecutionId when available.

---

# 3. Step Lifecycle

Steps are the execution units inside a DAG workflow.

Step lifecycle can include:

- pending;
- ready;
- claimable;
- claimed;
- running;
- completed;
- failed;
- waiting for retry;
- skipped;
- cancelled;
- blocked;
- waiting for input direction.

Step lifecycle must be explicit because distributed workers may compete to execute ready steps.

---

## Step Readiness

A step becomes ready when:

- dependencies are satisfied;
- execution is not paused;
- cancellation has not prevented execution;
- retry delay has passed;
- policy allows execution;
- required input is available;
- the step is not already claimed;
- the step is not completed;
- the step is not terminal.

Step readiness should be deterministic.

The same execution state should produce the same readiness decision.

---

## Step Claiming

Step claiming prevents unsafe duplicate execution.

A worker should claim a step before executing it.

Claim information can include:

- StepId;
- WorkerId;
- RuntimeInstanceId;
- ClaimToken;
- claim timestamp;
- claim expiry direction;
- correlation ID.

A claim makes ownership explicit.

If a worker crashes, claim expiry or recovery logic can allow safe reprocessing direction.

---

## Claim Safety

Claim safety should prevent:

- two workers executing the same step at the same time;
- stale workers writing results after claim loss;
- completed steps being executed again;
- finalization while steps are still legitimately running;
- retention/eviction while claims are active.

Claim decisions should be recorded in the Decision Ledger.

---

# 4. Pause

Pause is an execution control operation.

Pause should prevent new work from being scheduled while preserving execution state.

Pause should not destroy execution progress.

Pause can be used when:

- an operator wants to inspect state;
- a policy issue is detected;
- queue pressure is too high;
- provider behavior is suspicious;
- a user needs to intervene;
- a pipeline requires manual review direction.

---

## Pause Semantics

Pause should answer:

- can this execution be paused?
- is it already paused?
- are steps currently running?
- should running steps finish?
- should new steps be blocked?
- should workers observe pause state?
- should pause be recorded in the Decision Ledger?

A paused execution should remain inspectable.

Replay should later show when pause was requested and applied.

---

# 5. Resume

Resume allows a paused execution to continue.

Resume should evaluate:

- is the execution paused?
- is cancellation pending?
- are ready steps available?
- are retry timers still pending?
- does policy allow resume?
- is runtime capacity available?
- should resume be recorded in the Decision Ledger?

Resume should not bypass policy or lifecycle safety.

It should return the execution to normal scheduling when allowed.

---

# 6. Cancel

Cancel is one of the most important production controls.

Cancellation can apply to:

- queued run;
- assigned run;
- running execution;
- specific operation direction;
- future step execution;
- running worker direction.

Cancellation is more complex than simply setting a status.

---

## Cancellation Scenarios

Cancellation can happen when:

- a run is still queued;
- a run is assigned but not started;
- an execution is running;
- steps are already completed;
- a step is currently running;
- workers need to observe cancellation;
- external tools may already have side effects;
- finalization must mark execution as cancelled.

Each scenario needs careful behavior.

---

## Cancellation Semantics

Cancellation should answer:

- was cancellation requested?
- was the run queued?
- was the execution already created?
- are steps running?
- can running steps be interrupted?
- should new steps be prevented?
- did workers observe cancellation?
- did finalization mark the execution as cancelled?
- was cancellation recorded in the Decision Ledger?

Cancellation should be auditable.

A production runtime must explain how and when cancellation happened.

---

# 7. Waiting for Retry

Waiting for retry is a non-terminal state.

A failed step may not mean the execution has failed.

If retry is allowed, the step can enter waiting-for-retry.

Retry state can include:

- retry count;
- max retries;
- retry delay;
- next retry time;
- error reason;
- retry policy decision;
- retry scheduled event;
- retry exhaustion direction.

This state is important because deterministic retries require explicit state.

---

## Retry Readiness

A step waiting for retry becomes ready when:

- retry delay has passed;
- execution is not paused;
- cancellation is not terminal;
- policy still allows retry;
- max retries has not been reached;
- dependencies are still valid.

Retry should be visible in replay, Decision Ledger, MCP, and dashboard.

---

# 8. Waiting for Input

Some workflows need human or external input.

Waiting-for-input direction supports:

- human approval;
- manual review;
- missing data;
- external confirmation;
- correction step;
- sensitive operation approval;
- interactive workflow direction.

Waiting-for-input should be explicit.

A workflow waiting for input should not look failed.

---

## Input Submission

When input is submitted, the runtime should evaluate:

- is the execution waiting for input?
- is the user allowed to submit input?
- does input match expected schema?
- does policy allow continuation?
- which step receives the input?
- should the execution resume automatically?
- should the Decision Ledger record the input event?

This connects execution lifecycle to future human-in-the-loop workflows.

---

# 9. Finalization

Finalization decides when an execution reaches a terminal result.

Finalization should be deterministic and safe.

Possible final statuses:

- completed;
- failed;
- cancelled.

Finalization should only happen when the runtime can prove the execution has no remaining active work.

---

## Finalization Conditions

Finalization can evaluate:

- are all required steps completed?
- did any required step fail terminally?
- is cancellation requested?
- are any claims active?
- are retry windows pending?
- are steps waiting for input?
- are dependencies unresolved?
- is execution paused?
- are there ready steps still available?
- did policy force termination direction?

Finalization should not happen too early.

---

## Finalization Safety

Finalization should prevent:

- duplicate finalization;
- finalizing while workers still own active claims;
- finalizing while retry is pending;
- finalizing while cancellation is still propagating;
- finalizing before ledger events are written direction;
- finalizing before state is durable direction.

Finalization decisions should be recorded in the Decision Ledger.

Replay should explain why finalization happened.

---

# 10. Execution Control Through MCP

MCP is a natural control surface for execution lifecycle.

MCP tools can expose:

- inspect run;
- inspect execution;
- inspect step states;
- pause execution;
- resume execution;
- cancel execution;
- submit input direction;
- inspect retry state;
- inspect waiting state;
- inspect finalization state;
- replay execution;
- inspect Decision Ledger events;
- run diagnostics.

MCP should return structured responses with:

- status;
- execution ID;
- run ID;
- current state;
- allowed operations;
- errors;
- warnings;
- diagnostic summary;
- correlation ID.

This makes execution control operable by humans, agents, dashboards, and automation.

---

# 11. Execution Control Through Dashboard

The dashboard should visualize execution lifecycle.

Dashboard views can show:

- current execution state;
- allowed control operations;
- pause/resume/cancel buttons direction;
- step lifecycle;
- retry state;
- waiting-for-input state;
- finalization state;
- cancellation history;
- replay report;
- Decision Ledger timeline;
- diagnostics.

The dashboard should not hide lifecycle complexity.

It should make lifecycle state understandable.

---

# 12. Decision Ledger Integration

Execution control decisions should be recorded in the Decision Ledger.

Examples:

```text
execution.pause_requested
execution.paused
execution.resume_requested
execution.resumed
execution.cancel_requested
execution.cancelled
execution.waiting_for_retry
execution.retry_ready
execution.waiting_for_input
execution.input_submitted
execution.finalization_evaluated
execution.finalized

step.claim_attempted
step.claimed
step.claim_rejected
step.started
step.completed
step.failed
step.retry_scheduled
step.cancelled
```

These events make lifecycle transitions auditable.

A replay report should be able to show them.

---

# 13. Replay and Audit Integration

Replay should explain lifecycle behavior.

Replay can answer:

- when did the execution start?
- when did a step become ready?
- which worker claimed the step?
- when was retry scheduled?
- when was pause requested?
- when was resume requested?
- when was cancellation requested?
- did cancellation affect queued or running work?
- why did finalization happen?
- what was the final status?
- were retention and compaction applied later?

Lifecycle replay is essential for production debugging.

---

# 14. Observability Integration

Execution lifecycle should be observable.

Signals can include:

- executions by state;
- paused executions;
- resumed executions;
- cancelled executions;
- waiting-for-retry count;
- waiting-for-input count;
- finalization count;
- finalization failures;
- active claims;
- stale claims;
- claim rejection count;
- retry scheduled count;
- cancellation latency;
- pause duration;
- execution duration.

These metrics support operations and dashboard views.

---

# 15. Policy Integration

Execution control should be policy-aware.

Policies can decide:

- who can pause;
- who can resume;
- who can cancel;
- who can submit input;
- whether a retry is allowed;
- whether a step can execute;
- whether finalization can proceed;
- whether replay is allowed;
- whether retention/eviction/compaction can happen after finalization.

Control operations should not bypass governance.

Policy decisions should be recorded in the Decision Ledger.

---

# 16. Retention, Eviction, and Compaction Integration

Execution state lifecycle connects directly to retention, eviction, and compaction.

After finalization, the runtime can evaluate:

- should a snapshot be created?
- should replay report be generated?
- should hot state be evicted?
- should stale claims be cleaned?
- should history be compacted?
- should an archive be created?
- should ledger events be retained?
- should payloads be redacted or removed?

These lifecycle decisions must be policy-driven.

Eviction and compaction should not run against active executions.

---

# 17. Distributed Execution Safety

Execution control becomes harder with multiple workers and runtime instances.

The runtime must handle:

- workers claiming steps concurrently;
- stale workers;
- runtime instance failure;
- cancellation across instances;
- finalization race conditions;
- retry readiness across workers;
- shared queue dispatch;
- local queue processing;
- provider-based communication;
- transport failures.

Execution lifecycle must remain safe under distributed conditions.

---

## Distributed Control Requirements

Distributed control should support:

- stable RuntimeInstanceId;
- stable WorkerId;
- claim tokens;
- correlation IDs;
- atomic state updates direction;
- expected status checks;
- claim validation;
- cancellation propagation;
- idempotent finalization direction;
- replayable lifecycle events.

This is a key part of deterministic distributed AI execution.

---

# 18. Failure Scenarios

The lifecycle model should handle failure scenarios.

Examples:

- worker crashes after claiming a step;
- runtime instance stops reporting heartbeat;
- provider dispatch fails;
- HTTP runtime provider times out;
- step fails with retryable error;
- step fails with terminal error;
- cancellation is requested while a step is running;
- execution is paused while workers are active;
- finalization races with worker completion;
- replay report is missing;
- retention tries to evict state too early.

Each failure should produce clear state, diagnostics, and ledger events.

---

# 19. State Lifecycle Productization

The lifecycle foundation should be productized through:

- clearer public documentation;
- stronger API models;
- MCP tools;
- dashboard lifecycle views;
- replay timeline;
- Decision Ledger events;
- tests for lifecycle scenarios;
- diagnostics;
- observability metrics.

Execution lifecycle should become one of the most visible strengths of the platform.

---

# Current Foundation Summary

| Area | Status |
|---|---|
| Execution state foundation | Foundation exists |
| Step lifecycle foundation | Foundation exists |
| Run/execution separation | Foundation exists |
| Retry state direction | Foundation exists |
| Pause direction | Foundation exists |
| Resume direction | Foundation exists |
| Cancellation direction | Foundation exists |
| Finalization direction | Foundation exists |
| Claim ownership direction | Foundation exists |
| Worker identity | Foundation exists |
| Runtime instance identity | Foundation exists |
| MCP execution control | Foundation exists / active direction |
| Decision Ledger lifecycle events | Foundation exists |
| Replay/audit lifecycle visibility | Foundation exists |
| Observability lifecycle direction | Foundation exists |
| Retention/eviction/compaction after lifecycle | Foundation exists |
| Waiting-for-input direction | Active direction |
| Dashboard lifecycle views | Productization target |
| Stronger distributed lifecycle tests | Planned hardening direction |

---

# Productization Roadmap

## Step 1 — Document Lifecycle States

Improve documentation for:

- execution states;
- run states;
- step states;
- retry states;
- pause/resume/cancel semantics;
- finalization semantics;
- waiting-for-input direction.

## Step 2 — Expose Lifecycle Through MCP

Improve MCP tools for:

- inspect execution;
- inspect step states;
- pause;
- resume;
- cancel;
- inspect retry state;
- inspect finalization state;
- inspect waiting state;
- diagnostics.

## Step 3 — Improve Replay Timeline

Improve replay output for:

- state transitions;
- claim events;
- retry events;
- pause/resume/cancel events;
- finalization decision;
- policy decisions;
- retention lifecycle events.

## Step 4 — Add Dashboard Lifecycle Views

Add views for:

- execution lifecycle;
- step lifecycle;
- retry status;
- cancellation status;
- waiting-for-input status;
- finalization reason;
- allowed operations.

## Step 5 — Harden Distributed Lifecycle Safety

Improve tests and safety around:

- worker crash;
- stale claims;
- cancellation propagation;
- finalization race;
- retry timing;
- provider failure;
- state version mismatch;
- retention while active execution exists.

---

# Planned Improvements

The execution control and state lifecycle layer should continue improving through:

- stronger lifecycle documentation;
- clearer state transition model;
- richer MCP inspection;
- dashboard lifecycle visualization;
- replay timeline improvements;
- Decision Ledger event taxonomy;
- distributed lifecycle tests;
- waiting-for-input implementation direction;
- policy-aware control operations;
- retention-aware finalization;
- cancellation propagation hardening.

These are hardening and productization steps.

They build on the existing deterministic execution foundation.

---

# Final Statement

Execution control and state lifecycle are central to the Deterministic AI Runtime Platform.

The runtime should not only execute AI workflows.

It should know exactly where each execution, run, step, worker, and runtime instance stands in the lifecycle.

It should support pause, resume, cancel, retry, waiting-for-input, finalization, replay, audit, retention, eviction, and compaction as explicit lifecycle concerns.

This is what makes the platform suitable for production AI workflows.

A production AI runtime must not be a black box.

It must be stateful, controllable, observable, replayable, auditable, governable, and safe under distributed execution.
