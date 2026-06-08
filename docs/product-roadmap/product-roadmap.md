# Product Roadmap

<!-- Updated by ChatGPT: original content preserved; missing roadmap pillars and alignment notes were added without rewriting the document from scratch. -->

## Deterministic AI Runtime Platform

This document describes the public product roadmap for the Deterministic AI Runtime Platform.

The roadmap is intentionally realistic and must be understood in the context of a project currently built and maintained by a single developer. Several important foundations already exist, including deterministic execution, replay and audit, decision ledger, configuration-driven runtime behavior, context-driven execution, policy-driven runtime decisions, provider-driven architecture, retention/eviction/compaction, automatic snapshot direction, execution control and state lifecycle, distributed workers, MCP control-plane direction, observability and runtime telemetry direction, testing and reliability direction, developer experience/API/SDK/CLI direction, security and encryption hardening direction, memory/context/reasoning lifecycle direction, and multi-instance runtime direction.

Transforming these foundations into a complete product requires progressive work across runtime hardening, execution lifecycle visibility, APIs, SDK/CLI, MCP control, dashboard, pipeline builder, observability, deployment, retention lifecycle diagnostics, memory/context governance, security hardening, multi-tenant readiness, managed hosting direction, and enterprise-oriented controls.

Because the project is currently driven by one developer, the roadmap should be read as a product direction and prioritization guide, not as a fixed delivery promise.

---

## Product Goal

The goal is to evolve the project from a strong deterministic AI runtime foundation into a complete platform for production AI workflow execution.

The platform aims to help teams:

- define AI workflows;
- execute workflows deterministically;
- control running executions;
- replay and audit execution history;
- inspect runtime decisions;
- govern execution through configuration, context, policy, and providers;
- manage retention, eviction, compaction, snapshots, and archive direction safely;
- manage memory, context, and runtime reasoning evidence safely;
- observe runtime behavior through telemetry, logs, metrics, traces, ledger events, and diagnostics;
- expose developer-friendly APIs, SDK direction, CLI direction, examples, and local setup;
- coordinate workers and runtime instances;
- prepare distributed execution;
- prepare multi-tenant and managed hosting models;
- support technical controls required by audit-sensitive environments.

The long-term product direction is:

> Build AI workflows visually.  
> Run them deterministically.  
> Govern execution through configuration, context, policy, and providers.  
> Control them through MCP.  
> Replay and audit every execution.  
> Record runtime decisions in a ledger.  
> Observe runtime behavior in real time.  
> Manage execution history through retention, eviction, compaction, snapshots, and archive direction.  
> Manage memory and context as scoped, policy-driven, decay-aware runtime capabilities.  
> Scale execution through runtime instances and workers.

---

## Roadmap Philosophy

The roadmap follows four principles.

## 1. Stabilize Before Expanding

The runtime foundation is the most important asset.

Before adding too much UI or commercial packaging, the core execution layer must remain reliable, testable, observable, and maintainable.

## 2. Productize Existing Strengths

The project already has strong foundations around deterministic execution, replay, audit, decision ledger, configuration-driven behavior, context-driven execution, policy-driven runtime decisions, provider-driven architecture, retention/eviction/compaction, automatic snapshot direction, execution lifecycle control, workers, queues, MCP direction, observability/runtime telemetry, testing reliability, memory/context direction, security hardening direction, and distributed runtime direction.

The roadmap should expose those strengths through better documentation, APIs, demos, dashboard views, and product modules.

## 3. Build Incrementally

The platform should evolve through realistic milestones.

The goal is not to build everything at once. The goal is to deliver visible progress step by step:

- clearer docs;
- stronger runtime;
- better control APIs;
- MCP tools;
- basic dashboard;
- pipeline builder foundation;
- observability exports;
- lifecycle diagnostics;
- developer quickstart / API / SDK / CLI direction;
- Kubernetes-style demo;
- multi-tenant readiness;
- managed hosting direction.

## 4. Avoid Overclaiming

The platform should not claim automatic enterprise readiness, banking compliance, or universal production maturity too early.

The correct positioning is:

> The platform is designed to provide the technical foundations required for reliable, auditable, replayable, controllable, governable, observable, lifecycle-aware, memory/context-aware, and scalable AI workflow execution.

---

# Roadmap Overview

The roadmap is organized into several product tracks.

| Track | Purpose |
|---|---|
| Runtime Foundation | Harden deterministic execution, state, workers, retry, replay, and control. |
| Execution Control and State Lifecycle | Harden run lifecycle, execution lifecycle, step lifecycle, pause, resume, cancel, retry, waiting-for-input, claims, and finalization. |
| Replay and Audit | Strengthen replay reports, audit timeline, deterministic validation, memory/context evidence, lifecycle replay, retention-aware replay, and diagnostics. |
| Decision Ledger | Improve structured runtime decision history and future compliance-oriented audit support. |
| Policy Engine and Runtime Governance | Expose and harden configuration-driven, context-driven, policy-driven, and provider-driven execution foundations. |
| Retention, Eviction, Compaction, and Snapshotting | Harden execution data lifecycle management, automatic snapshots, hot-state cleanup, stale claim cleanup, archive direction, and safe policy-driven lifecycle decisions. |
| Memory, Context, and Reasoning Lifecycle | Define scoped memory, context injection, memory decay, runtime reasoning evidence, replay memory evidence, and policy-driven memory governance. |
| MCP Control Plane | Expose runtime operations through MCP tools and future MCP control UI. |
| Enterprise Dashboard | Make executions, runs, queues, workers, ledger, replay, policy, memory/context, lifecycle, security, and observability visible. |
| Pipeline Builder | Allow visual design of deterministic AI workflows. |
| Observability and Runtime Telemetry | Export logs, metrics, traces, ledger events, provider/transport telemetry, lifecycle telemetry, memory/context telemetry, and runtime health signals. |
| Developer Experience / API / SDK / CLI | Improve quickstart, examples, API packaging, SDK direction, CLI direction, diagnostics, and error model. |
| Testing and Reliability | Prove runtime guarantees through unit, integration, distributed, provider, MCP, replay, lifecycle, and chaos-style tests. |
| Security and Encryption Hardening | Improve RBAC-aware access, replay/ledger/MCP/dashboard security, redaction, payload protection, encrypted retention archive direction, and access-control direction. |
| Distributed Runtime | Improve shared queue, runtime instances, worker capacity, provider/transport behavior, and Kubernetes-style execution. |
| Multi-Tenant Readiness | Prepare isolation by tenant, project, pipeline, execution, storage, memory/context, telemetry, retention policy, and runtime capacity. |
| Managed Hosting | Prepare a long-term hosting model based on runtime instances and workers. |
| Regulated-Market Controls | Prepare technical controls for audit-sensitive and financial-service environments. |

---

# Current Foundation

The project already contains the foundation for several major product capabilities.

## Existing Foundation Areas

| Area | Current Direction |
|---|---|
| Deterministic runtime execution | Foundation exists |
| DAG-based workflow execution | Foundation exists |
| Execution state management | Foundation exists |
| Step lifecycle tracking | Foundation exists |
| Distributed worker model | Foundation exists |
| Runtime instance direction | Foundation exists |
| Queue and run management | Foundation exists |
| Shared queue / multi-instance direction | Foundation exists |
| Replay and audit | Foundation exists |
| Decision ledger | Foundation exists |
| Configuration-driven runtime behavior | Foundation exists |
| Context-driven execution | Foundation exists |
| Policy-driven runtime decisions | Foundation exists |
| Policy engine | Foundation exists |
| Provider-driven architecture | Foundation exists |
| Policy-driven concurrency and throttling | Foundation exists / active direction |
| Retention | Foundation exists |
| Eviction | Foundation exists |
| Compaction | Foundation exists |
| Automatic snapshot mechanism direction | Foundation exists / active direction |
| Safe retention decisions | Foundation exists / active direction |
| Execution control and state lifecycle | Foundation exists |
| MCP server / control-plane direction | Foundation exists |
| Redis coordination direction | Foundation exists |
| MongoDB durable audit/history direction | Foundation exists |
| Observability direction | Foundation exists |
| Runtime telemetry direction | Foundation exists / active direction |
| Provider-based hosting direction | Foundation exists |
| Runtime provider and transport model | Foundation exists |
| Testing and reliability strategy | Foundation exists / active direction |
| Developer experience / API / SDK / CLI | Productization target |
| Security and encryption hardening | Planned hardening direction |
| Memory, context, and reasoning lifecycle | Productization target |
| Memory decay policy direction | Productization target |
| Kubernetes-ready architecture direction | Direction exists |
| Dashboard product layer | Planned |
| Pipeline builder product layer | Planned |
| Multi-tenant readiness | Direction exists |
| Managed hosting model | Direction exists |
| Banking/finance technical readiness | Direction exists |
| Encrypted retention archives | Planned hardening direction |
| Encryption hardening | Planned hardening direction |

---

# Product Tracks

## 1. Runtime Foundation

The runtime foundation is the core of the platform.

It includes:

- execution lifecycle;
- DAG execution;
- step lifecycle;
- worker coordination;
- state transitions;
- retry direction;
- recovery direction;
- cancellation direction;
- finalization direction;
- replay support;
- audit support;
- observability events;
- configuration-driven behavior;
- context-driven execution;
- policy-driven decisions;
- retention/eviction/compaction/snapshot decisions;
- memory/context evidence direction;
- security/access-control decision direction.

## Roadmap Direction

The runtime should continue to improve in these areas:

- stronger execution state invariants;
- clearer step lifecycle semantics;
- more deterministic retry handling;
- better failure convergence;
- safer distributed coordination;
- stronger policy-driven concurrency and throttling visibility;
- stronger retention, eviction, compaction, and automatic snapshot safety;
- stronger execution lifecycle diagnostics;
- stronger memory/context evidence direction;
- stronger tests around concurrency;
- better runtime diagnostics;
- clearer extension points;
- cleaner SDK/API surface.

## Expected Outcome

A stable runtime core that can support product layers such as dashboard, pipeline builder, MCP control, replay, lifecycle diagnostics, memory/context governance, and managed hosting.

---

## 2. Execution Control and State Lifecycle

Execution control makes AI workflows operable.

The runtime should not behave like a fire-and-forget system.

It should expose and harden:

- run lifecycle;
- execution lifecycle;
- step lifecycle;
- pause;
- resume;
- cancel;
- retry;
- waiting-for-input direction;
- claim ownership;
- worker ownership;
- runtime instance ownership;
- finalization;
- lifecycle Decision Ledger events;
- lifecycle replay evidence;
- lifecycle diagnostics through MCP, API, dashboard, and telemetry.

## Roadmap Direction

Execution control and state lifecycle should progressively improve:

- pause/resume/cancel consistency;
- queued run cancellation;
- running execution cancellation;
- cancellation propagation direction;
- waiting-for-retry visibility;
- waiting-for-input direction;
- finalization diagnostics;
- claim ownership visibility;
- lifecycle replay timeline;
- MCP lifecycle tools;
- dashboard lifecycle views;
- lifecycle telemetry.

## Expected Outcome

AI workflows become controllable production workloads instead of fire-and-forget executions.


---

## 2. Replay and Audit

Replay and audit are major differentiators of the platform.

The platform should allow users to inspect previous executions and understand what happened.

## Roadmap Direction

Replay and audit should evolve toward:

- audit-only replay;
- replay reports;
- replay issue detection;
- deterministic validation;
- replay timeline reconstruction;
- comparison between execution state and replay output;
- policy decision replay;
- retry/cancellation replay;
- lifecycle replay;
- retention-aware replay;
- snapshot/archive reference transparency;
- memory/context evidence direction;
- exportable audit report direction;
- replay integration with dashboard;
- replay access through MCP;
- replay access through API.

## Expected Outcome

Users can investigate executions after completion or failure, validate runtime behavior, and understand execution history without manually searching raw logs.

---

## 3. Decision Ledger

The decision ledger records meaningful runtime decisions.

It should explain not only what happened, but why the runtime acted the way it did.

## Roadmap Direction

The decision ledger should progressively cover:

- execution lifecycle decisions;
- run lifecycle decisions;
- queue decisions;
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
- snapshot/archive decisions;
- memory/context decisions;
- security/access decisions;
- diagnostic decisions.

Future hardening may include:

- stronger event taxonomy;
- better correlation identifiers;
- dashboard viewer;
- export direction;
- integrity/fingerprint direction;
- encrypted payload direction;
- access-control direction.

## Expected Outcome

A structured audit history that supports debugging, replay, observability, and future enterprise review.

---

## 4. Policy Engine and Runtime Governance

The runtime already includes foundations for configuration-driven, context-driven, policy-driven, and provider-driven execution.

This product track is about making those foundations clearer, more visible, more testable, and easier to operate.

## Roadmap Direction

Policy engine and runtime governance should progressively expose and harden:

- configuration-driven runtime behavior;
- context-driven execution scope;
- policy-driven execution decisions;
- policy engine outcomes;
- provider-driven architecture;
- policy-driven concurrency;
- throttling decisions;
- tenant/project/pipeline-aware execution context;
- RBAC-aware execution direction;
- provider/model/tool access decisions;
- memory access and memory decay policy decisions;
- retention/snapshot/compaction policy decisions;
- security/access policy decisions;
- policy decision events in the decision ledger;
- policy visibility through MCP, API, dashboard, and observability.

## Expected Outcome

The runtime becomes more governable, explainable, and enterprise-ready because important decisions are evaluated through configuration, context, and policy rather than hidden hardcoded behavior.

---

## 5. Retention, Eviction, Compaction, and Snapshotting

The runtime already includes retention, eviction, compaction, and snapshot foundations.

This product track is about hardening execution data lifecycle management.

Retention, eviction, compaction, and snapshotting are not only cleanup jobs. They are part of replay, audit, storage cost control, hot-state safety, and future compliance-support direction.

## Roadmap Direction

Retention, eviction, compaction, and snapshotting should progressively improve:

- retention policy model;
- automatic snapshot mechanism direction;
- snapshot policy model direction;
- hot-state eviction safety;
- stale claim cleanup;
- completed execution cleanup;
- compaction of large histories;
- preservation of replay metadata;
- preservation of ledger references;
- archive direction;
- retained-history direction;
- retention decision events;
- eviction decision events;
- compaction decision events;
- snapshot/archive decision events;
- MCP lifecycle diagnostics;
- dashboard visibility;
- observability metrics;
- future encrypted retention archives.

## Expected Outcome

The runtime can manage execution history safely while preserving replay value, audit value, and operational visibility.

---

## 6. Memory, Context, and Reasoning Lifecycle

Memory and context are critical for production AI execution.

The platform should not treat memory as an invisible global state.

## Roadmap Direction

Memory, context, and reasoning lifecycle should progressively define:

- memory sources;
- scoped context injection;
- RBAC-aware memory access;
- memory freshness;
- memory decay;
- runtime reasoning evidence;
- memory access Decision Ledger events;
- replay memory evidence;
- memory retention and compaction;
- tenant-aware memory boundaries;
- memory policy rules;
- redaction and sensitive memory direction.

The platform should not claim to capture hidden model chain-of-thought.

It should capture runtime reasoning evidence: context used, memory source, policy decision, retrieved data reference, tool usage, branch decision, retry decision, and replay evidence.

## Expected Outcome

AI workflows become more explainable and safer because context and memory usage are scoped, policy-driven, replayable, decay-aware, and auditable.


---

## 6. MCP Control Plane

The MCP control plane is a key direction for making the runtime operationally controllable.

It can expose runtime operations as structured tools.

## Roadmap Direction

MCP tools should progressively support:

- submitting runs;
- inspecting runs;
- inspecting executions;
- replaying executions;
- pausing executions;
- resuming executions;
- cancelling executions;
- inspecting queues;
- inspecting runtime instances;
- inspecting decision ledger events;
- inspecting policy decisions;
- inspecting retention lifecycle decisions;
- inspecting memory/context decisions direction;
- running diagnostics;
- exposing runtime health.

## Expected Outcome

The runtime can be operated through an AI-compatible control plane, enabling controlled execution management and diagnostics.

---

## 7. Enterprise Dashboard

The dashboard is the visibility layer of the product.

The dashboard should make runtime execution understandable for developers, operators, and enterprise stakeholders.

## Roadmap Direction

The dashboard can be built progressively.

### Dashboard V1

- execution list;
- execution status;
- run status;
- queue status;
- runtime instance list;
- worker status;
- basic replay access;
- basic ledger viewer;
- basic policy decision view;
- basic lifecycle diagnostics;
- basic retention/eviction/compaction/snapshot view;
- basic memory/context evidence direction.

### Dashboard V2

- execution timeline;
- step-level details;
- replay comparison;
- retry history;
- cancellation history;
- queue pressure;
- worker utilization;
- runtime instance health;
- trace/ledger correlation;
- policy timeline;
- memory/context evidence;
- retention lifecycle activity.

### Dashboard V3

- observability panels;
- advanced filters;
- tenant/project views;
- audit report export;
- failure investigation views;
- cost/usage direction;
- compliance-oriented views;
- security/access-control views;
- memory/context governance views.

## Expected Outcome

Users can see what the runtime is doing instead of treating AI workflows as a black box.

---

## 8. Visual Pipeline Builder

The pipeline builder is the usability layer of the product.

It should allow teams to design AI workflows visually instead of only through code.

## Roadmap Direction

The pipeline builder can evolve in stages.

### Builder V1

- visual DAG editor;
- step creation;
- step dependency configuration;
- basic step settings;
- model/provider configuration direction;
- tool step direction;
- validation before execution.

### Builder V2

- input/output mapping;
- retry policy configuration;
- timeout configuration;
- concurrency policy configuration;
- policy configuration;
- memory/context configuration direction;
- retention profile configuration direction;
- human-in-the-loop steps;
- workflow versioning;
- test-run mode.

### Builder V3

- reusable components;
- templates;
- environment-specific configuration;
- approval workflow direction;
- rollback direction;
- comparison between pipeline versions;
- deployment workflow direction.

## Expected Outcome

The runtime becomes usable as a product platform, not only as an engineering library.

---

## 9. Observability and Runtime Telemetry

Observability is essential for production AI workflows.

The runtime should emit signals that explain execution behavior.

## Roadmap Direction

Observability should cover:

- structured logs;
- execution traces;
- metrics;
- queue pressure;
- worker utilization;
- runtime instance health;
- retry rate;
- failure rate;
- cancellation rate;
- replay activity;
- ledger events;
- policy decision events;
- retention/eviction/compaction/snapshot events;
- provider/transport telemetry;
- lifecycle telemetry;
- memory/context telemetry direction;
- MCP telemetry;
- correlation identifiers;
- export direction to Grafana, Kibana, OpenSearch, or SIEM-style tools.

## Expected Outcome

Runtime behavior becomes visible in real time and after execution, making operations, support, debugging, and demos much easier.

---

## 10. Developer Experience, API, SDK, and CLI

Developer experience turns the runtime into an adoptable platform.

## Roadmap Direction

Developer experience should progressively improve:

- quickstart;
- local setup;
- sample workflows;
- API documentation;
- RunId / ExecutionId clarity;
- SDK direction;
- CLI direction;
- MCP tool examples;
- diagnostics;
- error model;
- configuration examples;
- policy examples;
- step plugin examples;
- provider examples;
- test documentation.

## Expected Outcome

Developers can run, inspect, extend, test, and operate the platform more easily.

---

## 11. Testing and Reliability Strategy

Testing proves that the architecture works.

## Roadmap Direction

Testing and reliability should progressively improve:

- runtime invariant tests;
- DAG execution tests;
- replay/audit tests;
- Decision Ledger tests;
- policy engine tests;
- RBAC/scoped context tests;
- execution control tests;
- retry/recovery tests;
- worker/claim safety tests;
- shared queue tests;
- runtime instance tests;
- provider/transport tests;
- MCP integration tests;
- retention/eviction/compaction/snapshot tests;
- observability tests;
- chaos-style tests;
- performance/load direction.

## Expected Outcome

The platform can prove its runtime guarantees under concurrency, failure, replay, policy, lifecycle management, and distributed execution.

---

## 12. Security and Encryption Hardening

Security is a runtime lifecycle concern.

## Roadmap Direction

Security and encryption hardening should progressively improve:

- RBAC-aware access;
- ARN-inspired resource scopes;
- policy-driven security;
- access-controlled replay;
- access-controlled Decision Ledger;
- MCP access control;
- dashboard access control;
- provider/transport security;
- sensitive payload handling;
- redaction;
- secret reference direction;
- encrypted ledger payload direction;
- encrypted retention archive direction;
- audit of sensitive access.

## Expected Outcome

The platform becomes more credible for enterprise and audit-sensitive environments without overclaiming automatic compliance.


---

## 10. Distributed Runtime and Kubernetes Direction

The platform is designed to evolve toward distributed execution.

Runtime instances and workers can map naturally to containers or Kubernetes pods.

## Roadmap Direction

The distributed runtime should continue improving:

- shared queue dispatch;
- runtime instance registration;
- runtime instance heartbeat;
- worker capacity visibility;
- local queue visibility;
- shared run assignment;
- capacity-aware dispatch direction;
- policy-driven concurrency and throttling visibility;
- cancellation across runtime instances;
- replay across distributed executions;
- structured logs for distributed events;
- provider/transport telemetry;
- runtime provider and transport hardening;
- Kubernetes-style demo.

## Expected Outcome

The platform can demonstrate multi-instance, multi-worker AI workflow execution with visible scheduling, dispatch, execution, replay, and observability.

---

## 11. Multi-Tenant Readiness

Multi-tenant readiness is a long-term product foundation.

The platform should evolve toward isolating execution and operational data by tenant, project, and pipeline.

## Roadmap Direction

Multi-tenant readiness should progressively include:

- tenant identity;
- project identity;
- pipeline identity;
- execution isolation;
- run isolation;
- ledger isolation;
- replay data isolation;
- trace and metric separation;
- memory/context isolation direction;
- retention policy separation;
- security/access policy separation;
- runtime capacity allocation;
- quota direction;
- usage metering direction;
- RBAC direction.

## Expected Outcome

The platform can support self-hosted enterprise deployment, managed SaaS, dedicated clusters, and regulated customer environments.

---

## 12. Managed Hosting Model

The runtime architecture naturally supports a managed hosting model.

Because the runtime is built around runtime instances and workers, hosting can be modeled around execution capacity.

## Roadmap Direction

Managed hosting can evolve around:

- runtime instances;
- workers per instance;
- queue capacity;
- execution volume;
- replay/audit retention;
- storage usage;
- observability level;
- dedicated environment requirements;
- support level.

Deployment models can include:

- self-hosted deployment;
- managed cloud hosting;
- dedicated enterprise cluster;
- multi-tenant SaaS;
- private cloud deployment.

## Expected Outcome

The platform can evolve beyond a library into a managed AI execution platform.

---

## 13. Banking and Financial Services Readiness

The platform should be designed to support technical controls needed by audit-sensitive and regulated environments.

It does not claim automatic legal compliance.

The correct public positioning is:

> The platform is designed to provide technical controls that can support compliance implementation per customer, sector, and jurisdiction.

## Roadmap Direction

Relevant technical controls include:

- deterministic execution history;
- replayable workflows;
- decision ledger;
- audit reports;
- runtime control;
- policy decision foundation;
- policy engine direction;
- RBAC direction;
- tenant isolation direction;
- retention, eviction, compaction, and snapshot foundation;
- memory/context governance direction;
- encrypted ledger direction;
- encrypted retention archive direction;
- observability export;
- data residency direction;
- configurable compliance profile direction.

## Expected Outcome

The platform becomes more suitable for enterprise, financial-services, insurance, fintech, and other audit-sensitive use cases.

---

# Roadmap Timeline

The timeline below is intentionally conservative.

It is not a promise that every item will be completed in a fixed period. It describes a realistic order of execution.

Actual timing depends on available development capacity, product priorities, feedback, infrastructure needs, and the level of production hardening required. With a single developer, the roadmap must remain staged and scope-controlled.

---

## Phase 1 — Documentation, Stabilization, and Foundation Clarity

## Target Range

Short term.

Depending on available time and team capacity, this phase may take several weeks to a few months.

## Goals

- make the product understandable;
- document what already exists;
- document the current foundation;
- stabilize the runtime core;
- clarify public roadmap;
- improve examples and developer onboarding;
- strengthen tests around existing foundation;
- prepare demo scenarios.

## Deliverables

- public roadmap documentation;
- current foundation documentation;
- what-already-exists documentation;
- improvement backlog;
- clearer product positioning;
- improved README links;
- better demo workflow;
- stronger test visibility.

## Success Criteria

- a technical visitor can understand the product direction quickly;
- the repo shows that the project is not idea-stage only;
- core runtime concepts are documented clearly;
- next product steps are visible and credible.

---

## Phase 2 — Runtime, Governance, and API Productization

## Target Range

Short to mid term.

This phase may take a few months depending on scope and available development capacity.

## Goals

- improve runtime extension points;
- expose configuration-driven, context-driven, policy-driven, and provider-driven foundations more clearly;
- improve policy engine visibility;
- clean public APIs;
- strengthen Replay API;
- strengthen Execution Control API;
- improve queue/run management surface;
- improve MCP tool coverage;
- improve developer experience;
- prepare local Docker-based usage.

## Deliverables

- clearer SDK/API surface;
- Replay API direction;
- Execution Control API direction;
- MCP tools for runtime operations;
- sample workflows;
- local demo setup;
- improved diagnostics;
- more reliable control operations;
- clearer runtime governance surface.

## Success Criteria

- developers can run and inspect workflows more easily;
- runtime operations can be accessed through APIs and MCP;
- replay and control operations are easier to demonstrate;
- integration points are clearer.

---

## Phase 3 — Enterprise Dashboard V1

## Target Range

Mid term.

The first dashboard version should focus on visibility before advanced features.

## Goals

- expose execution visibility;
- expose run and queue visibility;
- expose runtime instance visibility;
- expose worker visibility;
- expose decision ledger events;
- expose policy decision events;
- expose retention/eviction/compaction/snapshot activity;
- expose memory/context events direction;
- expose lifecycle diagnostics;
- expose replay/audit views;
- expose basic observability data.

## Deliverables

- execution list and details;
- run/queue dashboard;
- runtime instance dashboard;
- worker activity view;
- decision ledger viewer;
- replay/audit viewer;
- basic metrics/logs panels.

## Success Criteria

- users can see what the runtime is doing;
- execution failures can be investigated visually;
- queues and runtime instances are visible;
- replay and ledger become product features, not hidden internals.

---

## Phase 4 — Pipeline Builder V1

## Target Range

Mid term to longer term.

The builder should start simple and evolve progressively.

## Goals

- create a visual DAG editor;
- allow step creation and configuration;
- connect pipeline definitions to runtime execution;
- validate workflows before execution;
- prepare versioning foundation;
- prepare memory/context configuration direction.

## Deliverables

- visual workflow canvas;
- step configuration panel;
- basic model/provider configuration;
- tool step direction;
- dependency configuration;
- validation before run;
- test-run direction;
- versioning foundation.

## Success Criteria

- users can define workflows visually;
- workflows can be executed by the deterministic runtime;
- the builder becomes the entry point for non-core contributors and future product users.

---

## Phase 5 — Observability and Distributed Runtime Demo

## Target Range

Mid term to longer term.

This phase depends on runtime stability and control-plane readiness.

## Goals

- improve structured logs;
- expose metrics;
- expose traces;
- correlate logs, traces, ledger, execution, run, worker, runtime instance, provider, transport, policy, lifecycle, and memory/context data;
- prepare Kubernetes-style demo;
- improve shared queue and runtime instance visibility.

## Deliverables

- structured runtime logs;
- metrics export direction;
- trace export direction;
- decision ledger export direction;
- runtime instance monitoring;
- worker utilization visibility;
- queue pressure visibility;
- multi-instance demo;
- Kubernetes-style deployment direction.

## Success Criteria

- distributed execution can be demonstrated clearly;
- runtime behavior can be observed across instances and workers;
- logs/metrics/traces can support production-style investigation.

---

## Phase 6 — Security, Retention, Eviction, Compaction, Snapshotting, Memory, and Regulated-Market Hardening

## Target Range

Longer term.

This should be approached carefully because security and compliance-oriented features require strong design and review.

## Goals

- improve retention model;
- improve automatic snapshot direction;
- improve eviction safety;
- improve compaction safety;
- improve hot-state cleanup visibility;
- define encrypted ledger direction;
- define encrypted retention archive direction;
- separate metadata and sensitive payloads;
- prepare tenant-level security boundary direction;
- prepare purpose-specific encryption key direction;
- improve access-control direction;
- prepare compliance profile direction;
- define memory/context governance direction;
- define memory decay direction.

## Deliverables

- retention policy model direction;
- eviction safety direction;
- compaction safety direction;
- retention decision event direction;
- encrypted payload envelope direction;
- ledger hardening direction;
- replay bundle protection direction;
- RBAC direction for sensitive audit access;
- audit of sensitive access direction;
- compliance profile foundation direction.

## Success Criteria

- the platform can explain how audit data should be protected;
- regulated-market technical controls become more credible;
- ledger, retention, eviction, compaction, snapshotting, replay, memory/context, and observability are aligned with security needs.

---

## Phase 7 — Multi-Tenant and Managed Hosting Readiness

## Target Range

Longer term.

This phase should come after the runtime, dashboard, observability, and control-plane foundations are stable enough.

## Goals

- define tenant/project/pipeline model;
- isolate executions, runs, ledger, replay, traces, metrics, memory/context, and retention lifecycle data by tenant;
- define runtime capacity allocation direction;
- prepare usage metering;
- prepare managed hosting deployment model;
- prepare dedicated cluster direction.

## Deliverables

- tenant model;
- project model;
- pipeline ownership model;
- tenant-aware ledger direction;
- tenant-aware replay direction;
- tenant-aware observability direction;
- quota direction;
- usage metering direction;
- managed hosting architecture direction.

## Success Criteria

- the platform can support both self-hosted and managed deployment models;
- tenant isolation becomes a product-level concept;
- managed hosting by runtime instance and worker capacity becomes realistic.

---

# 6-Month Execution Direction

The following is a possible short-term execution direction. It is intentionally described as a direction, not a guarantee.

| Period | Focus | Practical Outcome |
|---|---|---|
| Month 1 | Documentation and core stabilization | Clear roadmap, current foundation docs, tests visibility, demo preparation |
| Month 2 | Runtime governance, API, and MCP control-plane improvement | Replay/control APIs, lifecycle diagnostics, policy visibility, MCP tools, better local demo |
| Month 3 | Dashboard foundation | First execution/run/queue/runtime/policy/retention/lifecycle/memory views |
| Month 4 | Pipeline builder foundation | Basic visual DAG, step configuration, policy and memory/context configuration direction |
| Month 5 | Observability, retention, security, and DX hardening direction | Logs, metrics, traces, ledger/retention/eviction/compaction/snapshot hardening, quickstart/API/CLI direction |
| Month 6 | Distributed demo and pilot readiness direction | Multi-instance demo, Kubernetes-style direction, product documentation |

This schedule is only realistic if scope remains controlled and priorities stay focused.

Since the project is currently maintained by one developer, this timeline should be treated as staged direction rather than a strict calendar commitment.

---

# 12–24 Month Direction

The longer-term roadmap should remain flexible.

## 6–12 Months — Product Maturity

Focus:

- stable runtime;
- clear SDK/API surface;
- dashboard V1;
- pipeline builder V1;
- MCP control interface;
- replay and audit reports;
- public demos;
- stronger docs;
- early user feedback;
- lifecycle diagnostics;
- memory/context model direction.

## 12–18 Months — Enterprise Readiness

Focus:

- multi-tenant direction;
- RBAC direction;
- stronger observability;
- retention, eviction, compaction, snapshotting, and encryption hardening;
- memory/context governance direction;
- policy engine and compliance profile foundation;
- deployment templates;
- managed hosting architecture;
- enterprise pilot support.

## 18–24 Months — Commercial Scale Direction

Focus:

- managed hosting;
- dedicated enterprise clusters;
- advanced dashboard;
- advanced pipeline builder;
- billing/usage metering direction;
- financial-services technical controls;
- memory/context and retention policy profiles;
- partner ecosystem direction;
- production support direction.

This longer-term roadmap should evolve based on real users, demos, pilots, and product feedback.

---

# Prioritization

Not everything should be built at once.

The recommended priority order is:

1. Runtime stability.
2. Public documentation.
3. Execution control and state lifecycle visibility.
4. Replay and audit clarity.
5. Policy engine and runtime governance visibility.
6. MCP control-plane usability.
7. Retention, eviction, compaction, and snapshot hardening.
8. Decision Ledger visibility.
9. Developer experience / API / SDK / CLI direction.
10. Dashboard visibility.
11. Observability and runtime telemetry export.
12. Testing and reliability visibility.
13. Pipeline builder foundation.
14. Distributed runtime demo.
15. Security and encryption hardening.
16. Memory, context, and reasoning lifecycle.
17. Multi-tenant readiness.
18. Managed hosting model.
19. Regulated-market technical controls.

This order protects the core foundation while progressively adding product value.

---

# What Should Not Be Rushed

Some areas should not be rushed because they require strong design:

- multi-tenant isolation;
- encryption and key management;
- compliance profiles;
- full tenant-aware policy governance;
- billing/metering;
- enterprise RBAC;
- tenant-aware memory isolation;
- complete memory decay engine;
- managed hosting;
- dedicated clusters;
- regulated-market claims;
- production SLAs.

These areas should be designed carefully after the runtime and observability foundations are stable.

---

# Product Maturity Levels

The platform can be understood through maturity levels.

## Level 1 — Runtime Foundation

The system can execute deterministic workflows and track state.

## Level 2 — Replay and Audit Foundation

The system can inspect and replay executions.

## Level 3 — Runtime Governance

The system exposes configuration-driven, context-driven, policy-driven, and provider-driven execution foundations.

## Level 4 — Control Plane

The system can pause, resume, cancel, inspect, and diagnose runtime activity.

## Level 5 — Dashboard Visibility

The system exposes runtime behavior visually.

## Level 6 — Developer Experience and Runtime Tooling

The system exposes clear APIs, SDK direction, CLI direction, quickstart, examples, diagnostics, and local setup.

## Level 7 — Visual Workflow Product

Users can design and run workflows visually.

## Level 8 — Distributed Runtime

The system runs across runtime instances and workers.

## Level 9 — Memory and Context Governance

The system manages scoped memory, context injection, memory decay, replay memory evidence, and tenant-aware memory boundaries.

## Level 10 — Enterprise Readiness

The system supports tenant-aware execution, RBAC direction, observability export, retention/eviction/compaction/snapshot hardening, security hardening, and operational controls.

## Level 11 — Managed Platform

The system supports managed hosting, dedicated clusters, usage metering, and enterprise support models.

The current roadmap moves progressively through these levels.

---

# Expected Product Outcome

The roadmap aims to turn the platform into a complete execution infrastructure for production AI workflows.

The expected product outcome is a platform where users can:

- define AI workflows;
- execute them deterministically;
- inspect execution state;
- replay execution history;
- audit runtime decisions;
- govern execution through configuration, context, policy, and providers;
- control running workflows;
- observe distributed runtime behavior;
- scale through workers and runtime instances;
- manage execution history through retention, eviction, compaction, snapshots, and archive direction;
- manage memory and context safely;
- design workflows visually;
- prepare tenant-aware deployment;
- prepare managed hosting;
- prepare technical controls for audit-sensitive environments.

---

# Final Statement

The product roadmap is ambitious but should be executed progressively.

The platform already has important foundations around deterministic execution, replay, audit, decision ledger, configuration-driven behavior, context-driven execution, policy-driven decisions, policy engine foundation, provider-driven architecture, retention/eviction/compaction, automatic snapshot direction, execution control and lifecycle direction, workers, queues, MCP direction, distributed runtime direction, observability/runtime telemetry direction, testing reliability direction, security hardening direction, developer experience/API/SDK/CLI direction, and memory/context direction.

The next stage is productization.

That means making the platform:

- easier to understand;
- easier to run;
- easier to inspect;
- easier to control;
- easier to demonstrate;
- easier to extend;
- easier to operate in distributed environments;
- easier to govern through configuration, context, policy, and providers;
- easier to manage over time through retention, eviction, compaction, snapshots, and archive direction;
- easier to reason about through scoped memory, context, and runtime reasoning evidence.

The long-term goal is to make AI workflow execution reliable enough for production, transparent enough for audit, controllable enough for operations, and scalable enough for enterprise adoption.
