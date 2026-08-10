# Testing and Reliability Strategy

## Deterministic AI Runtime Platform

This document describes the testing and reliability strategy of the Deterministic AI Runtime Platform.

Testing is not secondary in this project.

The platform is designed for production AI workflow execution, where reliability depends on deterministic state transitions, replayability, auditability, policy decisions, runtime control, distributed worker safety, provider-based dispatch, retention lifecycle safety, and observability.

The key idea is:

> A deterministic AI runtime must prove its behavior through tests, not only through architecture diagrams.

The runtime should be tested under normal execution, failure, retries, cancellation, replay, shared queue dispatch, provider communication, distributed workers, policy decisions, retention/eviction/compaction, and MCP control-plane scenarios.

---

## Purpose

The purpose of the testing and reliability strategy is to make the platform trustworthy.

Production AI workflows can fail in many ways:

- a step fails;
- a model provider times out;
- a tool call fails;
- a worker crashes;
- a runtime instance becomes unavailable;
- a claim becomes stale;
- a run remains queued;
- cancellation arrives while a step is running;
- replay data is missing;
- ledger events are incomplete;
- policy denies an operation;
- retention tries to clean active state;
- provider dispatch fails;
- queue pressure increases;
- finalization races with worker completion.

The platform must be tested against these situations.

Reliability is not a future cosmetic layer.

It is part of the runtime foundation.

---

## Current Foundation

The project already includes important testing and reliability foundations.

These include:

- deterministic runtime tests direction;
- replay and audit tests direction;
- decision ledger tests direction;
- MCP integration tests direction;
- shared queue tests direction;
- runtime instance tests direction;
- provider-based runtime hosting tests direction;
- HTTP runtime provider integration direction;
- pause/resume/cancel tests direction;
- distributed worker and multi-instance tests direction;
- retention, eviction, and compaction safety direction;
- Redis coordination direction;
- MongoDB durable history direction;
- integration test infrastructure direction;
- WebApplicationFactory-style integration test direction;
- Docker-backed Redis/Mongo test direction;
- reliability and chaos-style test direction.

The roadmap is not to introduce testing from zero.

The roadmap is to harden, organize, document, expand, and productize the existing testing strategy.

---

## Core Principle

The testing principle is:

```text
Every runtime guarantee should have a test.
Every distributed behavior should have a failure scenario.
Every control operation should be replayable and auditable.
Every policy decision should be inspectable.
Every cleanup operation should be safe under concurrency.
```

This keeps the project credible.

---

# 1. Runtime Core Tests

Runtime core tests should validate deterministic execution behavior.

They should cover:

- execution creation;
- execution state transitions;
- step readiness;
- dependency resolution;
- DAG execution;
- step completion;
- step failure;
- retry behavior;
- finalization;
- cancellation;
- pause/resume behavior;
- waiting-for-input direction;
- replay metadata direction.

The goal is to prove the runtime behaves consistently.

---

## Runtime Invariant Tests

Runtime invariant tests should ensure that impossible states do not occur.

Examples:

- a completed step should not run again;
- a failed terminal step should not become completed without valid transition;
- execution should not finalize while required steps are still active;
- cancellation should prevent future step scheduling;
- pause should prevent new work from being scheduled;
- retry should respect max retry and delay;
- finalization should be idempotent direction;
- claims should not be overwritten unsafely.

These tests protect the deterministic core.

---

# 2. DAG Execution Tests

DAG tests should validate workflow structure and execution order.

They should cover:

- root steps;
- dependent steps;
- parallel branches;
- terminal steps;
- failed dependencies;
- skipped steps;
- conditional direction;
- invalid dependency direction;
- cycle detection direction;
- deterministic step selection.

DAG behavior is central to the platform.

It must remain stable as new step types and pipeline builder features are added.

---

# 3. Replay and Audit Tests

Replay and audit tests should prove that execution history can be inspected after execution.

They should cover:

- replay report generation;
- audit-only replay;
- replay after completed execution;
- replay after failed execution;
- replay after cancellation;
- replay issue detection;
- replay timeline;
- replay validation;
- replay with compacted history direction;
- replay after hot-state eviction direction;
- replay with missing ledger event direction;
- replay with retained snapshot direction.

Replay tests are critical because replay is one of the platform's strongest differentiators.

---

# 4. Decision Ledger Tests

Decision Ledger tests should validate structured decision recording.

They should cover:

- execution lifecycle events;
- run lifecycle events;
- queue events;
- dispatch events;
- claim events;
- worker events;
- runtime instance events;
- policy events;
- retry events;
- cancellation events;
- replay events;
- finalization events;
- retention events;
- eviction events;
- compaction events;
- archive events;
- correlation identifiers.

The ledger should be tested both for content and correlation.

A ledger event without useful correlation is not enough.

---

# 5. Policy Engine Tests

The policy engine already exists as a governance foundation.

Tests should validate:

- policy evaluation;
- allowed decisions;
- denied decisions;
- failed decisions;
- throttled decisions direction;
- delayed decisions direction;
- policy-by-context behavior;
- tenant policy direction;
- project policy direction;
- pipeline policy direction;
- provider/model policy direction;
- tool access policy direction;
- replay access policy direction;
- retention policy direction;
- concurrency/throttling policy direction;
- ledger recording of policy decisions.

Policy tests prove that governance is not only documentation.

---

# 6. RBAC and Scoped Context Tests

RBAC-aware execution context is a key foundation.

Tests should validate:

- resource scope creation direction;
- ARN-inspired resource matching direction;
- subject/action/resource/context model direction;
- allowed access;
- denied access;
- tenant boundary direction;
- project boundary direction;
- replay access direction;
- ledger access direction;
- MCP access direction;
- tool/provider access direction.

This is especially important for enterprise and financial-services readiness.

---

# 7. Execution Control Tests

Execution control tests should validate production control operations.

They should cover:

- pause execution;
- resume execution;
- cancel queued run;
- cancel running execution;
- cancellation while step is running;
- cancellation before ExecutionId is exposed direction;
- repeated cancellation;
- pause while running;
- resume after pause;
- replay after cancellation;
- ledger events for control operations;
- MCP control responses.

Control operations must be reliable.

If users cannot trust pause/resume/cancel, the runtime is not production-ready.

---

# 8. Retry and Recovery Tests

Retry tests should validate deterministic retry behavior.

They should cover:

- retryable failure;
- non-retryable failure;
- retry count increment;
- max retry reached;
- retry delay respected;
- retry success after failure;
- retry exhaustion causing failure;
- replay of retry history;
- ledger events for retry;
- cancellation while waiting for retry;
- pause while waiting for retry;
- distributed retry readiness direction.

Retry is one of the most important runtime reliability features.

---

# 9. Claim and Worker Safety Tests

Distributed workers require claim safety.

Tests should validate:

- single worker claims a step;
- competing workers cannot claim the same step unsafely;
- stale claim behavior;
- expired claim recovery direction;
- worker identity recorded;
- runtime instance identity recorded;
- stale worker cannot overwrite completed result direction;
- finalization does not happen while active claim exists;
- retention does not evict active claim state.

These tests protect against duplicate execution.

---

# 10. Shared Queue Tests

Shared queue tests should validate multi-instance scheduling.

They should cover:

- run accepted into shared queue;
- run queued;
- run assigned;
- run dispatched;
- queue pressure;
- queue cancellation;
- no double dispatch;
- dispatch failure;
- queue pause/resume direction;
- capacity-aware dispatch;
- policy-driven admission;
- runtime instance selection;
- shared queue pump behavior.

The shared queue is critical for Kubernetes-style execution and managed hosting.

---

# 11. Runtime Instance Tests

Runtime instance tests should validate distributed execution capacity.

They should cover:

- runtime instance registration;
- heartbeat;
- capacity reporting;
- worker count reporting;
- local queue reporting;
- assigned runs;
- instance unavailable direction;
- stale heartbeat direction;
- instance capacity exhausted direction;
- runtime-instance-only mode;
- control-plane with runtime instances;
- multiple runtime instances;
- multiple workers per instance.

Runtime instance visibility is the foundation for managed hosting.

---

# 12. Provider and Transport Tests

Provider-based runtime hosting must be tested.

Tests should validate:

- local provider dispatch;
- HTTP runtime provider dispatch;
- provider failure;
- provider timeout;
- runtime instance unavailable;
- invalid dispatch response;
- cancellation propagation;
- diagnostics response;
- correlation propagation;
- provider latency direction;
- provider error classification;
- no duplicate execution after provider retry direction.

Transport should not change runtime semantics.

The same execution rules must hold whether work is local or remote.

---

# 13. MCP Integration Tests

MCP integration tests should validate the control-plane surface.

They should cover:

- submit run;
- inspect run;
- inspect execution;
- replay execution;
- pause execution;
- resume execution;
- cancel execution;
- inspect queue;
- inspect runtime instances;
- inspect workers direction;
- inspect decision ledger direction;
- inspect policy decisions direction;
- inspect diagnostics;
- inspect retention lifecycle direction.

MCP is a product interface, so its behavior should be tested like a product surface.

---

# 14. Retention, Eviction, Compaction, and Snapshot Tests

Retention lifecycle tests are critical.

They should validate:

- retention policy evaluation;
- snapshot required;
- snapshot created;
- snapshot skipped direction;
- hot-state eviction after finalization;
- eviction skipped while execution is active;
- eviction skipped while claim is active;
- stale claim cleanup;
- compaction after finalization;
- compaction skipped when replay report missing;
- archive created direction;
- replay after compaction;
- replay after hot-state eviction;
- ledger events for lifecycle decisions;
- policy-by-context retention behavior.

This proves cleanup is safe and audit-aware.

---

# 15. Observability Tests

Observability tests should validate telemetry.

They should cover:

- correlation identifiers present;
- structured logs emitted direction;
- metrics emitted direction;
- trace/correlation direction;
- ledger events emitted;
- runtime instance telemetry;
- worker telemetry;
- queue telemetry;
- provider telemetry;
- retry/cancellation telemetry;
- retention lifecycle telemetry;
- MCP telemetry.

Observability should not be left untested.

---

# 16. Integration Tests

Integration tests should validate real combinations of components.

Examples:

- Redis-backed coordination;
- MongoDB-backed ledger/replay;
- MCP server with runtime;
- shared queue with runtime instances;
- HTTP provider with runtime-instance-only host;
- replay after distributed execution;
- cancellation through MCP with remote runtime;
- retention after execution;
- dashboard API direction.

Integration tests prove the architecture works together.

---

# 17. Chaos and Reliability Tests

Chaos-style tests should validate behavior under stress and failure.

Possible scenarios:

- many executions;
- many steps;
- many workers;
- multiple runtime instances;
- worker crash direction;
- runtime instance unavailable direction;
- provider timeout;
- queue pressure;
- cancellation during execution;
- retry storms direction;
- stale claims;
- ledger write failure direction;
- replay after failure;
- retention under load.

Chaos tests are important because distributed runtime systems fail in non-linear ways.

---


# Validated Adversarial Process-Host Campaign

The current foundation now includes a completed parallel HTTP and gRPC process-host crash-recovery campaign.

Each scenario validates:

```text
two impacted tenants
    one in-flight execution
    two local-queued runs
    one real external process kill

one safe tenant
    three runs
    no process kill
    zero recovery contamination
```

The crash boundary is based on durable state and an exact assigned-work inventory, not on a fixed delay.

Current validation ladder:

| Level | Classification |
|---|---|
| P10 | Repeatedly green |
| P15 | Green intermediate validation |
| P20 | Heavy-pressure validation |
| P30 | Reproducibly stable validated ceiling |
| P35 | Completed on HTTP and gRPC; experimental local-machine edge |

At P35, each transport validated:

```text
35 scenarios
105 tenants
315 DAG executions
15,750 logical DAG step completions
70 real process kills
210 affected jobs recovered
```

The HTTP run also measured 4,191,448 Redis and MongoDB operations and 18.29 GiB of datastore traffic.

The product interpretation is:

> Capacity degraded before correctness did.

This is not a universal capacity guarantee. It is evidence that identity, ownership, tenant isolation, recovery, replay, ledger, trace, and forensics remained correct while the local environment saturated.

Detailed reference:

- [`../ai/concurrency-hardening-and-adversarial-validation.md`](../ai/concurrency-hardening-and-adversarial-validation.md)

---

# 18. Performance and Load Tests

Performance tests should validate runtime capacity.

They can measure:

- execution throughput;
- step throughput;
- queue throughput;
- dispatch latency;
- worker utilization;
- replay generation time;
- ledger write volume;
- retention lifecycle time;
- provider latency;
- memory/hot-state pressure;
- durable storage growth.

These tests support managed hosting and capacity planning.

---

# 19. Test Documentation

Testing should be documented.

Documentation should explain:

- how to run unit tests;
- how to run integration tests;
- how to run Redis/Mongo-backed tests;
- how to run MCP integration tests;
- how to run shared queue tests;
- how to run provider tests;
- how to run manual stress tests;
- how to interpret failures;
- which tests require Docker;
- which tests are skipped/manual.

This improves contributor and evaluator trust.

---

# 20. Reliability Guarantees

The platform should progressively prove reliability guarantees.

Examples:

- steps are not executed twice under normal claim rules;
- finalization is safe;
- replay can inspect completed execution;
- policy decisions are recorded;
- cancellation is visible;
- retries are bounded;
- runtime instances expose identity and capacity;
- shared queue does not double-dispatch;
- retention does not evict active execution state;
- provider dispatch does not break execution semantics;
- MCP control operations are auditable.

Each guarantee should map to tests.

---

# Current Foundation Summary

| Area | Status |
|---|---|
| Runtime tests direction | Foundation exists |
| DAG execution tests direction | Foundation exists |
| Replay/audit tests direction | Foundation exists |
| Decision Ledger tests direction | Foundation exists |
| Policy engine tests direction | Foundation exists / active direction |
| Execution control tests direction | Foundation exists |
| Retry tests direction | Foundation exists |
| Claim/worker safety tests direction | Foundation exists |
| Shared queue tests direction | Foundation exists |
| Runtime instance tests direction | Foundation exists / active direction |
| Provider-based hosting tests direction | Foundation exists / active direction |
| HTTP runtime provider integration direction | Foundation exists / active direction |
| MCP integration tests direction | Foundation exists |
| Retention/eviction/compaction tests direction | Foundation exists / active direction |
| Redis/Mongo integration test direction | Foundation exists |
| Chaos/reliability test direction | Foundation exists / active direction |
| Validated HTTP/gRPC process-host concurrency campaign | Implemented / validated through P35; P35 is the experimental local-machine edge |
| Durable crash-gate validation | Implemented / validated |
| Exact multi-tenant recovery inventory | Implemented / validated |
| Safe-tenant non-impact under parallel process loss | Implemented / validated |
| Performance/load tests | Productization target |
| Public test documentation | Productization target |
| Test reporting dashboard direction | Future direction |

---

# Productization Roadmap

## Runtime Pool Reliability Evidence

The Runtime Pool foundation is covered by a dedicated validation ladder.

```text
identity
    -> lifecycle
    -> route registry
    -> HTTP/gRPC forwarding
    -> real child failure
    -> exact suppression
    -> exact work inventory
    -> deterministic claim
    -> claimed transition
    -> historical regression
```

Validated final gates:

| Gate | Result |
|---|---|
| Real process-host pool replacement | Green |
| Stable HTTP pool routing | Green |
| Stable gRPC pool routing | Green |
| Exact claimed recovery | Green |
| Historical Process HTTP | P10 green |
| Historical Process gRPC | P10 green |
| Existing Kubernetes HTTP | P5 green |
| Existing Kubernetes gRPC | P5 green |

The P5 Kubernetes gates remain regression evidence for the historical one-runtime-per-Pod hosting modes. Dedicated HTTP/gRPC KubernetesPool production scenarios now validate hierarchical child and Pod failure recovery, warm reuse, and bounded capacity.

---

## Next Reliability Priorities

1. Broader multi-node KubernetesPool lifecycle and fault-domain tests;
2. real Pod deletion with host-wide suppression;
3. distributed recovery claim durability;
4. multi-control-plane claim arbitration;
5. hierarchical runtime/Pod/node capacity tests;
6. Redis Cluster key-slot and failover tests;
7. longer mixed-mode soak and chaos validation;
8. production telemetry for pool, host, route, failure, suppression, and claim state.

