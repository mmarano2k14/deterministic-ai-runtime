# Documentation Index

This directory contains the documentation for **Deterministic AI Runtime**.

The main repository README is the public entry point. It explains the project, its purpose, current capabilities, validated evidence, and roadmap.

The complete technical reference is preserved at the root of this `docs/` directory.

Focused AI runtime documentation is organized under:

- [`ai/`](ai/)

---

## Start Here

| Document | Purpose |
|---|---|
| [`../README.md`](../README.md) | Main repository entry point with the current runtime capabilities, validated evidence, roadmap, and documentation map. |
| [`runtime-internals.md`](runtime-internals.md) | Complete technical reference preserved from the original README. |
| [`enterprise-readiness.md`](enterprise-readiness.md) | Matrix of enterprise AI execution questions and runtime answers. |
| [`ai/architecture-overview.md`](ai/architecture-overview.md) | High-level runtime architecture and major runtime layers, including shared control-plane orchestration, provider dispatch, Redis coordination, and multi-tenant runtime isolation. |
| [`ai/multi-tenant-control-plane-isolation.md`](ai/multi-tenant-control-plane-isolation.md) | Multi-tenant control-plane isolation, RBAC execution-context propagation, durable `ExecutionContextSnapshot`, tenant-aware registry/capacity/admission, Shared/Dedicated/Hybrid runtime visibility, and tenant-aware scale-out. |
| [`ai/multi-tenant-runtime-flow.md`](ai/multi-tenant-runtime-flow.md) | End-to-end ASCII runtime flow explaining MCP/RBAC context resolution, durable `ExecutionContextSnapshot`, shared run persistence, tenant-aware admission, tenant-aware scale-out, shared queue dispatch, local runtime queue execution, DAG worker loop, execution control, finalization, and observability. |
| [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md) | Runtime control-plane foundation covering replay, execution control, runtime queues, runtime registry/capacity, discovery, admission, shared controller orchestration, scale-out lifecycle, and tenant-aware dispatch. |
| [`ai/runtime-pool-architecture.md`](ai/runtime-pool-architecture.md) | Runtime Pool architecture across ProcessHostPool and KubernetesPool, including independent child identity, ProcessHost/Pod failure boundaries, exact routing, warm reuse, bounded capacity, and hierarchical recovery. |
| [`ai/runtime-pool-failure-recovery.md`](ai/runtime-pool-failure-recovery.md) | Exact Runtime Pool child and full-boundary recovery across ProcessHostPool and KubernetesPool, including durable failure facts, exact work claims, same-`ExecutionId` resume, warm reuse, and sibling-boundary isolation. |
| [`ai/runtime-pool-failure-authority.md`](ai/runtime-pool-failure-authority.md) | Shared durable MongoDB Runtime Pool failure authority, failure scopes, current-state separation, incident identity, exact suppression, and historical-evidence rules. |
| [`ai/runtime-lifecycle-journal.md`](ai/runtime-lifecycle-journal.md) | Append-only MongoDB runtime lifecycle history for hosts, Pods, runtimes, incidents, replacement, and run placement. |
| [`ai/runtime-pool-production-validation.md`](ai/runtime-pool-production-validation.md) | Final HTTP/gRPC × ProcessHostPool/KubernetesPool production matrix: 600 completed DAGs, 30,000 logical steps, 16 injected failures, 48 recoveries, zero loss/duplicate/capacity violations. |
| [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md) | Runtime discovery, registry, and capacity foundation covering Redis control-plane discovery, `ControlPlaneIdResolver`, runtime registration, tenant-filtered capacity descriptors, pump readiness, cleanup, local scale-out, and HTTP pooled runtime identity. |
| [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md) | MCP server as a runtime control-plane adapter, including host modes, RBAC integration, MCP tool groups, runtime role separation, local runtime pools, Redis/local scale-out, shared queue dispatch, and Kubernetes direction. |
| [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md) | Provider-based runtime instance administration for local, HTTP, gRPC, Redis command queue, and Kubernetes providers, including tenant-aware dispatch/status/control/scale-out capabilities and provider routing. |
| [`ai/http-runtime-provider.md`](ai/http-runtime-provider.md) | HTTP runtime provider reference covering hardened dispatch, timeout/retry/circuit breaker behavior, structured failure reasons, HTTP provider scale-out, Runtime Host Manager process-host provisioning, real `RuntimeInstanceOnly` process launch, tenant-aware Shared/Dedicated/Hybrid policy validation, and process-boundary observability. |
| [`ai/mcp-production-runtime-scenario-framework.md`](ai/mcp-production-runtime-scenario-framework.md) | MCP production runtime scenario framework covering Runtime Host Manager modes, HTTP/gRPC process-host scale-out, real `RuntimeInstanceOnly` child processes, Dedicated/Shared/Hybrid tenant scenarios, retention, ledger, trace, and replay validation across process boundaries. |
| [`ai/provider-agnostic-process-host-recovery.md`](ai/provider-agnostic-process-host-recovery.md) | Provider-agnostic process-host recovery reference explaining the shared HTTP/gRPC recovery scenario base, transport-neutral crash recovery contract, real process kill validation, strict DAG resume, local-queued redispatch, and tenant-safe non-impact proof. |
| [`ai/runtime-process-crash-recovery.md`](ai/runtime-process-crash-recovery.md) | Runtime process crash recovery reference covering health detection, unsafe runtime capacity, execution recovery reconciliation, in-flight DAG resume, local-queued redispatch, replacement runtime selection, and durable recovery truth. |
| [`ai/runtime-recovery-forensics.md`](ai/runtime-recovery-forensics.md) | Runtime recovery forensics reference covering `ForensicsId`, `RuntimeFailureIncidentId`, per-work-item recovery timelines, duplicate recovery detection, safe tenant non-impact proof, and MCP forensics queries. |
| [`ai/multi-tenant-runtime-crash-isolation.md`](ai/multi-tenant-runtime-crash-isolation.md) | Multi-tenant runtime crash isolation reference proving tenant A/B crash recovery while a safe tenant remains untouched, with no cross-tenant ledger leak, no recovery contamination, and no safe-tenant forensics. |
| [`ai/control-plane-ledger-causal-chain.md`](ai/control-plane-ledger-causal-chain.md) | Control-plane ledger causal chain reference covering scale-out, provider selection, host creation, registry/capacity visibility, recovery reconciliation, redispatch, tenant scoping, and audit proof. |
| [`ai/recovery-replay-ledger-trace-proof.md`](ai/recovery-replay-ledger-trace-proof.md) | Recovery proof reference explaining why recovered work must validate replay, ledger, trace, completion evidence, step evidence, forensics, and tenant-scoped observability after convergence. |
| [`ai/shared-controller-usage.md`](ai/shared-controller-usage.md) | Shared runtime controller usage, queue-first/direct-dispatch modes, Redis stores, scale-out request persistence, tenant snapshot propagation, manual drain, and background pump setup. |
| [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md) | Shared queue pump, fulfilled-run requeue, dispatch-time admission, context restoration, worker capacity visibility, and `MaxLocalWorkersPerExecution`. |
| [`ai/testing-strategy.md`](ai/testing-strategy.md) | Testing strategy and validation approach for distributed runtime guarantees, RBAC context propagation, tenant isolation, Redis/local scale-out, HTTP/gRPC process-host provisioning, runtime crash recovery, safe-tenant isolation, recovery forensics, replay/ledger/trace proof, requeue, dispatch, and execution evidence. |
| [`ai/concurrency-hardening-and-adversarial-validation.md`](ai/concurrency-hardening-and-adversarial-validation.md) | Adversarial concurrency and crash-recovery validation reference covering exact pre-crash inventories, durable crash gates, readiness, single-flight scale-out, claims and leases, P10–P35 validation evidence, local saturation boundaries, content-agnostic steps, and production runtime-pool interpretation. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated runtime decision ledger, audit foundations, retention auditability, and replay lifecycle event correlation. |
| [`ai/observability.md`](ai/observability.md) | High-level observability index summarizing ledger, tracing, metrics, logs, correlation, replay diagnostics, and roadmap direction. |
| [`ai/observability-tracing.md`](ai/observability-tracing.md) | Runtime tracing, trace timelines, correlation, trace storage modes, Mongo trace persistence, MemoryAndMongo mode, and tracing improvements. |
| [`ai/runtime-metrics.md`](ai/runtime-metrics.md) | Runtime metric domains, metric storage modes, worker/retention/storage/resolver/hot-state/policy metrics, and metrics improvements. |
| [`ai/replay-and-audit.md`](ai/replay-and-audit.md) | Deterministic Replay Engine V1, snapshot restore, fingerprint validation, replay metadata, ledger/timeline diagnostics, and replay improvements. |
| [`comparison-existing-tools.md`](comparison-existing-tools.md) | Ecosystem positioning against agent frameworks, workflow engines, orchestration tools, observability platforms, and distributed infrastructure. |
| [`roadmap.md`](roadmap.md) | Project roadmap organized by phases. |
| [`product-roadmap/runtime-pool-roadmap.md`](product-roadmap/runtime-pool-roadmap.md) | Delivered ProcessHostPool/KubernetesPool status and remaining multi-control-plane, Redis Cluster, multi-node, and managed-hosting scale work. |

---

## Recommended Reading Paths

### For CTOs, Engineering Managers, and Recruiters

Start with:

1. [`../README.md`](../README.md)
2. [`enterprise-readiness.md`](enterprise-readiness.md)
3. [`comparison-existing-tools.md`](comparison-existing-tools.md)
4. [`roadmap.md`](roadmap.md)

This path explains what the project is, why it matters, and how it maps to enterprise AI execution problems.

### For Architects and Senior Engineers

Start with:

1. [`../README.md`](../README.md)
2. [`ai/architecture-overview.md`](ai/architecture-overview.md)
3. [`ai/multi-tenant-control-plane-isolation.md`](ai/multi-tenant-control-plane-isolation.md)
4. [`ai/multi-tenant-runtime-flow.md`](ai/multi-tenant-runtime-flow.md)
5. [`enterprise-readiness.md`](enterprise-readiness.md)
6. [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md)
7. [`ai/runtime-pool-architecture.md`](ai/runtime-pool-architecture.md)
8. [`ai/runtime-pool-failure-recovery.md`](ai/runtime-pool-failure-recovery.md)
9. [`ai/runtime-pool-failure-authority.md`](ai/runtime-pool-failure-authority.md)
10. [`ai/runtime-lifecycle-journal.md`](ai/runtime-lifecycle-journal.md)
11. [`ai/runtime-pool-production-validation.md`](ai/runtime-pool-production-validation.md)
12. [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md)
13. [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md)
14. [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md)
15. [`ai/http-runtime-provider.md`](ai/http-runtime-provider.md)
16. [`ai/grpc-runtime-provider.md`](ai/grpc-runtime-provider.md)
17. [`ai/mcp-production-runtime-scenario-framework.md`](ai/mcp-production-runtime-scenario-framework.md)
18. [`ai/provider-agnostic-process-host-recovery.md`](ai/provider-agnostic-process-host-recovery.md)
19. [`ai/runtime-process-crash-recovery.md`](ai/runtime-process-crash-recovery.md)
20. [`ai/runtime-recovery-forensics.md`](ai/runtime-recovery-forensics.md)
21. [`ai/multi-tenant-runtime-crash-isolation.md`](ai/multi-tenant-runtime-crash-isolation.md)
22. [`ai/control-plane-ledger-causal-chain.md`](ai/control-plane-ledger-causal-chain.md)
23. [`ai/recovery-replay-ledger-trace-proof.md`](ai/recovery-replay-ledger-trace-proof.md)
24. [`ai/shared-controller-usage.md`](ai/shared-controller-usage.md)
25. [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md)
26. [`ai/distributed-execution.md`](ai/distributed-execution.md)
27. [`ai/execution-control-state.md`](ai/execution-control-state.md)
28. [`ai/observability.md`](ai/observability.md)
29. [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md)
30. [`ai/observability-tracing.md`](ai/observability-tracing.md)
31. [`ai/runtime-metrics.md`](ai/runtime-metrics.md)
32. [`ai/replay-and-audit.md`](ai/replay-and-audit.md)
33. [`runtime-internals.md`](runtime-internals.md)

This path gives both the strategic positioning and the complete technical depth.

### For Contributors

Start with:

1. [`ai/architecture-overview.md`](ai/architecture-overview.md)
2. [`ai/multi-tenant-control-plane-isolation.md`](ai/multi-tenant-control-plane-isolation.md)
3. [`ai/multi-tenant-runtime-flow.md`](ai/multi-tenant-runtime-flow.md)
4. [`ai/config-driven-runtime.md`](ai/config-driven-runtime.md)
5. [`ai/policy-driven-execution.md`](ai/policy-driven-execution.md)
6. [`ai/context-resolution-and-helpers.md`](ai/context-resolution-and-helpers.md)
7. [`ai/step-plugins.md`](ai/step-plugins.md)
8. [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md)
9. [`ai/runtime-pool-architecture.md`](ai/runtime-pool-architecture.md)
10. [`ai/runtime-pool-failure-recovery.md`](ai/runtime-pool-failure-recovery.md)
11. [`ai/runtime-pool-failure-authority.md`](ai/runtime-pool-failure-authority.md)
12. [`ai/runtime-lifecycle-journal.md`](ai/runtime-lifecycle-journal.md)
13. [`ai/runtime-pool-production-validation.md`](ai/runtime-pool-production-validation.md)
14. [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md)
15. [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md)
16. [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md)
17. [`ai/http-runtime-provider.md`](ai/http-runtime-provider.md)
18. [`ai/grpc-runtime-provider.md`](ai/grpc-runtime-provider.md)
19. [`ai/mcp-production-runtime-scenario-framework.md`](ai/mcp-production-runtime-scenario-framework.md)
20. [`ai/provider-agnostic-process-host-recovery.md`](ai/provider-agnostic-process-host-recovery.md)
21. [`ai/runtime-process-crash-recovery.md`](ai/runtime-process-crash-recovery.md)
22. [`ai/runtime-recovery-forensics.md`](ai/runtime-recovery-forensics.md)
23. [`ai/multi-tenant-runtime-crash-isolation.md`](ai/multi-tenant-runtime-crash-isolation.md)
24. [`ai/control-plane-ledger-causal-chain.md`](ai/control-plane-ledger-causal-chain.md)
25. [`ai/recovery-replay-ledger-trace-proof.md`](ai/recovery-replay-ledger-trace-proof.md)
26. [`ai/shared-controller-usage.md`](ai/shared-controller-usage.md)
27. [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md)
28. [`ai/distributed-execution.md`](ai/distributed-execution.md)
29. [`ai/execution-control-state.md`](ai/execution-control-state.md)
30. [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md)
31. [`ai/observability-tracing.md`](ai/observability-tracing.md)
32. [`ai/runtime-metrics.md`](ai/runtime-metrics.md)
33. [`ai/replay-and-audit.md`](ai/replay-and-audit.md)
34. [`ai/testing-strategy.md`](ai/testing-strategy.md)
35. [`runtime-internals.md`](runtime-internals.md)
36. [`roadmap.md`](roadmap.md)

This path gives the current architecture, configuration model, RBAC/context propagation model, tenant isolation model, control-plane/runtime split, extension model, technical reference, and next planned improvements.

---

## Core Documentation

### [`runtime-internals.md`](runtime-internals.md)

The complete technical reference preserved from the original README.

It includes detailed explanations of:

- runtime architecture
- DAG execution
- Redis hot state
- Redis Lua coordination
- distributed workers
- retry and recovery
- retention and compaction
- payload externalization
- rehydration resolver
- distributed concurrency and throttling
- execution control state
- runtime queue control
- observability
- deterministic replay engine and snapshot foundations
- replay metadata, ledger, and timeline diagnostics
- execution-correlated decision ledger
- roadmap and vision

This document intentionally keeps the original depth. It should not be deleted.

### [`enterprise-readiness.md`](enterprise-readiness.md)

A structured matrix answering key enterprise AI runtime questions:

- worker crashes
- duplicate execution prevention
- replay
- auditability
- concurrency limits
- pause/resume/cancel
- human-in-the-loop
- bounded memory/state
- multi-runtime-instance coordination
- deterministic convergence

### [`ai/architecture-overview.md`](ai/architecture-overview.md)

High-level architecture overview for the deterministic runtime.

This document explains:

- major runtime layers
- shared controller and shared queue orchestration
- scale-out request lifecycle
- fulfilled-run requeue
- provider-based dispatch
- local, local scale-out, and HTTP pooled runtime hosting
- runtime instance and worker capacity
- Redis hot state and distributed coordination
- replay, observability, retention, and policy layers
- multi-tenant control-plane/runtime isolation as a first-class architecture boundary

### [`ai/multi-tenant-control-plane-isolation.md`](ai/multi-tenant-control-plane-isolation.md)

Multi-tenant control-plane isolation reference.

This document explains:

- RBAC integration at the MCP/control-plane boundary
- why `ExecutionContextSnapshot.TenantId` is the durable tenant boundary
- why `ContextKey` is volatile RBAC/correlation/debug context
- why metadata is only diagnostic, not routing authority
- how shared runs persist `ExecutionContextSnapshot`
- how the shared queue dispatcher restores context before admission and dispatch
- how runtime local queues require an execution context snapshot before execution
- tenant runtime settings
- Shared, Dedicated, and Hybrid isolation modes
- runtime instance visibility rules
- tenant-aware registry and capacity filtering
- tenant-aware admission
- tenant-aware scale-out request persistence
- local scaler isolation by `RuntimeInstanceIdPrefix`
- validated test evidence for multi-tenant control-plane isolation

### [`ai/multi-tenant-runtime-flow.md`](ai/multi-tenant-runtime-flow.md)

End-to-end multi-tenant runtime flow reference.

This document explains:

- the complete MCP/API/control-plane to DAG execution path using a large ASCII flow
- how RBAC `ExecutionContext` becomes a durable `ExecutionContextSnapshot`
- where context can be lost across async, Redis, shared queue, provider, and local queue boundaries
- why ambient `IExecutionContextAccessor.Current` is not enough for background execution
- how shared runs, shared queue items, local queued runs, and background controller execution preserve the snapshot
- how tenant-aware admission sees only visible runtime registry and capacity records
- how tenant settings are copied into scale-out requests
- how local scale-out avoids cross-tenant counting by using `RuntimeInstanceIdPrefix`
- how worker execution, retry, recovery, execution control, finalization, and observability fit into the same flow
- the enterprise demo lesson learned: direct local runtime queue paths also require a durable snapshot

### [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md)

Execution-correlated runtime decision ledger foundations.

This document explains:

- execution-correlated runtime auditability
- structured runtime decision recording
- execution versus run correlation
- claim and concurrency audit visibility
- retry and recovery audit visibility
- queue and execution control observability
- human-in-the-loop auditability
- retention and compaction auditability
- snapshot persistence audit events
- finalization race visibility
- replay lifecycle event correlation

The document also explains how replay lifecycle events are correlated with the same execution ledger model used by the rest of the runtime.

### [`ai/replay-and-audit.md`](ai/replay-and-audit.md)

Deterministic replay and audit foundations.

This document explains:

- replay-as-validation using persisted snapshots
- audit-only replay
- restore from persisted snapshot
- deterministic replay fingerprint comparison
- replay metadata
- payload reference validation
- replay lifecycle ledger events
- replay timeline diagnostics
- 100-step distributed replay reference tests
- replay log examples
- replay TODO and improvement roadmap

### [`ai/observability.md`](ai/observability.md)

High-level observability index and summary.

This document links the focused observability areas:

- execution-correlated decision ledger
- observability and tracing
- runtime metrics

It explains how logs, metrics, traces, and ledger entries work together around a shared runtime correlation model.

### [`ai/observability-tracing.md`](ai/observability-tracing.md)

Runtime observability and tracing foundations.

This document explains:

- runtime observability facade
- runtime tracing facade
- in-memory trace recorder
- in-memory trace timeline
- trace correlation context
- trace store abstraction
- MongoDB-backed trace persistence
- trace storage modes: `Disabled`, `Memory`, `Mongo`, and `MemoryAndMongo`
- distributed chaos trace diagnostics
- tracing TODO and improvement roadmap

### [`ai/runtime-metrics.md`](ai/runtime-metrics.md)

Runtime metrics foundations.

This document explains:

- runtime metrics facade
- execution metrics
- worker metrics
- retention metrics
- storage metrics
- resolver metrics
- hot-state metrics
- policy metrics
- metric storage modes: `Disabled`, `Memory`, `Mongo`, and `MemoryAndMongo`
- distributed chaos metrics diagnostics
- metrics TODO and improvement roadmap

### [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md)

Runtime control-plane and orchestration foundations.

This document explains:

- replay control-plane facade
- execution control-plane facade
- local runtime queue control-plane facade
- runtime instance registry
- runtime instance capacity store
- control-plane discovery store
- control-plane id resolver
- runtime instance control-plane facade
- run admission and slot decisioning
- admission reservations
- Redis-backed scale-out request persistence
- scale-out watcher/provider selector lifecycle
- fulfilled scale-out shared run requeue
- RunId versus ExecutionId separation at the control-plane level
- queue pause/resume ledger correlation behavior
- shared runtime controller foundations
- RBAC execution-context restoration before background admission/dispatch
- tenant-aware runtime visibility and dispatch

### [`ai/runtime-pool-architecture.md`](ai/runtime-pool-architecture.md)

Professional reference for the opt-in Runtime Pool hosting model.

It documents:

- `PoolId`, `HostId`, `RuntimeInstanceId`, and `RouteId`;
- real process-host child lifecycle;
- stable HTTP and gRPC endpoints;
- exact route resolution and forwarding leases;
- graceful draining;
- targeted A1-to-A4 replacement;
- compatibility with historical Process and Kubernetes modes;
- ProcessHostPool/KubernetesPool production validation and remaining distributed-scale direction.

### [`ai/runtime-pool-failure-recovery.md`](ai/runtime-pool-failure-recovery.md)

Professional reference for exact failure and claimed recovery inside a Runtime Pool.

It documents:

- durable failure observations and exact failure scope;
- exact runtime and host-membership suppression;
- suppression-aware routing and capacity;
- exact assigned-work enumeration;
- deterministic inventory fingerprints;
- atomic claim and lease semantics;
- in-flight `ExecutionId` preservation;
- local-queued `SharedRunId` redispatch;
- child recovery with parent/sibling preservation;
- full ProcessHost and Pod boundary recovery;
- warm reuse and current distributed-coordination boundaries.

### [`ai/runtime-pool-failure-authority.md`](ai/runtime-pool-failure-authority.md)

Canonical reference for the shared durable MongoDB failure authority, exact failure scopes, incident identity, suppression evidence, and separation between correctness facts and lifecycle history.

### [`ai/runtime-lifecycle-journal.md`](ai/runtime-lifecycle-journal.md)

Canonical reference for append-only host, Pod, runtime, failure, replacement, and run-placement history with durable topology reconstruction after cleanup.

### [`ai/runtime-pool-production-validation.md`](ai/runtime-pool-production-validation.md)

Public evidence document for the HTTP/gRPC × ProcessHostPool/KubernetesPool matrix, including exact workload sizes, injected failures, recovered work, warm reuse, replay/ledger/forensics validation, and aggregate results.

### [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md)

Runtime discovery, registry, and capacity foundations.

This document explains:

- Redis control-plane discovery store
- MCP-published logical control-plane identity
- `ControlPlaneIdResolver`
- runtime-only host discovery resolution
- runtime instance registration
- runtime heartbeat
- runtime capacity descriptor publication
- worker and run-slot capacity visibility
- shared queue pump readiness gate
- provider metadata for local, HTTP, and local scale-out dispatch
- scale-out-created runtime capacity visibility
- HTTP pooled runtime identity model
- registry and capacity shutdown cleanup
- TTL and self-healing direction
- tenant-aware runtime registry and capacity filtering
- validated Redis registry, capacity, discovery, tenant visibility, and admission reservation behavior

### [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md)

MCP server as a runtime control-plane adapter.

This document explains:

- MCP server purpose and scope
- RBAC integration
- `ControlPlaneOnly` mode
- `ControlPlaneWithLocalRuntimeInstances` mode
- `RuntimeInstanceOnly` mode
- runtime role separation between control-plane hosts and executable runtime instances
- control-plane discovery publication
- runtime-only host identity resolution
- local runtime instance pool behavior
- HTTP pooled runtime provider behavior
- Redis/local scale-out execution flow
- shared queue dispatch flow
- MCP tool groups
- RunId versus ExecutionId behavior in MCP tools
- local queue preservation rules
- tenant-aware MCP tool execution and context propagation
- Kubernetes direction for MCP/control-plane deployment

### [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md)

Provider-based runtime instance administration model.

This document explains:

- why runtime instance providers are needed
- provider discovery through class attributes
- provider capabilities for dispatch, status, control, capacity, and scale-out
- provider router responsibilities
- local provider behavior
- local provider scale-out capability
- Redis command queue provider direction
- HTTP provider and implemented gRPC provider direction
- Kubernetes provider responsibilities
- admission and provider separation
- Redis admission reservation foundation
- HTTP pooled runtime provider validation
- descriptor metadata keys for provider routing
- tenant-aware dispatch and scale-out boundaries

### [`ai/http-runtime-provider.md`](ai/http-runtime-provider.md)

HTTP runtime provider reference.

This document explains:

- HTTP provider responsibilities as both dispatch transport and scale-out-capable provider
- dispatch hardening with timeout, retry, circuit breaker, and structured failure reasons
- persisted dispatch failure behavior through shared run records and shared queue dispatch
- `HttpAiRuntimeInstanceProvider` implementing `IAiRuntimeScaleOutProvider`
- `IAiHttpRuntimeScaleOutProvisioner` and HTTP scale-out options
- Redis-backed scale-out request fulfillment through watcher and provider selector
- tenant-aware HTTP scale-out for Shared, Dedicated, and Hybrid runtime modes
- Dedicated tenants not silently falling back to shared HTTP capacity
- Hybrid tenants falling back to shared HTTP capacity when policy allows it
- metadata and transport keys used for HTTP runtime registry/capacity publication
- Runtime Host Manager process-host provisioning
- real `RuntimeInstanceOnly` process launch and readiness
- process-boundary ledger, trace, replay, and retention validation

### [`ai/grpc-runtime-provider.md`](ai/grpc-runtime-provider.md)

gRPC runtime provider reference.

This document explains:

- gRPC provider responsibilities as dispatch transport and scale-out-capable provider
- `ControlPlaneWithGrpcRuntimeInstances` host mode
- `GrpcAiRuntimeInstanceProvider` and gRPC dispatch path
- `IAiGrpcRuntimeScaleOutProvisioner` and gRPC scale-out options
- Redis-backed scale-out request fulfillment through watcher and provider selector using `ProviderHint = grpc`
- Runtime Host Manager process-host provisioning for real `RuntimeInstanceOnly` gRPC processes
- Kestrel HTTP/2 transport requirement for gRPC command endpoints
- `provider.name = grpc` and `transport.name = grpc` metadata publication
- tenant-aware runtime registration, capacity visibility, and dispatch
- process-boundary crash recovery proof through the same recovery contract as HTTP
- current gRPC readiness hardening direction

### [`ai/mcp-production-runtime-scenario-framework.md`](ai/mcp-production-runtime-scenario-framework.md)

MCP production runtime scenario framework and HTTP/gRPC process-host validation reference.

This document explains:

- MCP Runtime Host Manager purpose and lifecycle boundary
- host creation modes: Fixture, Process, Attach, and Kubernetes
- HTTP/gRPC process-host scale-out flow
- `ProcessAiRuntimeHostCreationStrategy`
- real `RuntimeInstanceOnly` process launch
- runtime registration, heartbeat, capacity, and readiness
- tenant runtime settings precedence during HTTP scale-out
- Dedicated, Shared, and Hybrid process-host scenarios
- adversarial multi-tenant Dedicated isolation validation
- mixed-tenant full production validation scenario
- retention, ledger, trace, replay report, replay ledger, and replay trace validation across process boundaries
- intentional boundaries around shared pooling, Hybrid fallback, Kubernetes, and health/recovery ownership separation

### [`ai/provider-agnostic-process-host-recovery.md`](ai/provider-agnostic-process-host-recovery.md)

Provider-agnostic process-host recovery reference.

This document explains:

- why process-host crash recovery is now shared across HTTP and gRPC providers
- the provider-neutral responsibilities of health reconciliation, execution recovery reconciliation, registry/capacity visibility, and durable recovery truth
- the shared scenario base used by HTTP and gRPC process-host crash recovery tests
- real `RuntimeInstanceOnly` process kill validation
- in-flight DAG resume with preserved `ExecutionId`
- local-queued shared-run redispatch through preserved `SharedRunId`
- safe-tenant non-impact and cross-tenant leak prevention
- replay, ledger, trace, and forensics proof after recovery convergence

### [`ai/runtime-process-crash-recovery.md`](ai/runtime-process-crash-recovery.md)

Runtime process crash recovery reference.

This document explains:

- the boundary between runtime health reconciliation and execution recovery reconciliation
- why the HTTP provider reports transport failure signals but does not own recovery
- how unsafe runtime capacity is removed from admission
- how assigned work is enumerated after a runtime process stops heartbeating
- how in-flight DAG executions resume with the same durable `ExecutionId`
- how local-queued work is redispatched through durable `SharedRunId` state
- why the local runtime queue is volatile and not the source of truth
- how replacement runtime capacity is selected or created
- which stores form the durable recovery truth
- validated real process-host recovery scenarios

### [`ai/runtime-recovery-forensics.md`](ai/runtime-recovery-forensics.md)

Runtime recovery forensics reference.

This document explains:

- per-work-item recovery forensics
- `ForensicsId` formats for in-flight and local-queued recovery
- `RuntimeFailureIncidentId` correlation
- in-flight resume recovery timelines
- local-queued recovery timelines
- duplicate recovery detection and idempotence evidence
- safe tenant non-impact proof
- tenant-scoped MCP forensics queries
- how forensics complements ledger, trace, and replay

### [`ai/multi-tenant-runtime-crash-isolation.md`](ai/multi-tenant-runtime-crash-isolation.md)

Multi-tenant runtime crash isolation reference.

This document explains:

- the three-tenant crash isolation scenario
- why tenant A and tenant B can recover while tenant C remains untouched
- safe tenant invariants such as `SafeTenantNonImpactValidated=true` and `SafeTenantRecoveryLeakDetected=false`
- no cross-tenant ledger leakage
- no safe-tenant recovery forensics
- no safe-tenant recovered work
- tenant-scoped replay, ledger, trace, and recovery evidence
- why proving non-impact is as important as proving recovery

### [`ai/control-plane-ledger-causal-chain.md`](ai/control-plane-ledger-causal-chain.md)

Control-plane ledger causal chain reference.

This document explains:

- the difference between execution ledger and control-plane causal chain ledger
- scale-out request persistence evidence
- watcher/provider/host-manager causal evidence
- runtime process host creation evidence
- registry/capacity visibility evidence
- execution recovery reconciliation evidence
- recovered work redispatch evidence
- tenant-scoped control-plane ledger queries
- how causal chain evidence supports audit-grade recovery proof

### [`ai/recovery-replay-ledger-trace-proof.md`](ai/recovery-replay-ledger-trace-proof.md)

Recovery replay, ledger, and trace proof reference.

This document explains:

- why recovery completion alone is not enough
- required proof surfaces after recovery convergence
- strict replay validation for recovered and safe executions
- replay reports, replay ledger, and replay trace queries
- execution ledger evidence
- execution trace evidence
- completion and step-completion evidence
- forensics evidence
- safe-tenant proof and cross-tenant isolation proof

### [`ai/shared-controller-usage.md`](ai/shared-controller-usage.md)

Shared runtime controller and shared queue usage guide.

This document explains:

- in-memory shared controller setup
- Redis shared run store setup
- Redis shared queue setup
- Redis scale-out request store setup
- store-backed scale-out request publisher
- direct-dispatch and queue-first modes
- shared run submission
- shared run listing and cancellation
- manual pump/drain
- background shared queue service
- dispatch-time admission
- fulfilled scale-out requeue
- RBAC execution-context snapshot propagation
- tenant-aware controller request examples

### [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md)

Shared queue pump and worker capacity model.

This document explains:

- queue-first submit mode
- direct-dispatch scale-out path
- fulfilled scale-out shared run requeue
- manual shared queue drain
- background shared queue pump
- dispatch-time admission
- shared queue dispatcher context restoration
- pump identity versus assigned runtime identity
- local runtime queue preservation
- shared queue no-double-dispatch behavior
- local, HTTP, and gRPC provider dispatch foundations
- HTTP pooled runtime dispatch validation
- Redis admission reservation foundation
- shared queue pump readiness gate
- runtime worker capacity visibility
- worker-aware `CanAcceptRun`
- `MaxLocalWorkersPerExecution`
- tenant-aware dispatch-time admission
- future admission capacity reservation
- Kubernetes-oriented runtime hosting direction

### [`comparison-existing-tools.md`](comparison-existing-tools.md)

A high-level ecosystem positioning document comparing the runtime with existing categories such as:

- agent frameworks
- workflow engines
- data orchestration tools
- observability platforms
- distributed compute systems
- infrastructure orchestration

This document does not rank tools. It clarifies where Deterministic AI Runtime fits architecturally.

### [`roadmap.md`](roadmap.md)

The project roadmap organized into phases:

- Completed
- Phase 0 — Documentation Restructure
- Phase 1 — Enterprise Demo
- Phase 2 — Real Enterprise Sample
- Phase 3 — Correlated Observability, Tracing, and Metrics
- Phase 4 — Kubernetes Deployment Demo
- Phase 5 — Public API / SDK Polish
- Phase 6 — Deterministic Replay Engine and Audit Foundations
- Phase 7 — Replay Controller, HTTP APIs, Dashboard, and Operational Tooling
- Phase 8 — Cost and Provider Governance
- Phase 9 — Articles / Public Positioning

---

## Runtime Architecture and Execution

| Document | Purpose |
|---|---|
| [`ai/architecture-overview.md`](ai/architecture-overview.md) | High-level runtime architecture and major runtime layers, including control-plane scale-out, fulfilled-run requeue, provider-based dispatch, and multi-tenant isolation. |
| [`ai/multi-tenant-control-plane-isolation.md`](ai/multi-tenant-control-plane-isolation.md) | RBAC context propagation, durable execution snapshots, tenant-aware runtime visibility, Shared/Dedicated/Hybrid isolation, registry/capacity filtering, and tenant-aware scale-out. |
| [`ai/multi-tenant-runtime-flow.md`](ai/multi-tenant-runtime-flow.md) | End-to-end ASCII runtime flow from MCP/RBAC through shared runs, tenant-aware admission, scale-out, shared queue dispatch, local runtime queue execution, DAG worker execution, finalization, and observability. |
| [`ai/distributed-execution.md`](ai/distributed-execution.md) | Distributed workers, Redis coordination, claims, leases, deterministic convergence, and context restoration across distributed/background execution hops. |
| [`ai/execution-control-state.md`](ai/execution-control-state.md) | ExecutionId-level pause, resume, cancel, waiting-for-input, control-state behavior, and interaction with tenant-aware execution snapshots. |
| [`ai/runtime-queue-control.md`](ai/runtime-queue-control.md) | RunId-level background controller queue control, hot enqueue, queue pause/resume, and RunId versus ExecutionId separation. |
| [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md) | Runtime control-plane foundation for replay, execution control, runtime queue control, runtime instance registry/control, discovery, capacity, admission, scale-out request lifecycle, and shared orchestration. |
| [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md) | Runtime discovery, Redis registry, Redis capacity descriptors, ControlPlaneIdResolver, pump readiness, tenant-filtered visibility, local scale-out capacity visibility, cleanup, and HTTP pooled runtime identity. |
| [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md) | MCP server adapter over runtime control-plane foundations, including RBAC integration, host modes, tool groups, role separation, local runtime pool behavior, Redis/local scale-out execution, and Kubernetes direction. |
| [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md) | Provider-based runtime instance administration, dispatch, and scale-out model for local, HTTP, gRPC, Redis command queue, and Kubernetes providers. |
| [`ai/http-runtime-provider.md`](ai/http-runtime-provider.md) | HTTP runtime provider hardening and scale-out reference, including failure reasons, retry/timeout/circuit breaker policy, tenant-aware scale-out, Runtime Host Manager process-host provisioning, real runtime launch, and readiness. |
| [`ai/mcp-production-runtime-scenario-framework.md`](ai/mcp-production-runtime-scenario-framework.md) | MCP production runtime scenario framework, HTTP/gRPC process-host flow, Host Manager modes, real `RuntimeInstanceOnly` processes, mixed-tenant production validation, and durable observability/replay evidence. |
| [`ai/provider-agnostic-process-host-recovery.md`](ai/provider-agnostic-process-host-recovery.md) | Shared HTTP/gRPC process-host crash recovery model and test contract for real process kill, unsafe capacity suppression, assigned-work reconciliation, strict resume, redispatch, and safe-tenant isolation. |
| [`ai/runtime-process-crash-recovery.md`](ai/runtime-process-crash-recovery.md) | Runtime process crash recovery architecture covering unsafe runtime detection, assigned-work reconciliation, in-flight resume, local-queued redispatch, and durable recovery truth. |
| [`ai/runtime-recovery-forensics.md`](ai/runtime-recovery-forensics.md) | Per-work-item runtime recovery forensics, failure incident correlation, recovery timelines, duplicate recovery detection, and MCP forensics query boundaries. |
| [`ai/multi-tenant-runtime-crash-isolation.md`](ai/multi-tenant-runtime-crash-isolation.md) | Multi-tenant crash isolation architecture proving impacted tenants recover while safe tenants remain untouched and uncontaminated. |
| [`ai/control-plane-ledger-causal-chain.md`](ai/control-plane-ledger-causal-chain.md) | Control-plane causal ledger model for scale-out, host creation, recovery reconciliation, redispatch, and tenant-scoped audit evidence. |
| [`ai/recovery-replay-ledger-trace-proof.md`](ai/recovery-replay-ledger-trace-proof.md) | Recovery proof model requiring replay, ledger, trace, completion evidence, step evidence, forensics, and tenant-scoped observability after recovery. |
| [`ai/shared-controller-usage.md`](ai/shared-controller-usage.md) | Shared controller setup and usage, Redis shared stores, queue-first/direct-dispatch modes, scale-out lifecycle, and tenant snapshot propagation. |
| [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md) | Shared queue pump, queue-first submit mode, direct-dispatch scale-out path, fulfilled-run requeue, manual drain, dispatch-time admission, worker capacity visibility, and local worker caps per execution. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated runtime auditability, runtime decision recording, and replay lifecycle event correlation. |
| [`ai/observability.md`](ai/observability.md) | High-level observability index and summary linking ledger, tracing, metrics, and logs. |

---

## Reliability, State, and Recovery

| Document | Purpose |
|---|---|
| [`ai/retry-and-recovery.md`](ai/retry-and-recovery.md) | Retry engine, retry state, WaitingForRetry, Redis Lua transitions, and stale worker recovery. |
| [`ai/runtime-process-crash-recovery.md`](ai/runtime-process-crash-recovery.md) | Runtime process crash recovery for unsafe runtime instances, assigned-work reconciliation, in-flight DAG resume, local-queued redispatch, and replacement runtime selection. |
| [`ai/provider-agnostic-process-host-recovery.md`](ai/provider-agnostic-process-host-recovery.md) | Provider-neutral HTTP/gRPC process-host recovery proof model, shared scenario base, and transport-independent recovery invariants. |
| [`ai/runtime-recovery-forensics.md`](ai/runtime-recovery-forensics.md) | Durable forensics records and per-work-item timelines proving how each recovered item moved through detection, redispatch/resume, replacement runtime selection, and completion. |
| [`ai/multi-tenant-runtime-crash-isolation.md`](ai/multi-tenant-runtime-crash-isolation.md) | Multi-tenant recovery isolation proof showing impacted tenant recovery without safe-tenant recovery contamination or cross-tenant leakage. |
| [`ai/recovery-replay-ledger-trace-proof.md`](ai/recovery-replay-ledger-trace-proof.md) | Recovery validation proof requiring replay, ledger, trace, completion, step-completion, and forensics evidence for recovered and safe executions. |
| [`ai/retention-and-compaction.md`](ai/retention-and-compaction.md) | Bounded hot state, compaction, eviction, payload externalization, and resolver safety. |
| [`ai/replay-and-audit.md`](ai/replay-and-audit.md) | Deterministic Replay Engine V1, snapshot restore, audit-only replay, fingerprint validation, replay metadata, ledger/timeline diagnostics, and future replay APIs. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated decision ledger, retention auditability, control-state auditability, and replay lifecycle evidence. |
| [`ai/multi-tenant-control-plane-isolation.md`](ai/multi-tenant-control-plane-isolation.md) | Tenant boundary reliability, durable snapshot propagation, registry/capacity filtering, and prevention of cross-tenant dispatch leakage. |
| [`ai/multi-tenant-runtime-flow.md`](ai/multi-tenant-runtime-flow.md) | Runtime-flow reliability map showing every async/background/distributed boundary where the durable snapshot must be preserved or restored. |

---

## Distributed Governance and Observability

| Document | Purpose |
|---|---|
| [`ai/distributed-concurrency-throttling.md`](ai/distributed-concurrency-throttling.md) | Redis ZSET concurrency gate, provider/model/operation throttling, and admission policies. |
| [`ai/observability.md`](ai/observability.md) | High-level observability index summarizing logs, metrics, traces, ledger, correlation, and roadmap direction. |
| [`ai/observability-tracing.md`](ai/observability-tracing.md) | Runtime tracing, trace timelines, trace records, Mongo trace persistence, Memory/Mongo/MemoryAndMongo modes, and tracing improvements. |
| [`ai/runtime-metrics.md`](ai/runtime-metrics.md) | Runtime metric domains, metric storage modes, worker/retention/storage/resolver/hot-state/policy metrics, and metrics improvements. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated decision ledger, runtime audit visibility, and structured runtime lifecycle evidence. |
| [`ai/control-plane-ledger-causal-chain.md`](ai/control-plane-ledger-causal-chain.md) | Control-plane causal ledger evidence for scale-out, provider selection, host creation, runtime capacity visibility, recovery reconciliation, and redispatch. |
| [`ai/runtime-recovery-forensics.md`](ai/runtime-recovery-forensics.md) | Recovery forensics evidence linking incidents, recovered work items, timelines, runtime replacement, and tenant-scoped MCP queries. |
| [`ai/recovery-replay-ledger-trace-proof.md`](ai/recovery-replay-ledger-trace-proof.md) | Cross-layer proof model connecting replay, ledger, trace, forensics, completion evidence, and safe-tenant non-impact. |
| [`ai/testing-strategy.md`](ai/testing-strategy.md) | Integration testing strategy and validation approach for distributed runtime guarantees, RBAC context propagation, tenant isolation, HTTP hardening, Runtime Host Manager process-host provisioning, Redis/local scale-out request, requeue, dispatch, and execution evidence. |
| [`ai/concurrency-hardening-and-adversarial-validation.md`](ai/concurrency-hardening-and-adversarial-validation.md) | Exact concurrency-hardening proof model for process kills, recovery ownership, deterministic crash boundaries, safe-tenant controls, pressure classification, P35 evidence, and production-vs-local interpretation. |
| [`ai/mcp-production-runtime-scenario-framework.md`](ai/mcp-production-runtime-scenario-framework.md) | Production scenario evidence for HTTP/gRPC process-host execution, mixed tenant runtime modes, durable ledger/trace/replay, and real process-boundary validation. |
| [`ai/multi-tenant-runtime-flow.md`](ai/multi-tenant-runtime-flow.md) | Operational context/audit map tying tenant context, correlation ids, shared run ids, local run ids, execution ids, runtime instances, workers, ledger, tracing, metrics, and replay together. |

---

## Runtime Control Plane and Orchestration

| Document | Purpose |
|---|---|
| [`ai/multi-tenant-control-plane-isolation.md`](ai/multi-tenant-control-plane-isolation.md) | Tenant-aware control-plane isolation model, RBAC snapshot propagation, runtime visibility rules, tenant settings, admission, scale-out, and validated test evidence. |
| [`ai/multi-tenant-runtime-flow.md`](ai/multi-tenant-runtime-flow.md) | Full operational flow showing where the tenant snapshot is created, persisted, restored, dispatched, executed, finalized, and correlated for audit/debugging. |
| [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md) | Runtime control-plane foundation, replay/execution/queue/instance facades, discovery, capacity, admission, Redis-backed scale-out lifecycle, fulfilled-run requeue, and shared controller orchestration. |
| [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md) | Redis discovery, ControlPlaneIdResolver, runtime registry, tenant-filtered capacity descriptors, scale-out capacity visibility, readiness gate, cleanup lifecycle, and HTTP pooled identity model. |
| [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md) | MCP server as control-plane adapter, including RBAC integration, host modes, discovery publication, MCP tool groups, Redis/local scale-out execution, shared queue dispatch, local/HTTP/gRPC runtime behavior, and runtime role separation. |
| [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md) | Runtime instance provider model for provider-based dispatch, HTTP pooled runtime hosting, status, control, capacity, scale-out, descriptor metadata, and provider routing. |
| [`ai/http-runtime-provider.md`](ai/http-runtime-provider.md) | HTTP provider-specific reference for hardened dispatch, HTTP runtime scale-out, Redis scale-out watcher fulfillment, tenant-aware HTTP capacity, Runtime Host Manager process-host provisioning, and readiness. |
| [`ai/grpc-runtime-provider.md`](ai/grpc-runtime-provider.md) | gRPC provider-specific reference for gRPC dispatch, runtime scale-out, `ControlPlaneWithGrpcRuntimeInstances`, Runtime Host Manager process-host provisioning, HTTP/2 command transport, and crash recovery validation. |
| [`ai/grpc-runtime-provider.md`](ai/grpc-runtime-provider.md) | gRPC provider-specific reference for gRPC dispatch, gRPC runtime scale-out, Redis scale-out watcher fulfillment, tenant-aware gRPC capacity, Runtime Host Manager process-host provisioning, HTTP/2 transport, and readiness hardening direction. |
| [`ai/mcp-production-runtime-scenario-framework.md`](ai/mcp-production-runtime-scenario-framework.md) | End-to-end MCP production scenario reference covering Host Manager modes, HTTP/gRPC process-host scale-out, real runtime processes, Dedicated/Shared/Hybrid tenants, retention, ledger, trace, and replay. |
| [`ai/provider-agnostic-process-host-recovery.md`](ai/provider-agnostic-process-host-recovery.md) | Provider-neutral process-host crash recovery reference shared by HTTP and gRPC scenarios. |
| [`ai/runtime-process-crash-recovery.md`](ai/runtime-process-crash-recovery.md) | Runtime process crash recovery control-plane boundary, including health reconciliation, execution recovery reconciliation, HTTP provider failure signal boundaries, and replacement capacity. |
| [`ai/control-plane-ledger-causal-chain.md`](ai/control-plane-ledger-causal-chain.md) | Control-plane causal chain evidence for scale-out and recovery operations, including watcher/provider/host-manager/reconciler/redispatch phases. |
| [`ai/multi-tenant-runtime-crash-isolation.md`](ai/multi-tenant-runtime-crash-isolation.md) | Control-plane isolation proof for simultaneous impacted-tenant recovery and safe-tenant non-impact. |
| [`ai/shared-controller-usage.md`](ai/shared-controller-usage.md) | Shared controller usage, Redis store configuration, queue-first/direct-dispatch behavior, tenant snapshot propagation, scale-out lifecycle, manual drain, and background pump setup. |
| [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md) | Shared queue pump/manual drain, queue-first dispatch, direct-dispatch scale-out, fulfilled-run requeue, readiness gate, dispatch-time admission, pump identity separation, runtime worker capacity visibility, and `MaxLocalWorkersPerExecution`. |
| [`ai/runtime-queue-control.md`](ai/runtime-queue-control.md) | RunId-level local runtime queue control, hot enqueue, queue pause/resume, and queued/running cancellation behavior. |
| [`ai/execution-control-state.md`](ai/execution-control-state.md) | ExecutionId-level durable pause, resume, cancel, waiting-for-input, and human-input control state. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated runtime decision ledger and audit visibility used by control-plane operations. |
| [`ai/observability.md`](ai/observability.md) | Observability index connecting logs, metrics, traces, ledger, replay diagnostics, and control-plane visibility. |

---

## Runtime Extension and Configuration

| Document | Purpose |
|---|---|
| [`ai/config-driven-runtime.md`](ai/config-driven-runtime.md) | How pipeline definitions and structured configuration drive runtime behavior, with tenant runtime settings moving toward config/database-backed resolution. |
| [`ai/policy-driven-execution.md`](ai/policy-driven-execution.md) | Shared policy model used by retry, retention, concurrency, throttling, admission control, and future tenant-specific governance. |
| [`ai/context-resolution-and-helpers.md`](ai/context-resolution-and-helpers.md) | Input resolution, step context building, payload rehydration, provider metadata, policy context, helper services, and durable execution context snapshot propagation. |
| [`ai/step-plugins.md`](ai/step-plugins.md) | Step keys, registered executors, class attributes, assembly scanning, provider abstractions, and plugin-style runtime extension. |
| [`ai/rag-pipelines.md`](ai/rag-pipelines.md) | RAG retrieval, merge, compose, provider-oriented workflow execution, auto-registered RAG steps, and deterministic RAG pipelines. |

---

## Product Roadmap Documentation

The product roadmap documentation lives under:

- [`product-roadmap/`](product-roadmap/)

Useful entry points:

| Document | Purpose |
|---|---|
| [`product-roadmap/index.md`](product-roadmap/index.md) | Product roadmap index and product-platform reading guide. |
| [`product-roadmap/product-roadmap.md`](product-roadmap/product-roadmap.md) | Public product roadmap from runtime foundation toward dashboard, pipeline builder, MCP interface, managed hosting, security, memory/context lifecycle, and enterprise readiness. |
| [`product-roadmap/current-foundation.md`](product-roadmap/current-foundation.md) | Current architectural foundation for deterministic execution, replay, policy, providers, lifecycle management, observability, and control-plane direction. |
| [`product-roadmap/improvement-backlog.md`](product-roadmap/improvement-backlog.md) | Planned improvements required to productize the existing runtime foundation. |
| [`product-roadmap/multi-tenant-readiness.md`](product-roadmap/multi-tenant-readiness.md) | Product-level multi-tenant readiness direction. |
| [`product-roadmap/managed-hosting-model.md`](product-roadmap/managed-hosting-model.md) | Managed hosting model by runtime instance and worker capacity. |

---

## Documentation Status

Many focused documents started as documentation split placeholders, but several core runtime areas are now fully documented, including:

- architecture overview
- multi-tenant control-plane isolation
- multi-tenant runtime flow ASCII reference
- RBAC execution-context propagation
- execution control state
- runtime queue control
- runtime control-plane foundations
- MCP server control-plane usage
- runtime instance provider architecture direction
- HTTP runtime provider hardening and tenant-aware scale-out
- gRPC runtime provider dispatch, scale-out, process-host provisioning, and crash recovery validation
- provider-agnostic HTTP/gRPC process-host recovery contract
- Runtime Host Manager process-host provisioning
- MCP production runtime scenario framework
- mixed-tenant production validation
- runtime discovery/registry/capacity
- Redis-backed scale-out request lifecycle
- fulfilled-run requeue
- shared controller usage
- shared queue pump and worker capacity
- distributed execution
- distributed concurrency
- retention/compaction
- deterministic replay and audit foundations
- execution-correlated decision ledger foundations
- observability/tracing foundations
- runtime metrics foundations
- runtime process crash recovery
- runtime recovery forensics
- multi-tenant runtime crash isolation
- control-plane ledger causal chain
- recovery replay / ledger / trace proof
- testing strategy
- concurrency hardening and adversarial process-host validation

The complete technical reference remains preserved in:

- [`runtime-internals.md`](runtime-internals.md)

Focused documents should be expanded progressively by extracting, refining, and linking content from `runtime-internals.md`.

---

## Documentation Rule

The original technical depth must be preserved.

New focused documents should be extracted from `runtime-internals.md` gradually.

Do not delete technical content until it has been safely moved, reviewed, and linked from this index.

When adding new documentation:

1. Add core documentation directly under `docs/`.
2. Add focused AI runtime documentation under `docs/ai/`.
3. Link new documents from this index.
4. Keep links relative to this file.
5. Preserve the complete technical reference in `runtime-internals.md`.
6. Clearly distinguish between implemented features, available foundations, and planned work.
7. Keep replay documentation connected to ledger, tracing, and metrics because Replay V1 exposes replay metadata, replay lifecycle ledger events, and trace timeline diagnostics.
8. Keep observability overview, tracing, runtime metrics, and replay/audit linked together because they describe different layers of the same runtime visibility model.
9. Keep runtime control-plane documentation linked with runtime queue control, execution control state, instance visibility, admission, shared controller, shared queue pump, and Kubernetes preparation.
10. Keep MCP server control-plane and runtime instance provider documentation linked with runtime control-plane, shared controller, admission, local runtime queues, runtime capacity descriptors, RBAC authorization, tenant context propagation, and Kubernetes preparation.
11. Keep HTTP runtime provider documentation linked with runtime instance provider model, runtime control-plane, shared controller, shared queue pump, runtime discovery/registry/capacity, multi-tenant isolation, testing strategy, and future Remote MCP Runtime Host Manager work.
12. Keep gRPC runtime provider documentation linked with runtime instance provider model, runtime control-plane, shared controller, shared queue pump, runtime discovery/registry/capacity, provider-agnostic process-host recovery, testing strategy, and Runtime Host Manager process-host work.
13. Keep shared controller usage linked with shared queue pump, runtime control-plane, MCP control-plane, runtime discovery/registry/capacity, runtime instance provider model, and testing strategy.
14. Keep shared queue pump and worker capacity documentation linked with shared controller usage, runtime queue control, MCP control-plane, runtime instance provider model, runtime discovery/registry/capacity, multi-tenant isolation, and testing strategy.
15. Keep runtime discovery, registry, and capacity documentation linked with runtime control-plane, MCP control-plane, runtime instance provider model, shared queue pump readiness, multi-tenant isolation, and testing strategy.
16. Keep Redis/local scale-out documentation linked across runtime control-plane, MCP control-plane, runtime instance provider model, shared controller usage, shared queue pump, discovery/registry/capacity, config-driven runtime, and testing strategy.
17. Keep multi-tenant control-plane isolation documentation linked across architecture overview, runtime control-plane, discovery/registry/capacity, MCP server, runtime instance provider model, shared controller usage, shared queue pump, distributed execution, execution control state, testing strategy, README, and product roadmap documents.
18. Keep RBAC documentation connected to MCP tool authorization, `ExecutionContextSnapshot`, tenant-aware admission, runtime visibility, and all background/distributed hops.
19. Keep the multi-tenant runtime flow document linked with multi-tenant isolation, runtime control-plane, shared controller usage, shared queue pump, distributed execution, execution control state, testing strategy, and README because it is the operational map of the whole tenant-aware execution path.
20. Keep the MCP production runtime scenario framework linked with HTTP runtime provider, runtime instance provider model, MCP server control-plane, runtime discovery/registry/capacity, multi-tenant isolation, shared controller usage, shared queue pump, testing strategy, replay/audit, ledger, and observability docs because it proves the full HTTP/gRPC process-host production path across these boundaries.
21. Keep runtime process crash recovery linked with HTTP runtime provider, runtime control-plane, runtime discovery/registry/capacity, retry-and-recovery, MCP production scenarios, testing strategy, ledger, observability, replay/audit, and recovery forensics because process death crosses all these boundaries.
22. Keep runtime recovery forensics linked with runtime process crash recovery, multi-tenant crash isolation, observability, execution-correlated ledger, control-plane ledger causal chain, testing strategy, and MCP production scenarios because forensics is the per-work-item audit surface of recovery.
23. Keep multi-tenant runtime crash isolation linked with multi-tenant control-plane isolation, runtime process crash recovery, recovery forensics, control-plane ledger causal chain, recovery replay/ledger/trace proof, HTTP runtime provider, and testing strategy because safe-tenant non-impact is a core isolation proof.
24. Keep control-plane ledger causal chain linked with execution-correlated ledger, runtime control-plane, HTTP runtime provider, MCP production scenarios, runtime discovery/registry/capacity, runtime recovery forensics, and observability because it records infrastructure decisions that execution ledger alone does not explain.
25. Keep recovery replay/ledger/trace proof linked with replay/audit, observability, observability-tracing, execution-correlated ledger, runtime recovery forensics, runtime process crash recovery, and testing strategy because recovery is validated only after replay, ledger, trace, completion evidence, and forensics agree.
