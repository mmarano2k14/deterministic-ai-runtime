 # 12–24 Month Roadmap

## Deterministic AI Runtime Platform

This document describes the 12–24 month public roadmap direction for the Deterministic AI Runtime Platform.

This roadmap is intentionally strategic and realistic.

The project is currently designed, built, tested, documented, and maintained by a single developer. Because of that, the 12–24 month roadmap should not be read as a fixed delivery promise. It should be read as a long-term product direction that depends on available development capacity, funding, contributors, user feedback, technical validation, and product priorities.

The platform already has strong foundations in place:

- deterministic runtime execution;
- DAG-based workflow execution;
- durable execution state;
- step lifecycle tracking;
- replay and audit foundation;
- decision ledger foundation;
- policy engine foundation;
- pluggable policy-by-context model;
- RBAC-aware execution context;
- ARN-inspired resource scoping;
- configuration-driven runtime behavior;
- context-driven execution;
- provider-driven architecture;
- runtime control direction;
- MCP control-plane foundation;
- multiple runtime instances direction;
- multiple workers;
- shared queue and local queue model;
- admission control direction;
- policy-driven concurrency and throttling;
- provider-based runtime hosting;
- HTTP runtime provider direction;
- runtime-instance-only mode direction;
- managed hosting by runtime instance and worker capacity direction;
- observability direction;
- runtime telemetry direction;
- retention, eviction, compaction, and snapshot foundation;
- automatic snapshot mechanism direction;
- execution control and state lifecycle direction;
- testing and reliability direction;
- developer experience, API, SDK, and CLI direction;
- security and encryption hardening direction;
- memory, context, and reasoning lifecycle direction;
- banking and financial-services technical-control direction.

The 12–24 month roadmap is about turning these foundations into a mature product platform.

---

## Roadmap Philosophy

The long-term roadmap follows one principle:

> Build a credible deterministic AI execution platform by hardening the runtime first, then expanding into product interfaces, enterprise controls, managed hosting, and regulated-market readiness.

The roadmap should remain ambitious, but it must stay grounded.

The platform should not claim full SaaS maturity, full banking compliance, or enterprise-grade managed hosting before those layers are built, tested, secured, documented, and validated with real users.

The correct long-term position is:

> The platform is designed to become a reference foundation for deterministic distributed AI execution.

---

## Long-Term Product Goal

The long-term product goal is to evolve the project into a complete LLMOps execution platform where users can:

- define AI workflows;
- run them deterministically;
- govern execution through configuration, context, policy, and providers;
- control execution through MCP and dashboard;
- replay and audit every execution;
- inspect structured runtime decisions through the decision ledger;
- observe distributed runtime behavior through logs, metrics, traces, ledger events, runtime telemetry, provider/transport telemetry, and lifecycle telemetry;
- scale execution through runtime instances and workers;
- manage execution history through retention, eviction, compaction, snapshots, and archive direction;
- manage memory, context, and runtime reasoning evidence safely;
- isolate workloads by tenant, project, pipeline, execution, memory/context, telemetry, retention lifecycle, and resource scope;
- support self-hosted, dedicated, and managed deployment models;
- provide technical controls for audit-sensitive and financial-services environments.

---

## 12–24 Month Direction Summary

The roadmap can be divided into three long-term stages:

| Period | Focus | Goal |
|---|---|---|
| 6–12 Months | Product maturity | Stabilize runtime product surfaces, dashboard V1, pipeline builder V1, MCP tooling, replay/ledger usability, lifecycle diagnostics, telemetry, DX/API/CLI direction, and distributed demo. |
| 12–18 Months | Enterprise readiness | Multi-tenant boundaries, RBAC hardening, access control, observability exports, retention/snapshot/encryption hardening, memory/context governance, security hardening, deployment templates. |
| 18–24 Months | Commercial scale direction | Managed hosting direction, dedicated runtime capacity, usage metering direction, enterprise pilots, financial-services technical controls, memory/retention policy profiles, ecosystem growth. |

These are staged directions, not fixed delivery guarantees.

---

# 6–12 Months — Product Maturity

## Main Objective

The 6–12 month period should focus on transforming the project from strong architecture into a usable product foundation.

The target is not a complete enterprise SaaS yet.

The target is a clear, testable, demonstrable, and usable platform foundation.

---

## 1. Runtime Product Maturity

The runtime should continue to harden.

Focus areas:

- execution state invariants;
- step lifecycle consistency;
- retry behavior;
- pause/resume/cancel consistency;
- finalization safety;
- distributed claim safety;
- worker collision prevention;
- run/execution mapping;
- shared queue behavior;
- runtime instance coordination;
- provider-based hosting behavior;
- retention/eviction/compaction/snapshot safety;
- lifecycle diagnostics;
- memory/context evidence direction;
- provider/transport diagnostics.

## Expected Outcome

The runtime becomes stable enough to support product interfaces without constantly changing core semantics.

---

## 2. Replay and Audit Maturity

Replay and audit should become easier to use and explain.

Focus areas:

- replay report readability;
- replay issue classification;
- replay timeline reconstruction;
- deterministic orchestration validation;
- policy decision replay;
- retry/cancellation replay;
- retention-aware replay;
- compacted-history transparency;
- snapshot/archive reference transparency;
- memory/context evidence direction;
- lifecycle replay;
- audit report summary direction;
- replay access through MCP/API/dashboard.

## Expected Outcome

Replay becomes one of the visible product differentiators.

A user should be able to inspect an execution and understand what happened without manually reading raw logs.

---

## 3. Decision Ledger Maturity

The Decision Ledger should evolve into a product-visible audit layer.

Focus areas:

- stable event taxonomy;
- event versioning direction;
- query by execution/run/step/correlation;
- query by policy decision;
- query by runtime instance/worker;
- retention/eviction/compaction/snapshot events;
- memory/context events direction;
- security/access events direction;
- lifecycle events;
- replay integration;
- dashboard timeline;
- MCP inspection tools;
- export direction.

## Expected Outcome

The ledger becomes a core interface for explaining runtime behavior.

---

## 4. MCP Control Plane Maturity

MCP should become a reliable control surface.

Focus areas:

- tool documentation;
- consistent response models;
- predictable errors;
- replay tools;
- execution control tools;
- shared queue tools;
- runtime instance tools;
- worker inspection direction;
- decision ledger inspection;
- policy inspection;
- retention diagnostics;
- lifecycle diagnostics;
- memory/context diagnostics direction;
- security/access diagnostics direction;
- observability summaries.

## Expected Outcome

MCP becomes a meaningful operational interface, not only a technical integration.

---

## 5. Dashboard V1

The first dashboard version should focus on read-only and operational visibility.

Focus areas:

- execution list;
- execution details;
- run list;
- shared queue view;
- runtime instance view;
- worker view;
- replay report view;
- decision ledger view;
- policy decision view;
- retention/eviction/compaction/snapshot view;
- lifecycle diagnostics view;
- memory/context evidence direction;
- security/access-control direction;
- diagnostics view.

## Expected Outcome

The platform becomes visually understandable.

A technical visitor can see the runtime operating instead of only reading code and documentation.

---

## 6. Pipeline Builder V1

The first pipeline builder should be scoped carefully.

Focus areas:

- pipeline schema;
- step schema;
- dependency model;
- validation output;
- basic visual DAG direction;
- step configuration;
- provider/model configuration direction;
- retry/timeout configuration;
- policy configuration direction;
- memory/context configuration direction;
- retention profile configuration direction;
- test-run flow;
- pipeline-to-runtime execution.

## Expected Outcome

The project demonstrates the full product loop:

```text
Build -> Validate -> Run -> Observe -> Replay -> Audit -> Improve
```

---

## 7. Distributed Runtime Demo

The distributed runtime demo should become a flagship demonstration.

Focus areas:

- multiple runtime instances;
- multiple workers;
- shared queue;
- local queues;
- runtime instance registry;
- provider-based dispatch;
- HTTP runtime provider direction;
- runtime-instance-only mode;
- MCP inspection;
- replay after execution;
- decision ledger;
- observability across instances;
- provider/transport telemetry;
- lifecycle telemetry;
- memory/context telemetry direction.

## Expected Outcome

The project demonstrates why it is different from a simple AI workflow engine.

It shows distributed AI execution as a real architecture.

---

## 8. Developer Experience, API, SDK, and CLI Maturity

Developer experience should become more complete during the product maturity stage.

Focus areas:

- quickstart;
- local setup;
- sample workflows;
- API documentation;
- RunId / ExecutionId clarity;
- SDK direction;
- CLI direction;
- diagnostics;
- error model;
- configuration examples;
- policy examples;
- custom step examples;
- provider examples;
- MCP examples;
- test documentation.

## Expected Outcome

Developers can run, inspect, extend, test, and operate the platform more easily.

The runtime becomes easier to evaluate without requiring deep knowledge of every internal component.

---

## 9. Testing and Reliability Maturity

Testing should become a visible part of product trust.

Focus areas:

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

# 12–18 Months — Enterprise Readiness

## Main Objective

The 12–18 month period should focus on hardening the platform for enterprise scenarios.

This does not mean claiming full enterprise certification.

It means turning the technical foundations into controlled, secure, observable, and deployable product capabilities.

---

## 1. Multi-Tenant Readiness Hardening

The platform already has a foundation with RBAC-aware context and ARN-inspired resource scoping.

The next stage is to productize it.

Focus areas:

- tenant metadata;
- project metadata;
- pipeline metadata;
- tenant-aware execution context;
- tenant-aware replay access;
- tenant-aware decision ledger filtering;
- tenant-aware dashboard views;
- tenant-aware MCP access;
- tenant-aware retention policies;
- tenant-aware observability filtering;
- tenant-aware memory/context boundaries;
- tenant-aware retention lifecycle boundaries;
- tenant-aware telemetry filtering;
- tenant-aware runtime capacity direction.

## Expected Outcome

The platform can demonstrate clear tenant/project/pipeline boundaries.

---

## 2. RBAC and Policy Hardening

The policy engine already exists as a foundation and is pluggable by context.

The next stage is to make it easier to configure, inspect, and audit.

Focus areas:

- RBAC documentation;
- ARN-inspired resource documentation;
- subject/action/resource/context examples;
- policy examples by context;
- tenant/project/pipeline policy examples;
- provider/model/tool policy examples;
- replay/ledger access policies;
- retention policies;
- snapshot/compaction policies;
- memory access policies;
- memory decay policies;
- security/access policies;
- policy decision dashboard;
- policy inspection through MCP.

## Expected Outcome

The platform can show how different contexts use different policies without rewriting the runtime core.

---

## 3. Security Hardening

Security should become a major focus in this stage.

Focus areas:

- metadata/payload separation;
- redaction direction;
- access-controlled replay;
- access-controlled ledger views;
- access-controlled dashboard views;
- access-controlled MCP tools;
- audit of sensitive access;
- encrypted ledger payload direction;
- encrypted retention archive direction;
- snapshot/compaction policy profiles;
- encrypted replay bundle direction;
- tenant-aware encryption boundary direction;
- secret reference direction;
- security Decision Ledger events;
- security tests.

## Expected Outcome

The platform becomes more credible for enterprise and audit-sensitive use cases.

---

## 4. Observability Export

The platform should export runtime signals to external observability systems.

Focus areas:

- structured logs;
- metrics export direction;
- trace export direction;
- decision ledger export direction;
- runtime instance health;
- worker utilization;
- queue pressure;
- retry/failure/cancellation metrics;
- replay metrics;
- retention metrics;
- provider/transport telemetry;
- lifecycle telemetry;
- memory/context telemetry direction;
- MCP telemetry;
- OpenSearch/Kibana direction;
- Grafana direction;
- SIEM-style export direction.

## Expected Outcome

The runtime becomes observable both internally and externally.

This supports enterprise operations, Kubernetes demos, and production support.

---

## 5. Deployment Templates

Enterprise readiness requires deployability.

Focus areas:

- Docker Compose;
- Redis/Mongo setup;
- local demo setup;
- runtime-instance-only deployment example;
- control-plane deployment example;
- HTTP runtime provider example;
- Kubernetes manifests direction;
- Helm chart direction;
- observability stack examples;
- self-hosted deployment documentation.

## Expected Outcome

The platform becomes easier to evaluate and deploy.

---

## 6. Retention, Eviction, Compaction, Snapshot, and Archive Hardening

The platform already has retention, eviction, compaction, and snapshot foundations.

The next stage is to make them safer, more visible, and more configurable.

Focus areas:

- retention policy model;
- snapshot policy model;
- automatic snapshot before cleanup direction;
- tenant-aware retention;
- project/pipeline retention direction;
- hot-state eviction safety;
- stale claim cleanup;
- compacted-history reporting;
- archive references;
- encrypted retention archive direction;
- retention dashboard;
- retention ledger events;
- eviction ledger events;
- compaction ledger events;
- snapshot/archive ledger events;
- lifecycle telemetry;
- replay after compaction;
- audit export before purge direction.

## Expected Outcome

Execution history becomes manageable without losing replay and audit value.

---

## 7. Memory, Context, and Reasoning Lifecycle Hardening

Memory and context governance should become more concrete during the enterprise-readiness stage.

Focus areas:

- memory source model;
- memory access policies;
- scoped context injection;
- memory freshness;
- memory decay;
- runtime reasoning evidence;
- memory retention/compaction;
- replay memory evidence;
- tenant-aware memory boundaries;
- memory diagnostics through MCP and dashboard direction;
- memory redaction direction;
- sensitive memory handling direction.

The platform should not claim to expose hidden model chain-of-thought.

The correct direction is runtime reasoning evidence: context used, memory source, policy decision, retrieved data reference, tool usage, branch decision, retry decision, and replay evidence.

## Expected Outcome

The runtime can explain and govern which context and memory influenced an execution.

Memory becomes a controlled runtime capability instead of invisible global state.


---

# 18–24 Months — Commercial Scale Direction

## Main Objective

The 18–24 month period should focus on commercial scale direction.

This does not mean everything must become a full managed cloud in that period.

It means the platform should be positioned and prepared for commercial usage models.

---

## 1. Managed Hosting Direction

The runtime architecture naturally supports managed hosting by runtime instance and worker capacity.

Focus areas:

- runtime capacity model;
- worker capacity model;
- shared queue capacity;
- tenant capacity;
- reserved capacity direction;
- dedicated runtime instance direction;
- managed runtime deployment direction;
- usage metering direction;
- retention usage direction;
- observability level direction;
- support/SLA direction.

## Expected Outcome

The platform can explain and demonstrate how managed AI execution capacity would work.

---

## 2. Dedicated Enterprise Runtime

Some customers may require dedicated runtime infrastructure.

Focus areas:

- dedicated runtime instances;
- dedicated cluster direction;
- private cloud deployment direction;
- tenant-specific policies;
- tenant-specific retention;
- tenant-specific observability export;
- tenant-specific memory/context boundaries;
- tenant-specific retention lifecycle rules;
- dedicated support workflows;
- deployment isolation direction.

## Expected Outcome

The platform can support higher-trust enterprise deployment models.

---

## 3. Usage Metering Direction

Usage metering should be explored carefully.

Potential metering dimensions:

- executions;
- runs;
- steps;
- runtime instance time;
- worker capacity;
- replay operations;
- decision ledger volume;
- retained data size;
- snapshot/archive usage;
- memory/context usage direction;
- observability export volume;
- tenant capacity;
- dedicated environment usage.

## Expected Outcome

The commercial model can align with actual runtime architecture.

---

## 4. Financial-Services Technical Controls

Financial-services readiness should continue to mature.

Focus areas:

- country/sector policy profiles;
- policy sets by context;
- audit report export;
- access-controlled replay;
- access-controlled ledger;
- memory/context governance;
- encrypted retention archive direction;
- data residency direction;
- self-hosted and dedicated deployment patterns;
- observability export;
- sensitive access audit.

## Expected Outcome

The platform can support serious conversations with banks, fintech, insurance, and regulated enterprises without overclaiming automatic compliance.

---

## 5. Ecosystem and Integrations

The product can grow through integrations.

Potential integration directions:

- model providers;
- tool providers;
- vector stores;
- memory providers direction;
- document systems;
- observability platforms;
- identity providers;
- policy providers;
- storage providers;
- MCP clients;
- CI/CD workflows;
- Kubernetes deployment stacks.

## Expected Outcome

The platform becomes easier to adopt in real environments.

---

## 6. Developer and Enterprise Adoption

Adoption should grow through:

- documentation;
- examples;
- demos;
- GitHub visibility;
- public roadmap;
- local setup;
- templates;
- reference workflows;
- MCP examples;
- distributed runtime demo;
- dashboard screenshots;
- enterprise positioning.

## Expected Outcome

The project becomes easier to understand, evaluate, and trust.

---

# Product Maturity Targets

The long-term roadmap can be viewed through maturity levels.

| Maturity Level | Description |
|---|---|
| Level 1 — Runtime Foundation | Deterministic execution, state, steps, replay, ledger foundation. |
| Level 2 — Control Plane | MCP, runtime control, replay, pause/resume/cancel, diagnostics. |
| Level 3 — Distributed Runtime | Runtime instances, workers, shared queue, local queues, provider-based dispatch. |
| Level 4 — Product Visibility | Dashboard, replay views, ledger views, lifecycle views, observability. |
| Level 5 — Developer Experience | Quickstart, APIs, SDK direction, CLI direction, examples, diagnostics, and local setup. |
| Level 6 — Workflow Creation | Pipeline builder, validation, test-run, versioning direction. |
| Level 7 — Enterprise Controls | RBAC, policy engine, tenant context, retention/snapshot lifecycle, security hardening. |
| Level 8 — Memory and Context Governance | Scoped memory, context injection, memory decay, replay memory evidence, tenant-aware memory boundaries. |
| Level 9 — Deployment Readiness | Docker/Kubernetes examples, self-hosted, dedicated runtime direction. |
| Level 10 — Managed Hosting Direction | Runtime instance/worker capacity, usage metering, reserved capacity. |
| Level 11 — Regulated-Market Technical Controls | Financial-services readiness, audit reports, policy profiles, encryption hardening. |

The platform should move through these levels progressively.

---

# What Should Not Be Rushed

The following areas should not be rushed:

- full managed cloud service;
- full billing system;
- formal compliance claims;
- banking certification;
- production SLAs;
- multi-region enterprise deployment;
- full tenant-aware SaaS admin console;
- advanced policy UI;
- tenant-aware memory isolation;
- complete memory decay engine;
- complex encryption key hierarchy;
- marketplace ecosystem.

These are important long-term directions, but they require careful design, security review, customer validation, and operational maturity.

---

# Success Criteria by 12 Months

By around 12 months, the platform should ideally demonstrate:

- clear public positioning;
- strong documentation;
- stable runtime core;
- usable MCP control plane;
- visible replay and audit reports;
- visible decision ledger;
- policy engine visibility;
- distributed runtime demo;
- first dashboard foundation;
- first pipeline builder foundation;
- observability examples;
- retention/eviction/compaction/snapshot visibility;
- developer quickstart and examples;
- lifecycle diagnostics.

---

# Success Criteria by 18 Months

By around 18 months, the platform should ideally demonstrate:

- stronger multi-tenant model;
- RBAC and policy documentation;
- tenant/project/pipeline boundaries;
- access-control direction;
- observability export;
- deployment templates;
- retention/snapshot/encryption hardening direction;
- memory/context governance direction;
- dashboard maturity;
- pipeline builder maturity;
- enterprise demo scenarios.

---

# Success Criteria by 24 Months

By around 24 months, the platform should ideally demonstrate:

- managed hosting direction;
- dedicated runtime capacity direction;
- usage metering direction;
- stronger enterprise deployment story;
- financial-services technical-control story;
- memory/context and retention policy profile direction;
- country/sector policy profile direction;
- stronger security hardening;
- ecosystem/integration direction;
- credible product maturity beyond an engineering prototype.

---

# Long-Term Product Outcome

The long-term outcome is a platform where users can:

- build AI workflows;
- run them deterministically;
- inspect execution state;
- control runs and executions;
- replay and audit every execution;
- inspect decision history;
- govern behavior through policies;
- isolate resources by tenant/project/pipeline/context;
- observe distributed runtime behavior;
- scale execution through runtime instances and workers;
- manage retention, eviction, compaction, snapshots, and archives;
- manage memory and context safely;
- deploy locally, self-hosted, dedicated, or managed;
- support audit-sensitive and regulated-market technical controls.

---

# Final Statement

The 12–24 month roadmap is ambitious but should remain realistic.

The project is currently maintained by a single developer, so progress must be staged and focused.

The platform already has strong foundations.

The long-term roadmap is about turning those foundations into a mature product:

- runtime stability;
- control plane;
- replay and audit;
- decision ledger;
- policy governance;
- distributed execution;
- dashboard;
- pipeline builder;
- multi-tenant readiness;
- managed hosting;
- financial-services technical controls;
- developer experience and runtime tooling;
- security and encryption hardening;
- memory and context governance.

The ultimate goal is to make the Deterministic AI Runtime Platform a reference foundation for deterministic distributed AI execution.
