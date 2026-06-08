# Pluggable Runtime Architecture

## Deterministic AI Runtime Platform

This document describes the pluggable runtime architecture of the Deterministic AI Runtime Platform.

This is one of the most important architectural foundations of the project.

The platform is not designed as a fixed AI agent runner with hardcoded behavior. It is designed as a deterministic execution platform where the core runtime can remain stable while execution behavior, policies, providers, transports, storage, replay, ledger, observability, and hosting models can evolve behind clear extension points.

The key idea is:

> The runtime core should stay deterministic and controlled, while the surrounding execution capabilities should remain pluggable, configurable, context-aware, policy-driven, and provider-driven.

This is what allows the platform to grow from a local deterministic runtime into distributed AI execution infrastructure.

---

## Purpose

The purpose of the pluggable runtime architecture is to make the platform adaptable without making the core engine unstable.

Production AI workflows change quickly.

A runtime may need to support:

- different step types;
- different model providers;
- different tool execution models;
- different policy rules;
- different transport mechanisms;
- different storage backends;
- different ledger providers;
- different replay storage strategies;
- different observability exporters;
- different runtime hosting modes;
- different tenant or compliance contexts;
- different deployment models.

If all of this is hardcoded into the core engine, the runtime becomes fragile.

The platform avoids this by using pluggable extension points.

---

## Core Principle

The core principle is:

```text
Stable deterministic core
  + pluggable execution capabilities
  + context-driven behavior
  + policy-driven decisions
  + provider-driven infrastructure
  = adaptable production AI runtime
```

The deterministic runtime engine should own the execution lifecycle:

- execution state;
- step lifecycle;
- dependency resolution;
- step readiness;
- claiming;
- retry behavior;
- cancellation;
- finalization;
- replay evidence;
- decision recording;
- observability events.

But the runtime should not hardcode every possible:

- step implementation;
- provider;
- model;
- policy;
- transport;
- storage backend;
- ledger writer;
- replay store;
- observability exporter;
- deployment mode.

This is where the pluggable architecture becomes critical.

---

## Current Foundation

The project already contains several foundations that support pluggable runtime architecture.

These include:

- configuration-driven runtime behavior;
- context-driven execution;
- policy-driven runtime decisions;
- pluggable policy engine foundation;
- provider-driven architecture;
- provider-based runtime hosting;
- dynamic provider direction;
- local runtime provider direction;
- HTTP runtime provider direction;
- runtime-instance-only mode direction;
- control-plane with runtime instances direction;
- MCP control-plane foundation;
- decision ledger foundation;
- replay and audit foundation;
- Redis coordination direction;
- MongoDB durable history direction;
- retention, eviction, and compaction foundation;
- observability direction;
- multi-instance and multi-worker runtime direction.

This means the platform is already architected toward extension and substitution.

The roadmap is to make these extension points clearer, better documented, more visible, and easier to use.

---

# Architectural Layers

The pluggable runtime architecture can be understood as several layers.

```text
+------------------------------------------------------------+
|                    Product Interfaces                      |
|  Dashboard | Pipeline Builder | MCP Tools | APIs | CLI      |
+------------------------------------------------------------+
|                    Runtime Control Plane                   |
|  Submit | Inspect | Replay | Pause | Resume | Cancel       |
+------------------------------------------------------------+
|                  Deterministic Runtime Core                |
|  State | Steps | Claims | Retry | Recovery | Finalization  |
+------------------------------------------------------------+
|                Pluggable Execution Extensions              |
|  Steps | Tools | Models | Policies | Validators | Hooks    |
+------------------------------------------------------------+
|                Pluggable Infrastructure Providers          |
|  Storage | Queue | Ledger | Replay | Observability          |
+------------------------------------------------------------+
|                 Pluggable Hosting / Transport              |
|  Local | HTTP | Runtime Instance | Future gRPC/NATS/etc.   |
+------------------------------------------------------------+
|                 Distributed Runtime Foundation             |
|  Runtime Instances | Workers | Shared Queue | Local Queues  |
+------------------------------------------------------------+
```

The runtime core should remain deterministic.

The layers around it should remain extensible.

---

# 1. Pluggable Step Architecture

AI workflows are built from steps.

A step can represent:

- a prompt operation;
- a model call;
- a tool call;
- a retrieval operation;
- a validation operation;
- a transformation;
- a policy evaluation;
- a human approval step;
- a notification;
- an external API operation;
- a persistence operation;
- a custom business operation.

The platform should allow new step types to be added without rewriting the runtime core.

---

## Why Pluggable Steps Matter

Production AI workflows are not all the same.

One workflow may analyze documents.

Another may inspect logs.

Another may execute a tool.

Another may require approval.

Another may run a retrieval step before calling a model.

Another may need validation and fallback.

A fixed runtime cannot anticipate every workflow type.

Pluggable steps allow the platform to support new workflow capabilities while keeping execution semantics stable.

---

## Step Contract Direction

A pluggable step should be defined through a clear contract.

A step contract can include:

- step type;
- input schema direction;
- output schema direction;
- execution handler;
- validation rules;
- retry behavior;
- timeout behavior;
- cancellation behavior;
- policy requirements;
- side-effect marker;
- replay behavior;
- retention behavior;
- observability metadata.

This allows the runtime to understand how the step should behave without hardcoding its implementation.

---

## Step Execution Responsibilities

A pluggable step should not own the full runtime lifecycle.

The runtime should still own:

- state transition;
- claim ownership;
- retry state;
- cancellation state;
- finalization;
- decision ledger events;
- replay metadata;
- observability events.

The step implementation should focus on executing the step logic.

This separation prevents step plugins from breaking deterministic orchestration.

---

## Step Safety

Pluggable steps must be safe under runtime rules.

A step should respect:

- cancellation;
- timeout;
- retry limits;
- policy decisions;
- input/output contract;
- side-effect rules;
- replay restrictions;
- retention profile direction;
- observability requirements.

This is especially important for tool steps that can create side effects.

---

# 2. Pluggable Tool Execution

Tool execution is one of the most sensitive parts of AI workflows.

A tool can:

- read data;
- write data;
- call APIs;
- trigger transactions;
- send messages;
- update records;
- access files;
- perform external operations.

Because tools can create side effects, tool execution must be pluggable and governed.

---

## Tool Plugin Direction

A tool plugin can define:

- tool identity;
- allowed operations;
- input schema;
- output schema;
- timeout;
- retry policy;
- required permissions;
- side-effect level;
- replay behavior;
- audit sensitivity;
- observability metadata.

The runtime should be able to record tool-related decisions in the decision ledger.

Examples:

- tool access allowed;
- tool access denied;
- tool execution started;
- tool execution failed;
- tool execution completed;
- side-effecting operation recorded;
- replay skipped side-effecting tool.

---

## Tool Governance

Tool access should be controlled through policy.

A policy can decide:

- whether a tenant can use a tool;
- whether a pipeline can call a tool;
- whether a user can trigger a tool;
- whether the tool can run in production;
- whether approval is required;
- whether replay can inspect the result;
- whether payloads should be retained or redacted.

This is critical for enterprise and banking/financial-services readiness.

---

# 3. Pluggable Policy Engine

The policy engine is already a key foundation of the platform.

The policy engine should be understood as a pluggable runtime governance layer.

Policies should be attachable by context.

A policy can be created for:

- tenant;
- project;
- environment;
- pipeline;
- pipeline version;
- execution;
- run;
- step;
- user;
- RBAC context;
- resource scope;
- provider;
- model;
- tool;
- operation;
- retention profile;
- country or sector profile direction.

This is one of the strongest enterprise foundations of the platform.

---

## Policy-by-Context Model

The policy-by-context model can be summarized as:

```text
Runtime Context
  -> Policy Engine
      -> Policy Set
          -> Policy Decision
              -> Decision Ledger
                  -> Runtime Behavior
```

This allows different contexts to use different policies without rewriting the runtime core.

Examples:

```text
Development tenant -> relaxed provider policy
Production tenant -> approved models only
Banking tenant -> strict replay and ledger access policy
Sensitive pipeline -> approval required for tool execution
High-volume tenant -> throttling and concurrency limits
Regulated workload -> longer retention and audit export policy
```

The runtime remains the same.

The policies change by context.

---

## Policy Outcomes

A policy can produce structured outcomes such as:

- allowed;
- denied;
- failed;
- throttled;
- delayed;
- blocked;
- approval required;
- retry later;
- capacity unavailable;
- retention restricted;
- access restricted.

These outcomes should be recorded in the Decision Ledger.

This makes policy governance auditable.

---

## Policy Engine Responsibilities

The policy engine can support:

- execution admission;
- run admission;
- queue admission;
- step execution decisions;
- model/provider access;
- tool access;
- replay access;
- ledger access;
- retention behavior;
- export permissions;
- concurrency and throttling;
- runtime capacity decisions;
- tenant quotas;
- country/sector profile direction.

The runtime should call the policy engine at decision points.

The policy engine should return structured decisions.

The decision ledger should record those decisions.

---

# 4. Pluggable Provider Architecture

The platform is provider-driven.

Provider-driven architecture allows infrastructure concerns to evolve behind abstractions.

Provider areas include:

- runtime hosting providers;
- runtime communication providers;
- storage providers;
- hot-state providers;
- shared queue providers;
- runtime instance registry providers;
- decision ledger providers;
- replay providers;
- observability providers;
- model providers;
- tool providers;
- retention/archive providers.

This is what allows the runtime to evolve without becoming hardcoded to a single infrastructure model.

---

## Provider Responsibilities

A provider should hide infrastructure details behind a stable interface.

For example:

| Provider Type | Responsibility |
|---|---|
| Runtime Provider | Execute or dispatch work to a runtime instance. |
| Queue Provider | Store and coordinate queued runs. |
| Hot State Provider | Manage fast execution state. |
| Ledger Provider | Record structured runtime decisions. |
| Replay Provider | Store and retrieve replay reports. |
| Observability Provider | Export logs, metrics, traces, and runtime events. |
| Storage Provider | Store durable execution or audit records. |
| Transport Provider | Communicate between control plane and runtime instances. |
| Retention Provider | Archive, compact, evict, or retain execution history. |

This is how the architecture remains adaptable.

---

## Local and Remote Providers

The platform can support both local and remote providers.

Local providers are useful for:

- local development;
- tests;
- demos;
- single-process execution;
- simple deployments.

Remote providers are useful for:

- distributed runtime instances;
- HTTP runtime provider;
- runtime-instance-only mode;
- Kubernetes-style execution;
- managed hosting;
- dedicated runtime capacity.

This allows the platform to grow gradually.

---

# 5. Pluggable Transport Between Runtime Instances

Runtime transport is a major extension point.

The runtime should not be permanently tied to one communication mechanism.

The platform already includes HTTP runtime provider direction and provider-based hosting direction.

Over time, additional transports can be introduced without rewriting the core runtime.

Possible transport directions include:

- in-process/local transport;
- HTTP transport;
- future gRPC transport;
- future message bus transport;
- future NATS transport;
- future RabbitMQ transport;
- future Kafka-style event transport;
- future cloud queue transport.

The important point is not which transport is chosen first.

The important point is that transport is abstracted.

---

## Why Transport Pluggability Matters

Different deployments need different communication models.

A local demo can use in-process execution.

A distributed test can use HTTP.

A Kubernetes deployment may use service-to-service HTTP or gRPC.

An event-driven enterprise deployment may prefer a message bus.

A managed cloud may use internal routing.

The runtime should not need to be rewritten for each deployment.

Transport pluggability allows:

```text
Same runtime core
Different communication model
Different deployment model
Same execution semantics
```

This is extremely important for long-term product maturity.

---

## Control Plane to Runtime Instance Communication

The communication model can look like:

```text
MCP Tool
  -> Control Plane
      -> Shared Queue
          -> Runtime Provider / Transport
              -> Runtime Instance
                  -> Local Queue
                      -> Worker
```

The transport layer can be replaced while preserving:

- run identity;
- execution identity;
- runtime instance identity;
- worker identity;
- decision ledger;
- replay;
- observability;
- cancellation;
- diagnostics.

This is the point of a pluggable transport model.

---

## Runtime Instance to Control Plane Communication

Runtime instances may also report back to the control plane.

They may report:

- heartbeat;
- health;
- capacity;
- local queue depth;
- assigned runs;
- worker activity;
- execution status;
- failure status;
- diagnostics;
- observability metadata.

This communication should also remain provider-driven.

---

# 6. Pluggable Storage

The runtime separates hot state from durable history.

This is already an important foundation.

## Hot State

Hot state supports fast runtime coordination.

It can include:

- active execution state;
- claims;
- queue coordination;
- retry coordination;
- runtime instance heartbeat;
- worker activity;
- temporary execution metadata.

Redis is a natural fit for hot state and distributed coordination.

## Durable History

Durable history supports audit and investigation.

It can include:

- execution records;
- replay reports;
- decision ledger events;
- retained history;
- audit reports;
- diagnostics;
- archived data.

MongoDB is a natural fit for durable history.

The key architectural idea is:

```text
Hot state = fast runtime coordination
Durable history = replay, audit, ledger, retained evidence
```

The provider model allows these responsibilities to evolve.

---

# 7. Pluggable Decision Ledger

The Decision Ledger should remain provider-driven.

A ledger provider can write events to:

- in-memory store for tests;
- MongoDB for durable history;
- future event store direction;
- future OpenSearch direction;
- future SIEM export direction;
- future append-only store direction.

The runtime should not depend on one specific storage mechanism for all possible deployments.

The ledger provider should preserve:

- event type;
- timestamp;
- execution/run/step identity;
- runtime instance and worker identity;
- policy decision;
- reason;
- correlation ID;
- metadata;
- integrity metadata direction.

This keeps audit flexible.

---

# 8. Pluggable Replay and Audit

Replay and audit should also remain provider-driven.

Replay providers can support:

- replay report storage;
- replay report retrieval;
- replay metadata;
- deterministic validation result;
- replay issue list;
- audit report direction;
- retained-history references;
- compacted-history references;
- archive references.

This allows replay data to evolve independently from hot execution state.

A runtime should be able to evict hot state after durable replay/audit evidence is preserved.

---

# 9. Pluggable Observability

Observability should be pluggable.

The runtime should emit structured signals that can be consumed by different systems.

Possible observability outputs include:

- structured logs;
- metrics;
- traces;
- decision ledger events;
- replay reports;
- runtime health;
- queue pressure;
- worker utilization;
- runtime instance status;
- policy decision volume;
- retention activity.

Pluggable observability allows export direction toward:

- Grafana;
- Kibana;
- OpenSearch;
- SIEM-style tools;
- cloud observability platforms.

The runtime should emit the signals.

The observability provider decides where they go.

---

# 10. Pluggable Retention, Eviction, and Compaction

Retention, eviction, and compaction are part of the runtime foundation.

They should also be configurable and provider-driven.

A retention provider can support:

- retaining execution records;
- evicting hot state;
- cleaning stale claims;
- compacting old histories;
- archiving payload references;
- preserving replay reports;
- preserving ledger references;
- creating archive records;
- recording retention decisions;
- preparing encrypted archive direction.

Retention behavior may vary by:

- tenant;
- project;
- pipeline;
- environment;
- data sensitivity;
- compliance profile direction;
- storage cost;
- replay requirement.

This is why retention should be pluggable and policy-aware.

---

# 11. Pluggable Runtime Hosting

Runtime hosting should be pluggable.

The platform can support:

- local development mode;
- single runtime instance mode;
- local runtime instance pool;
- runtime-instance-only mode;
- control plane with runtime instances;
- HTTP runtime provider mode;
- Kubernetes-style runtime instances;
- future dedicated enterprise runtime;
- future managed cloud runtime.

The runtime core should not care whether a worker runs:

- in the same process;
- in another process;
- in another container;
- in a Kubernetes pod;
- in a dedicated customer runtime.

It should communicate through provider and transport abstractions.

---

# 12. Pluggable MCP Control Surface

MCP is the structured control-plane interface.

MCP tools can expose runtime operations without hardcoding one UI or one client.

MCP can support:

- submit run;
- inspect run;
- inspect execution;
- pause execution;
- resume execution;
- cancel execution;
- replay execution;
- inspect queue;
- inspect runtime instances;
- inspect workers;
- inspect decision ledger;
- inspect policy decisions;
- inspect retention decisions;
- inspect diagnostics;
- inspect observability.

This makes MCP a pluggable control interface for agents, developers, dashboards, and future automation.

---

# 13. Pluggability and Enterprise Readiness

Enterprise environments require flexibility.

A bank, fintech, SaaS company, internal platform team, or AI product team may require different:

- policies;
- providers;
- transports;
- storage;
- retention;
- observability;
- deployment model;
- audit requirements;
- tenant boundaries;
- access controls.

A rigid runtime cannot adapt.

A pluggable runtime can.

This is why pluggability is not only a developer convenience.

It is an enterprise requirement.

---

# 14. Pluggability and Banking / Financial Services

Banking and financial-services readiness depends heavily on context-specific policies and controlled execution.

The platform can support:

- policies by tenant;
- policies by project;
- policies by pipeline;
- policies by operation;
- policies by provider/model/tool;
- policies by retention profile;
- policies by country/sector profile direction;
- access-controlled replay;
- access-controlled ledger;
- data lifecycle controls;
- observability exports;
- dedicated runtime capacity direction.

Because the policy engine is pluggable, new governance behavior can be added through policies instead of rewriting the runtime core.

This is a major strategic advantage.

---

# 15. Pluggability and Managed Hosting

Managed hosting depends on runtime capacity and provider-based execution.

The pluggable architecture supports managed hosting through:

- runtime instances;
- workers;
- shared queue;
- local queues;
- admission control;
- runtime instance registry;
- provider-based communication;
- MCP diagnostics;
- observability;
- replay/audit;
- retention controls.

A managed hosting model can scale by:

- adding runtime instances;
- adding workers;
- changing providers;
- changing transport;
- changing queue strategy;
- adding tenant-specific policies;
- adding retention profiles;
- exporting observability.

This makes the architecture commercially adaptable.

---

# 16. Extension Point Summary

| Extension Point | Purpose |
|---|---|
| Pluggable Steps | Add new workflow capabilities without rewriting runtime core. |
| Pluggable Tools | Add governed external operations and side-effecting actions. |
| Pluggable Policies | Add governance by context, tenant, project, pipeline, provider, model, tool, operation, or retention profile. |
| Pluggable Providers | Swap infrastructure responsibilities behind stable abstractions. |
| Pluggable Transport | Communicate between control plane and runtime instances through local, HTTP, or future transports. |
| Pluggable Storage | Separate hot state from durable history and allow backend evolution. |
| Pluggable Ledger | Store structured runtime decisions in different durable systems. |
| Pluggable Replay | Store and retrieve replay/audit evidence independently. |
| Pluggable Observability | Export logs, metrics, traces, and runtime events to different systems. |
| Pluggable Retention | Control retention, eviction, compaction, and archive behavior by context. |
| Pluggable Hosting | Support local, remote, runtime-instance-only, Kubernetes, and managed hosting modes. |
| Pluggable MCP Tools | Expose runtime control through structured tools. |

---

# Current Foundation Summary

| Area | Status |
|---|---|
| Configuration-driven runtime | Foundation exists |
| Context-driven execution | Foundation exists |
| Policy engine | Foundation exists |
| Pluggable policy model | Foundation exists |
| Provider-driven architecture | Foundation exists |
| Provider-based hosting | Foundation exists |
| Dynamic provider direction | Foundation exists |
| Local runtime provider direction | Foundation exists |
| HTTP runtime provider direction | Foundation exists |
| Runtime-instance-only mode | Foundation exists |
| MCP control-plane foundation | Foundation exists |
| Decision ledger foundation | Foundation exists |
| Replay/audit foundation | Foundation exists |
| Redis hot-state coordination direction | Foundation exists |
| MongoDB durable history direction | Foundation exists |
| Retention/eviction/compaction foundation | Foundation exists |
| Observability direction | Foundation exists |
| Multi-instance runtime direction | Foundation exists / active direction |
| Multiple workers | Foundation exists |
| Pluggable transport beyond HTTP | Future extension direction |
| External observability exporters | Productization target |
| Enterprise policy profiles | Productization target |

---

# Productization Roadmap

## Step 1 — Document Extension Points

Improve documentation for:

- pluggable steps;
- pluggable policies;
- provider-driven architecture;
- runtime provider model;
- transport model;
- storage model;
- ledger provider model;
- replay provider model;
- observability provider model;
- retention provider model.

## Step 2 — Strengthen Public Examples

Add examples for:

- custom step;
- custom policy;
- policy-by-context;
- local provider;
- HTTP runtime provider;
- replay provider;
- ledger provider;
- observability export direction;
- retention policy direction.

## Step 3 — Harden Provider Interfaces

Improve:

- provider contracts;
- error handling;
- diagnostics;
- observability;
- test coverage;
- compatibility between local and remote providers.

## Step 4 — Expose Through MCP and Dashboard

Make extension behavior visible through:

- MCP diagnostics;
- dashboard views;
- provider status;
- policy decision views;
- transport diagnostics;
- runtime instance views;
- retention decisions.

## Step 5 — Prepare Enterprise Extension Model

Prepare:

- tenant-specific policies;
- country/sector policy profiles;
- dedicated runtime providers;
- self-hosted provider configuration;
- managed hosting provider direction;
- access-controlled extension points.

---

# Final Statement

The pluggable runtime architecture is one of the strongest foundations of the Deterministic AI Runtime Platform.

It allows the platform to evolve without breaking the deterministic core.

The runtime can remain stable while the system gains new:

- step types;
- tools;
- policies;
- providers;
- transports;
- storage backends;
- ledger backends;
- replay stores;
- observability exporters;
- retention strategies;
- hosting modes;
- MCP tools.

This is what allows the project to move beyond a simple AI workflow engine.

The long-term goal is to become a deterministic distributed AI execution platform that is flexible enough for developers, governable enough for enterprises, observable enough for operations, and extensible enough for managed hosting and regulated-market use cases.
