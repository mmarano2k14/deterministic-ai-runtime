# Developer Experience, API, SDK, and CLI

## Deterministic AI Runtime Platform

This document describes the developer experience, API, SDK, and CLI direction of the Deterministic AI Runtime Platform.

Developer experience is critical because a powerful runtime is only useful if developers can understand it, run it, integrate it, test it, inspect it, and operate it.

The platform already has strong runtime foundations:

- deterministic execution;
- DAG-based workflow execution;
- execution state;
- replay and audit;
- Decision Ledger;
- policy engine;
- RBAC-aware execution context;
- MCP control plane;
- runtime instances;
- workers;
- shared queue direction;
- provider-based runtime hosting;
- runtime provider and transport model;
- retention, eviction, compaction, and snapshot direction;
- observability direction;
- integration testing direction.

The next step is to make these foundations easier to use from the outside.

The key idea is:

> The runtime should be powerful internally, but simple and predictable for developers externally.

---

## Purpose

The purpose of developer experience is to reduce friction.

A developer should be able to:

- understand the platform quickly;
- run it locally;
- submit a workflow;
- inspect an execution;
- replay an execution;
- inspect Decision Ledger events;
- inspect policy decisions;
- use MCP tools;
- run integration tests;
- configure providers;
- configure policies;
- configure runtime instances and workers;
- understand errors;
- read examples;
- build on top of the runtime.

Without strong developer experience, the architecture remains difficult to adopt.

---

## Current Foundation

The platform already has important foundations for developer experience.

These include:

- public documentation direction;
- product roadmap documentation;
- deterministic runtime foundation;
- replay/audit foundation;
- Decision Ledger foundation;
- MCP server/control-plane foundation;
- provider-based hosting foundation;
- runtime-instance-only mode direction;
- HTTP runtime provider direction;
- shared queue direction;
- runtime instance registry direction;
- policy engine foundation;
- configuration-driven runtime behavior;
- context-driven execution;
- retention/eviction/compaction foundation;
- testing foundation;
- Redis/Mongo infrastructure direction.

The server-side foundation also implements immutable code publication and whole-run pinning, durable hosted-function invocation, real Python/TypeScript/.NET execution, custom `Concurrency` policies, and outbound MCP integration. These capabilities and their current limits are documented in [Hosted Multilanguage Execution](../ai/hosted-multilanguage-execution.md).

The public SDK contract/server boundary is now implemented separately from the hosted-execution internals. Publication services remain server components, but portable public contracts and an authorized MCP server adapter now expose publication plus execution submit/observe/result/cancel operations. Language-specific external SDK client packages and CLI distribution remain separate productization work. [Public SDK Boundary](../ai/public-sdk-boundary.md) and [Public SDK Boundary Validation](../ai/public-sdk-boundary-validation.md) document that boundary.

The roadmap is not to invent developer experience from zero.

The roadmap is to package, document, simplify, expose, and stabilize the developer-facing surface.

---

## Core Principle

The developer experience principle is:

```text
Complex runtime inside.
Clear API outside.
Predictable examples.
Strong diagnostics.
Readable errors.
Fast local setup.
```

Developers should not need to understand the full distributed runtime before running a simple example.

But when they need advanced features, the architecture should be visible and well documented.

---

# 1. Public API Surface

The public API surface should be clear and stable.

It should expose common operations such as:

- submit run;
- inspect run;
- inspect execution;
- inspect step state;
- pause execution;
- resume execution;
- cancel execution;
- replay execution;
- inspect replay report;
- inspect Decision Ledger events;
- inspect policy decisions;
- inspect queue state;
- inspect runtime instance state;
- inspect diagnostics.

The API should hide unnecessary internal complexity while keeping the core runtime concepts visible.

---

## API Design Principles

The API should be:

- predictable;
- strongly typed where possible;
- explicit that portable execution operations use `ExecutionId`, while operational/admin APIs may expose richer run identities only where appropriate;
- explicit about status;
- explicit about errors;
- correlation-friendly;
- replay-friendly;
- ledger-friendly;
- policy-aware;
- compatible with MCP and dashboard usage.

A developer should not need to guess which identity is public execution identity and which identities belong only to server scheduling/operations.

---

## Public Execution Identity

The implemented public SDK boundary deliberately exposes `ExecutionId` as the durable external execution handle while keeping queue/control-plane placement identities private.

```text
Public contract:
ExecutionId = durable workflow execution identity

Server internals:
SharedRunId / LocalRunId / RuntimeInstanceId / WorkerId / lease / epoch
```

This prevents clients from becoming coupled to scheduling, placement, claim, or recovery internals. Operational/admin surfaces may still correlate richer identities where explicitly authorized, but they are not part of the portable SDK execution contract.

---

# 2. SDK Direction

The public SDK boundary now provides the stable portable contracts and server adapter on which external client libraries can be built.

Implemented boundary capabilities include:

- publication and pipeline wire models;
- deterministic dependency/source upload descriptors;
- execution submission;
- execution observation;
- terminal result projection;
- cancellation requests;
- explicit schema versions;
- submission idempotency keys;
- dependency isolation from engine DLLs.

The next layer is language-specific client packaging and convenience APIs. Those clients can later add replay, ledger, diagnostics, richer polling/wait helpers, and CLI workflows without moving runtime authority into the client.

The SDK should not hide the runtime model too much. It should make the important concepts easier to use while preserving server ownership of execution.

---

## External SDK Boundary

The independent boundary is now implemented as `Multiplexed.AI.Sdk.Contracts` plus an authorized server adapter. The public contract assembly uses portable wire models and has no direct or transitive dependency on runtime engine DLLs. It covers pipeline/publication material plus submission, observation, result, and cancellation contracts.

The platform hosts approved execution environments. The runtime retains authorization, authoritative publication validation, DAG lifecycle, retries, recovery, result acceptance, queue ownership, and worker placement. A developer-operated worker is not required by the main target model.

Language defaults and local overrides describe function execution, not the programming language used to build the SDK client. Native primitives remain native and MCP remains a distinct invocation mode. SDK conveniences must not override immutable run pins or turn uncertain external effects into automatic retries.

What remains separate is the packaging of external .NET/TypeScript/Python client libraries, CLI tooling, and any broader HTTP/Gateway product surface desired around the same contracts.

---

## SDK Responsibilities

The implemented contract/server boundary already standardizes request/response shapes. External client libraries can build on it to help developers with:

- request creation;
- response parsing;
- status polling;
- waiting for completion;
- replay retrieval;
- diagnostics retrieval;
- cancellation requests;
- error classification;
- correlation ID propagation;
- local demo integration;
- test helper direction.

This supports adoption and reduces boilerplate.

---

# 3. CLI Direction

A CLI can provide a fast operational interface.

A CLI can support commands such as:

```text
ai-runtime run submit
ai-runtime run inspect
ai-runtime execution inspect
ai-runtime execution cancel
ai-runtime execution pause
ai-runtime execution resume
ai-runtime replay run
ai-runtime ledger inspect
ai-runtime queue inspect
ai-runtime instance list
ai-runtime diagnostics run
```

A CLI is useful for:

- developers;
- demos;
- CI scripts;
- support;
- local testing;
- operations;
- examples.

The CLI does not need to be complete at first.

A focused CLI can still provide major value.

---

## CLI Output

CLI output should support:

- human-readable tables;
- JSON output;
- correlation IDs;
- status codes;
- detailed diagnostics;
- links or references to replay reports;
- links or references to ledger events;
- error summaries.

Example output modes:

```text
--format table
--format json
--verbose
--correlation-id
```

This makes the CLI useful both for humans and automation.

---

# 4. Local Developer Setup

Local setup should be simple.

Developers should be able to run:

- local runtime;
- Redis direction;
- MongoDB direction;
- MCP server direction;
- sample workflows;
- replay examples;
- ledger examples;
- shared queue demo direction;
- runtime instance demo direction.

The local setup should avoid unnecessary complexity.

A good local setup can use:

- Docker Compose;
- sample appsettings;
- sample workflows;
- clear README instructions;
- troubleshooting section.

---

## Local Modes

The project should document local modes clearly:

| Mode | Purpose |
|---|---|
| In-memory/local mode | Simple development and unit testing. |
| Redis/Mongo local mode | Real coordination and durable history demo. |
| MCP local mode | Test control-plane tools. |
| Local multi-instance mode | Simulate distributed execution in one environment. |
| Runtime-instance-only mode | Test remote-style runtime host. |
| HTTP provider mode | Test provider-based dispatch. |

This helps developers choose the right mode.

---

# 5. Examples

Examples are essential.

The project should include examples for:

- simple workflow execution;
- DAG workflow execution;
- replay execution;
- Decision Ledger inspection;
- policy decision example;
- RBAC-scoped execution example;
- pause/resume/cancel example;
- shared queue example;
- runtime instance example;
- HTTP runtime provider example;
- retention/snapshot/compaction example direction;
- observability example;
- MCP tool example.

Examples should be small, focused, and runnable.

---

## Example Structure

Each example should include:

- purpose;
- setup;
- command or code;
- expected output;
- how to inspect replay;
- how to inspect ledger;
- how to troubleshoot.

This makes the project more approachable.

---

# 6. Error Model

Errors should be explicit and developer-friendly.

Common error categories include:

- invalid request;
- execution not found;
- run not found;
- step not found;
- runtime instance not found;
- provider unavailable;
- queue unavailable;
- policy denied;
- policy failed;
- operation not allowed in current state;
- execution already finalized;
- execution already cancelled;
- replay report not found;
- ledger unavailable;
- retention operation skipped;
- timeout;
- storage unavailable.

Errors should include:

- code;
- message;
- details;
- retryable flag direction;
- correlation ID;
- related RunId or ExecutionId;
- diagnostic hint direction.

Good errors reduce support cost.

---

# 7. Diagnostics Experience

Diagnostics should guide developers.

Diagnostics can summarize:

- execution status;
- failed steps;
- retry state;
- cancellation state;
- queue state;
- runtime instance state;
- provider state;
- Decision Ledger health;
- replay availability;
- policy decision summary;
- retention lifecycle status;
- observability status.

Diagnostics are especially important because the platform is distributed and stateful.

A developer should be able to ask:

> What is wrong with this execution?

And get a useful answer.

---

# 8. Configuration Experience

Configuration should be clear.

The platform is configuration-driven, so configuration must be documented carefully.

Configuration areas include:

- runtime mode;
- worker count;
- queue capacity;
- shared queue options;
- runtime instance options;
- provider options;
- HTTP runtime provider options;
- MCP options;
- replay options;
- ledger options;
- retention options;
- observability options;
- policy options;
- Redis options;
- MongoDB options.

Configuration should include examples for common modes.

---

## Configuration Examples

Common configuration examples should include:

- local development;
- Redis/Mongo-backed runtime;
- MCP control plane;
- runtime-instance-only host;
- control plane with local runtime instances;
- HTTP runtime provider;
- shared queue enabled;
- observability enabled direction;
- replay enabled;
- ledger strict/best-effort direction;
- retention policy direction.

Configuration examples make the platform easier to evaluate.

---

# 9. Policy Developer Experience

Because the policy engine is a major foundation, policy authoring must become developer-friendly.

Policy DX should include:

- policy examples;
- policy-by-context examples;
- RBAC context examples;
- ARN-inspired resource examples;
- policy decision result model;
- policy test direction;
- policy simulation direction;
- policy diagnostics;
- policy Decision Ledger events.

Developers should be able to create policies without modifying the runtime core.

---

## Policy Example Types

Examples can include:

- tenant quota policy;
- provider/model access policy;
- tool access policy;
- replay access policy;
- ledger access policy;
- retention policy;
- compaction policy;
- concurrency policy;
- throttling policy;
- banking-oriented policy profile direction.

This shows the value of the pluggable policy engine.

---

# 10. Step Developer Experience

Pluggable steps are important for extensibility.

Step developer experience should include:

- custom step examples;
- step contract documentation;
- input/output schema direction;
- cancellation behavior;
- retry behavior;
- timeout behavior;
- policy requirements;
- replay behavior;
- retention behavior;
- observability metadata.

A developer should understand how to add a new step type safely.

---

# 11. Provider Developer Experience

Provider-based architecture is one of the strongest foundations.

Provider DX should include:

- provider contract documentation;
- local provider example;
- HTTP runtime provider example;
- custom provider direction;
- provider result model;
- provider diagnostics;
- provider errors;
- provider telemetry;
- transport abstraction explanation.

This is especially important for future gRPC, message bus, NATS, RabbitMQ, or cloud queue directions.

---

# 12. MCP Developer Experience

MCP tools should be easy to discover and use.

MCP DX should include:

- tool list;
- tool purpose;
- input schema;
- output schema;
- examples;
- error behavior;
- diagnostics behavior;
- correlation IDs;
- replay examples;
- ledger examples;
- queue examples;
- runtime instance examples.

MCP is a major product interface.

It should be documented like an API.

---

# 13. Documentation Structure

The documentation should support several user journeys.

## New Visitor

A new visitor should understand:

- what the platform is;
- why it matters;
- what already exists;
- how it differs from LLMOps tools;
- where to start.

## Developer

A developer should understand:

- how to run it locally;
- how to submit a workflow;
- how to inspect execution;
- how to replay;
- how to inspect ledger;
- how to configure runtime.

## Contributor

A contributor should understand:

- architecture;
- extension points;
- tests;
- coding patterns;
- provider model;
- policy model.

## Enterprise Evaluator

An enterprise evaluator should understand:

- replay/audit;
- policy engine;
- RBAC context;
- retention lifecycle;
- observability;
- deployment direction;
- banking/financial-services technical controls.

Documentation should guide each audience.

---

# 14. Onboarding Path

A strong onboarding path can be:

```text
README
  -> Product Roadmap Index
      -> What Already Exists
      -> Current Foundation
      -> Quick Start
      -> Run Sample Workflow
      -> Replay Execution
      -> Inspect Decision Ledger
      -> MCP Tools
      -> Distributed Demo
```

This path should be easy to follow.

---

# 15. API and SDK Compatibility

As the platform evolves, public API and SDK surfaces should avoid unnecessary breaking changes.

The project now separates public models from internal models and uses explicit schema versions at the SDK boundary. Productization should continue defining:

- compatibility guidelines;
- deprecation policy;
- migration notes;
- distribution/package versioning;
- generated client/API documentation.

This matters if external developers or partners begin using the runtime.

---

# 16. Developer Trust

Developer trust comes from:

- clear docs;
- working examples;
- predictable APIs;
- meaningful errors;
- strong tests;
- visible replay;
- visible ledger;
- clear configuration;
- local setup;
- honest roadmap;
- no overclaiming.

This project should keep that trust.

---

# Current Foundation Summary

| Area | Status |
|---|---|
| Runtime foundation | Foundation exists |
| Replay/audit foundation | Foundation exists |
| Decision Ledger foundation | Foundation exists |
| Policy engine foundation | Foundation exists |
| RBAC/context foundation | Foundation exists |
| MCP control-plane foundation | Foundation exists |
| Provider-based hosting foundation | Foundation exists |
| HTTP runtime provider direction | Foundation exists |
| Runtime-instance-only mode | Foundation exists |
| Shared queue direction | Foundation exists |
| Runtime instance/worker model | Foundation exists |
| Retention/eviction/compaction foundation | Foundation exists |
| Observability direction | Foundation exists |
| Product roadmap documentation | Foundation exists |
| Immutable publication and run pinning | Server implementation with targeted validation |
| Hosted Python / TypeScript / .NET functions | Server implementation with real-process validation |
| Deterministic dependency packaging | Pure-Python wheels, locked Node source bundles, and managed .NET assembly closures implemented server-side; no runtime `pip`/npm/NuGet resolution |
| Hosted worker isolation | Selected Linux/amd64 OCI `SandboxedContainer` provider implemented and validated server-side; the public SDK boundary remains provider-agnostic and Kubernetes sandbox-Pod materialization remains separate |
| Hosted custom Concurrency policies | Implemented at the existing admission checkpoint |
| Hosted custom Retry policies | Implemented at the existing retry checkpoint through `retry/v1`; retry lifecycle authority remains server-side |
| Hosted custom Delegation policies | Implemented at the existing pre-child-allocation checkpoint through `delegation/v1`; child lifecycle authority remains server-side |
| Hosted custom Retention | Native-only in the current capability matrix |
| Policy kinds without independent checkpoints | `Timeout`, `CircuitBreaker`, `RateLimit`, `Validation`, and `Routing` are not advertised as hosted families |
| Outbound MCP transport and effect metadata | Implemented |
| Durable MCP external-effect evidence | Implemented server-side as an opt-in boundary with immutable intent, dispatch fencing, confirmed replay, conservative uncertainty handling, explicit reconciliation, and tenant-scoped persistence; public SDK clients remain provider-agnostic and must not become retry authority |
| Public SDK contracts + server boundary | Implemented and validated for publication, submission, observation, result, and cancellation; contract assembly is engine-independent |
| External SDK client libraries | Productization target (.NET/TypeScript/Python packaging and conveniences) |
| Broader API packaging | Productization target beyond the implemented MCP public boundary |
| CLI | Productization target |
| Quickstart documentation | Productization target |
| Examples | Productization target |
| Developer onboarding path | Productization target |

---

# Productization Roadmap

## Milestone 1 — Quickstart

Add or improve:

- local setup;
- prerequisites;
- run sample workflow;
- inspect execution;
- replay execution;
- inspect ledger;
- run MCP server direction.

## Milestone 2 — API Documentation

Document:

- run submission;
- execution inspection;
- replay;
- ledger inspection;
- pause/resume/cancel;
- queue inspection;
- runtime instance inspection;
- diagnostics.

## Milestone 3 — Examples

Add examples for:

- workflow execution;
- replay/audit;
- policy decision;
- custom step;
- custom policy;
- provider dispatch;
- MCP tools;
- retention lifecycle.

## Milestone 4 — CLI Direction

Prepare initial commands for:

- submit;
- inspect;
- replay;
- cancel;
- ledger;
- queue;
- instance;
- diagnostics.

## Milestone 5 — External SDK Libraries

Build language-specific client libraries on the implemented public contract/server boundary, with helpers for:

- publication and execution submission;
- status polling and waiting;
- terminal result retrieval;
- cancellation;
- replay/diagnostic surfaces as they are added publicly;
- error handling;
- correlation propagation.

---

# Planned Improvements

Developer experience should continue improving through:

- better quickstart;
- clearer README path;
- API docs;
- MCP tool docs;
- examples;
- configuration samples;
- external SDK library packaging;
- CLI direction;
- diagnostics;
- error model;
- policy authoring examples;
- step plugin examples;
- provider plugin examples;
- test documentation.

These are productization steps.

They make the existing runtime foundation easier to adopt.

---

# Final Statement

Developer experience is the bridge between a strong runtime and a usable platform.

The Deterministic AI Runtime Platform already has strong technical foundations.

The next step is to make those foundations easy to run, inspect, extend, integrate, and operate.

A production AI runtime should not only be powerful.

It should be approachable enough for developers to trust and use.
