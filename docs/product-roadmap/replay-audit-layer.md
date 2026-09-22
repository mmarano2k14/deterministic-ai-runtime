# Replay and Audit Layer

## Deterministic AI Runtime Platform

This document describes the Replay and Audit Layer of the Deterministic AI Runtime Platform.

Replay and audit are not secondary features in this project. They are part of the core reason the runtime exists.

The platform is designed around the idea that production AI execution must not be treated as a black box. A workflow should not only produce an output. It should also leave behind enough structured evidence to understand what happened, why it happened, which runtime decisions were made, which worker executed the work, which policies were evaluated, and whether the execution path can be inspected later.

The Replay and Audit Layer is the foundation that makes AI execution explainable after execution.

---

## Purpose

The Replay and Audit Layer exists to solve a production problem:

> If an AI workflow fails, behaves unexpectedly, produces a sensitive result, or affects a critical business process, teams must be able to inspect what happened after the fact.

In production AI systems, this requires more than raw logs.

The platform needs:

- execution state;
- step state;
- run metadata;
- decision ledger events;
- policy decision events;
- retry history;
- cancellation history;
- worker identity;
- runtime instance identity;
- correlation identifiers;
- replay metadata;
- audit report direction;
- diagnostic issues;
- retained execution history;
- safe retention, eviction, and compaction decisions.

Replay and audit make the runtime trustworthy.

---

## Current Foundation

The project already contains a meaningful replay and audit foundation.

The existing foundation includes:

- replay and audit direction;
- audit-only replay direction;
- replay reports;
- replay diagnostics;
- deterministic validation direction;
- execution timeline reconstruction direction;
- issue detection direction;
- decision ledger correlation direction;
- replay lifecycle event direction;
- replay metadata direction;
- fingerprint / integrity metadata direction;
- execution state inspection direction;
- step state inspection direction;
- policy decision visibility direction;
- retention and compaction decision visibility direction;
- integration and reliability testing direction.

The roadmap is not to invent replay from zero.

The roadmap is to harden, expose, productize, visualize, and scale the replay and audit foundation.

---

## What Replay Means in This Platform

Replay does not simply mean “run the AI workflow again.”

In this platform, replay means:

> Reconstruct, inspect, validate, and explain the execution history of a workflow using the runtime evidence produced during execution.

Replay can involve:

- loading execution state;
- reading step state;
- inspecting decision ledger events;
- inspecting replay metadata;
- inspecting policy decisions;
- inspecting retry history;
- inspecting cancellation history;
- inspecting worker/runtime identity;
- reconstructing execution timeline;
- detecting missing or inconsistent data;
- producing replay reports;
- validating deterministic runtime behavior.

Replay is a diagnostic and audit capability.

It is not uncontrolled re-execution.

---

## Replay vs Re-Execution

This distinction is important.

## Replay

Replay is used to inspect, validate, and explain an existing execution.

Replay can answer:

- what happened?
- which steps executed?
- which steps failed?
- which steps were skipped?
- which worker claimed the step?
- which policies were evaluated?
- which runtime decisions were recorded?
- when was retry scheduled?
- when was cancellation requested?
- why did the execution finalize?
- what evidence exists for audit?

Replay is safe because it does not blindly trigger side effects.

## Re-Execution

Re-execution means running the workflow again.

Re-execution can be risky because an AI workflow may include side effects such as:

- sending messages;
- calling external APIs;
- writing to databases;
- charging usage;
- triggering business actions;
- modifying documents;
- invoking tools;
- creating new records.

For this reason, re-execution should be treated carefully and should not be confused with replay.

The safe default is audit replay, not automatic re-execution.

---

## Audit-Only Replay

Audit-only replay is an important foundation.

Audit-only replay focuses on inspection and validation without triggering workflow side effects.

It is designed to help users understand execution history while avoiding accidental re-execution of tools, model calls, or external operations.

Audit-only replay can support:

- execution history review;
- incident investigation;
- compliance-oriented review direction;
- runtime diagnostics;
- failure analysis;
- policy decision inspection;
- ledger inspection;
- replay report generation;
- deterministic validation direction;
- support and debugging workflows.

This is especially important for enterprise and regulated environments.

---

## Replay Inputs

A replay operation can rely on multiple sources of runtime evidence.

These may include:

| Input | Purpose |
|---|---|
| Execution state | Provides the durable workflow state and final status. |
| Step state | Provides step-level status, timing, retry, failure, and output metadata. |
| Run metadata | Connects submitted work to execution identity and control-plane history. |
| Decision ledger | Explains runtime decisions, policy decisions, claims, retries, cancellation, and finalization. |
| Correlation identifiers | Connects logs, traces, ledger events, replay reports, workers, and runtime instances. |
| Runtime instance identity | Shows where work was hosted or assigned. |
| Worker identity | Shows which worker executed or claimed work. |
| Replay metadata | Preserves replay-specific inspection and validation information. |
| Retained history | Provides archived or compacted execution evidence. |
| Observability data | Provides logs, metrics, traces, and operational context. |

Replay quality depends on the quality of runtime evidence.

This is why replay is connected to state, ledger, observability, and retention.

---

## Replay Outputs

Replay should produce useful outputs.

Replay outputs can include:

- replay summary;
- execution timeline;
- step timeline;
- replay status;
- replay issues;
- validation results;
- missing data warnings;
- inconsistent state warnings;
- policy decision summary;
- retry history summary;
- cancellation summary;
- ledger correlation summary;
- trace correlation summary;
- retained-history summary;
- audit report direction.

The goal is to make replay readable and actionable.

A replay report should help a user understand the execution without manually searching through raw logs.

---

## Replay Report

The replay report is a central output of the Replay and Audit Layer.

A replay report should summarize:

- execution identity;
- run identity;
- replay time;
- replay mode;
- execution status;
- finalization reason;
- step count;
- completed steps;
- failed steps;
- skipped steps;
- cancelled steps;
- waiting steps;
- retry events;
- policy decisions;
- ledger events;
- replay issues;
- validation result;
- correlation identifiers;
- diagnostic notes.

The report should be useful for:

- debugging;
- audit review;
- support;
- engineering investigation;
- compliance-oriented review direction;
- future dashboard views.

---

## Replay Timeline

A replay timeline should reconstruct the sequence of runtime events.

The timeline can include:

- run submitted;
- run queued;
- run assigned;
- execution created;
- execution started;
- step became ready;
- step claimed;
- step started;
- step completed;
- step failed;
- retry scheduled;
- policy evaluated;
- queue dispatch recorded;
- cancellation requested;
- execution finalized;
- replay started;
- replay completed;
- retention decision recorded;
- compaction decision recorded.

A timeline is important because it turns distributed runtime behavior into a readable story.

---

## Replay Issues

Replay should be able to identify issues.

Replay issues may include:

- missing execution state;
- missing step state;
- missing ledger events;
- inconsistent final status;
- missing finalization event;
- missing correlation data;
- missing worker identity;
- missing runtime instance identity;
- missing retry metadata;
- invalid state transition;
- stale claim evidence;
- incomplete audit trail;
- compacted data warning;
- evicted hot-state warning;
- retained-history reference missing;
- replay report incomplete.

Replay issues should be classified clearly.

Possible issue levels:

- info;
- warning;
- error;
- critical.

This makes replay useful for both debugging and audit-oriented review.

---

## Deterministic Validation

Replay should support deterministic validation at the orchestration layer.

This does not mean every LLM output must be identical.

It means the runtime should validate deterministic orchestration evidence such as:

- step ordering;
- dependency satisfaction;
- state transitions;
- retry decisions;
- claim ownership;
- finalization conditions;
- cancellation behavior;
- policy decisions;
- queue decisions;
- ledger consistency;
- retained-history consistency.

The goal is:

> Validate the execution control path, not pretend that model output randomness does not exist.

This distinction keeps the platform honest and technically correct.

---

## Fingerprint and Integrity Metadata Direction

Replay and audit can benefit from fingerprint or integrity metadata.

Fingerprint metadata can help detect whether important replay inputs changed.

Potential fingerprint areas include:

- workflow definition;
- pipeline version;
- execution state;
- step state;
- replay report;
- decision ledger event group;
- retained archive;
- compacted history;
- payload reference;
- configuration snapshot direction;
- policy snapshot direction.

This helps the runtime support stronger audit and reproducibility direction over time.

Fingerprinting should be treated carefully. It should support trust, not overclaim perfect determinism of AI model output.

---

## Decision Ledger Integration

Replay depends heavily on the decision ledger.

The decision ledger can explain why runtime decisions happened.

Replay should be able to use ledger events such as:

- execution created;
- execution started;
- step selected;
- step claimed;
- step completed;
- step failed;
- policy evaluated;
- policy allowed;
- policy denied;
- retry scheduled;
- queue dispatch accepted;
- run assigned;
- cancellation requested;
- execution finalized;
- replay started;
- replay completed;
- retention decision made;
- eviction decision made;
- compaction decision made;
- archive decision made.

The decision ledger makes replay more than a state snapshot.

It gives replay the “why” behind the execution path.

---

## Policy Decision Replay

Policy decisions are important replay evidence.

Replay should be able to show:

- which policy was evaluated;
- which context was used;
- which decision was produced;
- whether the decision allowed or denied execution;
- whether throttling or delay direction applied;
- which tenant/project/pipeline/user/provider/model/operation context was involved;
- which ledger event recorded the decision.

Policy decision replay is important because enterprise AI workflows require governance.

A replay report that includes policy decisions becomes much more useful than a replay report that only shows step status.

---

## Retry Replay

Retry behavior must be visible in replay.

Replay should be able to show:

- which step failed;
- why it failed;
- whether retry was allowed;
- retry count;
- max retry direction;
- retry delay;
- next retry time;
- whether the retry eventually succeeded;
- whether max retry was reached;
- whether the execution failed because retry was exhausted.

Retry replay helps distinguish transient failure from terminal failure.

It also helps validate whether retry behavior was deterministic and controlled.

---

## Cancellation Replay

Cancellation must also be visible in replay.

Replay should be able to show:

- when cancellation was requested;
- whether the run was queued, assigned, or running;
- which execution was affected;
- whether running work received cancellation direction;
- which steps were already completed;
- which steps were cancelled;
- whether finalization happened as cancelled;
- which ledger events recorded cancellation;
- whether cancellation propagation was complete.

This is important because cancellation is an operational control feature.

If cancellation cannot be audited, it is not trustworthy enough for production.

---

## Retention, Eviction, and Compaction Replay

Retention, eviction, and compaction are part of the replay story.

Replay should understand when data has been retained, compacted, archived, or evicted.

This is important because replay may need to explain:

- why full payload data is no longer in hot state;
- where retained history is stored;
- whether data was compacted;
- whether replay value was preserved;
- whether ledger references still exist;
- whether archive references are available;
- whether eviction was safe;
- whether compaction skipped active execution;
- whether retention policy was applied.

Retention-aware replay is important for long-running systems.

A runtime should not fail silently when historical data was compacted. It should explain what was retained and what was intentionally reduced.

---

## Replay and Hot-State Eviction

Hot-state eviction is a runtime lifecycle concern.

Replay should not depend only on hot state.

If hot state was evicted after completion, replay should still be able to rely on durable history, replay reports, retained execution summaries, ledger events, and archive references.

This creates a clear distinction:

```text
Hot state = fast runtime coordination
Durable history = replay, audit, investigation, retained evidence
```

This is why storage separation matters.

Redis-style hot state should be allowed to expire or be evicted safely after durable history is preserved.

---

## Replay and Compacted History

Compaction should preserve enough information for replay and audit.

Compacted history may reduce payload size or remove unnecessary intermediate detail, but should preserve:

- execution identity;
- run identity;
- step identity;
- step status;
- final status;
- replay metadata;
- decision ledger references;
- correlation identifiers;
- archive references;
- diagnostic issues;
- enough information to explain the execution path.

Replay should be able to report when it uses compacted history.

This avoids confusion and improves audit transparency.

---

## Replay and Observability

Replay should connect to observability.

Relevant observability data includes:

- logs;
- traces;
- metrics;
- decision ledger events;
- execution timeline;
- worker activity;
- runtime instance health;
- queue pressure;
- retry metrics;
- failure metrics;
- cancellation metrics;
- retention metrics.

Replay should help connect historical execution state to runtime signals.

This is especially useful for distributed execution because events may be spread across workers, runtime instances, queues, and storage systems.

---

## Replay and Correlation

Correlation is essential for replay.

Replay should use correlation identifiers such as:

- ExecutionId;
- RunId;
- StepId;
- StepKey;
- RuntimeInstanceId;
- WorkerId;
- ClaimToken;
- CorrelationId.

These identifiers help connect:

- execution state;
- run state;
- step state;
- worker activity;
- runtime instance activity;
- queue events;
- ledger events;
- traces;
- logs;
- replay reports.

Without correlation, replay becomes incomplete.

With correlation, replay becomes a structured investigation tool.

---

## Replay and Distributed Execution

Distributed execution makes replay more important.

In a distributed runtime, work may be processed across:

- multiple workers;
- multiple runtime instances;
- local queues;
- shared queue;
- Redis coordination;
- durable history storage;
- decision ledger storage;
- observability exports.

Replay must be able to explain distributed behavior.

It should help answer:

- which runtime instance received the run?
- which worker claimed the step?
- did another worker attempt to claim the same step?
- was a claim stale?
- was queue dispatch recorded?
- did cancellation propagate?
- was finalization safe?
- did retention or eviction happen after completion?

Distributed replay is a major differentiator of the platform.

---

## Replay and MCP

Replay should be available through MCP control operations.

MCP replay tools can support:

- replay execution;
- inspect replay report;
- inspect replay issues;
- inspect execution timeline;
- inspect decision ledger for execution;
- inspect replay diagnostics;
- inspect replay validation result;
- inspect replay metadata.

This makes replay available as part of the runtime control plane.

MCP can turn replay into an operational tool that can be used by agents, developers, or future dashboard workflows.

---

## Replay and Dashboard

The dashboard should make replay understandable.

Future dashboard replay views can include:

- replay summary;
- execution timeline;
- step timeline;
- issue list;
- replay validation status;
- decision ledger correlation;
- trace correlation;
- retry history;
- cancellation history;
- retention/compaction status;
- audit report export direction.

The dashboard should turn replay from a raw technical report into a usable product feature.

---

## Audit Layer

The audit layer is the broader foundation around replay.

Audit includes:

- execution state;
- step state;
- decision ledger;
- policy decisions;
- run lifecycle;
- queue lifecycle;
- retry decisions;
- cancellation decisions;
- replay events;
- finalization events;
- retention decisions;
- correlation identifiers;
- retained history.

Audit is not only for regulated industries.

Audit is also useful for:

- debugging;
- customer support;
- incident response;
- engineering review;
- reliability improvement;
- internal governance;
- operational trust.

---

## Audit Reports

Audit reports should be generated from structured runtime evidence.

An audit report can include:

- execution identity;
- pipeline/workflow reference;
- run identity;
- start time;
- end time;
- final status;
- step summary;
- failure summary;
- retry summary;
- cancellation summary;
- policy decision summary;
- ledger event summary;
- replay validation status;
- retention status;
- correlation identifiers;
- diagnostic issues.

The goal is to produce a report that can be read by humans and used by tools.

---

## Audit Boundaries

Audit must be honest about its boundaries.

The platform can audit the runtime execution process.

It can record:

- what workflow was executed;
- which steps ran;
- which runtime decisions were made;
- which policies were evaluated;
- which worker executed work;
- which state transitions occurred;
- which replay evidence exists.

The platform should not overclaim that it can prove the internal reasoning of an LLM.

The correct positioning is:

> The platform audits the execution path, runtime decisions, state transitions, and operational evidence around AI workflows.

This is the reliable and defensible scope.

---

## Security and Sensitive Audit Data

Audit data can be sensitive.

It may include:

- prompts;
- model responses;
- tool inputs;
- tool outputs;
- document references;
- user context;
- RBAC context;
- tenant context;
- policy context;
- operational metadata.

The platform direction should support:

- redaction direction;
- metadata/payload separation;
- encrypted ledger payload direction;
- encrypted retention archive direction;
- access-controlled replay direction;
- audit of replay access direction.

Audit value must be balanced with data protection.

---

## Regulated-Market Readiness

Replay and audit are relevant for regulated and audit-sensitive environments.

They support technical controls such as:

- execution history;
- replayable workflow evidence;
- decision history;
- policy decision visibility;
- audit report direction;
- retention policy direction;
- encrypted retention direction;
- observability export direction;
- access-control direction;
- tenant isolation direction.

The platform does not claim automatic legal compliance.

The correct positioning is:

> The platform is designed to provide technical controls that can support compliance implementation per customer, sector, and jurisdiction.

---

## Productization Roadmap

The Replay and Audit Layer should evolve through productization steps.

## Milestone 1 — Strengthen Replay Reports

Improve:

- structure;
- readability;
- summary sections;
- issue classification;
- deterministic validation output;
- ledger correlation;
- trace correlation;
- retention status.

## Milestone 2 — Expose Replay Through APIs and MCP

Improve:

- replay API;
- audit report API direction;
- MCP replay tools;
- diagnostics tools;
- replay issue inspection;
- replay metadata access.

## Milestone 3 — Add Dashboard Views

Add:

- replay summary view;
- execution timeline;
- step timeline;
- ledger correlation view;
- retry/cancellation view;
- retention/compaction view;
- audit report export direction.

## Milestone 4 — Improve Retention-Aware Replay

Improve:

- retained-history access;
- compacted-history reporting;
- archive reference reporting;
- hot-state eviction transparency;
- replay after compaction;
- replay after retention policy application.

## Milestone 5 — Harden Security and Access Control

Improve:

- redacted replay views;
- sensitive payload access control;
- encrypted retention archive direction;
- audit of replay access;
- tenant-aware replay direction.

---

## Current Foundation Summary

| Area | Status |
|---|---|
| Replay and audit foundation | Foundation exists |
| Audit-only replay direction | Foundation exists |
| Replay report direction | Foundation exists |
| Replay diagnostics | Foundation exists |
| Deterministic validation direction | Foundation exists |
| Decision ledger correlation | Foundation exists |
| Policy decision replay | Foundation exists |
| Retry replay | Foundation exists |
| Cancellation replay | Foundation exists |
| Retention-aware replay | Foundation exists / active direction |
| Compaction-aware replay | Foundation exists / active direction |
| Hot-state eviction awareness | Foundation exists / active direction |
| Distributed execution replay | Direction exists |
| MCP replay access | Direction exists |
| Dashboard replay views | Productization target |
| Audit report export | Productization target |
| Encrypted replay/retention data | Planned hardening direction |
| Access-controlled replay | Planned hardening direction |

---

## Planned Improvements

The replay and audit layer should continue improving in the following areas:

- replay report readability;
- replay issue classification;
- deterministic validation clarity;
- ledger correlation;
- trace correlation;
- policy decision replay;
- retry/cancellation replay;
- retention-aware replay;
- compacted-history reporting;
- hot-state eviction transparency;
- audit report export direction;
- MCP replay tools;
- dashboard replay views;
- access-controlled replay direction;
- encrypted replay bundle direction;
- tenant-aware replay direction.

These are productization and hardening steps.

They make the existing replay and audit foundation easier to use, easier to inspect, easier to expose, and more suitable for production operations.

---

## Final Statement

The Replay and Audit Layer is one of the strongest foundations of the Deterministic AI Runtime Platform.

It gives the runtime the ability to explain execution after the fact.

It connects:

- execution state;
- step state;
- run identity;
- worker identity;
- runtime instance identity;
- decision ledger;
- policy decisions;
- retry history;
- cancellation history;
- retention decisions;
- observability;
- correlation;
- audit reports.

The long-term goal is to make AI workflow execution replayable enough for debugging, auditable enough for enterprise review, transparent enough for operations, and structured enough to support regulated-market technical controls.
