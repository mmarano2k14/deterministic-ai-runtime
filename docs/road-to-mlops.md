# Road to MLOps

## Purpose

This document describes the longer-term evolution path of the **Deterministic AI Runtime** project.

The current runtime already implements a substantial execution foundation:

- deterministic DAG execution
- distributed worker coordination
- Redis Lua atomic orchestration
- retry and recovery
- runtime instance crash recovery
- distributed throttling
- retention and compaction
- replay foundations
- execution control state
- context resolution
- observability foundations
- control-plane orchestration
- shared queue dispatch
- tenant-aware runtime visibility
- runtime process-host provisioning
- recovery forensics
- ledger, trace, and replay validation across process boundaries

The long-term direction is broader than a standalone runtime engine.

The runtime should evolve progressively toward:

```text
AI execution infrastructure
AI operations platform
enterprise orchestration layer
MLOps-oriented runtime infrastructure
```

The objective is not to become another prompt wrapper or lightweight workflow tool.

The objective is to explore what production-grade AI execution infrastructure should look like when:

- determinism matters
- distributed coordination matters
- runtime crash recovery matters
- tenant isolation matters
- replayability matters
- operational control matters
- auditability matters
- bounded state matters
- governance matters
- enterprise execution reliability matters

The direction is simple:

```text
Treat AI execution as distributed systems infrastructure.
```

---

## Current Foundation

The current runtime already demonstrates important execution infrastructure concepts.

### Distributed Execution

The runtime supports:

- distributed workers
- shared Redis execution state
- Redis Lua atomic coordination
- deterministic convergence
- distributed retry recovery
- stale worker recovery
- distributed throttling
- execution ownership
- execution control state
- runtime instance identity
- worker identity
- shared queue ownership
- dispatch-time admission

The runtime already behaves more like execution infrastructure than a traditional orchestration wrapper.

The important distinction is that execution is state-driven.

A worker does not own the truth.

A runtime process does not own the truth.

Durable runtime state owns the truth.

---

### Control Plane and Runtime Instance Model

The runtime now includes a control-plane layer above local runtime queues.

This layer provides:

- shared run submission
- queue-first dispatch
- direct-dispatch scale-out requests
- shared queue persistence
- shared queue pump and manual drain
- dispatch-time admission
- runtime registry visibility
- runtime capacity descriptors
- provider-based runtime dispatch
- provider-based scale-out
- fulfilled scale-out run requeue
- runtime instance health visibility
- tenant-aware registry and capacity filtering

The control plane is intentionally separate from the DAG execution engine.

The control plane decides where work should go.

The runtime instance local queue receives work.

The DAG engine executes durable execution state.

This separation is essential for future MLOps-oriented infrastructure because it allows runtime capacity, execution control, observability, and governance to evolve without rewriting the execution engine.

---

### Runtime Process Crash Recovery

The runtime now validates an important production boundary:

```text
A runtime process may die,
but the execution must not disappear with it.
```

The validated process-host crash recovery model separates four responsibilities:

```text
RuntimeInstanceHealthReconciler
    detects unsafe, stale, unhealthy, or draining runtime capacity
    prevents unsafe capacity from being selected for new work

ExecutionRecoveryReconciler
    enumerates work already assigned to unsafe runtime capacity
    recovers in-flight DAG executions
    redispatches local queued shared runs

HTTP provider
    reports transport and endpoint failure signals
    dispatches to safe HTTP runtime capacity
    does not own runtime recovery
    does not kill, restart, or replace runtimes

Local runtime queue
    is volatile
    is never the durable source of truth
```

Durable truth remains in:

```text
SharedRunStore
SharedQueue
RuntimeRunExecutionIndex
DAG store
runtime registry and capacity descriptors
ledger / trace / forensics / replay evidence
```

This matters because MLOps is not only about deploying models or workflows.

It is also about knowing what happens when an execution environment fails while work is already assigned to it.

The runtime distinguishes two recovery cases:

| Recovery Case | Meaning | Recovery Strategy |
|---|---|---|
| `InFlightExecution` | The runtime process died while a DAG execution already existed. | Resume the same durable `ExecutionId` on replacement capacity. |
| `LocalQueued` | The runtime process died after the shared run was dispatched locally but before a DAG `ExecutionId` existed. | Redispatch the durable `SharedRunId` through the shared queue. |

This identity model is central:

```text
ExecutionId
    durable DAG execution identity
    preserved for in-flight recovery

SharedRunId
    durable shared submission identity
    used to redispatch work that never reached DAG execution

LocalRunId
    attempt-local runtime queue identity
    may change after recovery
```

The validated architecture proves that a local runtime queue may be lost without losing the durable execution story.

---

### Multi-Tenant Crash Isolation

The runtime recovery model is tenant-aware.

The validated scenario uses one shared control plane and multiple tenant-scoped runtime processes.

When two tenant runtime processes are killed, the safe tenant must continue without being pulled into recovery.

The important enterprise question is not only:

```text
Did failed work recover?
```

It is also:

```text
Did unrelated tenant work remain untouched?
```

The validated recovery proof includes:

- impacted tenant work is recovered
- safe tenant work is not recovered because it was never impacted
- no cross-tenant recovery leakage is visible
- no safe tenant forensics entries are produced
- no safe tenant ledger contamination is detected
- replay, ledger, and trace remain readable after recovery

This is one of the foundations for a future MLOps platform because operational recovery must respect tenant boundaries.

A recovery system that restores failed work but contaminates unrelated tenant evidence is not enterprise-safe.

---

### Context Resolution

The runtime includes a dedicated context resolution and helper layer.

This layer resolves:

- input bindings
- previous step outputs
- payload references
- provider metadata
- policy context
- concurrency context
- replay-safe reconstruction
- RAG execution context
- tenant execution context snapshots
- recovery resume context

This is important because many orchestration systems become difficult to maintain when execution context is rebuilt manually in many different places.

The project treats context resolution as a first-class runtime concern.

In a future MLOps platform, this layer becomes even more important because governance, audit, replay, cost tracking, provider routing, and tenant isolation all depend on correct context propagation.

---

### Replay, Ledger, Trace, and Audit Foundations

The runtime already includes:

- terminal snapshots
- replay restoration
- replay fingerprint validation
- deterministic execution convergence
- retry diagnostics
- runtime tracing foundations
- execution-correlated ledger foundations
- replay report retrieval
- replay ledger retrieval
- replay trace retrieval
- process-boundary observability validation
- runtime recovery forensics

This creates the foundation for:

```text
replay systems
execution auditability
runtime governance
compliance-oriented execution history
incident reconstruction
recovery proof
```

Replay is not only a developer feature.

In an enterprise runtime, replay becomes an evidence mechanism.

It helps answer whether a completed or recovered execution can still be inspected, explained, and compared after the original runtime process has disappeared.

---

### Runtime Recovery Forensics

Recovery forensics records the operational evidence around runtime instance failure and assigned-work recovery.

It is not the same thing as tracing.

It is not the same thing as the execution ledger.

It is the incident-oriented recovery layer.

It helps answer:

- which runtime instance became unsafe?
- which tenant was affected?
- which shared runs were assigned to that runtime?
- which local queued runs had no `ExecutionId` yet?
- which in-flight executions needed resume?
- which replacement runtime was selected?
- which local run was created after recovery?
- did the same durable execution continue?
- did recovery leak into another tenant?

Typical recovery evidence includes:

```text
runtime-recovery:{ExecutionId}:{SharedRunId}:{LocalRunId}
runtime-recovery:local-queued:{SharedRunId}:{LocalRunId}
runtime-failure:{...}:{RuntimeInstanceId}
```

This forensics layer is important for the MLOps direction because operational AI systems need incident reconstruction, not only logs.

---

### Distributed Concurrency and Throttling

The runtime already supports:

- distributed concurrency admission
- Redis ZSET lease coordination
- provider throttling
- model throttling
- operation throttling
- execution-level limits
- runtime-instance limits
- worker-capacity visibility
- max local workers per execution

The `throttling-100` enterprise demo scenario demonstrates:

- provider-targeted throttling
- realtime throttling visibility
- bounded distributed capacity
- deterministic convergence despite throttling

This begins to move the runtime toward operational AI infrastructure rather than only orchestration.

In a future MLOps platform, throttling is not only a technical control.

It becomes part of provider governance, cost governance, tenant policy, and operational reliability.

---

## Why the MLOps Direction Matters

Most AI systems begin as experimentation.

Over time they evolve into operational infrastructure.

That transition creates new requirements:

- governance
- observability
- replayability
- execution control
- distributed coordination
- bounded execution state
- tenant isolation
- cost visibility
- provider governance
- runtime fleet management
- operational reliability
- auditability
- reproducibility
- incident reconstruction
- recovery proof

Traditional workflow systems often do not address these concerns deeply enough for AI execution.

Agent frameworks often focus on behavior and composition.

Observability platforms often focus on traces and model calls.

Infrastructure platforms often focus on containers and services.

The runtime direction is to progressively bridge the gap between these worlds.

It does not need to replace them.

It needs to provide the deterministic execution layer that makes AI workflows safer to operate under real production conditions.

---

## Long-Term Platform Direction

The runtime may evolve progressively into a broader platform.

The roadmap should be understood as:

```text
Runtime Engine
    ->
Distributed AI Execution Infrastructure
    ->
AI Operations Platform
    ->
MLOps-Oriented Runtime Layer
```

This does not mean every capability must be built immediately.

It means the current runtime architecture is intentionally designed so that future platform capabilities can be layered on top without rewriting the execution core.

The engine should remain focused on deterministic state transitions.

The control plane should evolve toward operational management.

The observability layer should evolve toward inspection, audit, dashboards, and incident reconstruction.

The provider layer should evolve toward runtime fleet integration.

The governance layer should evolve toward enterprise policy, cost, and compliance controls.

---

## Potential Future Areas

### AI Execution Control Plane

Possible future capabilities:

- centralized execution monitoring
- runtime fleet management
- distributed runtime coordination
- execution pause/resume/cancel dashboards
- execution replay management
- runtime cluster visibility
- operational execution controls
- recovery incident views
- unsafe runtime capacity views
- assigned-work recovery views
- tenant-scoped execution dashboards

The control plane should remain an operational layer above execution, not a replacement for the DAG engine.

---

### Runtime Governance

Possible future capabilities:

- provider governance
- model governance
- policy governance
- execution approval rules
- audit policies
- execution retention policies
- compliance workflows
- governance reporting
- tenant-specific runtime policies
- recovery policy configuration
- runtime isolation policy inspection

Governance should build on the existing policy-driven execution model rather than bypass it.

---

### Cost Governance

Possible future capabilities:

- token accounting
- provider budget limits
- tenant cost limits
- execution cost visibility
- provider fallback policies
- throttling based on budget pressure
- execution cost attribution
- retry cost attribution
- recovery cost attribution
- wasted call detection

Cost governance should eventually connect to execution identity, tenant identity, provider/model/operation metadata, and replay/ledger evidence.

---

### Multi-Agent Coordination

Possible future capabilities:

- agent identity
- agent execution permissions
- scoped execution contexts
- multi-agent orchestration
- agent-to-agent coordination
- execution isolation
- agent governance
- agent memory boundaries
- agent-specific replay and audit trails

The runtime does not need to become an agent framework to support agent workloads.

A stronger direction is to provide durable execution infrastructure underneath agent behavior.

---

### AI Memory and Decision Systems

Possible future capabilities:

- durable decision history
- execution memory systems
- memory retention policies
- decision replay
- long-running execution memory
- execution lineage
- execution graph persistence
- tenant-scoped memory boundaries
- memory compaction and rehydration policies

This area should extend the current retention, replay, ledger, and context resolution foundations.

---

### Operational Observability

Possible future capabilities:

- execution dashboards
- DAG visualization
- distributed tracing
- provider usage visibility
- replay visualization
- runtime health monitoring
- runtime capacity monitoring
- throttling visibility
- retry analytics
- recovery analytics
- safe tenant non-impact evidence
- execution drift visibility
- control-plane causal-chain views
- recovery forensics views

The runtime already emits important signals.

The future platform direction is to turn those signals into operational views that help teams understand what happened, why it happened, and whether the system recovered safely.

---

### Kubernetes and Runtime Operations

Possible future capabilities:

- Kubernetes deployment assets
- runtime autoscaling
- runtime operators
- distributed worker fleets
- runtime scheduling
- runtime orchestration APIs
- operational deployment tooling
- Kubernetes runtime provider
- pod-based runtime process replacement
- tenant-scoped runtime pools
- zero-downtime runtime upgrades

The current process-host Runtime Host Manager is an important stepping stone.

It proves that the control plane can request runtime capacity, launch a real external runtime process, wait for registration/capacity, dispatch over HTTP, and validate replay/ledger/trace across a process boundary.

Kubernetes can later replace the process creation strategy without changing the execution recovery model.

---

### Recovery and Incident Management

Possible future capabilities:

- runtime failure incident registry
- recovery dashboard
- recovery policy configuration
- recovery SLA metrics
- recovered work reports
- tenant impact reports
- safe tenant non-impact reports
- recovery replay comparison
- recovery forensics export
- automated incident summaries

This area builds directly on the validated runtime crash recovery proof.

The long-term value is not only that work can recover.

The long-term value is that the platform can explain the recovery.

---

## Important Positioning

This project should not be positioned as:

```text
finished commercial platform
fully complete MLOps suite
production SaaS product
```

The project should instead be positioned as:

```text
serious execution infrastructure foundation
advanced distributed AI runtime exploration
enterprise-oriented execution architecture
MLOps-oriented runtime direction
```

The runtime core is already substantial.

The broader platform direction is intentionally progressive.

The honest position is stronger than an exaggerated one:

```text
The project does not claim to solve every MLOps problem today.
It focuses first on the execution reliability layer that many AI systems need before higher-level operations can be trusted.
```

---

## Guiding Principles

All future platform evolution should preserve the current runtime principles:

- deterministic convergence
- explicit execution state
- replayability
- distributed safety
- atomic coordination
- bounded hot state
- explicit context resolution
- stateless workers
- policy-driven execution
- observable runtime behavior
- operational transparency
- tenant-aware isolation
- durable identity separation
- recovery without local queue dependency
- audit evidence after failure

Future platform capabilities should extend these foundations rather than bypass them.

---

## Relationship with Existing MLOps Tools

The long-term direction is not necessarily to replace existing MLOps systems.

Instead, the runtime may eventually complement:

- orchestration platforms
- model governance platforms
- deployment systems
- observability stacks
- vector infrastructure
- provider gateways
- AI governance tooling
- Kubernetes operations
- workflow engines
- agent frameworks

The strongest focus of this project remains:

```text
execution reliability
execution coordination
distributed runtime behavior
replayability
runtime governance foundations
recovery evidence
```

A useful way to position the runtime is:

```text
Existing MLOps tools often manage models, deployments, experiments, and monitoring.
This runtime focuses on the execution substrate that carries AI work safely through distributed state, failures, retries, recovery, and audit.
```

---

## Final Direction

The long-term ambition can be summarized simply:

```text
Treat AI execution as distributed systems infrastructure.
```

The runtime foundations already exist.

The broader AI operations and MLOps-oriented platform direction will continue evolving progressively over time.

The current recovery work strengthens that direction because it shows a concrete operational property:

```text
When a runtime process dies, the durable execution story can continue.
```

That is the kind of foundation an MLOps-oriented runtime infrastructure needs before dashboards, governance, cost controls, and deployment automation can become truly meaningful.
