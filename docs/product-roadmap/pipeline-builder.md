# Pipeline Builder

## Deterministic AI Runtime Platform

This document describes the Pipeline Builder direction for the Deterministic AI Runtime Platform.

The Pipeline Builder is the visual product layer that will allow users to design, configure, validate, version, and run deterministic AI workflows.

It is important to clarify one point:

> The Pipeline Builder is a productization layer on top of the existing runtime foundation.  
> It is not disconnected from the engine.

The project already has several foundations that make a future Pipeline Builder realistic:

- deterministic runtime execution;
- DAG-based workflow direction;
- step lifecycle foundation;
- execution state foundation;
- retry and recovery direction;
- policy-driven runtime decisions;
- provider-driven architecture;
- replay and audit foundation;
- decision ledger foundation;
- MCP control-plane foundation;
- distributed worker and runtime instance direction;
- observability direction;
- retention, eviction, and compaction foundation.

The Pipeline Builder should expose these foundations visually.

---

## Purpose

The purpose of the Pipeline Builder is to make deterministic AI workflow creation accessible through a visual interface.

Without a builder, workflows may be defined through code, configuration, tests, or internal structures.

That is powerful for engineering, but a product needs a clearer user experience.

The Pipeline Builder should allow users to:

- create AI workflow graphs visually;
- configure steps;
- connect dependencies;
- define model/provider/tool behavior;
- define input/output mapping;
- configure retry and timeout policies;
- configure policy and governance rules;
- define human-in-the-loop steps;
- validate workflows before execution;
- version pipeline definitions;
- execute test runs;
- inspect runtime behavior after execution;
- connect pipeline definitions to replay, audit, ledger, dashboard, and MCP control.

The builder turns the runtime into a workflow product.

---

## Product Positioning

The Pipeline Builder is not only a drag-and-drop UI.

It should become the visual design surface for deterministic AI execution.

It should allow users to define workflows that the runtime can execute safely, replay later, audit, observe, and scale.

The correct product relationship is:

```text
Pipeline Builder = define the workflow
Runtime Engine   = execute the workflow deterministically
Decision Ledger  = record runtime decisions
Replay/Audit     = inspect and validate execution history
Dashboard        = observe and operate execution
MCP Interface    = control execution and diagnostics
```

This separation keeps the product clean.

The builder should not contain hidden execution behavior.

The runtime remains the execution authority.

---

## Current Foundation

The Pipeline Builder is a future product layer, but it is backed by existing runtime foundations.

Existing foundations include:

- DAG execution foundation;
- step-based workflow structure;
- execution state;
- step lifecycle;
- deterministic scheduling direction;
- retry direction;
- cancellation direction;
- pause/resume direction;
- policy decision foundation;
- provider-driven execution direction;
- replay and audit foundation;
- decision ledger foundation;
- observability direction;
- MCP control-plane direction;
- runtime instance and worker direction.

This means the Pipeline Builder can be built on top of real runtime concepts rather than invented UI abstractions.

The goal is to expose the runtime model visually.

---

## Core Principle

The Pipeline Builder should follow this principle:

> A visual pipeline must compile into a deterministic runtime definition.

The visual graph should not be only decorative.

It should produce a workflow definition that the runtime can understand, validate, execute, replay, audit, and observe.

Every visual element should map to a runtime concept:

| Builder Concept | Runtime Concept |
|---|---|
| Pipeline | Workflow definition |
| Node | Step |
| Edge | Dependency |
| Step configuration | Runtime step metadata |
| Input mapping | Step input binding |
| Output mapping | Step output binding |
| Retry settings | Retry policy |
| Timeout settings | Execution policy |
| Concurrency settings | Policy/concurrency decision |
| Human approval | Waiting-for-input / approval step direction |
| Provider/model selection | Provider/model context |
| Tool selection | Tool execution step |
| Pipeline version | Workflow/pipeline version |
| Test run | Run + Execution |
| Execution view | Runtime state + replay + ledger |

This mapping is essential.

If the builder does not map clearly to runtime concepts, it will become hard to maintain and hard to audit.

---

# Builder Scope

The Pipeline Builder should evolve progressively.

It does not need to start as a fully advanced low-code platform.

It should start by exposing the most important runtime concepts.

---

## 1. Visual DAG Editor

The first foundation is a visual DAG editor.

The visual DAG editor should allow users to:

- create steps;
- connect steps;
- define dependencies;
- see execution order direction;
- identify root steps;
- identify terminal steps;
- detect disconnected steps;
- detect cycles;
- validate dependency structure;
- prepare a runtime-executable graph.

The DAG editor should respect deterministic execution constraints.

A workflow graph should be valid before it can run.

---

## DAG Validation

The builder should validate graph structure.

Validation can check:

- no cycles;
- no missing step references;
- no invalid dependencies;
- no duplicate step keys;
- no missing required input;
- no invalid terminal structure;
- no unsupported step type;
- no invalid provider/model configuration;
- no missing policy configuration when required;
- no invalid retry or timeout configuration.

Validation should happen before execution.

This prevents invalid workflows from reaching the runtime.

---

## 2. Step Types

The builder should support step types that map to runtime execution.

Possible step types include:

| Step Type | Purpose |
|---|---|
| Prompt Step | Prepare or execute an LLM prompt. |
| Model Step | Call a configured AI model/provider. |
| Tool Step | Execute a tool or external operation. |
| Retrieval Step | Retrieve data from a source or vector store direction. |
| Policy Step | Evaluate policy or governance logic. |
| Validation Step | Validate output or intermediate data. |
| Transformation Step | Transform input/output data. |
| Human Approval Step | Wait for human review or approval direction. |
| Conditional Step | Route execution based on state or output. |
| Notification Step | Notify a system or user direction. |
| Final Output Step | Produce final workflow output. |

The builder should not need to support every advanced type immediately.

The first version can support a small set and grow over time.

---

## 3. Step Configuration

Each step should have a configuration panel.

The configuration panel can expose:

- step name;
- step key;
- step type;
- description;
- input bindings;
- output bindings;
- provider/model settings;
- tool settings;
- retry settings;
- timeout settings;
- policy settings;
- concurrency settings;
- cancellation behavior direction;
- observability labels;
- retention behavior direction.

Step configuration should remain aligned with runtime configuration.

The builder should not create settings that the runtime cannot execute.

---

## 4. Input and Output Mapping

AI workflows require clear data flow.

The builder should support input/output mapping direction.

Input/output mapping can include:

- map execution input to a step;
- map previous step output to a later step;
- map tool output to model input;
- map retrieval result to prompt context;
- map validation result to branch condition;
- map final step output to workflow result.

This is essential for multi-step workflows.

A deterministic runtime needs to understand where each step gets its input and how outputs move through the graph.

---

## 5. Provider and Model Configuration

The builder should allow provider/model configuration direction.

This can include:

- provider selection;
- model selection;
- operation type;
- temperature direction;
- timeout direction;
- provider-specific configuration;
- policy restrictions;
- fallback direction;
- observability labels;
- cost/usage metadata direction.

Provider and model configuration should connect to runtime context.

This allows the policy engine and decision ledger to know which provider/model/operation was involved.

---

## 6. Tool Configuration

Tool steps should be configurable.

Tool configuration can include:

- tool name;
- tool input schema;
- tool output schema;
- allowed operations;
- timeout;
- retry policy;
- policy requirement;
- side-effect marker;
- audit sensitivity;
- replay behavior direction.

Tool configuration is important because tool steps may create side effects.

The builder should help users distinguish between:

```text
safe inspection step
```

and

```text
side-effecting operation
```

This matters for replay and audit.

---

## 7. Policy Configuration

Policy configuration is one of the strongest product directions.

Because the runtime is policy-driven, the builder should eventually expose policy settings visually.

Policy configuration can include:

- execution admission policy;
- step execution policy;
- provider/model access policy;
- tool access policy;
- concurrency policy;
- throttling policy;
- retry policy;
- cancellation policy;
- replay access policy;
- retention policy direction;
- sensitive data handling direction.

The builder should help users define governance behavior before execution.

A workflow should not only define what to run.

It should also define what is allowed to run.

---

## 8. Retry, Timeout, and Recovery Configuration

The builder should expose retry and recovery configuration.

This can include:

- max retries;
- retry delay;
- exponential backoff direction;
- retryable error types;
- non-retryable error types;
- timeout duration;
- failure behavior;
- fallback step direction;
- compensation step direction;
- human intervention direction.

This is important because AI workflows often depend on external services.

Retry and recovery behavior should be explicit, visible, and replayable.

---

## 9. Concurrency and Throttling Configuration

The builder should expose concurrency and throttling configuration direction.

This can include:

- global concurrency direction;
- pipeline concurrency direction;
- step concurrency direction;
- provider concurrency direction;
- model concurrency direction;
- operation concurrency direction;
- tenant quota direction;
- queue admission direction;
- runtime instance capacity direction.

This should integrate with the policy engine and decision ledger.

Concurrency and throttling decisions must be:

- deterministic;
- recorded;
- observable;
- auditable;
- safe under distributed execution.

---

## 10. Human-in-the-Loop Steps

The builder should support human-in-the-loop direction.

Human steps can include:

- approval;
- manual review;
- data correction;
- decision confirmation;
- sensitive action confirmation;
- escalation;
- waiting-for-input.

This is important because not every AI workflow should run fully automatically.

Some workflows require human control.

Human-in-the-loop steps should be visible in:

- execution state;
- dashboard;
- replay report;
- decision ledger;
- MCP control operations.

---

## 11. Conditional Branching

The builder should support conditional branching direction.

Conditional branching can allow:

- route based on step output;
- route based on validation result;
- route based on policy result;
- route based on confidence score direction;
- route based on human approval;
- route based on external data.

Branching should remain deterministic at the orchestration layer.

The runtime should record which branch was selected and why.

The decision ledger should preserve branch decisions when they matter.

---

## 12. Pipeline Versioning

Pipeline versioning is critical for replay and audit.

The builder should support versioned pipeline definitions.

Versioning can include:

- draft version;
- published version;
- active version;
- previous version;
- rollback direction;
- version comparison;
- execution pinned to pipeline version;
- replay linked to pipeline version;
- audit report linked to pipeline version.

This is important because an execution must be explainable later.

If a pipeline changes after execution, replay and audit should still know which version was used.

---

## 13. Pipeline Validation

Before execution, the builder should validate the pipeline.

Validation should include:

- graph validation;
- step configuration validation;
- required input validation;
- provider/model validation;
- tool configuration validation;
- policy configuration validation;
- retry/timeout validation;
- retention configuration validation;
- security boundary validation direction;
- tenant/project context validation direction.

A pipeline should not be executable until it passes validation.

Validation errors should be clear and actionable.

---

## 14. Test Run Mode

The builder should support test-run direction.

Test-run mode can allow users to execute a pipeline in a controlled environment.

Test runs can support:

- sample input;
- dry-run direction;
- audit-only validation direction;
- mock provider direction;
- restricted tool execution direction;
- replay report generation;
- decision ledger inspection;
- validation of step configuration;
- detection of missing inputs;
- demonstration workflow.

Test-run mode is important for product usability.

It allows users to verify workflows before production execution.

---

## 15. Templates and Reusable Components

The builder should eventually support templates.

Templates can include:

- document analysis pipeline;
- retrieval-augmented generation pipeline;
- approval workflow;
- classification workflow;
- summarization workflow;
- policy-check workflow;
- log analysis workflow;
- RBAC investigation workflow;
- support ticket workflow;
- audit workflow.

Reusable components can include:

- reusable steps;
- reusable prompts;
- reusable tools;
- reusable policy blocks;
- reusable retry profiles;
- reusable retention profiles direction.

Templates help users start quickly.

They also demonstrate product value.

---

## 16. Pipeline Execution from Builder

The builder should allow users to execute a pipeline.

Execution from the builder should create:

- RunId;
- ExecutionId;
- correlation ID;
- pipeline version reference;
- execution context;
- decision ledger events;
- replay metadata direction;
- dashboard visibility.

The builder should not execute workflows directly outside the runtime.

It should submit work to the runtime or control plane.

This keeps execution consistent and auditable.

---

## 17. Builder and MCP

The builder should align with MCP.

MCP can expose operations such as:

- submit pipeline run;
- inspect execution;
- replay execution;
- pause execution;
- resume execution;
- cancel execution;
- inspect ledger;
- inspect diagnostics.

The builder can use the same concepts exposed by MCP.

This keeps the product consistent.

A pipeline created visually should be controllable through MCP and visible in the dashboard.

---

## 18. Builder and Replay / Audit

The builder should connect directly to replay and audit.

A user should be able to:

- run a pipeline;
- inspect execution state;
- open replay report;
- inspect audit timeline;
- inspect policy decisions;
- inspect retry/cancellation history;
- inspect retained-history status.

This makes the builder more than a design surface.

It becomes part of the full execution lifecycle.

---

## 19. Builder and Decision Ledger

The builder should connect to the decision ledger.

Important builder-related ledger events can include:

- pipeline created direction;
- pipeline validated;
- pipeline validation failed;
- pipeline version published;
- pipeline run submitted;
- policy configuration applied;
- retry policy applied;
- retention policy applied;
- execution created from pipeline version.

The ledger should help explain how a visual pipeline became a runtime execution.

---

## 20. Builder and Dashboard

The builder and dashboard should work together.

The builder defines workflows.  
The dashboard observes workflow execution.

Users should be able to move between:

```text
Pipeline definition -> Execution -> Replay -> Ledger -> Dashboard diagnostics
```

This creates a complete product loop:

1. Build.
2. Validate.
3. Run.
4. Observe.
5. Replay.
6. Audit.
7. Improve.

---

## 21. Builder and Retention / Eviction / Compaction

The builder should eventually expose retention configuration direction.

This can include:

- retention profile;
- payload retention direction;
- replay report retention;
- ledger retention;
- trace retention;
- compact after completion direction;
- archive direction;
- hot-state eviction timing direction;
- sensitive payload handling direction.

This does not need to be part of the first version.

But it matters because execution data lifecycle is part of enterprise AI execution.

A pipeline may require different retention behavior depending on its purpose.

---

## 22. Builder and Multi-Tenant Readiness

The builder should eventually support tenant/project/pipeline boundaries.

This can include:

- tenant-owned pipelines;
- project-owned pipelines;
- pipeline permissions;
- pipeline version visibility;
- tenant-specific templates;
- tenant-specific providers;
- tenant-specific policies;
- tenant-specific retention profiles;
- tenant-specific runtime capacity direction.

Multi-tenant readiness is important for SaaS, managed hosting, and enterprise deployment models.

---

## 23. Builder and Security Direction

The builder can expose sensitive configuration.

Security direction should include:

- permission-controlled editing;
- tenant-aware access;
- project-level access;
- protected provider credentials direction;
- secret references instead of raw secrets;
- redacted prompt or payload views direction;
- audit of pipeline changes direction;
- version history;
- approval before publishing direction.

A pipeline builder is powerful.

It must not become an uncontrolled interface for running tools or accessing sensitive data.

---

## 24. Builder User Journeys

The builder should support practical user journeys.

## Journey 1 — Create a Simple AI Workflow

A user should be able to:

1. Create a pipeline.
2. Add a prompt/model step.
3. Add a validation step.
4. Add a final output step.
5. Validate the graph.
6. Run a test execution.
7. Inspect the result.

---

## Journey 2 — Create a Tool-Based Workflow

A user should be able to:

1. Add a model step.
2. Add a tool step.
3. Configure tool input.
4. Add policy requirement.
5. Add retry/timeout policy.
6. Run the pipeline.
7. Inspect replay and ledger events.

---

## Journey 3 — Create a Governed Workflow

A user should be able to:

1. Define pipeline context.
2. Configure provider/model access policy.
3. Configure tool access policy.
4. Configure concurrency/throttling direction.
5. Configure retention profile direction.
6. Validate policy settings.
7. Run the workflow.
8. Inspect policy decisions in the ledger.

---

## Journey 4 — Debug a Pipeline

A user should be able to:

1. Open failed execution from the dashboard.
2. Jump to the pipeline version.
3. Identify failed step.
4. Inspect retry/cancellation behavior.
5. Inspect replay report.
6. Adjust step configuration.
7. Create a new version.
8. Run again.

---

## Journey 5 — Prepare a Production Pipeline

A user should be able to:

1. Validate graph.
2. Validate provider/model configuration.
3. Validate policy configuration.
4. Validate retry/timeout behavior.
5. Validate retention profile direction.
6. Run test execution.
7. Review replay report.
8. Publish version.

---

# Builder Data Model Direction

The builder should produce a runtime-compatible definition.

A pipeline definition can include:

- PipelineId;
- version;
- name;
- description;
- tenant/project context direction;
- steps;
- dependencies;
- input schema direction;
- output schema direction;
- provider/model configuration;
- tool configuration;
- retry policies;
- timeout policies;
- concurrency policies;
- policy references;
- retention profile direction;
- observability labels;
- metadata;
- created/updated timestamps.

A step definition can include:

- StepId or StepKey;
- name;
- type;
- dependencies;
- configuration;
- input mapping;
- output mapping;
- retry settings;
- timeout settings;
- policy references;
- provider/model/tool references;
- retention behavior direction;
- observability metadata.

The builder definition should be stable enough for replay and audit.

---

# Builder Validation Output

Validation output should be structured.

It can include:

- valid/invalid status;
- errors;
- warnings;
- missing inputs;
- invalid dependencies;
- invalid policies;
- invalid provider/model configuration;
- invalid retention configuration;
- unsupported step type;
- cycle detection;
- recommended fixes.

This makes the builder usable and safer.

---

# Builder and Single-Developer Roadmap

Because the project is currently built and maintained by one developer, the Pipeline Builder should be staged carefully.

The first version should not try to become a full enterprise low-code product.

It should prove the runtime-product connection.

## Suggested Stages

### Stage 1 — Pipeline Definition Model

- define pipeline schema direction;
- define step schema direction;
- define dependency model;
- define validation output;
- connect pipeline definition to runtime execution.

### Stage 2 — Basic Visual DAG Editor

- add nodes;
- connect nodes;
- edit step names;
- edit basic configuration;
- validate graph;
- run test execution.

### Stage 3 — Runtime Integration

- submit run;
- generate RunId;
- generate ExecutionId;
- show execution status;
- open dashboard execution;
- open replay report;
- open ledger events.

### Stage 4 — Policy / Retry / Provider Configuration

- provider/model configuration;
- retry/timeout configuration;
- policy configuration;
- concurrency/throttling direction;
- tool configuration.

### Stage 5 — Versioning and Templates

- pipeline versioning;
- version comparison direction;
- publish version direction;
- templates;
- reusable blocks.

### Stage 6 — Enterprise Hardening

- tenant/project boundaries;
- permission-aware editing;
- retention profile direction;
- redacted views;
- audit of pipeline changes;
- controlled publish workflow.

This staged approach keeps the roadmap realistic.

---

# Current Foundation Summary

| Area | Status |
|---|---|
| DAG execution foundation | Foundation exists |
| Step lifecycle foundation | Foundation exists |
| Execution state foundation | Foundation exists |
| Retry/recovery direction | Foundation exists |
| Policy-driven execution | Foundation exists |
| Provider-driven architecture | Foundation exists |
| Replay/audit foundation | Foundation exists |
| Decision ledger foundation | Foundation exists |
| MCP control-plane foundation | Foundation exists |
| Dashboard integration concepts | Foundation exists |
| Observability direction | Foundation exists |
| Retention/eviction/compaction foundation | Foundation exists |
| Visual builder UI | Productization target |
| Pipeline schema | Productization target |
| Visual DAG editor | Productization target |
| Pipeline versioning | Productization target |
| Templates | Productization target |
| Enterprise permission model | Planned hardening direction |
| Tenant-aware pipeline builder | Planned hardening direction |

---

# Productization Roadmap

## Milestone 1 — Define Pipeline Schema

Create a stable definition model for:

- pipelines;
- steps;
- dependencies;
- input/output bindings;
- provider/model configuration;
- tool configuration;
- retry/timeout policies;
- policy references;
- retention profile direction;
- metadata.

## Milestone 2 — Add Validation

Add validation for:

- graph structure;
- step configuration;
- provider/model configuration;
- policy configuration;
- retry/timeout configuration;
- unsupported patterns;
- tenant/project context direction.

## Milestone 3 — Build Visual DAG Editor

Add:

- graph canvas;
- nodes;
- edges;
- step configuration panel;
- validation panel;
- execution button;
- test-run flow.

## Milestone 4 — Connect to Runtime

Add:

- submit run;
- create execution;
- monitor status;
- open execution dashboard;
- open replay report;
- open decision ledger timeline.

## Milestone 5 — Add Governance Configuration

Add:

- policy configuration;
- concurrency/throttling direction;
- tool access direction;
- provider/model access direction;
- retention profile direction.

## Milestone 6 — Add Versioning and Templates

Add:

- pipeline versions;
- draft/published state;
- version comparison;
- rollback direction;
- reusable templates;
- reusable step blocks.

## Milestone 7 — Add Enterprise Hardening

Add:

- tenant/project ownership;
- access control;
- audit of changes;
- redaction;
- secret references;
- compliance profile direction.

---

# Planned Improvements

The Pipeline Builder should continue through staged productization:

- pipeline definition model;
- visual DAG editor;
- step configuration panel;
- graph validation;
- runtime execution integration;
- test-run mode;
- provider/model configuration;
- tool configuration;
- policy configuration;
- retry/timeout configuration;
- concurrency/throttling configuration;
- human-in-the-loop steps;
- replay/audit integration;
- decision ledger integration;
- dashboard integration;
- versioning;
- templates;
- tenant/project boundaries;
- retention profile direction;
- security hardening.

These are productization steps.

They expose the existing runtime foundation through a visual workflow creation experience.

---

# Final Statement

The Pipeline Builder is the visual design layer for the Deterministic AI Runtime Platform.

It should allow users to create AI workflows that are not only executable, but deterministic, replayable, auditable, governable, observable, and scalable.

The builder is powered by existing platform foundations:

- DAG execution;
- step lifecycle;
- execution state;
- policy-driven runtime;
- provider-driven architecture;
- replay and audit;
- decision ledger;
- MCP control;
- dashboard visibility;
- distributed execution;
- retention, eviction, and compaction.

The long-term goal is to create a complete product loop:

> Build visually.  
> Validate safely.  
> Run deterministically.  
> Observe in the dashboard.  
> Control through MCP.  
> Replay and audit every execution.  
> Improve the pipeline through structured runtime evidence.
