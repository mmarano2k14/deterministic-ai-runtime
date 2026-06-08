# Product Roadmap Index

## Deterministic AI Runtime Platform

This document is the public roadmap index for the Deterministic AI Runtime Platform.

It is the main entry point for the product roadmap documentation. It connects the vision, current foundation, runtime architecture, product modules, extension points, enterprise direction, and long-term roadmap.

The platform is positioned as:

> A deterministic distributed AI execution infrastructure for production AI workflows.

More specifically:

> A deterministic LLMOps execution runtime that makes AI workflows controllable, replayable, auditable, governable, observable, and scalable across runtime instances and workers.

This roadmap is public GitHub documentation. It focuses on technical direction, product positioning, current foundation, planned improvements, and long-term architecture goals.

It does not include private partnership terms, private commercial negotiations, pricing details, investor discussions, or legal deal structure.

---

## Purpose of This Roadmap

The goal of this roadmap is to make the project understandable, credible, and easy to navigate.

The project is evolving from a strong engineering foundation into a complete product for production-grade AI workflow execution.

The roadmap helps explain:

- what the platform is;
- why it exists;
- what already exists today;
- which foundations are already present;
- what still needs hardening and productization;
- how the runtime is designed;
- how MCP, replay, ledger, policy, providers, transport, dashboard, pipeline builder, multi-tenancy, and managed hosting fit together;
- how the platform can evolve toward enterprise and audit-sensitive environments without overclaiming automatic compliance.

The roadmap should be read as a product direction and architecture guide, not as a fixed delivery promise.

---

## Long-Term Product Direction

The long-term product direction is:

> Build AI workflows visually.  
> Run them deterministically.  
> Govern execution through configuration, context, policy, and providers.  
> Control execution through MCP.  
> Replay and audit every execution.  
> Record runtime decisions in a ledger.  
> Observe distributed runtime behavior in real time.  
> Manage execution history through retention, eviction, and compaction.  
> Scale execution through runtime instances and workers.  
> Support self-hosted, dedicated, and managed deployment models.  
> Provide technical controls for audit-sensitive and financial-services environments.

The ultimate goal is to become a reference foundation for deterministic distributed AI execution.

---

## Product Positioning

The platform can be positioned as:

> A deterministic LLMOps execution platform for production AI workflows.

Alternative positioning:

> The execution layer for reliable, auditable, replayable, governable, and scalable AI agents.

Enterprise positioning:

> A runtime platform that helps organizations control, audit, replay, observe, govern, and scale AI workflow execution in production.

Infrastructure positioning:

> Deterministic distributed AI execution infrastructure.

Developer positioning:

> Build AI workflows, run them deterministically, replay every execution, and control the runtime through MCP.

---

## Why This Platform Exists

Most AI agent and LLM workflow systems are easy to demonstrate but difficult to operate safely in production.

A demo can call a model, invoke a tool, chain prompts, or run an agent.

Production is different.

Enterprise AI execution requires answers to questions such as:

- What exactly happened during an AI execution?
- Which workflow or pipeline version was used?
- Which model, provider, or tool was called?
- Which worker executed each step?
- Which runtime instance hosted the work?
- Which policy allowed or denied an action?
- Which RBAC scope was used?
- Why was a retry scheduled?
- Can the execution be paused, resumed, or cancelled?
- Can a failed workflow be inspected or recovered?
- Can duplicate execution be prevented?
- Can an execution be replayed and audited?
- Can runtime behavior be observed across multiple instances and workers?
- Can execution history be retained, compacted, archived, and protected?
- Can policies change by tenant, project, pipeline, provider, model, tool, operation, country, or sector context?
- Can the system evolve toward tenant isolation and regulated environments?

This project is designed around those production questions from the beginning.

---

## What Makes This Platform Different

Many LLMOps tools observe what happened after execution.

This platform is designed to control the execution path from the beginning.

```text
Traditional LLMOps:
Prompt -> Model -> Trace -> Evaluate -> Monitor

Deterministic AI Runtime:
Configure -> Contextualize -> Evaluate Policy -> Admit -> Queue
-> Dispatch -> Claim -> Execute -> Record -> Replay -> Audit
-> Retain -> Observe -> Control -> Scale
```

The key difference is:

> Most tools observe AI execution after the fact.  
> This platform is designed to control, record, replay, audit, govern, and scale the execution itself.

---

## Current Foundation Summary

The project already contains important foundations.

This roadmap should not present the platform as idea-stage only.

| Foundation | Status |
|---|---|
| Deterministic runtime execution | Foundation exists |
| DAG-based workflow execution | Foundation exists |
| Execution state management | Foundation exists |
| Step lifecycle tracking | Foundation exists |
| Replay and audit | Foundation exists |
| Decision ledger | Foundation exists |
| Policy engine | Foundation exists |
| Pluggable policy-by-context model | Foundation exists |
| Configuration-driven runtime behavior | Foundation exists |
| Context-driven execution | Foundation exists |
| Provider-driven architecture | Foundation exists |
| RBAC-aware execution context | Foundation exists |
| ARN-inspired resource scoping | Foundation exists / active direction |
| Runtime control through pause/resume/cancel | Foundation exists |
| MCP control-plane foundation | Foundation exists |
| Multiple runtime instances | Foundation exists / active direction |
| Multiple workers | Foundation exists |
| Shared queue and local queue model | Foundation exists |
| Admission control direction | Foundation exists / active direction |
| Policy-driven concurrency and throttling | Foundation exists |
| Provider-based runtime hosting | Foundation exists |
| Dynamic runtime provider direction | Foundation exists |
| HTTP runtime provider direction | Foundation exists |
| Runtime-instance-only mode | Foundation exists |
| Runtime provider and transport model | Foundation exists |
| Retention, eviction, and compaction | Foundation exists |
| Observability direction | Foundation exists |
| Multi-tenant readiness foundation | Foundation exists |
| Managed hosting by runtime instance and worker capacity | Active product direction |
| Banking and financial-services technical controls | Active product direction |

The next stage is to harden, expose, document, demonstrate, secure, and productize these foundations.

---

## Product Direction

The platform is evolving toward a complete LLMOps execution infrastructure composed of:

- deterministic runtime execution;
- DAG-based AI workflow orchestration;
- execution state management;
- step lifecycle tracking;
- distributed worker execution;
- runtime instances;
- local queues;
- shared queue direction;
- admission control;
- capacity-aware dispatch;
- replay and audit capabilities;
- decision ledger;
- configuration-driven runtime behavior;
- context-driven execution;
- policy-driven runtime decisions;
- pluggable policy engine;
- RBAC-aware execution context;
- ARN-inspired resource scoping;
- provider-driven architecture;
- pluggable runtime architecture;
- runtime provider and transport model;
- provider-based runtime hosting;
- policy-driven concurrency and throttling;
- retention, eviction, and compaction foundation;
- execution control through pause, resume, and cancel;
- MCP control-plane interface;
- enterprise dashboard;
- visual pipeline builder;
- distributed runtime instances and workers;
- multi-tenant readiness;
- managed hosting by runtime instance and worker capacity;
- banking and financial-services-oriented technical controls;
- observability through logs, metrics, traces, decision history, replay, and runtime health.

---

# Roadmap Documents

## Folder Structure

```text
docs/
  product-roadmap/
    index.md
    product-vision.md
    product-roadmap.md
    current-foundation.md
    what-already-exists.md
    improvement-backlog.md
    llmops-positioning.md
    deterministic-runtime-engine.md
    pluggable-runtime-architecture.md
    runtime-provider-and-transport-model.md
    policy-engine-and-governance.md
    replay-audit-layer.md
    decision-ledger.md
    enterprise-dashboard.md
    pipeline-builder.md
    mcp-control-interface.md
    multi-tenant-readiness.md
    managed-hosting-model.md
    banking-financial-services-readiness.md
    roadmap-6-months.md
    roadmap-12-24-months.md
```

---

## Roadmap Navigation

| Order | Document | File | Purpose |
|---:|---|---|---|
| 1 | Product Roadmap Index | `index.md` | Main entry point for the product roadmap. |
| 2 | Product Vision | `product-vision.md` | Explains the long-term product direction and why the platform exists. |
| 3 | Product Roadmap Overview | `product-roadmap.md` | Summarizes the complete product roadmap and product modules. |
| 4 | Current Foundation | `current-foundation.md` | Shows the existing technical foundation and architecture direction. |
| 5 | What Already Exists Today | `what-already-exists.md` | Lists current capabilities already implemented or in progress. |
| 6 | Improvement Backlog | `improvement-backlog.md` | Describes planned improvements required for productization. |
| 7 | LLMOps Positioning | `llmops-positioning.md` | Positions the platform as an execution-first LLMOps runtime. |
| 8 | Deterministic Runtime Engine | `deterministic-runtime-engine.md` | Explains the core runtime execution model. |
| 9 | Pluggable Runtime Architecture | `pluggable-runtime-architecture.md` | Explains pluggable steps, policies, providers, storage, ledger, replay, observability, retention, hosting, and MCP extension points. |
| 10 | Runtime Provider and Transport Model | `runtime-provider-and-transport-model.md` | Explains provider-based hosting, dynamic runtime providers, HTTP provider, runtime-instance-only mode, shared queue dispatch, and future transport options. |
| 11 | Policy Engine and Governance | `policy-engine-and-governance.md` | Explains the pluggable policy engine, policy-by-context model, RBAC scope, ARN-inspired resources, governance, and policy decision recording. |
| 12 | Replay and Audit Layer | `replay-audit-layer.md` | Explains replay, audit, diagnostics, validation, replay reports, and retained execution evidence. |
| 13 | Decision Ledger | `decision-ledger.md` | Explains the decision ledger foundation for structured runtime decision history. |
| 14 | Enterprise Dashboard | `enterprise-dashboard.md` | Describes the dashboard for executions, runs, queues, workers, ledger, replay, policy, retention, diagnostics, and observability. |
| 15 | Pipeline Builder | `pipeline-builder.md` | Describes the visual workflow builder for deterministic AI pipelines. |
| 16 | MCP Control Interface | `mcp-control-interface.md` | Describes MCP-based execution control, replay, diagnostics, queue, runtime instance, ledger, and policy inspection. |
| 17 | Multi-Tenant Readiness | `multi-tenant-readiness.md` | Describes tenant, project, pipeline, execution, RBAC, ledger, replay, retention, and runtime capacity boundaries. |
| 18 | Managed Hosting Model | `managed-hosting-model.md` | Describes the hosting model based on runtime instances, workers, shared queue, admission, providers, transport, and MCP. |
| 19 | Banking and Financial Services Readiness | `banking-financial-services-readiness.md` | Describes technical controls for audit-sensitive and regulated environments without claiming automatic compliance. |
| 20 | 6-Month Roadmap | `roadmap-6-months.md` | Describes the short-term execution plan for a single-developer project moving toward productization and demos. |
| 21 | 12–24 Month Roadmap | `roadmap-12-24-months.md` | Describes the longer-term direction toward product maturity, enterprise readiness, and commercial scale. |

---

# Roadmap Section Index

This table maps the roadmap topics to the public documentation files.

| # | Section | GitHub File | Purpose |
|---:|---|---|---|
| 1 | Executive Summary | `product-roadmap.md` | Short public summary of the product direction. |
| 2 | Product Vision | `product-vision.md` | Long-term vision of deterministic distributed AI execution infrastructure. |
| 3 | Market Problem | `llmops-positioning.md` | Explains why production AI workflows need more than prompt execution, tracing, or monitoring. |
| 4 | Current Foundation | `current-foundation.md` | Shows the existing technical base. |
| 5 | What Already Exists Today | `what-already-exists.md` | Proves the project is not only an idea. |
| 6 | Deterministic Runtime Foundation | `deterministic-runtime-engine.md` | Runtime foundation for controlled execution. |
| 7 | DAG Execution Engine | `deterministic-runtime-engine.md` | DAG-based step execution, workflow structure, and execution lifecycle. |
| 8 | Execution State Management | `current-foundation.md` | Durable state, hot state direction, execution lifecycle, and state transitions. |
| 9 | Distributed Worker Model | `current-foundation.md` | Runtime instances, workers, worker identity, and execution capacity. |
| 10 | Shared Queue Direction | `current-foundation.md` | Shared queue above local queues for multi-instance execution. |
| 11 | Runtime Instance / Worker Model | `managed-hosting-model.md` | Technical and commercial basis for instance/worker hosting model. |
| 12 | Redis / MongoDB Direction | `current-foundation.md` | Redis coordination/hot state direction and MongoDB ledger/audit/storage direction. |
| 13 | Replay / Audit Foundation | `replay-audit-layer.md` | Replay, audit, validation, diagnostics, reports, retained evidence, and reproducibility direction. |
| 14 | Decision Ledger Foundation | `decision-ledger.md` | Ledger as audit foundation for runtime decisions. |
| 15 | Configuration-Driven Runtime | `policy-engine-and-governance.md` | Runtime behavior controlled through options, host modes, providers, queue settings, worker settings, replay settings, retention settings, and observability settings. |
| 16 | Context-Driven Execution | `policy-engine-and-governance.md` | Runtime behavior scoped by tenant, project, pipeline, execution, run, step, user, RBAC, provider, model, operation, runtime instance, worker, and correlation context. |
| 17 | Policy-Driven Runtime | `policy-engine-and-governance.md` | Runtime decisions evaluated through policies instead of hardcoded orchestration behavior. |
| 18 | Policy Engine Foundation | `policy-engine-and-governance.md` | Pluggable policy engine with policy-by-context model. |
| 19 | RBAC-Aware Execution Context | `multi-tenant-readiness.md` | Scoped execution context for safe AI workflow execution. |
| 20 | ARN-Inspired Resource Scoping | `multi-tenant-readiness.md` | Resource identity model for tenant/project/pipeline/execution/tool/model/operation scopes. |
| 21 | Policy-Driven Concurrency and Throttling | `policy-engine-and-governance.md` | Controlled admission, limits, throttling, and concurrency protection across scopes. |
| 22 | Provider-Driven Architecture | `pluggable-runtime-architecture.md` | Hosting, storage, hot state, shared queue, registry, ledger, replay, observability, model/provider execution, and retention behind abstractions. |
| 23 | Pluggable Runtime Architecture | `pluggable-runtime-architecture.md` | Pluggable steps, tools, policies, providers, storage, ledger, replay, observability, retention, hosting, and MCP tools. |
| 24 | Pluggable Steps and Tools | `pluggable-runtime-architecture.md` | Add new workflow capabilities and governed tool operations without rewriting the deterministic core. |
| 25 | Runtime Provider and Transport Model | `runtime-provider-and-transport-model.md` | Local provider, HTTP provider, dynamic runtime providers, runtime-instance-only mode, and future transport options. |
| 26 | Pluggable Transport Between Instances | `runtime-provider-and-transport-model.md` | Transport abstraction for local, HTTP, future gRPC, message bus, NATS, RabbitMQ, or cloud queue direction. |
| 27 | Runtime-Instance-Only Mode | `runtime-provider-and-transport-model.md` | Runtime host mode for remote runtime instances, containers, pods, and managed execution units. |
| 28 | Admission Control | `managed-hosting-model.md` | Policy-aware run admission, queue admission, capacity evaluation, throttling, and dispatch decisions. |
| 29 | Retention, Eviction, and Compaction Foundation | `current-foundation.md` | Retention, hot-state eviction, compaction, archive direction, cleanup safety, and retention decision events. |
| 30 | MCP Server / Control Plane Direction | `mcp-control-interface.md` | MCP as control surface for run, replay, pause, resume, cancel, diagnostics, ledger, policy, and runtime inspection. |
| 31 | Pause / Resume / Cancel Direction | `mcp-control-interface.md` | Execution control through API, MCP, and future UI. |
| 32 | Observability Direction | `current-foundation.md` | Logs, metrics, traces, ledger, replay, runtime health, Grafana/Kibana/OpenSearch direction. |
| 33 | Tests and Reliability Work | `current-foundation.md` | Integration tests, replay tests, shared queue tests, provider tests, MCP tests, and reliability work. |
| 34 | Improvement Backlog | `improvement-backlog.md` | Planned improvements required for product maturity. |
| 35 | Enterprise Dashboard UI | `enterprise-dashboard.md` | Dashboard for executions, runs, queues, instances, workers, ledger, replay, policy, retention, and metrics. |
| 36 | Visual Pipeline Builder | `pipeline-builder.md` | Visual DAG builder, workflow design, step configuration, validation, versioning, and test-run mode. |
| 37 | User / Tenant Management | `multi-tenant-readiness.md` | Users, tenants, projects, roles, isolation, quotas direction, RBAC, and scoped resources. |
| 38 | Stronger SDK / API Packaging | `improvement-backlog.md` | External developer usability and product packaging. |
| 39 | Hosted Demo | `roadmap-6-months.md` | Hosted demo for technical visitors, partners, pilots, and product validation. |
| 40 | Documentation Improvements | `improvement-backlog.md` | README, docs, examples, onboarding, architecture pages. |
| 41 | Kubernetes Deployment Demo | `roadmap-6-months.md` | Multi-instance, multi-worker, shared queue, provider-based transport, and observability demo. |
| 42 | Encryption Hardening | `banking-financial-services-readiness.md` | High-level encryption and audit-data protection direction. |
| 43 | Compliance Profile Foundation | `banking-financial-services-readiness.md` | High-level direction for future configurable country/sector policy profiles. |
| 44 | Production Observability Export | `improvement-backlog.md` | Export direction toward Grafana, Kibana, OpenSearch, and SIEM-style tools. |
| 45 | CLI / SDK Improvements | `improvement-backlog.md` | CLI/admin tooling, SDKs, and developer experience. |
| 46 | Cloud Deployment Templates | `improvement-backlog.md` | Docker, Kubernetes, Helm, cloud deployment templates. |
| 47 | Product Modules Overview | `product-roadmap.md` | Overview of runtime, replay, ledger, dashboard, builder, MCP, policy, providers, transport, and hosting. |
| 48 | Banking / Financial Services Readiness | `banking-financial-services-readiness.md` | Technical controls for audit-sensitive and regulated environments. |
| 49 | Multi-Tenant SaaS Readiness | `multi-tenant-readiness.md` | Tenants, projects, pipelines, executions, ledger, replay, metrics, quotas, RBAC, and scoped resources. |
| 50 | Execution Dashboard | `enterprise-dashboard.md` | Execution list, timeline, status, failures, duration, replay, ledger, policy, and controls. |
| 51 | Run / Queue Dashboard | `enterprise-dashboard.md` | Submitted, queued, running, completed, dispatch, queue pressure, shared/local queue. |
| 52 | Runtime Instance Dashboard | `enterprise-dashboard.md` | Instances, workers, capacity, heartbeat, local queue, load, unhealthy detection. |
| 53 | Decision Ledger Dashboard | `enterprise-dashboard.md` | Ledger viewer, decisions, policies, claims, correlation IDs, retention, and replay links. |
| 54 | Replay / Audit Dashboard | `enterprise-dashboard.md` | Replay execution, validate, inspect issues, inspect policy decisions, and export reports direction. |
| 55 | Observability Dashboard | `enterprise-dashboard.md` | Logs, metrics, traces, latency, throughput, failures, retries, queue saturation, workers, instances. |
| 56 | Visual DAG Editor | `pipeline-builder.md` | Drag/drop workflow graph and step layout. |
| 57 | Step Configuration | `pipeline-builder.md` | Model, provider, tools, input/output mapping, retry, timeout, policy, and retention configuration direction. |
| 58 | Retry / Timeout / Concurrency Policy Configuration | `pipeline-builder.md` | Policies configurable per step or pipeline. |
| 59 | Human-in-the-Loop Steps | `pipeline-builder.md` | Approval, waiting-for-input, manual review, and intervention steps. |
| 60 | Pipeline Versioning | `pipeline-builder.md` | Version history, rollback, and comparison direction. |
| 61 | Pipeline Templates | `pipeline-builder.md` | Templates for repeatable enterprise AI workflow patterns. |
| 62 | MCP Tool Explorer | `mcp-control-interface.md` | List tools, schemas, inputs, outputs, diagnostics, and operational behavior. |
| 63 | MCP Execution Control | `mcp-control-interface.md` | Submit run, replay, pause, resume, cancel, diagnostics. |
| 64 | MCP Request / Response History | `mcp-control-interface.md` | History of MCP calls and operational control-plane interactions. |
| 65 | Differentiation vs Existing LLMOps Tools | `llmops-positioning.md` | Key difference: control execution from the beginning, not only observe after execution. |
| 66 | Managed Hosting by Instance / Worker | `managed-hosting-model.md` | Commercial direction aligned with architecture: instance, worker, queue, provider, transport, retention, observability. |
| 67 | Self-Hosted Deployment | `managed-hosting-model.md` | Customer runs platform in their infrastructure. |
| 68 | Managed Cloud Hosting | `managed-hosting-model.md` | Platform provider hosts runtime capacity. |
| 69 | Dedicated Enterprise Cluster | `managed-hosting-model.md` | Isolated cluster direction for enterprise and regulated customers. |
| 70 | Multi-Tenant SaaS | `multi-tenant-readiness.md` | SaaS model with tenant isolation and shared platform operations. |
| 71 | 6-Month Roadmap | `roadmap-6-months.md` | Public short-term productization plan for a single-developer project. |
| 72 | 12–24 Month Roadmap | `roadmap-12-24-months.md` | Long-term product maturity and commercial scale path. |
| 73 | Final Product Positioning | `product-roadmap.md` | Central product message and strategic positioning. |
| 74 | Final Vision Statement | `product-vision.md` | Build visually, run deterministically, govern through policy, control through MCP, replay/audit, and scale by instances/workers. |

---

# Product Pillars

## 1. Deterministic Runtime Engine

The runtime engine is the core execution layer.

It is responsible for:

- executing AI workflows;
- managing execution state;
- coordinating workers;
- controlling step transitions;
- handling retries;
- supporting recovery direction;
- preventing unsafe duplicate execution;
- supporting replay and audit;
- exposing runtime control operations;
- supporting distributed execution direction.

---

## 2. Replay and Audit Layer

Replay and audit are strategic capabilities.

They help teams:

- inspect previous executions;
- validate runtime behavior;
- diagnose failures;
- compare replay results;
- generate audit reports direction;
- investigate production incidents;
- improve trust in AI workflow execution.

---

## 3. Decision Ledger

The Decision Ledger is the structured audit foundation.

It records important runtime decisions such as:

- execution lifecycle decisions;
- run lifecycle decisions;
- step claim decisions;
- policy decisions;
- retry decisions;
- queue and dispatch decisions;
- cancellation decisions;
- replay decisions;
- finalization decisions;
- retention decisions;
- eviction decisions;
- compaction decisions;
- archive decisions.

---

## 4. Policy Engine and Runtime Governance

The Policy Engine is a core governance foundation.

It supports:

- configuration-driven runtime behavior;
- context-driven execution;
- policy-driven decisions;
- pluggable policy-by-context model;
- RBAC-aware execution context;
- ARN-inspired resource scoping;
- policy-driven concurrency and throttling;
- policy decisions recorded in the Decision Ledger.

The runtime model can be summarized as:

```text
Configuration defines how the runtime is deployed.
Context defines what is being executed and under which scope.
Policy defines whether the operation is allowed and how it should behave.
Providers define how infrastructure responsibilities are implemented.
```

---

## 5. Pluggable Runtime Architecture

The runtime is designed around extension points.

It supports or is designed to support:

- pluggable steps;
- pluggable tools;
- pluggable policies;
- pluggable providers;
- pluggable transport;
- pluggable storage;
- pluggable ledger;
- pluggable replay;
- pluggable observability;
- pluggable retention;
- pluggable hosting modes;
- pluggable MCP tools.

This allows the deterministic core to remain stable while execution capabilities evolve.

---

## 6. Runtime Provider and Transport Model

The runtime provider and transport model allows execution to move from local execution to distributed runtime instances.

It includes:

- local runtime provider;
- HTTP runtime provider direction;
- runtime-instance-only mode;
- control plane with runtime instances;
- provider-based dispatch;
- shared queue pump;
- runtime instance registry;
- future gRPC/message-bus transport direction.

This is a key foundation for Kubernetes-style execution and managed hosting.

---

## 7. Retention, Eviction, and Compaction

Retention, eviction, and compaction are part of the runtime foundation.

They are not only cleanup concerns. They are product, storage, replay, audit, cost-control, and future compliance-support concerns.

This foundation includes direction for:

- retaining execution records;
- preserving replay reports;
- preserving decision ledger events;
- evicting expired hot state;
- cleaning stale claims;
- compacting large execution histories;
- archiving payloads;
- preserving audit value while reducing storage pressure;
- recording retention, eviction, compaction, and archive decisions;
- preparing encrypted retention archive direction.

---

## 8. MCP Control Interface

The MCP control interface exposes runtime operations through a structured control plane.

It should support:

- submit run;
- inspect execution;
- pause execution;
- resume execution;
- cancel execution;
- replay execution;
- inspect runtime instances;
- inspect queues;
- inspect decision ledger;
- inspect policy decisions;
- inspect retention decisions;
- trigger diagnostics;
- expose MCP tool schemas;
- provide operational visibility.

---

## 9. Enterprise Dashboard

The dashboard is the product visibility layer.

It should expose:

- executions;
- runs;
- queues;
- runtime instances;
- workers;
- decision ledger;
- policy decisions;
- replay reports;
- audit reports direction;
- retention/eviction/compaction activity;
- logs;
- metrics;
- traces;
- failure and retry visibility;
- queue pressure;
- runtime health.

---

## 10. Visual Pipeline Builder

The pipeline builder is the product usability layer.

It should support:

- visual DAG design;
- step configuration;
- provider/model configuration;
- tool execution steps;
- input/output mapping;
- conditional branches;
- retry policy configuration;
- timeout configuration;
- concurrency policy configuration;
- policy configuration;
- human-in-the-loop steps;
- pipeline versioning;
- pipeline templates;
- test-run mode.

---

## 11. Multi-Tenant Readiness

The platform is intended to evolve toward multi-tenant readiness.

This includes isolation of:

- tenants;
- users;
- projects;
- pipelines;
- executions;
- runs;
- replay data;
- decision ledger entries;
- policy decisions;
- metrics;
- traces;
- retention policies;
- runtime capacity;
- worker allocation;
- usage metering direction.

---

## 12. Managed Hosting Model

The long-term hosting model is aligned with the runtime architecture.

Potential deployment models include:

- self-hosted deployment;
- managed cloud hosting;
- dedicated enterprise cluster;
- multi-tenant SaaS;
- managed hosting by runtime instance and worker capacity.

This allows the product to evolve from a technical runtime into a scalable execution platform.

---

## 13. Banking and Financial Services Readiness

The platform is designed to support technical controls needed by audit-sensitive and regulated environments.

The public wording must remain careful:

> The platform does not claim automatic legal compliance. It is designed to provide technical controls that can support compliance implementation per customer, sector, and jurisdiction.

Relevant technical directions include:

- deterministic execution history;
- replayable workflows;
- Decision Ledger;
- pluggable policy engine;
- RBAC-aware execution context;
- ARN-inspired resource scopes;
- audit reports direction;
- runtime control;
- tenant isolation;
- observability export;
- retention, eviction, and compaction foundation;
- encrypted ledger and encrypted retention hardening direction;
- country/sector policy profile direction;
- data residency direction.

---

# Architecture Extension Points

The following documents describe the most important extension points.

| Extension Point | Document | Why It Matters |
|---|---|---|
| Pluggable runtime architecture | `pluggable-runtime-architecture.md` | Explains how the runtime can evolve through pluggable steps, policies, providers, transport, storage, ledger, replay, observability, retention, hosting, and MCP tools. |
| Runtime provider and transport model | `runtime-provider-and-transport-model.md` | Explains how the control plane communicates with runtime instances through local, HTTP, and future transport models. |
| Policy engine and governance | `policy-engine-and-governance.md` | Explains how policies can be created by context and recorded through the Decision Ledger. |
| MCP control interface | `mcp-control-interface.md` | Explains how runtime control is exposed through structured MCP tools. |
| Managed hosting model | `managed-hosting-model.md` | Explains how runtime instances, workers, shared queues, admission, providers, and transport become the foundation for hosting. |

---

# Current Product Status

The project is transitioning from a strong engineering foundation toward a clearer product roadmap.

The current focus is to:

- explain the existing foundation clearly;
- create public product roadmap documentation;
- organize roadmap documentation;
- prepare product modules;
- define planned improvements;
- expose pluggable runtime architecture;
- expose provider and transport model;
- expose policy engine and governance model;
- prepare dashboard and pipeline builder direction;
- strengthen MCP control-plane positioning;
- expose configuration-driven, context-driven, policy-driven, and provider-driven runtime foundations more clearly;
- expose policy-driven concurrency/throttling foundations more clearly;
- expose retention, eviction, and compaction foundations more clearly;
- prepare Kubernetes-style distributed execution demonstrations;
- improve observability exports;
- harden replay, audit, ledger, retention, eviction, compaction, access control, and encryption direction.

---

# Key Differentiator

Many LLMOps tools focus mainly on monitoring, tracing, prompt management, or evaluation.

This platform focuses on the execution layer itself.

> Most tools observe what happened after execution.  
> This platform is designed to control, record, replay, audit, govern, and scale the execution from the beginning.

This is why the platform is positioned as deterministic distributed AI execution infrastructure.

---

# Working Principle

This roadmap is intentionally iterative.

The first objective is to make the project understandable and credible.  
The second objective is to turn the current technical foundation into a complete product.  
The long-term objective is to support enterprise-grade and regulated AI workflow execution at scale.

The roadmap must remain ambitious, but realistic.

The project is currently maintained by a single developer, so the roadmap should focus on staged productization, strong demos, visible architecture, and carefully prioritized hardening.
