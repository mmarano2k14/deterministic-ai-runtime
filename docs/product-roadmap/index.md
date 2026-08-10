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
| Automatic snapshot mechanism direction | Foundation exists / active direction |
| Hot-state eviction and stale claim cleanup | Foundation exists |
| Lifecycle policy model | Foundation exists |
| Runtime telemetry and diagnostics | Foundation exists |
| Execution control and state lifecycle | Foundation exists |
| Testing and reliability strategy | Foundation exists |
| Developer experience and API packaging | Productization target |
| Security and encryption hardening | Planned hardening direction |
| Memory, context, and reasoning lifecycle | Productization target |
| Memory decay policy direction | Productization target |

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
- observability through logs, metrics, traces, decision history, replay, and runtime health;
- automatic snapshot mechanism direction;
- hot-state eviction and stale claim cleanup;
- policy-driven lifecycle rules;
- runtime telemetry and diagnostics;
- execution control and state lifecycle;
- testing and reliability strategy;
- developer API, SDK, and CLI direction;
- security and encryption hardening;
- memory, context, and reasoning lifecycle;
- memory decay and freshness direction.

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
    runtime-pool-roadmap.md
    policy-engine-and-governance.md
    replay-audit-layer.md
    decision-ledger.md
    retention-eviction-compaction.md
    observability-and-runtime-telemetry.md
    execution-control-and-state-lifecycle.md
    testing-and-reliability-strategy.md
    developer-experience-api-sdk-cli.md
    security-and-encryption-hardening.md
    memory-context-reasoning-lifecycle.md
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

> All file references in the navigation tables below use relative Markdown links so they are clickable inside GitHub.


| Order | Document | File | Purpose |
|---:|---|---|---|
| 1 | Product Roadmap Index | [`index.md`](index.md) | Main entry point for the product roadmap. |
| 2 | Product Vision | [`product-vision.md`](product-vision.md) | Explains the long-term product direction and why the platform exists. |
| 3 | Product Roadmap Overview | [`product-roadmap.md`](product-roadmap.md) | Summarizes the complete product roadmap and product modules. |
| 4 | Current Foundation | [`current-foundation.md`](current-foundation.md) | Shows the existing technical foundation and architecture direction. |
| 5 | What Already Exists Today | [`what-already-exists.md`](what-already-exists.md) | Lists current capabilities already implemented or in progress. |
| 6 | Improvement Backlog | [`improvement-backlog.md`](improvement-backlog.md) | Describes planned improvements required for productization. |
| 7 | LLMOps Positioning | [`llmops-positioning.md`](llmops-positioning.md) | Positions the platform as an execution-first LLMOps runtime. |
| 8 | Deterministic Runtime Engine | [`deterministic-runtime-engine.md`](deterministic-runtime-engine.md) | Explains the core runtime execution model. |
| 9 | Pluggable Runtime Architecture | [`pluggable-runtime-architecture.md`](pluggable-runtime-architecture.md) | Explains pluggable steps, policies, providers, storage, ledger, replay, observability, retention, hosting, and MCP extension points. |
| 10 | Runtime Provider and Transport Model | [`runtime-provider-and-transport-model.md`](runtime-provider-and-transport-model.md) | Explains provider-based hosting, dynamic runtime providers, HTTP provider, runtime-instance-only mode, shared queue dispatch, and future transport options. |
| 11 | Runtime Pool Roadmap | [`runtime-pool-roadmap.md`](runtime-pool-roadmap.md) | Documents delivered ProcessHostPool/KubernetesPool capability and the remaining multi-control-plane, Redis Cluster, multi-node, and managed-hosting scale work. |
| 12 | Policy Engine and Governance | [`policy-engine-and-governance.md`](policy-engine-and-governance.md) | Explains the pluggable policy engine, policy-by-context model, RBAC scope, ARN-inspired resources, governance, and policy decision recording. |
| 13 | Replay and Audit Layer | [`replay-audit-layer.md`](replay-audit-layer.md) | Explains replay, audit, diagnostics, validation, replay reports, and retained execution evidence. |
| 14 | Decision Ledger | [`decision-ledger.md`](decision-ledger.md) | Explains the decision ledger foundation for structured runtime decision history. |
| 15 | Retention, Eviction, and Compaction | [`retention-eviction-compaction.md`](retention-eviction-compaction.md) | Explains policy-driven retention, automatic snapshots, hot-state eviction, stale claim cleanup, compaction, archive direction, and lifecycle auditability. |
| 16 | Observability and Runtime Telemetry | [`observability-and-runtime-telemetry.md`](observability-and-runtime-telemetry.md) | Explains runtime telemetry across executions, runs, queues, workers, runtime instances, providers, transports, policies, replay, ledger, MCP, and retention lifecycle. |
| 17 | Execution Control and State Lifecycle | [`execution-control-and-state-lifecycle.md`](execution-control-and-state-lifecycle.md) | Explains execution/run/step lifecycle, pause, resume, cancel, retry, waiting-for-input, finalization, claims, and distributed lifecycle safety. |
| 18 | Testing and Reliability Strategy | [`testing-and-reliability-strategy.md`](testing-and-reliability-strategy.md) | Explains runtime, replay, ledger, policy, MCP, provider, queue, lifecycle, observability, chaos, and distributed reliability test strategy. |
| 19 | Developer Experience, API, SDK, and CLI | [`developer-experience-api-sdk-cli.md`](developer-experience-api-sdk-cli.md) | Explains API packaging, SDK direction, CLI direction, local setup, examples, diagnostics, error model, and developer onboarding. |
| 20 | Security and Encryption Hardening | [`security-and-encryption-hardening.md`](security-and-encryption-hardening.md) | Explains RBAC-aware access control, ARN-inspired scopes, replay/ledger/MCP/dashboard security, redaction, payload protection, encrypted retention archives, and security hardening direction. |
| 21 | Memory, Context, and Reasoning Lifecycle | [`memory-context-reasoning-lifecycle.md`](memory-context-reasoning-lifecycle.md) | Explains scoped memory, context injection, memory decay, freshness, runtime reasoning evidence, memory replay, and policy-driven memory governance. |
| 22 | Enterprise Dashboard | [`enterprise-dashboard.md`](enterprise-dashboard.md) | Describes the dashboard for executions, runs, queues, workers, ledger, replay, policy, retention, diagnostics, and observability. |
| 23 | Pipeline Builder | [`pipeline-builder.md`](pipeline-builder.md) | Describes the visual workflow builder for deterministic AI pipelines. |
| 24 | MCP Control Interface | [`mcp-control-interface.md`](mcp-control-interface.md) | Describes MCP-based execution control, replay, diagnostics, queue, runtime instance, ledger, and policy inspection. |
| 25 | Multi-Tenant Readiness | [`multi-tenant-readiness.md`](multi-tenant-readiness.md) | Describes tenant, project, pipeline, execution, RBAC, ledger, replay, retention, memory, and runtime capacity boundaries. |
| 26 | Managed Hosting Model | [`managed-hosting-model.md`](managed-hosting-model.md) | Describes the hosting model based on runtime instances, workers, shared queue, admission, providers, transport, and MCP. |
| 27 | Banking and Financial Services Readiness | [`banking-financial-services-readiness.md`](banking-financial-services-readiness.md) | Describes technical controls for audit-sensitive and regulated environments without claiming automatic compliance. |
| 28 | 6-Month Roadmap | [`roadmap-6-months.md`](roadmap-6-months.md) | Describes the short-term execution plan for a single-developer project moving toward productization and demos. |
| 29 | 12–24 Month Roadmap | [`roadmap-12-24-months.md`](roadmap-12-24-months.md) | Describes the longer-term direction toward product maturity, enterprise readiness, and commercial scale. |
---

# Roadmap Section Index

This table maps the roadmap topics to the public documentation files.

| # | Section | GitHub File | Purpose |
|---:|---|---|---|
| 1 | Executive Summary | [`product-roadmap.md`](product-roadmap.md) | Short public summary of the product direction. |
| 2 | Product Vision | [`product-vision.md`](product-vision.md) | Long-term vision of deterministic distributed AI execution infrastructure. |
| 3 | Market Problem | [`llmops-positioning.md`](llmops-positioning.md) | Explains why production AI workflows need more than prompt execution, tracing, or monitoring. |
| 4 | Current Foundation | [`current-foundation.md`](current-foundation.md) | Shows the existing technical base. |
| 5 | What Already Exists Today | [`what-already-exists.md`](what-already-exists.md) | Proves the project is not only an idea. |
| 6 | Deterministic Runtime Foundation | [`deterministic-runtime-engine.md`](deterministic-runtime-engine.md) | Runtime foundation for controlled execution. |
| 7 | DAG Execution Engine | [`deterministic-runtime-engine.md`](deterministic-runtime-engine.md) | DAG-based step execution, workflow structure, and execution lifecycle. |
| 8 | Execution State Management | [`current-foundation.md`](current-foundation.md) | Durable state, hot state direction, execution lifecycle, and state transitions. |
| 9 | Execution Control and State Lifecycle | [`execution-control-and-state-lifecycle.md`](execution-control-and-state-lifecycle.md) | Pause, resume, cancel, retry, waiting-for-input, finalization, claims, run lifecycle, step lifecycle, and distributed lifecycle safety. |
| 10 | Distributed Worker Model | [`current-foundation.md`](current-foundation.md) | Runtime instances, workers, worker identity, and execution capacity. |
| 11 | Shared Queue Direction | [`current-foundation.md`](current-foundation.md) | Shared queue above local queues for multi-instance execution. |
| 12 | Runtime Instance / Worker Model | [`managed-hosting-model.md`](managed-hosting-model.md) | Technical and commercial basis for instance/worker hosting model. |
| 13 | Redis / MongoDB Direction | [`current-foundation.md`](current-foundation.md) | Redis coordination/hot state direction and MongoDB ledger/audit/storage direction. |
| 14 | Replay / Audit Foundation | [`replay-audit-layer.md`](replay-audit-layer.md) | Replay, audit, validation, diagnostics, reports, retained evidence, and reproducibility direction. |
| 15 | Decision Ledger Foundation | [`decision-ledger.md`](decision-ledger.md) | Ledger as audit foundation for runtime decisions. |
| 16 | Configuration-Driven Runtime | [`policy-engine-and-governance.md`](policy-engine-and-governance.md) | Runtime behavior controlled through options, host modes, providers, queue settings, worker settings, replay settings, retention settings, and observability settings. |
| 17 | Context-Driven Execution | [`policy-engine-and-governance.md`](policy-engine-and-governance.md) | Runtime behavior scoped by tenant, project, pipeline, execution, run, step, user, RBAC, provider, model, operation, runtime instance, worker, and correlation context. |
| 18 | Policy-Driven Runtime | [`policy-engine-and-governance.md`](policy-engine-and-governance.md) | Runtime decisions evaluated through policies instead of hardcoded orchestration behavior. |
| 19 | Policy Engine Foundation | [`policy-engine-and-governance.md`](policy-engine-and-governance.md) | Pluggable policy engine with policy-by-context model. |
| 20 | RBAC-Aware Execution Context | [`multi-tenant-readiness.md`](multi-tenant-readiness.md) | Scoped execution context for safe AI workflow execution. |
| 21 | ARN-Inspired Resource Scoping | [`multi-tenant-readiness.md`](multi-tenant-readiness.md) | Resource identity model for tenant/project/pipeline/execution/tool/model/operation scopes. |
| 22 | Policy-Driven Concurrency and Throttling | [`policy-engine-and-governance.md`](policy-engine-and-governance.md) | Controlled admission, limits, throttling, and concurrency protection across scopes. |
| 23 | Provider-Driven Architecture | [`pluggable-runtime-architecture.md`](pluggable-runtime-architecture.md) | Hosting, storage, hot state, shared queue, registry, ledger, replay, observability, model/provider execution, and retention behind abstractions. |
| 24 | Pluggable Runtime Architecture | [`pluggable-runtime-architecture.md`](pluggable-runtime-architecture.md) | Pluggable steps, tools, policies, providers, storage, ledger, replay, observability, retention, hosting, and MCP tools. |
| 25 | Pluggable Steps and Tools | [`pluggable-runtime-architecture.md`](pluggable-runtime-architecture.md) | Add new workflow capabilities and governed tool operations without rewriting the deterministic core. |
| 26 | Runtime Provider and Transport Model | [`runtime-provider-and-transport-model.md`](runtime-provider-and-transport-model.md) | Local, HTTP, and gRPC providers, runtime-instance-only hosting, stable Runtime Pool routing, and future message-bus transports. |
| 27 | Runtime Pool Architecture and Recovery | [`runtime-pool-roadmap.md`](runtime-pool-roadmap.md) | Delivered ProcessHostPool and KubernetesPool identity, lifecycle, HTTP/gRPC transport, hierarchical recovery, durable failure authority, warm reuse, and bounded capacity. |
| 28 | Pluggable Transport Between Instances | [`runtime-provider-and-transport-model.md`](runtime-provider-and-transport-model.md) | Transport abstraction for local, HTTP, implemented gRPC, stable Runtime Pool endpoints, and future message-bus, NATS, RabbitMQ, or cloud queue direction. |
| 29 | Runtime-Instance-Only Mode | [`runtime-provider-and-transport-model.md`](runtime-provider-and-transport-model.md) | Runtime host mode for remote runtime instances, containers, pods, and managed execution units. |
| 30 | Admission Control | [`managed-hosting-model.md`](managed-hosting-model.md) | Policy-aware run admission, queue admission, capacity evaluation, throttling, and dispatch decisions. |
| 31 | Retention, Eviction, and Compaction Foundation | [`retention-eviction-compaction.md`](retention-eviction-compaction.md) | Policy-driven retention, automatic snapshots, hot-state eviction, stale claim cleanup, compaction, archive direction, replay preservation, and lifecycle auditability. |
| 32 | Automatic Snapshot Mechanism | [`retention-eviction-compaction.md`](retention-eviction-compaction.md) | Snapshot creation before cleanup, compaction, archive, or replay-sensitive lifecycle transitions. |
| 33 | Hot-State Eviction | [`retention-eviction-compaction.md`](retention-eviction-compaction.md) | Safe eviction of Redis/hot runtime state after durable evidence and policy requirements are satisfied. |
| 34 | Lifecycle Policy Rules | [`retention-eviction-compaction.md`](retention-eviction-compaction.md) | Retention, eviction, snapshot, compaction, archive, and purge direction controlled by policy. |
| 35 | MCP Server / Control Plane Direction | [`mcp-control-interface.md`](mcp-control-interface.md) | MCP as control surface for run, replay, pause, resume, cancel, diagnostics, ledger, policy, and runtime inspection. |
| 36 | Pause / Resume / Cancel Direction | [`execution-control-and-state-lifecycle.md`](execution-control-and-state-lifecycle.md) | Execution control through API, MCP, dashboard, lifecycle state, ledger, and replay. |
| 37 | Observability and Runtime Telemetry | [`observability-and-runtime-telemetry.md`](observability-and-runtime-telemetry.md) | Telemetry across execution, run, queue, runtime instance, worker, provider, transport, policy, replay, ledger, MCP, and lifecycle activity. |
| 38 | Grafana / Kibana / OpenSearch Direction | [`observability-and-runtime-telemetry.md`](observability-and-runtime-telemetry.md) | Runtime telemetry export direction for dashboards, logs, metrics, traces, and operational visibility. |
| 39 | Tests and Reliability Work | [`testing-and-reliability-strategy.md`](testing-and-reliability-strategy.md) | Runtime, replay, ledger, policy, MCP, provider, queue, lifecycle, observability, chaos, and distributed reliability tests. |
| 40 | Improvement Backlog | [`improvement-backlog.md`](improvement-backlog.md) | Planned improvements required for product maturity. |
| 41 | Developer Experience / API / SDK / CLI | [`developer-experience-api-sdk-cli.md`](developer-experience-api-sdk-cli.md) | Developer-facing API, SDK, CLI, quickstart, examples, diagnostics, errors, and onboarding. |
| 42 | Security and Encryption Hardening | [`security-and-encryption-hardening.md`](security-and-encryption-hardening.md) | RBAC-aware access control, replay/ledger/MCP/dashboard security, redaction, payload protection, encrypted archives, and encryption direction. |
| 43 | Memory, Context, and Reasoning Lifecycle | [`memory-context-reasoning-lifecycle.md`](memory-context-reasoning-lifecycle.md) | Scoped memory, context injection, memory decay, freshness, runtime reasoning evidence, memory replay, and policy-driven memory governance. |
| 44 | Enterprise Dashboard UI | [`enterprise-dashboard.md`](enterprise-dashboard.md) | Dashboard for executions, runs, queues, instances, workers, ledger, replay, policy, retention, telemetry, diagnostics, and metrics. |
| 45 | Visual Pipeline Builder | [`pipeline-builder.md`](pipeline-builder.md) | Visual DAG builder, workflow design, step configuration, validation, versioning, and test-run mode. |
| 46 | User / Tenant Management | [`multi-tenant-readiness.md`](multi-tenant-readiness.md) | Users, tenants, projects, roles, isolation, quotas direction, RBAC, and scoped resources. |
| 47 | Hosted Demo | [`roadmap-6-months.md`](roadmap-6-months.md) | Hosted demo for technical visitors, partners, pilots, and product validation. |
| 48 | Documentation Improvements | [`improvement-backlog.md`](improvement-backlog.md) | README, docs, examples, onboarding, architecture pages. |
| 49 | Kubernetes Deployment Demo | [`roadmap-6-months.md`](roadmap-6-months.md) | Multi-instance, multi-worker, shared queue, provider-based transport, and observability demo. |
| 50 | Compliance Profile Foundation | [`banking-financial-services-readiness.md`](banking-financial-services-readiness.md) | High-level direction for future configurable country/sector policy profiles. |
| 51 | Cloud Deployment Templates | [`improvement-backlog.md`](improvement-backlog.md) | Docker, Kubernetes, Helm, cloud deployment templates. |
| 52 | Product Modules Overview | [`product-roadmap.md`](product-roadmap.md) | Overview of runtime, replay, ledger, dashboard, builder, MCP, policy, providers, transport, lifecycle, memory, and hosting. |
| 53 | Banking / Financial Services Readiness | [`banking-financial-services-readiness.md`](banking-financial-services-readiness.md) | Technical controls for audit-sensitive and regulated environments. |
| 54 | Multi-Tenant SaaS Readiness | [`multi-tenant-readiness.md`](multi-tenant-readiness.md) | Tenants, projects, pipelines, executions, ledger, replay, metrics, quotas, RBAC, memory, and scoped resources. |
| 55 | Execution Dashboard | [`enterprise-dashboard.md`](enterprise-dashboard.md) | Execution list, timeline, status, failures, duration, replay, ledger, policy, telemetry, and controls. |
| 56 | Run / Queue Dashboard | [`enterprise-dashboard.md`](enterprise-dashboard.md) | Submitted, queued, running, completed, dispatch, queue pressure, shared/local queue. |
| 57 | Runtime Instance Dashboard | [`enterprise-dashboard.md`](enterprise-dashboard.md) | Instances, workers, capacity, heartbeat, local queue, load, unhealthy detection. |
| 58 | Decision Ledger Dashboard | [`enterprise-dashboard.md`](enterprise-dashboard.md) | Ledger viewer, decisions, policies, claims, correlation IDs, retention, and replay links. |
| 59 | Replay / Audit Dashboard | [`enterprise-dashboard.md`](enterprise-dashboard.md) | Replay execution, validate, inspect issues, inspect policy decisions, inspect memory evidence, and export reports direction. |
| 60 | Observability Dashboard | [`enterprise-dashboard.md`](enterprise-dashboard.md) | Logs, metrics, traces, latency, throughput, failures, retries, queue saturation, workers, instances. |
| 61 | Visual DAG Editor | [`pipeline-builder.md`](pipeline-builder.md) | Drag/drop workflow graph and step layout. |
| 62 | Step Configuration | [`pipeline-builder.md`](pipeline-builder.md) | Model, provider, tools, input/output mapping, retry, timeout, policy, memory, and retention configuration direction. |
| 63 | Retry / Timeout / Concurrency Policy Configuration | [`pipeline-builder.md`](pipeline-builder.md) | Policies configurable per step or pipeline. |
| 64 | Human-in-the-Loop Steps | [`pipeline-builder.md`](pipeline-builder.md) | Approval, waiting-for-input, manual review, and intervention steps. |
| 65 | Pipeline Versioning | [`pipeline-builder.md`](pipeline-builder.md) | Version history, rollback, and comparison direction. |
| 66 | Pipeline Templates | [`pipeline-builder.md`](pipeline-builder.md) | Templates for repeatable enterprise AI workflow patterns. |
| 67 | MCP Tool Explorer | [`mcp-control-interface.md`](mcp-control-interface.md) | List tools, schemas, inputs, outputs, diagnostics, and operational behavior. |
| 68 | MCP Execution Control | [`mcp-control-interface.md`](mcp-control-interface.md) | Submit run, replay, pause, resume, cancel, diagnostics. |
| 69 | MCP Request / Response History | [`mcp-control-interface.md`](mcp-control-interface.md) | History of MCP calls and operational control-plane interactions. |
| 70 | Differentiation vs Existing LLMOps Tools | [`llmops-positioning.md`](llmops-positioning.md) | Key difference: control execution from the beginning, not only observe after execution. |
| 71 | Managed Hosting by Instance / Worker | [`managed-hosting-model.md`](managed-hosting-model.md) | Commercial direction aligned with architecture: instance, worker, queue, provider, transport, retention, observability. |
| 72 | Self-Hosted Deployment | [`managed-hosting-model.md`](managed-hosting-model.md) | Customer runs platform in their infrastructure. |
| 73 | Managed Cloud Hosting | [`managed-hosting-model.md`](managed-hosting-model.md) | Platform provider hosts runtime capacity. |
| 74 | Dedicated Enterprise Cluster | [`managed-hosting-model.md`](managed-hosting-model.md) | Isolated cluster direction for enterprise and regulated customers. |
| 75 | Multi-Tenant SaaS | [`multi-tenant-readiness.md`](multi-tenant-readiness.md) | SaaS model with tenant isolation and shared platform operations. |
| 76 | 6-Month Roadmap | [`roadmap-6-months.md`](roadmap-6-months.md) | Public short-term productization plan for a single-developer project. |
| 77 | 12–24 Month Roadmap | [`roadmap-12-24-months.md`](roadmap-12-24-months.md) | Long-term product maturity and commercial scale path. |
| 78 | Final Product Positioning | [`product-roadmap.md`](product-roadmap.md) | Central product message and strategic positioning. |
| 79 | Final Vision Statement | [`product-vision.md`](product-vision.md) | Build visually, run deterministically, govern through policy, control through MCP, replay/audit, manage memory/context, and scale by instances/workers. |
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

## 2. Execution Control and State Lifecycle

Execution control makes the runtime operable.

It covers:

- run lifecycle;
- execution lifecycle;
- step lifecycle;
- pause;
- resume;
- cancel;
- retry;
- waiting-for-input direction;
- claim ownership;
- finalization;
- lifecycle Decision Ledger events;
- replayable state transitions;
- distributed lifecycle safety.

This is what separates a production runtime from a fire-and-forget agent runner.

---

## 3. Replay and Audit Layer

Replay and audit are strategic capabilities.

They help teams:

- inspect previous executions;
- validate runtime behavior;
- diagnose failures;
- compare replay results;
- generate audit reports direction;
- investigate production incidents;
- inspect memory/context evidence;
- improve trust in AI workflow execution.

---

## 4. Decision Ledger

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
- archive decisions;
- memory/context decisions;
- security decisions.

---

## 5. Policy Engine and Runtime Governance

The Policy Engine is a core governance foundation.

It supports:

- configuration-driven runtime behavior;
- context-driven execution;
- policy-driven decisions;
- pluggable policy-by-context model;
- RBAC-aware execution context;
- ARN-inspired resource scoping;
- policy-driven concurrency and throttling;
- policy-driven lifecycle rules;
- policy-driven memory access and decay direction;
- policy decisions recorded in the Decision Ledger.

The runtime model can be summarized as:

```text
Configuration defines how the runtime is deployed.
Context defines what is being executed and under which scope.
Policy defines whether the operation is allowed and how it should behave.
Providers define how infrastructure responsibilities are implemented.
```

---

## 6. Pluggable Runtime Architecture

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
- pluggable memory/context providers direction;
- pluggable hosting modes;
- pluggable MCP tools.

This allows the deterministic core to remain stable while execution capabilities evolve.

---

## 7. Runtime Provider and Transport Model

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

## Runtime Pool Foundation

The ProcessHostPool and KubernetesPool foundations are implemented and validated.

It adds:

- reusable warm process-host capacity;
- independently registered runtime instances;
- stable exact HTTP/gRPC routing;
- targeted child replacement;
- exact failure isolation;
- deterministic recovery claims;
- existing transition-service reuse;
- compatibility with historical Process and Kubernetes modes.

The next Runtime Pool product milestones are distributed multi-control-plane recovery ownership, Redis Cluster validation, multi-node scale, and managed-hosting operations.

See [`runtime-pool-roadmap.md`](runtime-pool-roadmap.md).

---

## 8. Retention, Eviction, Compaction, and Snapshotting

Retention, eviction, compaction, and snapshotting are part of the runtime foundation.

They are not only cleanup concerns. They are product, storage, replay, audit, cost-control, memory lifecycle, and future compliance-support concerns.

This foundation includes direction for:

- retaining execution records;
- preserving replay reports;
- preserving decision ledger events;
- creating automatic snapshots before cleanup;
- evicting expired hot state;
- cleaning stale claims;
- compacting large execution histories;
- archiving payloads;
- preserving audit value while reducing storage pressure;
- recording retention, eviction, compaction, snapshot, and archive decisions;
- preparing encrypted retention archive direction.

---

## 9. Observability and Runtime Telemetry

Observability makes the runtime operable.

It should cover:

- executions;
- runs;
- queues;
- runtime instances;
- workers;
- providers;
- transports;
- policies;
- replay;
- ledger;
- MCP;
- memory/context usage;
- retention lifecycle;
- logs;
- metrics;
- traces;
- external exports to Grafana, Kibana, OpenSearch, and SIEM-style systems direction.

---

## 10. Memory, Context, and Reasoning Lifecycle

Memory and context must be governed.

This pillar covers:

- scoped memory;
- context injection;
- RBAC-aware memory access;
- memory source tracking;
- memory freshness;
- memory decay;
- runtime reasoning evidence;
- replay memory evidence;
- memory retention;
- memory compaction;
- memory archive direction;
- policy-driven memory lifecycle;
- tenant-aware memory boundaries.

The runtime should not treat memory as an invisible global state.

---

## 11. MCP Control Interface

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
- inspect memory/context decisions;
- trigger diagnostics;
- expose MCP tool schemas;
- provide operational visibility.

---

## 12. Enterprise Dashboard

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
- memory/context evidence;
- logs;
- metrics;
- traces;
- failure and retry visibility;
- queue pressure;
- runtime health.

---

## 13. Visual Pipeline Builder

The pipeline builder is the product usability layer.

It should support:

- visual DAG design;
- step configuration;
- provider/model configuration;
- tool execution steps;
- input/output mapping;
- memory/context configuration direction;
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

## 14. Testing and Reliability Strategy

Reliability must be proven.

The platform should continue strengthening tests around:

- runtime invariants;
- DAG execution;
- replay/audit;
- Decision Ledger;
- policy engine;
- RBAC/scoped context;
- execution control;
- retry/recovery;
- claims and worker safety;
- shared queue;
- runtime instances;
- provider/transport;
- MCP integration;
- retention/eviction/compaction/snapshot;
- observability;
- chaos and load direction.

---

## 15. Developer Experience, API, SDK, and CLI

Developer experience turns the runtime into an adoptable platform.

This pillar covers:

- public API surface;
- RunId / ExecutionId clarity;
- SDK direction;
- CLI direction;
- local setup;
- examples;
- error model;
- diagnostics;
- configuration examples;
- policy developer experience;
- step developer experience;
- provider developer experience;
- MCP developer experience.

---

## 16. Security and Encryption Hardening

Security hardening protects the runtime lifecycle.

It includes:

- RBAC-aware access control;
- ARN-inspired resources;
- policy-driven security;
- access-controlled replay;
- access-controlled Decision Ledger;
- MCP security;
- dashboard security;
- provider/transport security;
- sensitive payload handling;
- redaction;
- encrypted retention archives direction;
- encrypted ledger payload direction;
- tenant-aware security boundaries.

---

## 17. Multi-Tenant Readiness

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
- memory/context data;
- retention policies;
- runtime capacity;
- worker allocation;
- usage metering direction.

---

## 18. Managed Hosting Model

The long-term hosting model is aligned with the runtime architecture.

Potential deployment models include:

- self-hosted deployment;
- managed cloud hosting;
- dedicated enterprise cluster;
- multi-tenant SaaS;
- managed hosting by runtime instance and worker capacity.

This allows the product to evolve from a technical runtime into a scalable execution platform.

---

## 19. Banking and Financial Services Readiness

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
- memory/context governance;
- observability export;
- retention, eviction, compaction, and snapshot foundation;
- encrypted ledger and encrypted retention hardening direction;
- country/sector policy profile direction;
- data residency direction.

---

# Architecture Extension Points

The following documents describe the most important extension points.

| Extension Point | Document | Why It Matters |
|---|---|---|
| Pluggable runtime architecture | [`pluggable-runtime-architecture.md`](pluggable-runtime-architecture.md) | Explains how the runtime can evolve through pluggable steps, policies, providers, transport, storage, ledger, replay, observability, retention, hosting, and MCP tools. |
| Runtime provider and transport model | [`runtime-provider-and-transport-model.md`](runtime-provider-and-transport-model.md) | Explains how the control plane communicates with runtime instances through local, HTTP, and future transport models. |
| Policy engine and governance | [`policy-engine-and-governance.md`](policy-engine-and-governance.md) | Explains how policies can be created by context and recorded through the Decision Ledger. |
| MCP control interface | [`mcp-control-interface.md`](mcp-control-interface.md) | Explains how runtime control is exposed through structured MCP tools. |
| Managed hosting model | [`managed-hosting-model.md`](managed-hosting-model.md) | Explains how runtime instances, workers, shared queues, admission, providers, and transport become the foundation for hosting. |
| Retention, eviction, and compaction | [`retention-eviction-compaction.md`](retention-eviction-compaction.md) | Explains policy-driven lifecycle management, automatic snapshots, hot-state eviction, compaction, archive direction, and replay preservation. |
| Observability and runtime telemetry | [`observability-and-runtime-telemetry.md`](observability-and-runtime-telemetry.md) | Explains how execution, queue, provider, worker, policy, replay, ledger, MCP, memory, and lifecycle telemetry become visible. |
| Execution control and state lifecycle | [`execution-control-and-state-lifecycle.md`](execution-control-and-state-lifecycle.md) | Explains lifecycle states, pause/resume/cancel, retry, claims, finalization, and distributed lifecycle safety. |
| Memory, context, and reasoning lifecycle | [`memory-context-reasoning-lifecycle.md`](memory-context-reasoning-lifecycle.md) | Explains scoped memory, context injection, memory decay, runtime reasoning evidence, replay memory evidence, and policy-driven memory governance. |
| Security and encryption hardening | [`security-and-encryption-hardening.md`](security-and-encryption-hardening.md) | Explains access control, replay/ledger/MCP/dashboard security, redaction, payload protection, and encrypted archive direction. |

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
- expose automatic snapshot, hot-state eviction, stale claim cleanup, and policy-driven compaction foundations more clearly;
- expose observability and runtime telemetry as a product foundation;
- expose execution control and state lifecycle as a production differentiator;
- expose testing and reliability strategy as proof of architecture;
- expose developer experience, API, SDK, and CLI direction;
- expose security and encryption hardening direction carefully without overclaiming;
- expose memory, context, reasoning lifecycle, and memory decay as a future product differentiator;

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

The roadmap is now broad enough to represent the product direction. Future additions should be selective and should only be added when they clarify a major product pillar or architectural foundation.
