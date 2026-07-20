# Deterministic AI Runtime

A deterministic, multi-tenant .NET runtime for durable AI workflow execution across local workers, real child processes, and Kubernetes-hosted runtime instances.

Deterministic AI Runtime treats AI orchestration as a distributed systems problem. It combines DAG execution, Redis-backed coordination, durable execution state, provider-based dispatch, crash recovery, replay, audit, observability, execution control, and tenant isolation behind a shared control plane.

The current Kubernetes milestone adds SDK-driven `RuntimeInstanceOnly` Pod and Service provisioning, layered readiness, Service/NodePort/port-forward/Gateway exposure, tenant-aware registry and capacity publication, and Pod crash recovery while preserving the same HTTP or gRPC runtime transport and durable execution protocol used by process-host scenarios.

The runtime foundations are intentionally designed as the base for a broader product platform for deterministic AI execution, runtime control, replay, governance, observability, dashboarding, pipeline building, managed hosting, and enterprise-oriented AI operations.

[![Version](https://img.shields.io/badge/Version-1.0.7.1-blue)](./CHANGELOG.md)
[![Changelog](https://img.shields.io/badge/Changelog-view-lightgrey)](./CHANGELOG.md)
![AI Runtime](https://img.shields.io/badge/AI-Deterministic%20Execution-purple)
![Runtime](https://img.shields.io/badge/Runtime-distributed-brightgreen)
![Redis](https://img.shields.io/badge/Redis-required-red?logo=redis)
![MongoDB](https://img.shields.io/badge/MongoDB-required-green?logo=mongodb)
![Status](https://img.shields.io/badge/Status-active%20development-orange)

### Start Here

- [Architecture overview](docs/ai/architecture-overview.md)
- [Kubernetes Runtime Host Provider](docs/ai/kubernetes-runtime-host-provider.md)
- [Provider-agnostic process-host recovery](docs/ai/provider-agnostic-process-host-recovery.md)
- [Multi-tenant crash isolation](docs/ai/multi-tenant-runtime-crash-isolation.md)
- [Complete documentation index](docs/index.md)

---

## Latest Updates

The latest milestone moves the same runtime execution and recovery protocol from child processes into Kubernetes Pods. Kubernetes now owns host lifecycle, networking, and readiness; HTTP and gRPC providers remain responsible for runtime commands; the shared control plane remains responsible for admission, scale-out, dispatch, registry/capacity visibility, and recovery.

| Area | Summary |
|---|---|
| Kubernetes Runtime Host Provider | Added `KubernetesAiRuntimeHostCreationStrategy` as a Runtime Host Manager lifecycle strategy that creates and converges `RuntimeInstanceOnly` Pods and Services through the Kubernetes .NET SDK. |
| Kubernetes transport preservation | Kubernetes changes where a runtime is hosted, not how commands are dispatched: HTTP remains HTTP and gRPC remains gRPC through the existing runtime providers. |
| Layered Kubernetes readiness | Host startup distinguishes Kubernetes resource readiness, runtime registration/capacity readiness, and transport endpoint readiness instead of treating `Pod Running` as executable capacity. |
| Kubernetes networking | Supports direct Service DNS, NodePort, per-runtime `kubectl port-forward`, and shared Gateway API routing through `HTTPRoute` or `GRPCRoute`. |
| Kubernetes crash recovery | Targeted Kubernetes SDK scenarios validate Pod deletion/crash detection, unsafe-capacity suppression, replacement runtime provisioning, in-flight resume, local-queued redispatch, and safe-tenant non-impact. |
| Runtime process crash recovery | Validated recovery after real `RuntimeInstanceOnly` process crashes, including in-flight DAG resume, local queued work redispatch, replacement runtime selection, and completion through normal dispatch paths. |
| Multi-tenant crash isolation | Validated a three-tenant crash scenario where two tenant runtimes are killed while a safe tenant continues without recovery contamination or cross-tenant ledger/forensics leakage. |
| Runtime recovery forensics | Added recovery forensics records and timelines that explain why work was recovered, which runtime failed, which durable identities were used, and how replacement dispatch/resume proceeded. |
| Recovery replay / ledger / trace proof | Validated that recovered executions remain replayable and that ledger, trace, replay report, replay ledger, and replay trace remain readable after process crash recovery. |
| Control-plane causal-chain proof | Added causal-chain validation across scale-out request persistence, watcher observation, provider selection, host creation, process start, registry/capacity visibility, and recovery redispatch. |
| RBAC execution-context integration | Integrated RBAC execution context as the entry-point authority for MCP/control-plane operations and persisted a durable `ExecutionContextSnapshot` across shared and local runtime paths. |
| Multi-tenant control-plane isolation | Added tenant-aware runtime visibility using `ExecutionContextSnapshot.TenantId` as the durable isolation boundary, with Shared, Dedicated, and Hybrid runtime modes. |
| End-to-end multi-tenant runtime flow | Added `docs/ai/multi-tenant-runtime-flow.md`, a complete ASCII flow showing MCP/RBAC entry, durable snapshot creation, shared run persistence, tenant-aware admission, scale-out, shared queue dispatch, local runtime queue execution, DAG creation, worker loop, execution control, finalization, and observability. |
| Tenant-aware registry, capacity, and admission | Registry and capacity listing now filter runtime instances through tenant visibility rules before admission, dispatch, MCP visibility, or scale-out decisions. |
| Shared queue context restoration | The shared queue dispatcher restores the persisted `ExecutionContextSnapshot` before dispatch-time admission, capacity filtering, reservation, and provider dispatch. |
| Runtime local queue snapshot requirement | Runtime background execution now requires an `ExecutionContextSnapshot` so direct queued runs and shared-dispatched runs preserve tenant/user/project identity before creating the durable `ExecutionId`. |
| Tenant-scoped scale-out settings | Scale-out requests preserve tenant runtime settings such as isolation mode, fallback behavior, runtime instance prefix, worker count, run slots, queue capacity, and maximum runtime instances. |
| Local scaler prefix isolation | Local runtime scale-out now counts existing hosts by tenant-aware `RuntimeInstanceIdPrefix`, preventing shared capacity from blocking dedicated or hybrid tenant scale-out. |
| Redis-backed scale-out request lifecycle | Added persisted scale-out requests, request observation, fulfilled/rejected lifecycle handling, provider hint propagation, and Redis-backed scale-out coordination. |
| Provider-based scale-out capability | Added scale-out as a runtime instance provider capability through `IAiRuntimeScaleOutProvider`, resolved by the existing provider router through `AiRuntimeScaleOutProviderSelector`. |
| gRPC runtime provider | Added a gRPC runtime provider for `ControlPlaneWithGrpcRuntimeInstances`, with gRPC dispatch, provider metadata, HTTP/2 runtime transport, and `RuntimeInstanceOnly` gRPC host process validation. |
| gRPC runtime scale-out | Added gRPC provider scale-out through Redis scale-out requests, watcher/provider selection, Runtime Host Manager process-host provisioning, runtime self-registration, capacity publication, and normal shared queue dispatch. |
| Provider-agnostic process-host recovery | Refactored real process-host crash recovery into a provider-neutral HTTP/gRPC recovery framework so the same recovery contract validates across transport providers. |
| Local runtime scale-out | Added local runtime instance scaling through `LocalAiRuntimeInstanceProvider` and `AiLocalRuntimeInstanceScaler`, allowing runtime capacity to be created dynamically from zero executable local instances. |
| Fulfilled-run requeue and pump dispatch | Added fulfilled scale-out shared-run requeue so the watcher creates capacity but the normal shared queue pump still owns claim, dispatch-time admission, provider dispatch, and queue/run state transitions. |
| MCP Redis local scale-out validation | Validated the full MCP flow: submit with no runtime capacity, request scale-out, fulfill the request, create/register runtime capacity, requeue the run, dispatch through the pump, expose `LocalRunId` and `ExecutionId`, and complete the runtime run. |
| Enterprise runtime demo validation | Fixed and validated direct demo execution context propagation so the demo runner persists `ExecutionContextSnapshot` before the local background controller restores tenant/user/project context. |
| HTTP runtime provider hardening | Added timeout, retry, circuit-breaker behavior, structured HTTP dispatch failure reasons, and shared-run dispatch failure persistence for the HTTP runtime provider. |
| HTTP runtime scale-out foundation | Added HTTP provider scale-out capability through `IAiRuntimeScaleOutProvider` and `IAiHttpRuntimeScaleOutProvisioner`, with Redis-backed scale-out request fulfillment into runtime registry and capacity metadata. |
| Runtime Host Manager host lifecycle | Runtime Host Manager now separates command transport from lifecycle across `Fixture`, `Process`, `Attach`, and `Kubernetes` creation modes. |
| HTTP process-host production validation | Validated the full HTTP process-host lifecycle: submit with no runtime capacity, Redis scale-out request, watcher, HTTP provider, Host Manager, process launch, runtime registration/capacity readiness, HTTP dispatch, DAG execution, and completion. |
| Tenant-aware HTTP scale-out validation | Validated HTTP scale-out for Shared, Dedicated, and Hybrid runtime modes, including Dedicated no shared fallback, Hybrid visibility behavior, and tenant runtime settings precedence during provisioning. |
| Durable process-boundary observability | Validated ledger, trace, replay report, replay ledger, replay trace, and retention across real runtime process boundaries. |
| MCP production scenario framework | Added `docs/ai/mcp-production-runtime-scenario-framework.md`, documenting the Host Manager, HTTP process-host flow, tenant runtime modes, final mixed-tenant production validation, and remaining boundaries. |

For detailed changes, see [`CHANGELOG.md`](./CHANGELOG.md), [`docs/index.md`](docs/index.md), [`docs/ai/multi-tenant-control-plane-isolation.md`](docs/ai/multi-tenant-control-plane-isolation.md), [`docs/ai/multi-tenant-runtime-flow.md`](docs/ai/multi-tenant-runtime-flow.md), [`docs/ai/http-runtime-provider.md`](docs/ai/http-runtime-provider.md), [`docs/ai/grpc-runtime-provider.md`](docs/ai/grpc-runtime-provider.md), [`docs/ai/kubernetes-runtime-host-provider.md`](docs/ai/kubernetes-runtime-host-provider.md), [`docs/ai/provider-agnostic-process-host-recovery.md`](docs/ai/provider-agnostic-process-host-recovery.md), [`docs/ai/mcp-production-runtime-scenario-framework.md`](docs/ai/mcp-production-runtime-scenario-framework.md), [`docs/ai/runtime-process-crash-recovery.md`](docs/ai/runtime-process-crash-recovery.md), [`docs/ai/runtime-recovery-forensics.md`](docs/ai/runtime-recovery-forensics.md), [`docs/ai/multi-tenant-runtime-crash-isolation.md`](docs/ai/multi-tenant-runtime-crash-isolation.md), [`docs/ai/control-plane-ledger-causal-chain.md`](docs/ai/control-plane-ledger-causal-chain.md), [`docs/ai/recovery-replay-ledger-trace-proof.md`](docs/ai/recovery-replay-ledger-trace-proof.md), and the product roadmap documentation under [`docs/product-roadmap/index.md`](docs/product-roadmap/index.md).

---

## Overview

Deterministic AI Runtime is a .NET runtime for executing complex AI workflows as controlled, observable, recoverable, replayable, auditable, and tenant-isolated distributed executions.

It is designed for workloads such as:

- LLM orchestration
- RAG pipelines
- tool execution
- decision workflows
- long-running AI processes
- multi-step distributed execution
- shared control-plane orchestration
- multi-tenant AI runtime hosting
- Kubernetes-hosted runtime instance pools
- process crash recovery workflows
- recovery forensics and incident investigation
- operational replay and audit workflows

The runtime treats AI orchestration as a systems problem, not only as a prompt engineering problem.

It provides a state-driven execution layer where:

- workflows are modeled as DAGs
- RBAC execution context is captured into a durable `ExecutionContextSnapshot`
- tenant identity is propagated across shared runs, background dispatch, runtime queues, and distributed execution
- the end-to-end multi-tenant runtime flow is documented from MCP/RBAC entry to final execution observability
- `ExecutionContextSnapshot.TenantId` is the durable tenant isolation boundary
- `ContextKey` remains useful for RBAC correlation, access-context lookup, and diagnostics, but is not the tenant isolation boundary
- workers are stateless
- Redis stores hot execution state
- Redis Lua scripts enforce atomic coordination
- MongoDB stores durable payloads and snapshots
- context helpers resolve inputs, payloads, provider metadata, and policy context
- policies control retry, retention, and concurrency
- metrics, traces, and ledger events share runtime correlation
- executions can be replay-validated from persisted snapshots
- execution can be paused, resumed, cancelled, or blocked for human input
- submitted runs can be admitted, assigned, globally queued, queue-first submitted, pumped manually or by background service, dispatched, scale-out requested, rejected, listed, retrieved, or cancelled through the shared runtime controller
- tenant-aware admission filters Shared, Dedicated, and Hybrid runtime instances before assignment, fallback, or scale-out
- runtime instances expose run capacity, worker capacity, queue pressure, and worker-aware `CanAcceptRun`
- MCP/control-plane discovery allows runtime-only hosts to resolve the logical control-plane identity before registration
- Redis registry, capacity, and admission reservation stores validate shared runtime coordination
- local, process-host, and Kubernetes-hosted runtime instances use the same shared run, dispatch, and execution contracts
- HTTP runtime provider hardening validates timeout, retry, circuit-breaker behavior, structured failure reasons, and persisted dispatch failure handling
- HTTP runtime scale-out foundations validate Redis-backed provider selection, provisioner execution, tenant-aware registry/capacity metadata, and Shared/Dedicated/Hybrid policy behavior
- gRPC runtime provider foundations validate `ControlPlaneWithGrpcRuntimeInstances`, gRPC dispatch, provider metadata, HTTP/2 runtime transport, gRPC scale-out, and real `RuntimeInstanceOnly` process-host execution
- Kubernetes Runtime Host Provider creates deterministic Pods and Services through the Kubernetes .NET SDK without moving command transport into Kubernetes-specific code
- Kubernetes client modes separate fast in-memory lifecycle proofs from real cluster interaction through the Kubernetes SDK
- Kubernetes networking supports direct Service DNS, NodePort, per-runtime port-forward, and shared Gateway API routing
- layered readiness prevents registry/capacity publication until the configured Kubernetes, runtime, and transport boundaries have converged
- Kubernetes crash scenarios validate that Pod loss is handled by the existing health and recovery reconcilers rather than by transport providers
- provider-agnostic process-host recovery validates the same runtime crash recovery contract across HTTP and gRPC providers
- Runtime Host Manager validates `Fixture`, `Process`, `Attach`, and `Kubernetes` host creation boundaries; process and Pod hosts both run the same `RuntimeInstanceOnly` application
- MCP production runtime scenarios validate full process-boundary execution, durable ledger/trace/replay visibility, retention, mixed-tenant behavior, and HTTP/gRPC process-host recovery
- runtime process crash recovery validates that killed runtime processes do not imply lost work when durable shared state, execution indexes, DAG state, registry/capacity, ledger, trace, replay, and forensics are available
- in-flight recovered executions resume with the same durable `ExecutionId`, while local queued work that had not started yet is redispatched through its durable `SharedRunId`
- safe tenant non-impact is validated as a first-class guarantee: tenants not affected by a crash should not receive recovery entries, recovery forensics, or cross-tenant ledger leakage
- tenant runtime settings can define the desired isolation mode, fallback behavior, runtime instance prefix, worker count, local queue capacity, max concurrent runs, and max runtime instances

The project should be read as an AI execution infrastructure foundation. The runtime core is already substantial, while the longer-term direction is to evolve toward a broader product platform for deterministic AI execution, operational control, replay/audit, dashboarding, pipeline building, managed hosting, and enterprise AI workflow governance.

---

## Why This Exists

Most AI projects focus on prompts, agents, RAG, embeddings, or model calls.

That is enough for prototypes.

Once AI moves to production, the hard problem becomes execution:

- How do you coordinate multiple workers?
- How do you avoid duplicate execution?
- How do you recover after worker crashes?
- How do you recover after a full runtime process dies?
- How do you prove that unrelated tenants were not touched by recovery?
- How do you replay an execution?
- How do you control retries?
- How do you throttle providers and models?
- How do you keep memory bounded?
- How do you resolve context safely across steps, payloads, providers, and policies?
- How do you pause, resume, or cancel safely?
- How do you support human-in-the-loop workflows?
- How do you preserve RBAC and tenant identity after the original request leaves the MCP/API context?
- How do you prevent one tenant from seeing or consuming another tenant's dedicated runtime capacity?
- How do you scale out runtime instances without losing tenant-specific settings?
- How do you create runtime Pods without coupling Kubernetes lifecycle to HTTP or gRPC dispatch?
- How do you distinguish a `Running` Pod from a registered, capacity-published, routable runtime?
- How do you recover assigned work after a Pod is deleted or crashes?
- How do you prove deterministic convergence?

This runtime exists to address those production execution concerns.

---

## What Problem This Runtime Fixes

Modern AI applications often start as simple prompts, agents, or RAG pipelines.

That works for prototypes, but production AI execution quickly becomes a distributed systems problem.

This runtime is designed to fix the operational problems that appear when AI workflows must run reliably across multiple steps, workers, runtime instances, providers, queues, control-plane operations, and tenants.

| Problem | What the Runtime Provides |
|---|---|
| AI workflows are hard to reproduce | Deterministic DAG execution, persisted execution state, replay metadata, fingerprints, snapshots, and replay validation. |
| Workers can crash mid-execution | Redis-backed execution state, stale running-step recovery, retry state, and deterministic convergence. |
| Runtime processes can die with assigned work | Runtime process crash recovery reconciles assigned work, resumes in-flight DAG executions by `ExecutionId`, redispatches local queued shared runs by `SharedRunId`, and validates completion through replacement runtime capacity. |
| Crash recovery can accidentally affect unrelated tenants | Tenant-scoped recovery queries, runtime visibility filtering, ledger/forensics isolation, and safe tenant non-impact validation prove that unaffected tenants remain outside the recovery surface. |
| Multiple workers can duplicate work | Redis Lua atomic claims, claim tokens, lease validation, and single-owner step execution. |
| Retry behavior becomes unpredictable | Step-scoped retry state, retry scheduling, recovery separation, and policy-driven retry behavior. |
| Provider and model calls can overload external services | Distributed concurrency gates, Redis ZSET leases, provider/model/operation limits, and throttling policies. |
| Runtime memory can grow without bounds | Retention, compaction, eviction, payload externalization, and payload rehydration. |
| Context can become corrupted or inconsistent | Dedicated context resolution helpers for inputs, previous step outputs, payloads, provider metadata, policy context, RAG context, RBAC execution context, and durable `ExecutionContextSnapshot`. |
| Tenant identity can be lost across async/background hops | RBAC execution context is captured into `ExecutionContextSnapshot` and restored by shared queue dispatchers and runtime background controllers before admission or execution. |
| Runtime flow becomes difficult to reason about | A dedicated end-to-end ASCII flow documents every hop from MCP/RBAC entry to shared run storage, admission, scale-out, queue dispatch, local runtime queue execution, DAG creation, worker execution, finalization, and observability. |
| Dedicated tenant runtimes can leak into shared scheduling | Tenant-aware visibility rules filter registry and capacity descriptors before admission, dispatch, MCP visibility, and scale-out decisions. |
| Hybrid tenants need safe shared fallback | Hybrid tenants can use dedicated/hybrid capacity first and fall back only to Shared runtime capacity when tenant settings explicitly allow fallback. |
| Tenant-specific scale-out can create the wrong runtime prefix | Scale-out records carry tenant runtime settings including isolation mode, fallback flags, `RuntimeInstanceIdPrefix`, worker count, run slots, queue capacity, and maximum runtime instances. |
| Queued work is hard to control | RunId-level queue control, queue pause/resume, queued cancellation, running cancellation bridge, shared run records, and shared queue coordination. |
| Live executions are hard to control | ExecutionId-level pause, resume, cancel, waiting-for-input, and human input submission. |
| Runtime behavior is hard to audit | Execution-correlated decision ledger, runtime lifecycle events, claim/retry/recovery/concurrency events, control-plane events, tenant metadata, and replay lifecycle events. |
| Distributed runtime instances are hard to observe | Runtime instance registration, heartbeat, Redis-backed registry, Redis-backed capacity descriptors, queue pressure, available run slots, worker identity propagation, active worker count, available worker count, tenant ownership metadata, and worker-aware `CanAcceptRun`. |
| Queued global work needs controlled dispatch | Queue-first submit mode, shared queue pump, readiness gate, manual drain, dispatch-time admission, tenant-aware capacity visibility, admission reservations, and no-double-dispatch shared queue coordination. |
| One execution can consume too many local workers | `MaxLocalWorkersPerExecution`, effective worker reservation, active/free worker visibility, and worker-aware runtime capacity snapshots. |
| Control-plane operations become coupled to internals | Adapter-neutral control-plane facades for MCP, HTTP API, CLI, dashboards, and Kubernetes runtime-host orchestration. |
| Dispatch and host lifecycle can become coupled | Local, HTTP, and gRPC providers own command dispatch while Runtime Host Manager strategies independently own `Fixture`, `Process`, `Attach`, or `Kubernetes` host lifecycle. |
| Kubernetes must not publish false-ready capacity | Kubernetes startup uses separate resource, runtime registration/capacity, and transport readiness gates before scale-out is fulfilled and normal dispatch begins. |
| Runtime-only hosts can register under the wrong control-plane id | Redis control-plane discovery store and `ControlPlaneIdResolver` allow runtime-only hosts to resolve the MCP-published logical control-plane identity before registration. |
| Shutdown cleanup can race with disposed discovery/logging/Redis dependencies | Registry unregister and capacity descriptor cleanup reuse the known resolved control-plane id and are best-effort during shutdown. |
| HTTP dispatch can accidentally target the parent transport host | HTTP pooled runtime model treats `runtime-http-*` child runtime instances as dispatch targets and the HTTP host as transport infrastructure. |
| HTTP provider failures can become opaque under pressure | HTTP provider hardening adds timeout, retry, circuit breaker, structured failure reasons, and persisted dispatch failure visibility. |
| HTTP runtime capacity needs tenant-aware scale-out before real remote provisioning | HTTP scale-out now participates in the same Redis-backed request lifecycle, watcher, selector, provider, registry, capacity, and tenant visibility model as local scale-out. |
| Runtime host lifecycle must not be owned directly by command providers | Runtime Host Manager separates provider scale-out from host creation and supports `Fixture`, `Process`, `Attach`, and `Kubernetes` modes without changing HTTP/gRPC transport semantics. |
| Production tests need to prove real process boundaries | MCP production scenarios launch real `RuntimeInstanceOnly` processes, wait for registration/capacity readiness, dispatch over HTTP, execute DAG workloads, and validate ledger/trace/replay across process boundaries. |
| Kubernetes networking differs between local development and clusters | The host strategy can publish direct Service DNS, NodePort, per-runtime port-forward, or shared Gateway API endpoints while preserving runtime identity and provider metadata. |

The main goal is to make production AI execution controllable, observable, recoverable after worker and runtime process failures, auditable, replayable, tenant-isolated, and eventually scalable across runtime instances without turning the runtime core into a transport-specific scheduler.

The runtime treats AI execution as infrastructure:

```text
Prompt / Tool / RAG call
    ↓
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
Workflow step
    ↓
Deterministic DAG state
    ↓
Distributed worker coordination
    ↓
Runtime control plane
    ↓
Tenant-aware registry / capacity / admission
    ↓
Replay, audit, metrics, tracing, and capacity visibility
```

This is the difference between a prototype agent and an operational AI execution platform.

---

## The Production AI Execution Problem

Production AI workloads are no longer single prompts running in isolation.

They become distributed execution systems with:

- multiple pipeline steps
- parallel branches
- dependencies
- retries
- external providers
- large payloads
- compacted or externalized state
- context resolution across previous step outputs
- failure recovery
- operational controls
- audit and replay requirements
- multiple workers or runtime instances
- multiple tenants
- runtime-specific capacity policies
- admission, scheduling, dispatch, and scale-out decisions that must remain isolated by tenant

Without a real execution runtime, these systems often become fragile:

- hidden retry loops
- duplicated work
- lost state
- corrupted execution progress
- unbounded memory growth
- unclear ownership
- inconsistent input/context reconstruction
- RBAC context lost after background dispatch
- tenants accidentally sharing dedicated runtime capacity
- scale-out creating runtime instances with the wrong prefix or capacity settings
- poor or fragmented observability
- impossible replay

This project explores what an AI execution runtime should look like when reliability, determinism, context resolution, tenant isolation, and distributed coordination are treated as first-class design requirements.

---

## Core Capabilities

| Capability | Status | Summary |
|---|---:|---|
| Deterministic DAG execution | Implemented | Workflows execute through dependency-aware DAG state. |
| Redis hot state | Implemented | Active execution state is stored in Redis. |
| Redis Lua atomic coordination | Implemented | Critical transitions use Lua-backed atomic operations. |
| Distributed workers | Implemented | Workers can claim and execute steps safely. |
| Multi-runtime-instance execution foundations | Implemented | Runtime instances can coordinate through shared Redis-backed execution state. |
| Context resolution and helpers | Foundation available | Input bindings, previous step outputs, provider metadata, policy context, RBAC context, execution snapshots, and payload rehydration are resolved through helper layers. |
| Deterministic convergence | Implemented | Final state is derived from state transitions, not worker ordering. |
| Retry and recovery | Implemented | Retry state, waiting windows, and stale running-step recovery are separated. |
| Runtime process crash recovery | Implemented / validated | Real `RuntimeInstanceOnly` process crashes are recovered through durable shared run state, runtime execution indexes, DAG execution state, replacement runtime capacity, redispatch/resume, and recovery proof. |
| Multi-tenant runtime crash isolation | Implemented / validated | Process crash recovery is tenant-scoped; impacted tenants recover their assigned work while safe tenants remain completed, replayable, and free of recovery/forensics contamination. |
| Runtime recovery forensics | Implemented / validated | Recovery records capture incident identity, failed runtime, durable identities, recovery classification, replacement runtime, timeline events, and safe tenant non-impact evidence. |
| Recovery replay / ledger / trace proof | Implemented / validated | Recovered runs are validated through completion, replay report, replay ledger, replay trace, execution ledger, runtime trace, and tenant-scoped proof queries. |
| Retention and compaction | Implemented | Hot state can be compacted/evicted while payloads remain resolvable. |
| Distributed concurrency and throttling | Implemented | Redis ZSET leases enforce global, pipeline, step, execution, instance, provider, model, and operation limits. |
| Enterprise throttling scenario | Implemented | `throttling-100` demonstrates provider-level distributed throttling with realtime visibility and deterministic convergence. |
| Policy-driven execution | Implemented | Retry, retention, and concurrency use configurable policy definitions. |
| Execution control state | Implemented | ExecutionId-level pause, resume, cancel, waiting-for-input, and human input submission. |
| Runtime queue control | Implemented | RunId-level queue pause/resume, queued cancellation, running cancellation bridge, hot enqueue, and required execution context snapshot propagation. |
| Runtime control plane foundation | Implemented | Adapter-neutral facades expose replay, execution control, local queue control, runtime instance control, admission, shared runtime controller, shared queue dispatch, queue pump/manual drain, background service, Redis-backed scale-out request lifecycle, tenant-aware isolation, and scale-out publication for API/MCP/CLI/dashboard/Kubernetes adapters. |
| RBAC execution context integration | Implemented / validated | MCP/control-plane operations capture RBAC execution context and persist `ExecutionContextSnapshot` so tenant/user/project identity survives shared queues, background services, dispatchers, and runtime local queues. |
| Multi-tenant control-plane isolation | Implemented / validated | Tenant-aware registry, capacity, admission, shared queue dispatch, and scale-out paths use `ExecutionContextSnapshot.TenantId` as the durable tenant boundary. |
| End-to-end multi-tenant runtime flow documentation | Documented | A full ASCII flow explains how RBAC context becomes a durable snapshot, how admission and scale-out remain tenant-aware, how background hops restore context, and how execution reaches final observability. |
| Shared/Dedicated/Hybrid runtime visibility | Implemented / validated | Shared tenants see shared capacity, dedicated tenants see only owned dedicated capacity unless configured otherwise, and hybrid tenants can prefer dedicated capacity with explicit shared fallback. |
| Tenant-scoped runtime settings | Implemented foundation / validated | Tenant settings currently provide hardcoded runtime isolation mode, fallback behavior, runtime instance prefix, worker count, max concurrent runs, local queue capacity, and maximum runtime instances. |
| Runtime instance registry and control | Implemented / validated | Runtime instances can register, heartbeat, expose queue capacity, expose worker capacity, publish tenant visibility descriptors, be listed, marked draining, or unregistered through in-memory and Redis-backed registry foundations. |
| Redis control-plane discovery | Implemented / validated | MCP/control-plane hosts can publish a logical control-plane discovery descriptor and runtime-only hosts can resolve it before registration. |
| Redis admission reservations | Implemented / validated | Redis-backed admission reservations protect selected runtime capacity during provider dispatch scenarios. |
| HTTP pooled runtime dispatch | Implemented / validated | `ControlPlaneWithHttpRuntimeInstances` dispatches through the HTTP provider into `RuntimeInstanceOnly` hosts with pooled `runtime-http-*` child runtime instances. |
| HTTP runtime provider hardening | Implemented / validated | HTTP dispatch supports provider unavailable handling, dispatch timeout, retry, circuit breaker, non-retryable HTTP failure classification, invalid response handling, and persisted shared-run failure reasons. |
| HTTP runtime scale-out foundation | Implemented / validated | `HttpAiRuntimeInstanceProvider` participates as an `IAiRuntimeScaleOutProvider` and delegates capacity materialization to `IAiHttpRuntimeScaleOutProvisioner`. |
| gRPC runtime provider | Implemented / validated | `ControlPlaneWithGrpcRuntimeInstances` dispatches to gRPC runtime instances using provider metadata, HTTP/2 transport, and `RuntimeInstanceOnly` gRPC host processes. |
| gRPC runtime scale-out foundation | Implemented / validated | The gRPC provider participates in Redis-backed scale-out through provider selection, Runtime Host Manager process-host provisioning, runtime self-registration, capacity visibility, and shared queue dispatch. |
| Kubernetes Runtime Host Provider | Implemented / validated foundation | Runtime Host Manager can create and delete `RuntimeInstanceOnly` Pods and Services through fake or Kubernetes SDK clients while preserving HTTP/gRPC command transports. |
| Kubernetes readiness and networking | Implemented / validated foundation | Supports resource readiness, runtime registration/capacity readiness, transport readiness, Service DNS, NodePort, per-runtime port-forward, and shared Gateway API routing. |
| Kubernetes crash recovery | Implemented / targeted validation | Pod deletion/crash scenarios reuse the provider-agnostic health and execution recovery model for unsafe-capacity suppression, replacement capacity, strict resume, redispatch, and safe-tenant non-impact; higher-parallelism stability hardening remains active. |
| Provider-agnostic process-host recovery | Implemented / validated | Real process-host crash recovery is shared by HTTP and gRPC scenarios through a provider-neutral recovery test framework and profile model. |
| Runtime Host Manager lifecycle strategies | Implemented / validated | Host creation modes separate provider scale-out from lifecycle; process mode launches child hosts and Kubernetes mode creates Pod/Service resources through `KubernetesAiRuntimeHostCreationStrategy`. |
| MCP production runtime scenario framework | Implemented / validated | Production scenarios validate HTTP process-host scale-out, runtime registration/capacity readiness, Dedicated/Shared/Hybrid tenants, retention, ledger, trace, replay report, replay ledger, and replay trace across process boundaries. |
| Tenant-aware HTTP scale-out | Implemented / validated | HTTP scale-out preserves tenant runtime settings and validates Shared, Dedicated, Hybrid, Dedicated no shared fallback, Hybrid visibility behavior, and tenant runtime settings precedence through Redis-backed scale-out requests. |
| Run admission / slot decisions | Implemented | Admission can assign runs to a tenant-visible runtime instance, request scale-out, queue globally, or reject according to policy. Direct-dispatch mode preserves `RequestScaleOut`; queue-first mode intentionally queues globally first. |
| Redis-backed scale-out request lifecycle | Implemented / validated | Scale-out requests are persisted in Redis, observed by a watcher, resolved through provider selection, fulfilled or rejected, linked back to the original shared run, and enriched with tenant runtime settings. |
| Local runtime scale-out and fulfilled-run requeue | Implemented / validated | The local provider/scaler can create tenant-scoped runtime capacity dynamically; fulfilled requests requeue the shared run so the normal pump performs dispatch and execution proceeds through the local queue. |
| Shared Runtime Controller V1 | Implemented | Shared controller handles assigned dispatch, queue-first submission, global queue enqueue, Redis-backed shared queue dispatch, dispatch-time admission, queue pump/background consumption, Redis-backed scale-out request publication, fulfilled-run requeue, tenant-aware visibility, and shared run visibility. |
| Shared queue pump and manual drain | Implemented / validated | Queue-first runs can remain globally queued, be dispatched by a background pump after readiness, or be manually drained later through MCP/control-plane tools. The dispatcher restores tenant execution context before dispatch-time admission. |
| Redis runtime capacity descriptors | Implemented / validated | Runtime instances publish Redis-backed capacity descriptors for worker count, active/free workers, max workers per execution, run slots, queue pressure, heartbeat, provider metadata, tenant metadata, and capacity-aware admission. |
| Runtime worker capacity visibility | Implemented / validated | Runtime snapshots expose `WorkerCount`, `ActiveWorkerCount`, `AvailableWorkerCount`, `MaxLocalWorkersPerExecution`, and worker-aware `CanAcceptRun`. |
| MCP server control-plane adapter | Foundation available / validated | MCP tools expose shared run, shared queue, manual drain, background pump, runtime instance, runtime queue, replay, execution control, observability, worker-capacity visibility operations, and RBAC-backed tenant-aware control-plane operations. |
| Runtime instance provider model | Implemented foundations / validated local, HTTP, gRPC, and Kubernetes scenarios | Providers own command dispatch and scale-out capabilities; Runtime Host Manager strategies own process or Kubernetes lifecycle. Redis command queue remains future direction. |
| RunId vs ExecutionId separation | Implemented | Controller lifecycle identity is separated from durable DAG execution identity. |
| Snapshot and Replay API foundations | Implemented | Terminal snapshots, replay metadata, deterministic fingerprint validation, audit-only replay, restore replay, ledger loading, and timeline loading are available. |
| Execution-correlated decision ledger | Implemented | Durable correlated ledger events exist for execution lifecycle, run lifecycle, queue control, claims, steps, retry, recovery, policy evaluation, concurrency, execution control, human input, snapshots, storage failures, replay lifecycle, retention, compaction, tenant context, and finalization. |
| Observability, metrics, and tracing | Foundation available | Runtime metrics, trace recording, realtime events, correlated trace timelines, and configurable Memory/Mongo/MemoryAndMongo persistence exist; hardened OpenTelemetry integration and dashboarding remain planned. |
| Product-platform evolution | Direction defined | Long-term product direction is documented under `docs/product-roadmap/`, including roadmap, dashboard, pipeline builder, MCP interface, memory/context lifecycle, security, testing, observability, multi-tenant readiness, and managed hosting direction. |
| Durable decision ledger | Foundation available | Execution-correlated runtime ledger foundations are implemented and aligned with runtime correlation, including replay lifecycle visibility. |
| Public API / SDK polish | Planned | Future work for cleaner external developer experience. |

---

## Architecture at a Glance

```text
Client / API / MCP / Controller
        |
        v
RBAC Execution Context / ExecutionContextSnapshot / Tenant Isolation Layer
        |
        +--> User / Project / Namespace / ContextKey
        +--> TenantId / TenantGroupId
        +--> Durable execution context snapshot
        |
        v
Runtime Orchestration / Shared Control Plane Layer
        |
        +--> Shared Run Store
        +--> Shared Queue
        +--> Queue Pump / Manual Drain
        +--> Dispatch-Time Admission
        +--> Runtime Readiness Gate
        +--> Scale-Out Request Publication
        +--> Fulfilled-Run Requeue
        |
        v
Tenant-Aware Registry / Capacity / Admission Layer
        |
        +--> Shared runtime visibility
        +--> Dedicated runtime visibility
        +--> Hybrid runtime visibility
        +--> Tenant runtime settings
        +--> Tenant-scoped scale-out limits
        |
        v
Redis Discovery / Registry / Capacity / Reservation / Scale-Out Request Layer
        |
        +--> Control-plane discovery
        +--> Runtime instance registry
        +--> Runtime capacity descriptors
        +--> Admission reservations
        +--> Scale-out requests
        |
        v
Runtime Instance Provider / Command Transport Layer
        |
        +--> Local provider
        +--> HTTP provider
        +--> gRPC provider
        +--> Scale-out provider selection
        |
        v
Runtime Host Manager / Host Lifecycle Layer
        |
        +--> Fixture strategy
        +--> Process strategy
        +--> Attach strategy
        +--> Kubernetes strategy
                |
                +--> RuntimeInstanceOnly Pod
                +--> Service / NodePort
                +--> Per-runtime port-forward
                +--> Shared Gateway API route
                +--> Resource / runtime / transport readiness
        |
        +--> Runtime health signals / unsafe capacity suppression
        +--> Execution recovery reconciliation
        +--> In-flight DAG resume / local queued redispatch
        |
        v
Pipeline Definition + DAG Resolution
        |
        v
Context Resolution and Helper Layer
        |
        +--> Input binding resolution
        +--> Previous step output resolution
        +--> Payload rehydration
        +--> Provider / model / operation context
        +--> Policy context
        +--> Concurrency context
        +--> RAG retrieval / merge / compose context
        +--> Replay-safe context
        |
        v
DAG Execution Engine
        |
        +--> Execution Control Gate
        |
        +--> Concurrency / Throttling Engine
        |
        v
Redis Hot State + Redis Lua Coordination
        |
        +--> Atomic step claims
        +--> Retry scheduling
        +--> Worker recovery
        +--> Control state
        +--> Distributed leases
        +--> Shared run records
        +--> Shared queue claims
        +--> Runtime registry entries
        +--> Runtime capacity descriptors
        +--> Control-plane discovery
        +--> Admission reservations
        |
        v
Runtime Instances / Local Queues / Stateless Workers
        |
        v
Step Executors
        |
        +--> LLM
        +--> RAG
        +--> Tools
        +--> Decisions
        |
        v
MongoDB Payloads / Snapshots / Replay Validation
        |
        v
Execution-Correlated Decision Ledger
        |
        v
Correlated Observability / Metrics / Tracing Foundations
```

The runtime is intentionally split into layers:

- RBAC context is captured once and converted into durable execution snapshots
- tenant isolation rules filter runtime visibility before admission and dispatch
- orchestration starts and manages executions
- DAG state determines what can run
- context helpers resolve inputs, payloads, metadata, RBAC snapshots, tenant data, and policy context
- Redis coordinates distributed workers
- policies control runtime behavior
- workers execute claimed steps
- persistence stores large payloads and snapshots
- replay validates deterministic reconstruction from persisted snapshots
- shared controller coordinates run admission, shared run persistence, queue-first submission, global queue dispatch, queue pumping/manual drain, dispatch-time admission, and scale-out publication
- runtime instance providers route selected runs to local, HTTP, or gRPC runtime instances while preserving local queues
- HTTP runtime provider hardening protects remote dispatch with timeout, retry, circuit-breaker, and structured failure reporting
- HTTP and gRPC scale-out validate the control-plane capacity lifecycle through provider selection, registry/capacity publication, tenant visibility, Redis-backed scale-out fulfillment, Runtime Host Manager process launch, registration/capacity readiness, and provider dispatch
- Kubernetes scale-out reuses that same lifecycle, replacing child-process creation with deterministic Pod/Service creation while keeping the selected HTTP/gRPC provider unchanged
- Kubernetes readiness is layered: a Pod may exist or report `Running` before the runtime has registered, published safe capacity, and exposed a reachable transport endpoint
- host cleanup deletes only resources owned by the failed startup invocation, preserving convergence and avoiding destructive cleanup of pre-existing runtime resources
- runtime process crash recovery keeps responsibility boundaries explicit: health reconciliation suppresses unsafe capacity, execution recovery reconciles already assigned work, and HTTP/gRPC providers report transport signals without owning recovery
- runtime capacity snapshots expose run slots, worker pressure, provider metadata, tenant visibility, and worker-aware `CanAcceptRun`
- correlated observability records runtime behavior across ledger, metrics, traces, workers, tenants, and executions

---

## Kubernetes Runtime Hosting

Kubernetes is implemented as a **Runtime Host Manager lifecycle strategy**, not as a replacement runtime provider.

That distinction preserves the execution architecture:

```text
Shared run admission
    ↓
Scale-out request
    ↓
HTTP or gRPC scale-out provider
    ↓
Runtime Host Manager
    ↓
KubernetesAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly Pod + Service
    ↓
Kubernetes resource readiness
    ↓
Runtime registration and capacity readiness
    ↓
HTTP/gRPC transport readiness
    ↓
Scale-out fulfilled
    ↓
Normal shared queue dispatch
```

### What Kubernetes Owns

- deterministic Pod and Service creation from `AiRuntimeHostStartRequest`
- runtime identity, labels, annotations, environment variables, ports, and tenant metadata
- fake-client lifecycle proofs and real Kubernetes SDK interaction
- Service DNS, NodePort, per-runtime port-forward, and shared Gateway API exposure
- layered readiness and startup timeout handling
- ownership-safe cleanup after partial creation failure
- explicit runtime host deletion

### What Kubernetes Does Not Own

- shared run admission
- provider selection
- HTTP or gRPC command semantics
- shared queue claim and dispatch
- durable DAG state
- retry, replay, ledger, trace, or forensics semantics
- health suppression or execution recovery policy

A Pod can be `Running` while the runtime is still unusable. The host strategy therefore treats readiness as separate boundaries:

```text
Kubernetes resource ready
    ≠ runtime registered
    ≠ capacity safely published
    ≠ HTTP/gRPC endpoint reachable
```

When a Pod disappears, Kubernetes terminates the host boundary. The existing health reconciler removes unsafe capacity, the execution recovery reconciler classifies assigned work, in-flight DAGs resume with the same `ExecutionId`, and local queued work is redispatched through its durable `SharedRunId`.

Targeted SDK-backed scenarios validate Kubernetes scale-out, readiness, transport preservation, Pod deletion/crash recovery, and tenant-safe non-impact. Higher-parallelism stability on local Minikube/Docker Desktop environments remains an active hardening area rather than a claimed production guarantee.

For the complete component map, lifecycle, networking modes, readiness model, configuration, tests, and source map, see [`docs/ai/kubernetes-runtime-host-provider.md`](docs/ai/kubernetes-runtime-host-provider.md).

---

## Multi-Tenant Control-Plane Isolation

The current runtime now includes a tenant-aware control-plane foundation.

The core rule is:

```text
ExecutionContextSnapshot.TenantId
    = durable tenant isolation boundary
```

RBAC execution context is used at the API/MCP/tool edge. The stable execution identity is then captured and persisted as `ExecutionContextSnapshot`.

```text
MCP/API call
    ↓
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
SharedRunRecord
    ↓
Shared Queue / Dispatcher / Admission
    ↓
Runtime local queue
    ↓
Background controller restores context
    ↓
DAG ExecutionId
```

### ContextKey vs TenantId

`ContextKey` is useful for:

- RBAC access-context lookup
- correlation
- debugging
- demo override headers
- diagnostics

`TenantId` is used for:

- durable runtime isolation
- tenant-aware registry filtering
- tenant-aware capacity filtering
- admission decisions
- scale-out settings
- runtime instance visibility
- future persistence partitioning

Do not use `ContextKey` as the durable tenant boundary.

### Isolation Modes

The current runtime supports three validated isolation modes:

```text
Shared
Dedicated
Hybrid
```

Shared runtime behavior:

```text
Shared runtime:
    visible to shared/default tenants
    visible to hybrid/dedicated tenants only when tenant settings allow shared fallback
```

Dedicated runtime behavior:

```text
Dedicated runtime:
    visible only to matching TenantId or TenantGroupId
```

Hybrid runtime behavior:

```text
Hybrid runtime:
    visible only to matching TenantId or TenantGroupId
    may allow its owning tenant to fall back to shared capacity
    does not make unowned hybrid runtimes globally visible
```

### Tenant Runtime Settings

Current tenant runtime settings are hardcoded as a foundation and will later become configuration or database backed.

The settings include:

```text
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
MaxRuntimeInstances
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
```

Current validated examples:

```text
tenant-a
    IsolationMode = Dedicated
    PreferDedicatedCapacity = true
    AllowSharedFallback = false
    RuntimeInstanceIdPrefix = tenant-a-runtime

tenant-b
    IsolationMode = Hybrid
    PreferDedicatedCapacity = true
    AllowSharedFallback = true
    RuntimeInstanceIdPrefix = tenant-b-runtime

default / unknown / test-tenant
    IsolationMode = Shared
    PreferDedicatedCapacity = false
    AllowSharedFallback = true
    RuntimeInstanceIdPrefix = runtime-instance
```

### Tenant-Aware Dispatch Flow

```text
Shared run submitted
    ↓
ExecutionContextSnapshot persisted
    ↓
Registry/capacity listed through tenant visibility evaluator
    ↓
Admission selects only tenant-visible runtime instances
    ↓
Shared queue dispatcher restores snapshot before dispatch-time admission
    ↓
Provider dispatches into selected runtime local queue
    ↓
Runtime background controller restores snapshot before execution
    ↓
ExecutionId created with tenant context available
```

### Tenant-Aware Scale-Out Flow

```text
No tenant-visible capacity
    ↓
Admission = RequestScaleOut
    ↓
Scale-out request persisted with tenant settings
    ↓
Watcher observes request
    ↓
Provider selector resolves scale-out provider
    ↓
Local scaler creates instance using tenant RuntimeInstanceIdPrefix
    ↓
New runtime registers and publishes tenant-aware capacity
    ↓
Scale-out request fulfilled
    ↓
Shared run requeued
    ↓
Pump dispatches using normal tenant-aware admission
```

This keeps scale-out and dispatch separated while preserving tenant isolation.

The same tenant-aware scale-out contract is now validated for the HTTP provider process-host path. The HTTP provisioner can delegate host lifecycle to the Runtime Host Manager, launch a real `RuntimeInstanceOnly` process in `Process` mode, wait for runtime self-registration and capacity readiness, and then mark the Redis scale-out request fulfilled.

### End-to-End Runtime Flow

The complete runtime path is documented in [`docs/ai/multi-tenant-runtime-flow.md`](docs/ai/multi-tenant-runtime-flow.md).

That document shows the full execution path:

```text
MCP Client / Tool
    ↓
MCP Server / Control Plane
    ↓
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
Shared Runtime Controller
    ↓
Shared Run Store
    ↓
Tenant-Aware Admission
    ↓
Tenant-Visible Registry / Capacity
    ↓
AssignToInstance or RequestScaleOut
    ↓
Shared Queue / Requeue
    ↓
Shared Queue Dispatcher restores context
    ↓
Runtime Provider Dispatch
    ↓
Runtime Instance Local Queue
    ↓
Background Controller restores context
    ↓
DAG Execution Creation
    ↓
Worker Loop / Step Execution
    ↓
Execution Control / Finalization
    ↓
Shared Run Final Status
    ↓
Observability / Ledger / Tracing / Replay
```

The flow is useful when debugging or reviewing the runtime because it shows where tenant context can be lost and where it must be restored. It also makes the key invariant explicit: no background, queued, shared, or distributed runtime run should execute without a durable `ExecutionContextSnapshot`.

---

## Enterprise Readiness

The project is designed around production questions that enterprise AI systems must answer.

| Enterprise Question | Runtime Direction |
|---|---|
| What happens if a worker crashes? | Running steps can be recovered through stale-claim detection and Redis-backed recovery. |
| What happens if a runtime process dies? | Assigned work is recovered from durable shared run state, runtime execution indexes, DAG state, registry/capacity state, and recovery forensics rather than from the dead local queue. |
| How do you recover in-flight work differently from local queued work? | In-flight DAG executions resume with the same `ExecutionId`; local queued work that never started is redispatched through its durable `SharedRunId`. |
| How do you prove recovery did not affect unrelated tenants? | Safe tenant non-impact is validated through completed safe runs, replay proofs, zero safe-tenant recovered work, zero safe-tenant forensics, and no cross-tenant ledger leakage. |
| How do you prevent duplicate executions? | Atomic Redis Lua claims and claim tokens enforce single step ownership. |
| How do you replay a workflow? | Terminal snapshots, replay metadata, deterministic fingerprint validation, audit-only replay, and restore replay provide replay foundations. |
| How do you audit an AI decision? | Execution-correlated decision ledger events, correlated metrics/traces, execution state, step results, retry metadata, recovery state, snapshots, replay reports, tenant context, and observability provide audit foundations. |
| How do you limit concurrency? | Distributed Redis ZSET leases and policy-driven throttling enforce limits. |
| How do you resolve execution context safely? | Context helpers resolve inputs, step outputs, payload references, provider metadata, policy context, RBAC snapshots, and tenant identity consistently. |
| How do you pause/resume/cancel safely? | Execution control state blocks new claims and coordinates deterministic finalization. |
| How do you control human-in-the-loop? | WaitingForInput and SubmitHumanInput are supported through durable control state. |
| How do you keep memory/state bounded? | Retention, compaction, eviction, and payload externalization control hot state size. |
| How do you coordinate multiple runtime instances? | Shared Redis state, Lua coordination, leases, shared run records, Redis-backed shared queue claims, Redis discovery, Redis registry/capacity stores, admission reservations, queue pump/background consumption, dispatch-time admission, provider-hosting foundations, and deterministic convergence enable coordination. |
| How do you preserve RBAC after background dispatch? | RBAC execution context is persisted into `ExecutionContextSnapshot` and restored by shared queue dispatchers and runtime background controllers before admission or execution. |
| How do you isolate tenants in registry, capacity, admission, and scale-out? | Tenant-aware visibility evaluates Shared, Dedicated, and Hybrid runtime descriptors against `TenantId`, `TenantGroupId`, and tenant runtime settings before scheduling. |
| How do tenant-specific runtime settings affect scale-out? | Scale-out requests preserve tenant isolation mode, fallback policy, runtime instance prefix, worker count, run slots, queue capacity, and max instance count. |
| How do you prevent one execution from consuming all workers? | `MaxLocalWorkersPerExecution`, active/free worker tracking, effective worker reservation, and worker-aware runtime capacity snapshots. |
| How do you dispatch queued work without an automatic background pump? | Queue-first submit mode plus manual shared queue drain through MCP/control-plane tooling. |
| How do you submit work when no runtime capacity exists? | Direct-dispatch admission can return `RequestScaleOut`, persist a Redis-backed scale-out request, and let the scale-out watcher/provider path create runtime capacity. |
| How do you execute the run after scale-out succeeds? | The fulfilled scale-out request requeues the original shared run; the normal shared queue pump then performs claim, dispatch-time admission, provider dispatch, and local runtime execution. |
| How do runtime-only hosts join the correct MCP/control-plane scope? | Redis control-plane discovery and `ControlPlaneIdResolver` allow runtime-only hosts to resolve the MCP-published logical identity before registration. |
| How do you validate remote-style HTTP runtime dispatch before Kubernetes? | HTTP pooled provider scenarios validate `RuntimeInstanceOnly` HTTP hosts with internal `runtime-http-*` child runtime instances. |
| How do you harden HTTP runtime dispatch under failure? | HTTP provider dispatch now has timeout, retry, circuit-breaker behavior, structured failure reasons, and persisted shared-run dispatch failure visibility. |
| How do you validate HTTP runtime scale-out with a real runtime process? | HTTP process-host scenarios use Redis-backed scale-out requests, watcher, HTTP provider, Runtime Host Manager, `ProcessAiRuntimeHostCreationStrategy`, real `RuntimeInstanceOnly` process launch, registration/capacity readiness, and normal HTTP dispatch. |
| How do you validate gRPC runtime scale-out with a real runtime process? | gRPC process-host scenarios use Redis-backed scale-out requests, watcher, gRPC provider, Runtime Host Manager, `ProcessAiRuntimeHostCreationStrategy`, real `RuntimeInstanceOnly` gRPC process launch, HTTP/2 transport, registration/capacity visibility, and normal gRPC dispatch. |
| How do you scale out the same runtime into Kubernetes? | The selected HTTP/gRPC scale-out provider delegates lifecycle to Runtime Host Manager, which invokes `KubernetesAiRuntimeHostCreationStrategy` to create a `RuntimeInstanceOnly` Pod and Service before normal provider dispatch resumes. |
| How do you avoid treating `Pod Running` as ready capacity? | Kubernetes resource readiness, runtime registration/capacity readiness, and transport endpoint readiness are evaluated separately before fulfillment. |
| How do you preserve transport semantics in Kubernetes? | Kubernetes owns host lifecycle and endpoint exposure; the existing HTTP or gRPC provider still owns command serialization, dispatch, failures, retries, and provider metadata. |
| How do you recover after a Pod crash? | Pod loss is detected through the normal runtime health boundary; unsafe capacity is suppressed, assigned work is reconciled, replacement capacity is created, in-flight DAGs resume by `ExecutionId`, and local queued work is redispatched by `SharedRunId`. |
| How do you avoid duplicating recovery logic per provider? | Provider-agnostic process-host recovery reuses the same recovery contract for HTTP and gRPC through provider profiles, while provider-specific code remains limited to transport, scale-out, metadata, and host settings. |
| How do you prove process-boundary observability? | MCP production scenarios validate ledger, trace, replay report, replay ledger, replay trace, and retention from the parent MCP process after execution occurs in a child runtime process. |
| How do you prove deterministic convergence? | Integration tests and enterprise demo scenarios validate completion, replay fingerprints, distributed execution, throttling, recovery behavior, atomic retention, compaction consistency, ledger visibility, and trace timeline visibility. |
| How does this evolve toward a product platform? | Runtime foundations are designed to support future AI execution control planes, governance, observability, replay, dashboard, pipeline builder, MCP tools, managed hosting, memory/context governance, and operational workflows. |

For the detailed enterprise matrix, see [`docs/enterprise-readiness.md`](docs/enterprise-readiness.md).

---

## Runtime Control Plane

The runtime includes an adapter-neutral control-plane foundation for operational runtime control.

The control plane currently exposes foundations for:

- replay and audit control
- execution control
- local runtime queue control
- runtime instance registration and heartbeat
- runtime instance visibility and draining
- Redis control-plane discovery
- control-plane id resolution
- runtime capacity publication and cleanup
- RBAC execution-context capture
- durable `ExecutionContextSnapshot` propagation
- tenant-aware Shared/Dedicated/Hybrid runtime visibility
- tenant-aware registry and capacity filtering
- tenant-aware admission
- tenant-scoped scale-out settings
- admission reservations
- run admission / slot decisions
- Shared Runtime Controller V1
- Redis-backed runtime instance registry and capacity descriptors
- Redis control-plane discovery and id resolution
- Redis admission reservation store
- Redis-backed control-plane discovery
- MCP server control-plane adapter documentation
- runtime instance provider model direction
- shared run persistence
- Redis-backed shared run store
- Redis-backed shared queue
- direct assigned-run dispatch
- queue-first submit mode
- global shared queue dispatch
- dispatch-time admission
- shared queue pump
- manual shared queue drain
- hosted shared queue background consumption
- runtime worker-capacity visibility
- HTTP pooled runtime provider dispatch
- HTTP runtime provider timeout, retry, and circuit-breaker hardening
- HTTP runtime provider structured dispatch failure persistence
- HTTP runtime scale-out provider and provisioner foundation
- gRPC runtime provider dispatch and scale-out foundation
- Kubernetes Runtime Host Provider lifecycle strategy
- Kubernetes fake and SDK clients
- deterministic `RuntimeInstanceOnly` Pod and Service creation
- Service/NodePort/port-forward/Gateway transport exposure
- layered Kubernetes, runtime, and transport readiness
- Kubernetes Pod crash recovery through the shared recovery contract
- Runtime Host Manager process-host provisioning
- Kubernetes Runtime Host Manager strategy
- Kubernetes fake and SDK client modes
- deterministic Pod and Service construction
- Service, NodePort, per-runtime port-forward, and Gateway API exposure
- Kubernetes resource, runtime, and transport readiness
- ownership-safe Kubernetes startup cleanup and explicit host deletion
- runtime process crash recovery reconciliation
- runtime recovery forensics and incident evidence
- tenant-aware HTTP scale-out for Shared, Dedicated, and Hybrid runtime modes
- MCP production runtime scenario validation
- Redis-backed scale-out request publication
- scale-out watcher/provider selector lifecycle
- fulfilled scale-out shared-run requeue
- Redis discovery/registry/capacity foundations
- future HTTP API, CLI, dashboard, and managed Kubernetes operational adapters

The control plane separates three identity levels.

```text
SharedRunId
= shared controller / global queue lifecycle id

RunId
= local runtime queue / background controller lifecycle id

ExecutionId
= authoritative durable DAG execution id
```

This separation allows the runtime to manage global queue work, local queue work, and durable execution state without mixing their lifecycles.

### RBAC and Tenant Isolation in the Control Plane

External MCP/API/tool calls should enter the runtime through an RBAC execution context.

The runtime then maps that context into a durable `ExecutionContextSnapshot`.

```text
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
Shared run request
    ↓
Shared run record
    ↓
Shared queue item
    ↓
Runtime local queue run
    ↓
Durable ExecutionId
```

The control plane must preserve this snapshot across:

- direct assigned dispatch
- queue-first submission
- shared queue persistence
- manual drain
- background pump dispatch
- dispatch-time admission
- provider dispatch
- local runtime queue enqueue
- runtime background execution
- scale-out request publication
- fulfilled-run requeue

Without this, a background service could lose the original tenant and incorrectly list all registry/capacity entries or select the wrong runtime capacity.

### ExecutionId-Level Control

Implemented execution control capabilities include:

- pause execution
- resume execution
- cancel execution
- wait for human input
- submit human input
- block new claims based on control state
- cancellation finalization override

### RunId-Level Queue Control

Implemented controller queue capabilities include:

- pause queue
- resume queue
- cancel queued run
- cancel running run by bridging to ExecutionId cancellation
- hot enqueue while controller is running
- hot enqueue while queue is paused
- require `ExecutionContextSnapshot` before starting local background execution

This makes the runtime controllable, not only executable.

### Runtime Instance Control

Implemented runtime instance capabilities include:

- register runtime instance
- heartbeat runtime instance
- get runtime instance status
- list runtime instances
- expose local queue pressure
- expose available run slots
- expose active worker count
- expose available worker count
- expose max local workers per execution
- expose worker-aware CanAcceptRun
- expose tenant ownership and isolation descriptors
- filter runtime visibility by tenant settings
- mark runtime instance as draining
- unregister runtime instance

These foundations now support Kubernetes Pod visibility and lifecycle convergence while continuing to prepare dashboards, multi-tenant scheduling policy, pooled host reuse, and production autoscaling hardening.

### Runtime Discovery, Registry, and Capacity

Implemented discovery and visibility capabilities include:

- Redis control-plane discovery descriptor publication
- `ControlPlaneIdResolver`
- runtime-only host identity resolution before registration
- Redis runtime instance registry
- Redis runtime capacity store
- runtime capacity descriptor publication
- runtime capacity descriptor cleanup
- tenant visibility descriptors
- shared queue pump readiness gate
- cleanup without late rediscovery dependency

The current validated flow is:

```text
MCP Control Plane
    ↓
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
Redis Control-Plane Discovery Store
    ↓
ControlPlaneIdResolver
    ↓
RuntimeInstanceOnly Host
    ↓
Runtime Instance Registry
    ↓
Runtime Capacity Store
    ↓
Tenant Visibility Filter
    ↓
Shared Queue Pump Readiness
    ↓
Provider Dispatch
```

### Run Admission / Slot Decisions

Implemented admission capabilities include:

- assign a run to a tenant-visible runtime instance
- prefer a requested runtime instance when available and visible
- prefer dedicated capacity when tenant settings require it
- use shared fallback only when tenant settings allow it
- select the least-loaded available instance from the tenant-visible set
- request scale-out when no tenant-visible instance has capacity
- queue globally later when shared queue fallback is enabled
- reject when no capacity or fallback exists

Admission does not enqueue runs directly.

It only decides what should happen next. The shared runtime controller now applies that decision by dispatching assigned runs, enqueueing globally queued runs, publishing scale-out requests, or recording rejected runs.

### Shared Runtime Controller V1

Implemented shared controller capabilities include:

- submit shared run
- submit shared run in queue-first mode
- persist `ExecutionContextSnapshot` with the shared run
- get shared run
- list shared runs
- cancel shared run
- assign run to runtime instance
- dispatch assigned run locally or through provider metadata
- queue run globally
- claim globally queued run
- dispatch globally queued run
- restore execution context at dispatch time
- re-admit queued work at dispatch time
- separate pump identity from assigned runtime identity
- requeue failed dispatch
- mark shared run dispatched
- mark shared queue item dispatched
- publish tenant-scoped scale-out request
- pump shared queue manually
- consume shared queue through hosted background service
- coordinate shared queue dispatch through Redis
- prevent double dispatch through Redis atomic claim

The current shared controller flow is:

```text
SubmitRun
  -> RBAC ExecutionContext
  -> ExecutionContextSnapshot
  -> IAiRunAdmissionController

  -> AssignToInstance
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiSharedRunDispatcher.DispatchAsync(...)
      -> IAiSharedRunStore.MarkDispatchedAsync(...)
      -> SharedRun.Status = Dispatched

  -> QueueGlobally / QueueFirst
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiSharedQueue.EnqueueAsync(...)
      -> SharedRun.Status = QueuedGlobally
      -> SharedQueueItem.Status = Pending

  -> SharedQueuePump / ManualDrain
      -> IAiSharedQueue.ClaimNextAsync(...)
      -> IAiSharedRunStore.GetAsync(...)
      -> restore ExecutionContextSnapshot
      -> IAiRunAdmissionController.AdmitAsync(...)
      -> IAiRuntimeAdmissionReservationStore.TryReserveAsync(...)
      -> IAiSharedRunDispatcher.DispatchAsync(...)
      -> IAiSharedQueue.MarkDispatchedAsync(...)
      -> IAiSharedRunStore.MarkDispatchedAsync(...)

  -> RequestScaleOut
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiRuntimeScaleOutRequestPublisher.PublishAsync(...)
      -> Redis scale-out request persisted with tenant runtime settings
      -> SharedRun.Status = ScaleOutRequested
      -> watcher/provider/scaler creates tenant-scoped capacity
      -> fulfilled run is requeued
      -> shared queue pump dispatches normally

  -> Reject
      -> IAiSharedRunStore.CreateAsync(...)
      -> SharedRun.Status = Rejected
```

For details, see [`docs/ai/runtime-control-plane.md`](docs/ai/runtime-control-plane.md).

---

## Distributed Execution and Coordination

The runtime uses Redis as a hot state and coordination layer.

Redis is not only used as a cache. It is used as the active distributed execution state.

Critical operations include:

- creating execution state
- restoring execution context before local runtime execution
- claiming ready DAG steps
- validating claim ownership
- completing steps
- failing steps
- scheduling retries
- recovering stale running steps
- enforcing distributed concurrency leases
- storing execution control state

Redis Lua scripts are used for atomic transitions where race conditions must be avoided.

This allows multiple workers or runtime instances to cooperate safely without direct worker-to-worker communication.

Distributed execution must preserve the same execution context that was created at submission time.

```text
SharedRun.ExecutionContextSnapshot
    ↓
Runtime local queue request
    ↓
Background controller restore
    ↓
ExecutionId creation
    ↓
Worker execution
```

The tenant context is not inferred from logs, metadata, or runtime instance names during execution.

It is restored from the durable snapshot.

---

## Context Resolution and Runtime Helpers

The runtime includes a helper layer that connects declarative configuration to concrete execution behavior.

This layer resolves:

- input bindings from execution state
- previous step outputs
- compacted or externalized payloads
- provider, providerKey, model, and operation metadata
- retry policy context
- retention policy context
- concurrency context
- RBAC execution context
- durable `ExecutionContextSnapshot`
- tenant and tenant-group identity
- RAG retrieval, merge, and compose context
- replay-safe comparison data

This keeps the DAG engine focused on orchestration and prevents plugins, policies, providers, and control-plane components from manually reconstructing raw execution state.

For details, see [`docs/ai/context-resolution-and-helpers.md`](docs/ai/context-resolution-and-helpers.md).

---

## Observability, Replay, and Audit Foundations

The runtime includes foundations for production visibility and replayability:

- execution lifecycle metrics
- retry and recovery metrics
- runtime process crash recovery events
- recovery forensics timelines
- safe tenant non-impact evidence
- control-plane causal-chain proof
- retention metrics
- resolver metrics
- storage metrics
- context resolution diagnostics
- concurrency admission diagnostics
- realtime runtime events
- readable console runtime events
- execution-correlated decision ledger
- durable runtime lifecycle audit events
- tenant context visibility
- RBAC context correlation
- claim, concurrency, retry, and recovery audit visibility
- queue and execution control audit visibility
- atomic retention and compaction auditability
- shared runtime controller foundations
- Redis-backed shared queue coordination
- queue pump and background consumption
- queue-first and manual drain visibility
- dispatch-time admission visibility
- runtime worker capacity visibility
- tenant-aware registry and capacity filtering
- Redis-backed scale-out request publication
- tenant-scoped scale-out settings
- scale-out watcher/provider selector lifecycle
- fulfilled scale-out shared-run requeue
- Redis discovery/registry/capacity foundations
- snapshot persistence audit events
- replay lifecycle ledger events
- replay metadata
- replay report generation
- replay snapshot loading
- replay deterministic fingerprint validation
- replay dependency graph validation
- replay step state validation
- replay payload reference validation
- replay ledger event loading
- replay trace timeline loading
- replay diagnostic output for ledger and timeline inspection
- correlated trace recording foundations
- correlated metric and trace storage modes
- in-memory, MongoDB, and MemoryAndMongo observability persistence
- terminal snapshots
- replay restoration
- deterministic replay fingerprint validation

Runtime metrics and traces can be configured as `Disabled`, `Memory`, `Mongo`, or `MemoryAndMongo`. This allows local diagnostics, durable MongoDB-backed inspection, or both at the same time while keeping the execution runtime independent from observability storage choices.

Replay can validate persisted executions without re-running LLMs, tools, external providers, or side effects. It can expose replay metadata, decision ledger events, tenant context, and trace timeline events when requested.

OpenTelemetry-style distributed tracing, richer dashboards, HTTP replay APIs, replay audit tooling, advanced decision lineage, lifecycle diagnostics, memory/context evidence, and product-roadmap features remain roadmap items.

---

## Current Status

This project is under active development.

It should be treated as an advanced reference implementation and evolving AI infrastructure project, not as a polished commercial product.

The strongest areas today are:

- deterministic DAG execution
- Redis-backed distributed state
- Redis Lua atomic coordination
- context resolution and helper foundations
- retry/recovery semantics
- runtime process crash recovery
- multi-tenant crash isolation
- recovery forensics
- recovery replay / ledger / trace proof
- retention/compaction
- distributed concurrency and throttling
- executable distributed throttling scenario
- execution control state
- shared run records
- shared queue claims
- runtime instance registry entries
- runtime capacity descriptors
- control-plane discovery descriptors
- admission reservations
- runtime queue control
- runtime control-plane foundations
- RBAC execution context propagation into durable runtime snapshots
- tenant-aware Shared/Dedicated/Hybrid runtime visibility
- tenant-aware registry, capacity, admission, shared queue dispatch, and scale-out settings
- Shared Runtime Controller V1
- Redis-backed runtime instance registry and capacity descriptors
- Redis control-plane discovery and id resolution
- Redis admission reservation store
- Redis-backed control-plane discovery
- MCP server control-plane adapter documentation
- runtime instance provider model direction
- Redis-backed shared run store and shared queue
- shared queue dispatcher, pump, manual drain, and background service
- queue-first submit mode
- dispatch-time admission
- runtime worker capacity visibility
- local and HTTP pooled runtime provider hosting foundations
- HTTP runtime provider dispatch hardening
- HTTP runtime provider scale-out foundation
- gRPC runtime provider dispatch and scale-out foundation
- Runtime Host Manager process-host provisioning
- real `RuntimeInstanceOnly` process launch from HTTP and gRPC scale-out
- Kubernetes Runtime Host Manager scale-out
- fake and Kubernetes SDK lifecycle clients
- deterministic `RuntimeInstanceOnly` Pod and Service creation
- direct Service, NodePort, per-runtime port-forward, and Gateway API endpoint construction
- Kubernetes resource, runtime registration/capacity, and transport readiness boundaries
- gRPC transport preservation across Kubernetes host creation
- Kubernetes Pod deletion/crash boundary validation
- replacement runtime creation and strict recovery through existing reconcilers
- tenant-aware HTTP Shared/Dedicated/Hybrid scale-out validation
- MCP production runtime scenario validation
- provider-agnostic HTTP/gRPC process-host recovery validation
- durable ledger / trace / replay validation across process boundaries
- Redis-backed scale-out request publication
- scale-out watcher/provider selector lifecycle
- fulfilled scale-out shared-run requeue
- Redis discovery/registry/capacity foundations
- runtime instance registry and control
- run admission / slot decisioning
- queue and execution control observability
- replay/snapshot validation foundations
- replay metadata, ledger, and trace timeline diagnostics
- correlated observability, tracing, metrics, and realtime logging foundations
- integration-test-driven validation
- enterprise runtime demo execution context snapshot propagation
- end-to-end multi-tenant runtime flow documentation
- large unit and integration suite covering local, process-host, and targeted Kubernetes scenarios

Areas still evolving include:

- public API/SDK polish
- remote runtime instance dispatch hardening beyond the validated HTTP timeout/retry/circuit-breaker and gRPC process-host foundations
- provider-based runtime instance administration beyond local/HTTP/gRPC foundations
- higher-parallelism Kubernetes stability hardening for local Minikube/Docker Desktop environments
- tenant settings persistence through configuration or database-backed provider
- final shared runtime pooling semantics and Hybrid shared fallback process-host validation
- production-cluster Kubernetes RBAC, Gateway, ingress, and provider capability hardening
- Mongo persistence partitioning and indexes for tenant-aware ledger/replay/correlation
- HTTP replay/control-plane APIs and controller abstractions
- OpenTelemetry/exporter polish for tracing and metrics
- operational dashboarding
- Kubernetes demo UI, deployment experience, and operational packaging
- real enterprise sample workflows
- product-roadmap platform capabilities
- dashboard and pipeline builder product layers
- memory/context lifecycle productization
- security and encryption hardening direction
- developer experience / API / SDK / CLI polish
- production documentation split

---

## Enterprise Runtime Demo

A local enterprise-oriented demo is available in [`demo/enterprise-runtime/`](demo/enterprise-runtime/README.md).

The demo is designed to prove that the runtime behaves like distributed AI execution infrastructure, not a toy agent framework.

It currently includes:

- Docker Compose infrastructure for Redis and MongoDB
- scenario documentation for enterprise runtime behaviors
- an external sample step plugin under `Samples/Multiplexed.Sample.External.Plugins.Steps`
- a JSON pipeline at `demo/enterprise-runtime/pipelines/enterprise-demo-pipeline.json`
- interactive console scenario selection
- distributed runtime worker participation
- realtime readable runtime logs
- live progress output
- execution pause, resume, and cancel controls
- retry recovery summaries
- retention and hot-state summaries
- replay validation for supported scenarios
- replay metadata, ledger, and timeline diagnostics in integration tests
- distributed provider throttling through the `throttling-100` scenario
- `RunId` and `ExecutionId` separation
- terminal completion through the controller path
- execution-correlated runtime ledger visibility
- correlated metrics and tracing diagnostics
- MemoryAndMongo observability validation
- queue and execution control audit events
- retry, recovery, and concurrency ledger validation
- atomic retention and compaction auditability
- shared runtime controller foundations
- Redis-backed shared queue coordination
- queue pump and background consumption
- queue-first and manual drain visibility
- dispatch-time admission visibility
- runtime worker capacity visibility
- RBAC execution context propagation
- durable execution context snapshot propagation into demo runtime runs
- tenant-aware registry and capacity filtering
- Redis-backed scale-out request publication
- tenant-scoped scale-out settings
- scale-out watcher/provider selector lifecycle
- fulfilled scale-out shared-run requeue
- Redis discovery/registry/capacity foundations

The current executable console scenarios are:

```text
json
chaos-100
chaos-500
throttling-100
```

The `throttling-100` scenario demonstrates:

- distributed provider throttling
- Redis lease-based concurrency admission
- realtime `[THROTTLED]` visibility
- randomized provider distribution with OpenAI as the throttled target
- bounded provider capacity under worker pressure
- deterministic convergence despite throttling delays

The demo validates the controller execution path, distributed worker participation, runtime controls, realtime logging, correlated observability, tenant/context propagation, and terminal completion behavior. It is intended to show distributed AI execution infrastructure, not only a simple batch or in-memory execution path.

Future demo work will expand into human-in-the-loop, advanced replay workflows, a Kubernetes control-plane/runtime visualization UI, production-oriented deployment packaging, real enterprise sample workflows, dashboard visibility, pipeline builder direction, MCP control scenarios, tenant-aware operations, memory/context lifecycle diagnostics, and broader product-platform capabilities.

---

## Roadmap

The roadmap is organized into phases.

| Phase | Focus | Status |
|---|---|---|
| Completed | Core runtime foundations already implemented | Implemented / validated by tests |
| Phase 0 | README review and documentation restructure | Completed (V1) |
| Phase 1 | Enterprise demo | Completed (V1) - controller demo, distributed workers, runtime controls, chaos scenarios, retention/replay, throttling scenario, and context snapshot propagation validated |
| Phase 1.5 | Runtime process crash recovery | Completed / validated - real process kill recovery, in-flight resume, local queued redispatch, safe tenant non-impact, forensics, and replay/ledger/trace proof validated |
| Phase 2 | Real enterprise sample | Planned |
| Phase 3 | Correlated observability, tracing, and metrics | Foundations available / active polish |
| Phase 4 | Kubernetes runtime hosting and deployment demo | Implemented foundation / active hardening - SDK-driven Pod/Service lifecycle, HTTP/gRPC transport preservation, layered readiness, scale-out, Gateway/port-forward exposure, Pod crash recovery, and tenant-aware scenarios are implemented; demo UI, pooled host reuse, and production-cluster hardening remain active |
| Phase 5 | Public API / SDK polish | Planned |
| Phase 6 | Deterministic Replay Engine and Audit Foundations | Completed (V1) |
| Phase 7 | Replay Controller, HTTP APIs, Dashboard, and Operational Tooling | Planned |
| Phase 8 | Cost, Provider Governance, and Tenant Governance | Planned |
| Phase 9 | Articles and public positioning | Planned |

The roadmap above tracks the current runtime and enterprise demo evolution.

The broader product direction is tracked in [`docs/product-roadmap/index.md`](docs/product-roadmap/index.md), which describes how these deterministic execution foundations evolve toward a complete product platform: runtime engine, replay/audit, Decision Ledger, policy governance, retention lifecycle, observability, execution control, MCP interface, enterprise dashboard, pipeline builder, memory/context lifecycle, security hardening, testing reliability, multi-tenant readiness, managed hosting, and banking/financial-services technical-control direction.

For the runtime roadmap, see [`docs/roadmap.md`](docs/roadmap.md). For the product roadmap, see [`docs/product-roadmap/product-roadmap.md`](docs/product-roadmap/product-roadmap.md).

---

## Roadmap to Product

The runtime foundations are now documented as the base for a product roadmap, not only as a road-to-MLOps direction.

The product roadmap explains how the project can evolve from deterministic execution infrastructure into a broader product platform with:

- deterministic runtime engine
- execution control and state lifecycle
- replay and audit layer
- execution-correlated Decision Ledger
- policy engine and runtime governance
- tenant-aware control-plane isolation
- end-to-end multi-tenant runtime flow visibility
- tenant runtime settings and managed capacity
- retention, eviction, compaction, snapshot, and archive lifecycle
- observability and runtime telemetry
- MCP control interface
- enterprise dashboard
- visual pipeline builder
- developer experience / API / SDK / CLI direction
- testing and reliability strategy
- security and encryption hardening
- memory, context, and reasoning lifecycle
- multi-tenant readiness
- managed hosting by runtime instance and worker capacity
- banking and financial-services technical-control direction

The main product roadmap entry point is [`docs/product-roadmap/index.md`](docs/product-roadmap/index.md).

---

## Documentation

The full documentation map is available here:

- [`docs/index.md`](docs/index.md) — Documentation index and reading guide.
- [`docs/runtime-internals.md`](docs/runtime-internals.md) — Complete technical reference preserved from the original README.
- [`docs/enterprise-readiness.md`](docs/enterprise-readiness.md) — Enterprise readiness matrix.
- [`docs/comparison-existing-tools.md`](docs/comparison-existing-tools.md) — Ecosystem positioning and comparison with existing tools.
- [`docs/roadmap.md`](docs/roadmap.md) — Project roadmap.
- [`docs/product-roadmap/index.md`](docs/product-roadmap/index.md) — Product roadmap index and product-platform reading guide.
- [`docs/product-roadmap/product-roadmap.md`](docs/product-roadmap/product-roadmap.md) — Public product roadmap from runtime foundation toward dashboard, pipeline builder, MCP interface, managed hosting, security, memory/context lifecycle, and enterprise readiness.
- [`docs/product-roadmap/what-already-exists.md`](docs/product-roadmap/what-already-exists.md) — Summary of the foundations already implemented or actively in place today.
- [`docs/product-roadmap/current-foundation.md`](docs/product-roadmap/current-foundation.md) — Current architectural foundation for deterministic execution, replay, policy, providers, lifecycle management, observability, and control-plane direction.
- [`docs/product-roadmap/improvement-backlog.md`](docs/product-roadmap/improvement-backlog.md) — Planned improvements required to productize the existing runtime foundation.
- [`docs/product-roadmap/roadmap-6-months.md`](docs/product-roadmap/roadmap-6-months.md) — Short-term productization direction for a single-developer project.
- [`docs/product-roadmap/roadmap-12-24-months.md`](docs/product-roadmap/roadmap-12-24-months.md) — Longer-term product maturity, enterprise readiness, and commercial scale direction.
- [`docs/ai/architecture-overview.md`](docs/ai/architecture-overview.md) — High-level runtime architecture and major runtime layers, including control-plane scale-out, fulfilled-run requeue, provider-based dispatch, and tenant-aware context propagation.
- [`docs/ai/multi-tenant-control-plane-isolation.md`](docs/ai/multi-tenant-control-plane-isolation.md) — RBAC execution-context propagation, durable `ExecutionContextSnapshot`, tenant-aware Shared/Dedicated/Hybrid runtime isolation, registry/capacity filtering, admission, shared queue dispatch, and tenant-scoped scale-out settings.
- [`docs/ai/multi-tenant-runtime-flow.md`](docs/ai/multi-tenant-runtime-flow.md) — End-to-end ASCII runtime flow from MCP/RBAC entry to durable snapshot, shared run store, tenant-aware admission, scale-out, shared queue dispatch, local runtime queue, DAG execution, worker loop, finalization, and observability.
- [`docs/ai/runtime-control-plane.md`](docs/ai/runtime-control-plane.md) — Runtime control-plane foundation for replay, execution control, runtime queue control, runtime instance visibility/control, discovery, capacity, run admission, Shared Runtime Controller V1, Redis shared queue coordination, queue pump/background service, Redis-backed scale-out lifecycle, fulfilled-run requeue, and tenant-aware control-plane operations.
- [`docs/ai/runtime-discovery-registry-capacity.md`](docs/ai/runtime-discovery-registry-capacity.md) — Runtime discovery, Redis registry, Redis capacity descriptors, ControlPlaneIdResolver, pump readiness, local scale-out capacity visibility, cleanup lifecycle, HTTP pooled runtime identity model, and tenant-aware visibility filtering.
- [`docs/ai/mcp-server-control-plane.md`](docs/ai/mcp-server-control-plane.md) — MCP server as a runtime control-plane adapter, including host modes, tool groups, RBAC integration, runtime role separation, shared queue dispatch, provider scale-out, and process/Kubernetes host lifecycle integration.
- [`docs/ai/runtime-instance-provider-model.md`](docs/ai/runtime-instance-provider-model.md) — Provider-based runtime instance administration, dispatch, status/control, and scale-out model for local, Redis command queue, HTTP, gRPC, and Kubernetes providers, including tenant-scoped provider dispatch and scale-out.
- [`docs/ai/http-runtime-provider.md`](docs/ai/http-runtime-provider.md) — HTTP runtime provider reference covering dispatch hardening, retry, timeout, circuit breaker behavior, structured failure reasons, HTTP scale-out provider capability, Runtime Host Manager process-host provisioning, tenant-aware Shared/Dedicated/Hybrid scale-out validation, and process-boundary readiness.
- [`docs/ai/grpc-runtime-provider.md`](docs/ai/grpc-runtime-provider.md) — gRPC runtime provider reference covering `ControlPlaneWithGrpcRuntimeInstances`, gRPC dispatch, HTTP/2 runtime transport, provider metadata, gRPC scale-out, Runtime Host Manager process-host provisioning, readiness boundary, and validated crash recovery.
- [`docs/ai/kubernetes-runtime-host-provider.md`](docs/ai/kubernetes-runtime-host-provider.md) — Kubernetes Runtime Host Provider reference covering Host Manager lifecycle, fake and SDK clients, Pod/Service construction, Service/NodePort/port-forward/Gateway exposure, layered readiness, registry/capacity publication, cleanup ownership, multi-tenant metadata, and Pod crash recovery boundaries.
- [`docs/ai/provider-agnostic-process-host-recovery.md`](docs/ai/provider-agnostic-process-host-recovery.md) — Provider-neutral process-host recovery reference explaining how HTTP and gRPC share the same real process crash recovery contract, recovery profiles, and proof model.
- [`docs/ai/mcp-production-runtime-scenario-framework.md`](docs/ai/mcp-production-runtime-scenario-framework.md) — MCP production runtime scenario framework covering Runtime Host Manager modes, HTTP process-host scale-out, real `RuntimeInstanceOnly` child processes, Dedicated/Shared/Hybrid tenant scenarios, retention, ledger, trace, and replay validation across process boundaries.
- [`docs/ai/runtime-process-crash-recovery.md`](docs/ai/runtime-process-crash-recovery.md) — Runtime process crash recovery reference covering in-flight DAG resume, local queued redispatch, durable identity semantics, health/recovery/provider boundaries, and validated process kill behavior.
- [`docs/ai/runtime-recovery-forensics.md`](docs/ai/runtime-recovery-forensics.md) — Runtime recovery forensics reference covering incident identity, recovery timelines, recovered work classification, replacement runtime evidence, and audit-safe recovery records.
- [`docs/ai/multi-tenant-runtime-crash-isolation.md`](docs/ai/multi-tenant-runtime-crash-isolation.md) — Multi-tenant crash isolation proof covering impacted tenants, safe tenant non-impact, tenant-scoped recovery queries, and cross-tenant ledger/forensics leakage prevention.
- [`docs/ai/control-plane-ledger-causal-chain.md`](docs/ai/control-plane-ledger-causal-chain.md) — Control-plane causal-chain ledger reference covering scale-out request persistence, watcher observation, provider selection, host creation, process start, capacity visibility, and recovery redispatch evidence.
- [`docs/ai/recovery-replay-ledger-trace-proof.md`](docs/ai/recovery-replay-ledger-trace-proof.md) — Recovery proof reference connecting completion, replay report, replay ledger, replay trace, execution ledger, runtime trace, and recovery forensics after process crash recovery.
- [`docs/ai/shared-controller-usage.md`](docs/ai/shared-controller-usage.md) — Shared runtime controller usage, Redis shared stores, direct-dispatch and queue-first modes, manual drain, background pump setup, tenant snapshot propagation, and scale-out request lifecycle.
- [`docs/ai/shared-queue-pump-and-worker-capacity.md`](docs/ai/shared-queue-pump-and-worker-capacity.md) — Shared queue pump, queue-first submit mode, direct-dispatch scale-out path, fulfilled-run requeue, manual drain, dispatch-time admission, pump identity separation, runtime worker capacity visibility, `MaxLocalWorkersPerExecution`, and context restoration during dispatch.
- [`docs/ai/distributed-execution.md`](docs/ai/distributed-execution.md) — Distributed workers, Redis coordination, claims, leases, deterministic convergence, and execution-context restoration across distributed/background runtime hops.
- [`docs/ai/execution-control-state.md`](docs/ai/execution-control-state.md) — ExecutionId-level pause, resume, cancel, waiting-for-input, control-state behavior, and snapshot-aware runtime execution control.
- [`docs/ai/testing-strategy.md`](docs/ai/testing-strategy.md) — Integration testing strategy and validation approach for distributed runtime guarantees, including Redis/local scale-out request, requeue, dispatch, execution evidence, RBAC snapshot propagation, and tenant isolation coverage.
- [`demo/enterprise-runtime/README.md`](demo/enterprise-runtime/README.md) — Local enterprise runtime demo using Docker Compose, Redis, MongoDB, external demo steps, controller execution, distributed workers, and scenario documentation.
- [`demo/enterprise-runtime/scenarios/06-distributed-throttling.md`](demo/enterprise-runtime/scenarios/06-distributed-throttling.md) — Executable distributed throttling scenario documentation.
- [`demo/enterprise-runtime/scenarios/08-deterministic-convergence.md`](demo/enterprise-runtime/scenarios/08-deterministic-convergence.md) — Deterministic convergence scenario documentation.

Focused AI runtime documentation:

- [`docs/ai/architecture-overview.md`](docs/ai/architecture-overview.md)
- [`docs/ai/multi-tenant-control-plane-isolation.md`](docs/ai/multi-tenant-control-plane-isolation.md)
- [`docs/ai/multi-tenant-runtime-flow.md`](docs/ai/multi-tenant-runtime-flow.md)
- [`docs/ai/distributed-execution.md`](docs/ai/distributed-execution.md)
- [`docs/ai/execution-control-state.md`](docs/ai/execution-control-state.md)
- [`docs/ai/runtime-queue-control.md`](docs/ai/runtime-queue-control.md)
- [`docs/ai/runtime-control-plane.md`](docs/ai/runtime-control-plane.md)
- [`docs/ai/runtime-discovery-registry-capacity.md`](docs/ai/runtime-discovery-registry-capacity.md)
- [`docs/ai/mcp-server-control-plane.md`](docs/ai/mcp-server-control-plane.md)
- [`docs/ai/runtime-instance-provider-model.md`](docs/ai/runtime-instance-provider-model.md)
- [`docs/ai/http-runtime-provider.md`](docs/ai/http-runtime-provider.md)
- [`docs/ai/grpc-runtime-provider.md`](docs/ai/grpc-runtime-provider.md)
- [`docs/ai/kubernetes-runtime-host-provider.md`](docs/ai/kubernetes-runtime-host-provider.md)
- [`docs/ai/provider-agnostic-process-host-recovery.md`](docs/ai/provider-agnostic-process-host-recovery.md)
- [`docs/ai/mcp-production-runtime-scenario-framework.md`](docs/ai/mcp-production-runtime-scenario-framework.md)
- [`docs/ai/runtime-process-crash-recovery.md`](docs/ai/runtime-process-crash-recovery.md)
- [`docs/ai/runtime-recovery-forensics.md`](docs/ai/runtime-recovery-forensics.md)
- [`docs/ai/multi-tenant-runtime-crash-isolation.md`](docs/ai/multi-tenant-runtime-crash-isolation.md)
- [`docs/ai/control-plane-ledger-causal-chain.md`](docs/ai/control-plane-ledger-causal-chain.md)
- [`docs/ai/recovery-replay-ledger-trace-proof.md`](docs/ai/recovery-replay-ledger-trace-proof.md)
- [`docs/ai/shared-controller-usage.md`](docs/ai/shared-controller-usage.md)
- [`docs/ai/shared-queue-pump-and-worker-capacity.md`](docs/ai/shared-queue-pump-and-worker-capacity.md)
- [`docs/ai/retry-and-recovery.md`](docs/ai/retry-and-recovery.md)
- [`docs/ai/retention-and-compaction.md`](docs/ai/retention-and-compaction.md)
- [`docs/ai/distributed-concurrency-throttling.md`](docs/ai/distributed-concurrency-throttling.md)
- [`docs/ai/replay-and-audit.md`](docs/ai/replay-and-audit.md)
- [`docs/ai/observability.md`](docs/ai/observability.md)
- [`docs/ai/observability-tracing.md`](docs/ai/observability-tracing.md)
- [`docs/ai/runtime-metrics.md`](docs/ai/runtime-metrics.md)
- [`docs/ai/execution-correlated-ledger.md`](docs/ai/execution-correlated-ledger.md)
- [`docs/ai/testing-strategy.md`](docs/ai/testing-strategy.md)
- [`docs/ai/config-driven-runtime.md`](docs/ai/config-driven-runtime.md)
- [`docs/ai/policy-driven-execution.md`](docs/ai/policy-driven-execution.md)
- [`docs/ai/context-resolution-and-helpers.md`](docs/ai/context-resolution-and-helpers.md)
- [`docs/ai/step-plugins.md`](docs/ai/step-plugins.md)
- [`docs/ai/rag-pipelines.md`](docs/ai/rag-pipelines.md)

These files were extracted progressively from `docs/runtime-internals.md`.

---

## Validation Evidence

The current implementation is validated by a large unit and integration test suite.

Current validated evidence includes:

```text
large green test suite
enterprise runtime demo passing
HTTP/gRPC process-host recovery validation passing
targeted Kubernetes SDK scale-out, readiness, and Pod crash recovery scenarios passing
```

Validated areas include:

- deterministic DAG execution
- Redis Lua atomic claiming
- retry and recovery
- retention and replay compatibility
- execution control state
- runtime queue control
- shared runtime controller
- Redis shared run store
- Redis shared queue
- dispatch-time admission
- manual drain
- background shared queue pump
- runtime readiness gate
- Redis registry and capacity stores
- Redis control-plane discovery
- Redis admission reservations
- HTTP pooled runtime provider dispatch
- HTTP runtime provider dispatch hardening
- HTTP provider timeout, retry, and circuit breaker behavior
- HTTP provider structured dispatch failure persistence
- HTTP runtime scale-out provider and provisioner
- HTTP Redis-backed scale-out request fulfillment
- Runtime Host Manager process-host provisioning
- real `RuntimeInstanceOnly` process launch from HTTP and gRPC scale-out
- tenant-aware HTTP Shared/Dedicated/Hybrid scale-out scenarios
- Dedicated tenant no shared HTTP fallback
- Hybrid tenant shared HTTP fallback
- Redis-backed scale-out request store
- store-backed scale-out request publisher
- scale-out watcher
- provider-based scale-out selector
- local runtime scaler
- fulfilled scale-out run requeue
- MCP Redis local scale-out execution
- RBAC execution context integration
- `ExecutionContextSnapshot` propagation
- tenant-aware Shared/Dedicated/Hybrid visibility
- tenant-aware registry/capacity/admission filtering
- tenant-scoped scale-out settings
- local scaler runtime prefix isolation
- direct local runtime queue snapshot requirement
- enterprise runtime demo snapshot propagation
- end-to-end multi-tenant runtime flow documented and linked from README/index
- MCP production runtime scenario framework documented and linked from README/index
- durable replay / ledger / trace validation across process boundaries
- runtime process crash recovery after real HTTP and gRPC process kills
- provider-agnostic process-host recovery scenarios
- in-flight execution recovery preserving `ExecutionId`
- local queued shared-run redispatch using `SharedRunId`
- safe tenant non-impact during multi-tenant crash recovery
- tenant-scoped recovery forensics and no cross-tenant leakage
- control-plane causal-chain proof for scale-out/replacement/recovery

Example scale-out evidence:

```text
Initial ActiveLocalInstances = 0
Admission = RequestScaleOut
SharedRun.Status = ScaleOutRequested
ScaleOutRequest.Status = Fulfilled
ScaleOutRuntimeInstanceId = host-...:mcp-scaleout-runtime-1
ActiveLocalInstances = 1
SharedRun.Status = Dispatched
QueueStatus = Dispatched
LocalRunId = available
ExecutionId = available
RuntimeRunStatus = completed
```

Example HTTP pooled dispatch evidence:

```text
Runs = 50
StepsPerRun = 100
RuntimeInstances = runtime-http-1, runtime-http-2, runtime-http-3
RedisAiSharedRunStore = validated
RedisAiSharedQueue = validated
RedisAiRuntimeAdmissionReservationStore = validated
```

Example HTTP and gRPC provider hardening / scale-out evidence:

```text
HTTP dispatch timeout = validated
HTTP retry success = validated
HTTP retry exhausted = validated
HTTP circuit open = validated
HTTP non-retryable failure = validated
HTTP shared scale-out = validated
HTTP dedicated scale-out = validated
HTTP hybrid scale-out = validated
Dedicated tenant no shared HTTP fallback = validated
Hybrid tenant shared HTTP fallback = validated
Redis-backed scale-out request fulfillment through providerHint=http = validated
Redis-backed gRPC scale-out request fulfillment through providerHint=grpc = validated
gRPC dispatch over HTTP/2 to real RuntimeInstanceOnly process = validated
```

Example HTTP/gRPC process-host evidence:

```text
Dedicated + Shared + Hybrid tenants
3 tenants
4 runs per tenant
35 DAG steps per run
12 runs
420 DAG steps
real RuntimeInstanceOnly HTTP/gRPC processes
retention enabled
ledger enabled
trace enabled
replay enabled
replay report enabled
replay ledger enabled
replay trace enabled
Mongo / Redis durable observability
```

Example tenant isolation evidence:

```text
tenant-a
    Dedicated
    no shared fallback
    scale-out creates tenant-a-runtime-*

tenant-b
    Hybrid
    dedicated preferred
    shared fallback allowed
    scale-out creates tenant-b-runtime-*

default/test-tenant
    Shared
    uses shared runtime-instance-* capacity
```

---

Example Kubernetes runtime-host evidence:

```text
HostCreationMode = Kubernetes
Runtime host = RuntimeInstanceOnly Pod
Pod + Service lifecycle = Kubernetes SDK
Command transport = existing HTTP or gRPC provider
Resource readiness = validated
Runtime registration/capacity readiness = validated
Transport endpoint readiness = validated where required
Scale-out request = fulfilled after convergence
Pod UID disappearance after kill = validated
In-flight recovery preserves ExecutionId
Local queued recovery preserves SharedRunId
Safe-tenant recovery contamination = false in targeted recovery scenarios
```

The Kubernetes proof is intentionally scoped: targeted readiness, scale-out, transport, and crash-recovery scenarios are implemented, while higher-parallelism stability in resource-constrained local clusters remains under hardening.

---

Example provider-agnostic runtime process crash recovery evidence:

```text
3 tenants
9 runs total
2 tenant runtime processes killed
1 safe tenant runtime not killed
6 recovered work items
0 safe tenant recovered work items
9/9 runs completed
9/9 replay proofs available
ledger / trace / replay report / replay ledger / replay trace readable
cross-tenant ledger leak detected = false
safe tenant recovery leak detected = false
```

The HTTP/gRPC crash recovery scenarios prove that the runtime can recover assigned work after real runtime process death without treating the dead local queue as durable truth and without contaminating unrelated tenant evidence.

## License

This project is licensed under the **Business Source License 1.1 (BSL)**.

- Free for development, testing, and internal use
- Commercial production use requires a license
- Automatically converts to Apache 2.0 on 2029-01-01

See the repository license file for full terms.