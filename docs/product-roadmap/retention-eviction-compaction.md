# Retention, Eviction, and Compaction

## Deterministic AI Runtime Platform

This document describes the Retention, Eviction, and Compaction foundation of the Deterministic AI Runtime Platform.

Retention, eviction, and compaction are not secondary cleanup jobs in this architecture.

They are part of the runtime lifecycle.

They define how execution evidence is preserved, how hot runtime state is safely removed, how large histories are compacted, how replay value is protected, and how audit data remains usable over time.

The key idea is:

> Retention, eviction, snapshotting, archiving, and compaction must be governed by policy, not by blind cleanup logic.

The platform is designed so that data lifecycle decisions can be:

- context-aware;
- policy-driven;
- safe under distributed execution;
- recorded in the Decision Ledger;
- visible through replay and audit;
- observable through metrics, logs, and traces;
- compatible with future tenant, country, sector, and compliance profiles.

---

## Purpose

The purpose of this document is to explain how the runtime manages execution data over time.

Production AI workflows generate many kinds of state and evidence:

- hot execution state;
- step state;
- claim state;
- retry state;
- queue state;
- worker state;
- runtime instance state;
- replay reports;
- decision ledger events;
- traces;
- metrics;
- payload references;
- retained execution summaries;
- snapshots;
- archives;
- compacted histories.

This data cannot live forever in hot state.

But it also cannot be deleted blindly.

A production AI runtime must preserve enough evidence for:

- replay;
- audit;
- diagnostics;
- support;
- incident investigation;
- policy review;
- operational visibility;
- future compliance-support direction.

At the same time, it must control:

- memory pressure;
- Redis hot-state growth;
- durable storage growth;
- old payload accumulation;
- stale claims;
- completed coordination records;
- expired runtime metadata;
- replay archive size.

Retention, eviction, snapshotting, and compaction solve this problem.

---

## Current Foundation

The platform already includes an important foundation around retention, eviction, and compaction.

This foundation is connected to:

- deterministic execution state;
- hot state;
- durable history;
- Redis coordination direction;
- MongoDB durable history direction;
- replay and audit;
- decision ledger;
- policy engine;
- policy-by-context model;
- runtime control;
- distributed workers;
- shared queue;
- runtime instances;
- observability;
- multi-tenant readiness;
- banking and financial-services readiness.

The roadmap is not to invent data lifecycle management later.

The roadmap is to harden, expose, test, document, and productize the existing foundation.

---

## Core Principle

The core principle is:

```text
Active execution state must remain safe.
Replay and audit evidence must be preserved.
Hot state must be evicted when it is no longer needed.
Large histories must be compacted when policy allows it.
Snapshots must preserve enough evidence before cleanup.
All lifecycle decisions must be governed by policy.
```

This is the difference between runtime lifecycle management and a basic cleanup task.

---

## Retention, Eviction, Compaction, Snapshotting

These terms are related but not identical.

| Concept | Meaning |
|---|---|
| Retention | Defines what should be preserved and for how long. |
| Eviction | Removes data from hot or fast-access state when it is safe. |
| Compaction | Reduces the size of retained history while preserving audit/replay value. |
| Snapshot | Captures an execution state or evidence bundle before cleanup, replay, archive, or compaction. |
| Archive | Stores retained evidence outside hot state for longer-term access. |
| Policy | Defines when retention, eviction, compaction, snapshotting, and archive operations are allowed. |

The runtime should treat all of these as lifecycle decisions.

---

# 1. Retention

Retention defines what the platform keeps.

Retention can apply to:

- execution records;
- final execution status;
- step state summaries;
- replay reports;
- decision ledger events;
- audit reports direction;
- trace history;
- payload references;
- archived payloads;
- retained execution snapshots;
- compacted histories;
- runtime diagnostics.

Retention answers:

- What must be kept?
- How long should it be kept?
- Which tenant or project owns it?
- Which workflow or pipeline produced it?
- Which replay report depends on it?
- Which audit requirement needs it?
- Which policy controls it?
- Should it be encrypted later?
- Can it be exported?
- Can it be compacted?

Retention is a runtime governance concern.

It is not only a storage setting.

---

## Retention Policy

Retention should be defined by policy.

A retention policy can decide:

- retain execution state for a duration;
- retain replay reports for a duration;
- retain decision ledger events for a duration;
- retain traces for a duration;
- retain payload references but not raw payloads;
- archive before eviction;
- compact before archive;
- export before purge direction;
- encrypt archive direction;
- deny deletion because audit period is active;
- allow deletion after policy expiry.

Retention policy can depend on context.

---

## Retention Context

Retention decisions can be based on:

- tenant;
- project;
- environment;
- pipeline;
- pipeline version;
- execution;
- run;
- step;
- user or actor direction;
- RBAC context;
- provider;
- model;
- tool;
- operation;
- data sensitivity;
- replay requirement;
- audit requirement;
- country or sector profile direction;
- compliance profile direction;
- storage pressure;
- execution status;
- finalization status.

This is why retention must be policy-driven.

A development workflow and a banking workflow should not necessarily have the same retention behavior.

---

# 2. Hot State Eviction

Hot state eviction removes data from fast runtime state when it is safe.

Hot state may include:

- active execution state;
- step coordination state;
- claim state;
- retry coordination state;
- shared queue metadata;
- local queue metadata;
- runtime instance heartbeat;
- worker activity;
- temporary execution metadata;
- temporary admission records;
- transient locks or leases.

Redis-style hot state is useful for fast coordination.

But hot state should not grow forever.

The runtime must eventually evict data that is no longer needed for active execution.

---

## What Can Be Evicted

Potential eviction targets include:

- completed execution hot state;
- finalized execution coordination records;
- expired claims;
- stale claim records;
- old retry coordination records;
- completed queue metadata;
- expired runtime instance heartbeat records;
- old worker activity records;
- local cache entries;
- transient admission records;
- temporary dispatch metadata.

Eviction should happen only when safe.

---

## What Must Not Be Evicted Too Early

The runtime should avoid evicting data when:

- execution is still running;
- execution is paused;
- execution is waiting for input;
- a step is still claimed;
- a claim is still valid;
- retry is pending;
- finalization is not complete;
- replay report has not been generated;
- durable history has not been persisted;
- decision ledger events have not been written;
- archive or snapshot has not been created when policy requires it;
- status or version does not match expected state.

This is why eviction must be state-aware and policy-driven.

---

## Eviction Safety Model

Safe eviction should check:

- execution status;
- finalization status;
- step status;
- claim status;
- claim expiry;
- retry state;
- queue state;
- replay metadata status;
- durable persistence status;
- decision ledger write status;
- expected state version;
- policy result;
- correlation identifiers.

The runtime should prefer guarded eviction over blind deletion.

A good eviction model should answer:

> Is it safe to remove this hot state now?

If the answer is unclear, eviction should be skipped and recorded.

---

# 3. Automatic Snapshot Mechanism

Automatic snapshots are a critical part of safe retention and compaction.

Before removing, archiving, or compacting important execution data, the runtime should be able to create a snapshot of the relevant execution evidence.

A snapshot can preserve enough information for replay, audit, diagnostics, and future inspection.

The key principle is:

> Evict hot state only after the runtime has preserved the required execution evidence.

---

## Snapshot Purpose

Snapshots can support:

- replay after hot-state eviction;
- audit after compaction;
- diagnostics after execution completion;
- retained execution summaries;
- archive creation;
- issue investigation;
- durable history preservation;
- recovery direction;
- compliance-support direction.

Snapshots help separate active runtime coordination from long-term execution evidence.

```text
Hot state = active runtime coordination
Snapshot = preserved execution evidence
Archive / Durable history = long-term audit and replay support
```

---

## Snapshot Triggers

Snapshots can be triggered automatically by runtime lifecycle events.

Examples:

- execution completed;
- execution failed;
- execution cancelled;
- execution finalized;
- retention policy evaluated;
- hot-state eviction requested;
- compaction requested;
- archive requested;
- replay report generated;
- audit report requested direction;
- policy requires snapshot before cleanup;
- tenant retention profile requires durable evidence;
- storage pressure requires compaction.

Snapshot triggers should be policy-aware.

Not every execution needs the same snapshot depth.

---

## Snapshot Content

A snapshot can include:

- ExecutionId;
- RunId;
- CorrelationId;
- final execution status;
- finalization reason;
- pipeline/workflow reference;
- pipeline version direction;
- step summary;
- step statuses;
- retry summary;
- cancellation summary;
- policy decision summary;
- claim ownership summary;
- worker identity summary;
- runtime instance identity summary;
- decision ledger references;
- replay report reference;
- retained payload references;
- trace references;
- metrics summary direction;
- retention policy result;
- compaction policy result;
- archive reference;
- integrity/fingerprint metadata direction.

The snapshot does not always need to store full payloads.

It should preserve enough evidence according to policy.

---

## Snapshot Depth

Snapshots can have different levels.

| Snapshot Level | Purpose |
|---|---|
| Minimal Snapshot | Preserve final status, execution identity, step summary, ledger references. |
| Replay Snapshot | Preserve enough information to support replay after hot-state eviction. |
| Audit Snapshot | Preserve stronger evidence for audit-sensitive workflows. |
| Diagnostic Snapshot | Preserve failure, retry, cancellation, worker, runtime instance, and trace references. |
| Compliance-Oriented Snapshot Direction | Preserve evidence required by a tenant/country/sector policy profile. |

Snapshot depth should be controlled by policy.

A low-risk development execution may only need a minimal snapshot.

A financial-services workflow may require a stronger audit snapshot.

---

## Snapshot and Replay

Replay should be able to use snapshots.

If hot state has been evicted, replay can still use:

- durable execution record;
- snapshot;
- replay report;
- decision ledger events;
- retained step summary;
- compacted history;
- archive references;
- correlation identifiers.

This is important because replay should not depend only on active hot state.

Replay must remain possible after execution lifecycle cleanup when policy requires it.

---

# 4. Compaction

Compaction reduces the size of retained execution data while preserving meaning.

Compaction is not deletion.

Compaction is controlled reduction.

It should preserve enough evidence for replay, audit, support, and diagnostics while reducing storage pressure.

---

## What Can Be Compacted

Compaction can apply to:

- large step payloads;
- intermediate outputs;
- verbose step histories;
- trace detail;
- repeated diagnostic events;
- old runtime events;
- tool output metadata;
- model response payloads direction;
- retained execution histories;
- replay evidence bundles;
- archived records.

Compaction should keep the parts that matter.

---

## What Compaction Should Preserve

Compaction should preserve:

- execution identity;
- run identity;
- pipeline identity;
- pipeline version direction;
- step identity;
- step status;
- final status;
- status history summary;
- retry summary;
- cancellation summary;
- policy decision references;
- decision ledger references;
- worker identity;
- runtime instance identity;
- correlation identifiers;
- replay report reference;
- archive reference;
- retained payload reference;
- integrity/fingerprint metadata direction.

The purpose is to preserve explainability while reducing size.

---

## Compaction Policy

Compaction should be controlled by policy.

A compaction policy can decide:

- compact after execution completion;
- compact after a delay;
- compact only after replay report exists;
- compact only after audit snapshot exists;
- compact only if execution finalized;
- compact only if no active claim exists;
- compact only for low-risk workflows;
- keep full history for sensitive workflows;
- keep full ledger but compact payloads;
- compact traces but keep decision events;
- archive before compaction;
- encrypt archive before compaction direction.

Compaction should never happen blindly.

---

## Compaction Safety

Compaction should be skipped when:

- execution is active;
- execution is not finalized;
- replay report is missing;
- decision ledger references are missing;
- snapshot policy has not been satisfied;
- required archive does not exist;
- state version mismatch is detected;
- expected status mismatch is detected;
- policy denies compaction;
- audit hold is active direction;
- tenant retention profile requires full history.

Skipped compaction should be recorded.

This makes lifecycle behavior explainable.

---

# 5. Archive Direction

Archive direction defines how retained execution evidence can be moved out of hot or primary operational storage.

Archives can store:

- replay reports;
- audit snapshots;
- compacted histories;
- payload references;
- archived payloads direction;
- execution summaries;
- ledger references;
- trace references;
- export bundles direction.

Archive is important because not all retained data must remain in hot state or primary query paths.

---

## Archive Policy

Archive behavior should be defined by policy.

Policies can decide:

- whether archive is required;
- when archive should happen;
- what snapshot level is required;
- whether payloads should be included;
- whether payloads should be redacted;
- whether archive should be encrypted direction;
- whether archive should be tenant-scoped;
- whether archive should be country/sector scoped;
- whether audit export is required before purge direction.

Archive should be part of the runtime lifecycle.

---

# 6. Policy-Driven Lifecycle Management

Retention, eviction, snapshotting, compaction, and archive behavior should all be governed by policy.

This is the most important part of the design.

The runtime should not contain one hardcoded cleanup behavior for every workflow.

Instead:

```text
Runtime Context
  -> Policy Engine
      -> Retention / Eviction / Compaction / Snapshot Policy
          -> Lifecycle Decision
              -> Decision Ledger
                  -> Replay / Audit / Observability
```

This makes lifecycle management:

- adaptable;
- explainable;
- auditable;
- tenant-aware;
- sector-aware direction;
- safe under distributed execution.

---

## Policy Types

Lifecycle policies can include:

- retention policy;
- eviction policy;
- snapshot policy;
- compaction policy;
- archive policy;
- purge policy direction;
- payload policy;
- replay preservation policy;
- audit hold policy direction;
- tenant policy;
- country/sector policy direction.

Each policy can produce a structured decision.

---

## Policy Outcomes

Lifecycle policy outcomes can include:

- retain;
- evict;
- skip eviction;
- compact;
- skip compaction;
- snapshot required;
- snapshot completed;
- archive required;
- archive completed;
- purge allowed direction;
- purge denied direction;
- encryption required direction;
- redaction required direction;
- audit hold active direction;
- retry lifecycle operation later.

These outcomes should be recorded in the Decision Ledger.

---

# 7. Decision Ledger Integration

Lifecycle decisions should be recorded in the Decision Ledger.

Examples of ledger events:

```text
retention.policy_evaluated
retention.record_retained
retention.snapshot_required
retention.snapshot_created
retention.archive_required
retention.archive_created
retention.completed
retention.failed

eviction.policy_evaluated
eviction.hot_state_removed
eviction.stale_claim_removed
eviction.skipped_execution_active
eviction.skipped_claim_active
eviction.skipped_snapshot_missing
eviction.failed
eviction.completed

compaction.policy_evaluated
compaction.started
compaction.completed
compaction.skipped_execution_active
compaction.skipped_replay_missing
compaction.skipped_policy_denied
compaction.failed

archive.policy_evaluated
archive.created
archive.skipped
archive.failed
```

These events make lifecycle management auditable.

A user should be able to answer:

- why was data retained?
- why was hot state evicted?
- why was compaction allowed?
- why was compaction skipped?
- was a snapshot created first?
- was replay value preserved?
- did policy require archive?
- which tenant or pipeline policy controlled the decision?

---

# 8. Observability Integration

Retention, eviction, snapshotting, and compaction should be observable.

Runtime signals can include:

- retention evaluations;
- retention failures;
- snapshot count;
- snapshot failure rate;
- eviction count;
- skipped eviction count;
- stale claim cleanup count;
- compaction count;
- skipped compaction count;
- archive count;
- archive failure rate;
- retained data size direction;
- hot-state size direction;
- compaction ratio direction;
- replay availability after compaction;
- policy denial count;
- lifecycle operation latency.

These signals should be available through:

- logs;
- metrics;
- traces;
- Decision Ledger;
- MCP diagnostics;
- dashboard views;
- future observability exports.

This is important for production operations.

---

# 9. Replay and Audit Integration

Replay and audit depend on lifecycle decisions.

Replay should understand whether:

- hot state is still available;
- hot state was evicted;
- snapshot exists;
- replay report exists;
- retained history exists;
- compacted history is being used;
- archive reference exists;
- payloads were redacted;
- payloads were not retained by policy;
- ledger references are still available.

Replay reports should be transparent.

If replay uses compacted history, the report should say so.

If payloads were removed by policy, the report should say so.

If snapshot data was used instead of hot state, the report should say so.

This makes replay honest and audit-friendly.

---

# 10. Distributed Execution Safety

Retention, eviction, and compaction must be safe under distributed execution.

A distributed runtime can include:

- multiple runtime instances;
- multiple workers;
- shared queue;
- local queues;
- active claims;
- retry windows;
- remote runtime providers;
- provider-based dispatch;
- runtime-instance-only hosts.

Lifecycle management must avoid interfering with active execution.

---

## Distributed Safety Checks

Before eviction or compaction, the runtime should check:

- is execution finalized?
- are any steps running?
- are any claims active?
- are any retry windows pending?
- is cancellation still propagating?
- is finalization complete?
- has the runtime instance released ownership?
- has the worker completed or failed?
- has durable history been written?
- has ledger data been recorded?
- has snapshot policy been satisfied?
- has expected version/status matched?

This protects the runtime from race conditions.

---

## Atomic Coordination Direction

Some lifecycle operations may require atomic coordination.

Redis/Lua-style atomic operations are a natural direction for:

- checking expected status;
- verifying claim expiry;
- removing hot state safely;
- marking compaction state;
- preventing duplicate lifecycle operations;
- avoiding cleanup while execution is active;
- protecting state transitions under concurrency.

The goal is not only cleanup.

The goal is safe cleanup under distributed execution.

---

# 11. Multi-Tenant Retention

Retention should evolve toward tenant-aware behavior.

Tenant-aware retention can control:

- execution retention duration;
- replay report retention;
- ledger retention;
- trace retention;
- payload retention;
- archive requirement;
- encrypted archive direction;
- compaction rules;
- eviction timing;
- purge direction;
- audit hold direction.

Different tenants may require different lifecycle behavior.

This is important for SaaS, self-hosted enterprise, dedicated clusters, and regulated environments.

---

# 12. Banking and Financial Services Direction

Banking and financial-services environments need strong lifecycle control.

The platform should support technical controls such as:

- policy-defined retention;
- policy-defined snapshots;
- replay preservation;
- audit evidence preservation;
- ledger retention;
- access-controlled replay direction;
- encrypted archive direction;
- audit hold direction;
- country/sector policy profiles direction;
- data residency direction;
- export before purge direction.

The platform does not claim automatic legal compliance.

The correct positioning is:

> The platform provides technical controls that can support compliance implementation per customer, sector, and jurisdiction.

Retention, eviction, compaction, and snapshot policies are part of those technical controls.

---

# 13. MCP Control and Diagnostics

MCP should expose lifecycle diagnostics.

MCP tools can support:

- inspect retention policy result;
- inspect snapshot status;
- inspect archive status;
- inspect hot-state eviction status;
- inspect compaction status;
- inspect lifecycle decision ledger events;
- inspect replay availability after compaction;
- inspect skipped lifecycle operations;
- inspect retention diagnostics;
- inspect stale claim cleanup diagnostics.

This makes lifecycle management operable.

A runtime should not clean itself silently.

---

# 14. Dashboard Views

The dashboard should expose lifecycle activity.

Dashboard views can include:

- retained executions;
- snapshot status;
- archive status;
- hot-state eviction activity;
- stale claim cleanup activity;
- compaction activity;
- skipped eviction;
- skipped compaction;
- lifecycle policy decisions;
- replay after compaction status;
- retained data size direction;
- storage pressure direction;
- encrypted archive direction.

This makes retention, eviction, and compaction visible to operators.

---

# 15. Security and Encryption Hardening

Lifecycle data can be sensitive.

Snapshots, replay reports, ledger events, payload references, and archives may contain sensitive metadata or payloads.

Future hardening should include:

- metadata/payload separation;
- redaction direction;
- encrypted snapshots direction;
- encrypted retention archives direction;
- encrypted replay bundles direction;
- encrypted ledger payload direction;
- access-controlled decryption direction;
- tenant-aware encryption boundary direction;
- purpose-specific keys direction;
- key rotation direction;
- audit of sensitive access direction.

The policy engine should be able to require encryption or redaction depending on context.

---

# 16. Lifecycle Model Summary

The lifecycle model can be summarized as:

```text
Execution running
  -> runtime state updated
  -> decision ledger records decisions
  -> execution finalized
  -> retention policy evaluated
  -> snapshot policy evaluated
  -> snapshot created when required
  -> replay report preserved
  -> archive policy evaluated
  -> archive created when required
  -> compaction policy evaluated
  -> history compacted when safe
  -> eviction policy evaluated
  -> hot state evicted when safe
  -> lifecycle decisions remain auditable
```

This is the desired execution data lifecycle.

---

# 17. What Should Not Happen

The runtime should avoid:

- blind deletion;
- hot-state eviction before finalization;
- compaction without replay metadata;
- archive without policy decision;
- purge without audit check;
- deleting data required for replay;
- losing ledger references;
- losing correlation identifiers;
- cleaning active claims;
- compacting active executions;
- hiding lifecycle decisions from operators.

This is why lifecycle management belongs inside runtime governance.

---

# Current Foundation Summary

| Area | Status |
|---|---|
| Retention foundation | Foundation exists |
| Eviction foundation | Foundation exists |
| Compaction foundation | Foundation exists |
| Hot-state cleanup direction | Foundation exists |
| Stale claim cleanup direction | Foundation exists |
| Snapshot mechanism direction | Foundation exists / active direction |
| Replay-aware retention | Foundation exists / active direction |
| Ledger-aware lifecycle decisions | Foundation exists |
| Policy-driven lifecycle rules | Foundation exists |
| Policy-by-context retention model | Foundation exists |
| Distributed lifecycle safety direction | Foundation exists / active direction |
| Archive direction | Foundation exists / active direction |
| MCP lifecycle diagnostics | Productization target |
| Dashboard lifecycle visibility | Productization target |
| Tenant-aware retention policies | Planned hardening direction |
| Encrypted retention archives | Planned hardening direction |
| Country/sector lifecycle profiles | Productization target |

---

# Productization Roadmap

## Milestone 1 — Document Lifecycle Policies

Improve documentation for:

- retention policy;
- eviction policy;
- snapshot policy;
- compaction policy;
- archive policy;
- purge policy direction;
- policy-by-context examples.

## Milestone 2 — Expose Lifecycle Decisions

Expose through:

- Decision Ledger;
- MCP diagnostics;
- replay reports;
- dashboard views;
- logs;
- metrics;
- traces.

## Milestone 3 — Strengthen Snapshot and Replay Integration

Improve:

- automatic snapshot before cleanup;
- replay after hot-state eviction;
- replay after compaction;
- snapshot metadata;
- archive references;
- retained-history summaries.

## Milestone 4 — Harden Distributed Safety

Improve:

- expected status checks;
- version checks;
- claim checks;
- finalization checks;
- atomic cleanup operations direction;
- no cleanup while execution is active;
- tests for concurrent cleanup and execution.

## Milestone 5 — Add Tenant and Compliance Profile Direction

Prepare:

- tenant-specific retention;
- project/pipeline retention profiles;
- banking/financial-services lifecycle policies;
- country/sector profile direction;
- encrypted archive direction;
- audit hold direction.

---

# Planned Improvements

The retention, eviction, and compaction layer should continue improving in the following areas:

- lifecycle policy documentation;
- policy-by-context examples;
- automatic snapshot visibility;
- replay after eviction;
- replay after compaction;
- retention decision ledger events;
- eviction decision ledger events;
- compaction decision ledger events;
- archive decision ledger events;
- MCP lifecycle diagnostics;
- dashboard lifecycle views;
- lifecycle metrics;
- distributed cleanup safety;
- encrypted retention archives;
- tenant-aware lifecycle policies;
- country/sector lifecycle profiles.

These are productization and hardening steps.

They build on the existing runtime lifecycle foundation.

---

# Final Statement

Retention, eviction, and compaction are core runtime lifecycle capabilities.

They are not simple background cleanup jobs.

They define how the platform preserves execution evidence, protects replay and audit value, controls hot-state growth, compacts historical data, creates snapshots, archives retained history, and applies governance rules over execution data.

The strongest principle is:

> All lifecycle rules should be defined by policy.

This means retention, snapshotting, eviction, compaction, archiving, encryption direction, redaction direction, and purge direction can evolve by context without rewriting the deterministic runtime core.

That is why this layer is critical for enterprise readiness, managed hosting, multi-tenant execution, banking and financial-services technical controls, and long-running production AI workflows.
