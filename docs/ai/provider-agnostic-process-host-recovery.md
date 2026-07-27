# Provider-Agnostic Process-Host Recovery

Status: Implemented / validated for historical HTTP and gRPC process-host recovery and for the opt-in process-host Runtime Pool recovery chain: exact child failure journaling, capacity suppression, A1-only assigned-work enumeration, deterministic claim arbitration, claimed transitions, targeted A4 replacement, and sibling isolation.

This document describes the provider-agnostic recovery model used by the Deterministic AI Runtime when a real runtime process disappears.

It explains how the same recovery contract is validated across multiple runtime transports without duplicating recovery logic inside each provider.

Related documents:

- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Testing Strategy](testing-strategy.md)

---

## Purpose

Process-host recovery must not be tied to one transport.

A runtime process can be contacted through HTTP, gRPC, a future Redis command queue, or Kubernetes service discovery. The transport can change, but the recovery contract must remain the same.

The provider-agnostic recovery model proves that:

```text
runtime process failure
    != HTTP-only failure
    != gRPC-only failure
    != provider-owned recovery
```

The real contract is:

```text
A runtime instance became unsafe.
The control plane must stop routing new work to it.
The execution recovery reconciler must recover only work assigned to that unsafe runtime.
The provider must only deliver commands or request capacity through its transport.
The recovery proof must remain identical across providers.
```

This matters because production runtime reliability is an architecture guarantee, not a transport-specific feature.

---

## Core Principle

The runtime provider is not the recovery owner.

```text
Runtime provider
    dispatches commands
    reports transport failures
    participates in provider scale-out
    delegates process creation to the Runtime Host Manager when configured

Runtime health reconciler
    detects unsafe runtime instances
    suppresses unsafe capacity from admission

Execution recovery reconciler
    enumerates assigned work
    resumes in-flight executions
    redispatches local-queued shared runs
    writes recovery evidence

Runtime Host Manager
    creates or attaches runtime hosts
```

This separation is the reason the same process-host recovery tests can be reused for HTTP and gRPC.

---

## Runtime Pool Recovery Model

The Runtime Pool adds an exact local recovery authority around the existing provider-neutral transition boundary.

```text
real A1 child exits
    -> FailureId for A1
    -> suppress A1 capacity
    -> remove A1 route
    -> create A4
    -> enumerate A1 work
    -> acquire one deterministic claim
    -> call existing ownership resolver
    -> call existing recovery transition service
```

The pool-specific layer does not duplicate in-flight resume or local-queued redispatch logic.

It supplies exact authority:

```text
FailureId
PoolId
HostId
RuntimeInstanceId
RouteId
InventoryFingerprint
ClaimId
LeaseId
```

The existing transition boundary remains responsible for durable execution and shared-run recovery semantics.

A2 and A3 are excluded by first-class runtime identity, not by convention or log filtering.

See:

- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)

---

## Provider-Neutral Recovery Contract

A provider-agnostic process-host recovery scenario must validate the same invariants regardless of transport.

```text
1. A real RuntimeInstanceOnly process is started.
2. The runtime registers itself with provider metadata.
3. The runtime publishes heartbeat and capacity.
4. The shared queue dispatches real work to that runtime through the selected provider.
5. An in-flight DAG execution reaches a configured step threshold.
6. The runtime process is killed.
7. The runtime becomes unsafe and is no longer eligible for new admission.
8. Assigned work is enumerated from durable state.
9. In-flight work resumes with the same ExecutionId.
10. Local-queued work is redispatched through the same SharedRunId.
11. Replacement tenant-visible capacity is selected or created.
12. The recovered work completes.
13. Replay, ledger, trace, and forensics evidence is validated.
14. Safe tenants remain untouched.
```

The transport may be HTTP or gRPC.

The recovery proof must not change.

---

## Shared Scenario Base

The process-host crash recovery tests are now structured around a provider-neutral base:

```text
ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
```

The base owns the recovery contract.

Provider-specific test classes only supply a runtime profile.

```text
HTTP test wrapper
    -> HttpProcessHostScenarioRuntimeProfile
    -> shared recovery base

GRC test wrapper
    -> GrpcProcessHostScenarioRuntimeProfile
    -> shared recovery base
```

This prevents the runtime from having one recovery behavior for HTTP and another for gRPC.

If the recovery base passes for both providers, the architecture has proven the recovery model is not transport-bound.

---

## Runtime Profile Contract

Each provider profile defines the transport-specific setup needed by the shared scenario base.

A process-host scenario runtime profile provides values such as:

```text
ProviderName
ProviderLabel
LogPrefix
RequestedBy
Source
BuildSettings(...)
```

The profile is responsible for configuring provider-specific runtime settings.

The base is responsible for executing the provider-neutral recovery scenario.

This boundary keeps provider configuration separate from recovery validation.

---

## HTTP Profile

The HTTP profile configures the control plane and runtime processes for HTTP transport.

Typical shape:

```text
AiMcpHost:Mode = ControlPlaneWithHttpRuntimeInstances
AiRuntimeInstanceRegistration:ProviderName = http
AiHttpRuntimeScaleOut:Enabled = true
AiHttpRuntimeScaleOut:Mode = HostManager
AiHttpRuntimeScaleOut:HostCreationMode = Process
```

The process-host path is:

```text
Scale-out watcher
    ↓
HTTP runtime provider
    ↓
AiHttpRuntimeScaleOutProvisioner
    ↓
Runtime Host Manager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly process
    ↓
HTTP runtime command endpoint
```

---

## gRPC Profile

The gRPC profile configures the control plane and runtime processes for gRPC transport.

Typical shape:

```text
AiMcpHost:Mode = ControlPlaneWithGrpcRuntimeInstances
AiRuntimeInstanceRegistration:ProviderName = grpc
AiRuntimeInstanceRegistration:ProviderMetadata:provider.name = grpc
AiRuntimeInstanceRegistration:ProviderMetadata:transport.name = grpc
AiGrpcRuntimeScaleOut:Enabled = true
AiGrpcRuntimeScaleOut:Mode = HostManager
AiGrpcRuntimeScaleOut:HostCreationMode = Process
AiGrpcRuntimeScaleOut:EndpointTemplate = http://127.0.0.1:{port}/{runtimeInstanceId}
Kestrel:EndpointDefaults:Protocols = Http2
```

The process-host path is:

```text
Scale-out watcher
    ↓
gRPC runtime provider
    ↓
gRPC runtime scale-out provisioner
    ↓
Runtime Host Manager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly process
    ↓
gRPC runtime command service over HTTP/2
```

The gRPC profile proves that the recovery base does not rely on HTTP command endpoints.

---

## End-to-End Provider-Agnostic Flow

The shared recovery flow is:

```text
Submit shared runs
    ↓
Tenant-aware admission
    ↓
No or insufficient tenant-visible capacity
    ↓
Redis scale-out request persisted
    ↓
Scale-out watcher observes request
    ↓
Provider selector resolves provider from provider hint / registration options
    ↓
Selected provider delegates host creation to Runtime Host Manager
    ↓
Real RuntimeInstanceOnly process starts
    ↓
Runtime self-registers with provider metadata
    ↓
Runtime publishes heartbeat and capacity
    ↓
Shared queue pump dispatches run through selected provider
    ↓
Runtime local queue starts DAG execution
    ↓
Runtime process is killed
    ↓
Health reconciliation suppresses unsafe capacity
    ↓
Execution recovery reconciles assigned work
    ↓
Replacement capacity is selected or created
    ↓
Recovered work completes
    ↓
Replay / ledger / trace / forensics proof is validated
```

This flow is identical for HTTP and gRPC except for the provider transport.

---

## Assigned Work Recovery

The provider-agnostic base validates the same two assigned-work categories.

### InFlightExecution

An in-flight execution already has a durable `ExecutionId`.

Recovery action:

```text
Resume the same ExecutionId on replacement runtime capacity.
```

Invariant:

```text
ExecutionIdBefore == ExecutionIdAfter
```

This proves the runtime resumed the existing durable DAG instead of creating a new execution.

### LocalQueued

A local-queued shared run was delivered to a runtime local queue, but no durable DAG execution exists yet.

Recovery action:

```text
Redispatch the same SharedRunId through durable shared-run state.
```

Invariant:

```text
SharedRunId is preserved.
A new LocalRunId may be created on the replacement runtime.
ExecutionId is created only when the replacement runtime starts DAG execution.
```

This proves the local runtime queue is treated as volatile.

---

## Tenant Isolation Contract

Provider-agnostic recovery must remain tenant-scoped.

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

The provider transport must not weaken this boundary.

Tenant context must flow through:

```text
SharedRunRecord
SharedQueueItem
scale-out request
provider selection
host start request
runtime registration
capacity descriptor
provider dispatch
runtime local queue
DAG execution
ledger / trace / replay / forensics queries
```

The scenario must prove:

```text
Tenant A recovery uses Tenant A visible capacity.
Tenant B recovery uses Tenant B visible capacity.
Safe tenant capacity remains visible and normal.
Safe tenant does not receive recovery work.
Safe tenant does not receive recovery forensics.
Impacted tenant queries do not see safe tenant recovery entries.
```

---

## Safe Tenant Non-Impact

The strongest provider-agnostic proof includes a safe tenant running in the same shared control plane.

Scenario shape:

```text
Tenant A runtime process killed
Tenant B runtime process killed
Tenant C runtime process not killed
```

Expected outcome:

```text
Tenant A recovered work = 3
Tenant B recovered work = 3
Tenant C recovered work = 0
Tenant C recovery forensics = 0
Tenant C completed runs = 3
Strict replay validation = 9/9
CrossTenantLedgerLeakDetected = false
SafeTenantRecoveryLeakDetected = false
SafeTenantNonImpactValidated = true
```

This proves recovery is not a global panic button.

It is assigned-work reconciliation for unsafe runtime instances only.

---

## Validated Test Wrappers

HTTP wrapper:

```text
HttpProcessHostRealRuntimeCrashRecoveryScenarioTests
```

Validated HTTP scenarios:

```text
Http_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

gRPC wrapper:

```text
GrpcProcessHostRealRuntimeCrashRecoveryScenarioTests
```

Validated gRPC scenarios:

```text
Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

The matching test shape is intentional.

It proves that HTTP and gRPC share the same recovery contract.

---

## Provider-Specific Settings, Provider-Neutral Proof

Providers are allowed to have different technical settings.

Examples:

```text
HTTP provider
    uses HTTP runtime command endpoint
    may use HTTP readiness checks
    reports http-* failure reasons

gRPC provider
    uses gRPC runtime command service
    requires HTTP/2 for local process-host transport
    reports grpc-* failure reasons
```

But the proof remains provider-neutral:

```text
same durable SharedRunId model
same durable ExecutionId model
same runtime registry and capacity model
same health reconciliation boundary
same execution recovery reconciler
same forensics model
same replay / ledger / trace proof
same safe-tenant non-impact contract
```

---

## Failure Reasons Are Signals, Not Recovery Commands

Provider failure reasons are transport signals.

Examples:

```text
http-dispatch-timeout
http-provider-unavailable
http-circuit-open
http-command-failed

grpc-dispatch-timeout
grpc-provider-unavailable
grpc-circuit-open
grpc-command-failed
```

These signals may contribute to endpoint health or unsafe runtime detection.

They do not directly recover assigned work.

The execution recovery reconciler owns recovery.

---

## Runtime Host Manager Boundary

The Runtime Host Manager is the lifecycle boundary shared by HTTP and gRPC.

```text
Provider
    ↓
Scale-out provisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly process
```

The provider should not directly own process creation policy.

The provider can request capacity.

The host manager creates or attaches capacity.

The runtime process self-registers and publishes capacity.

The shared queue pump dispatches only after capacity becomes visible.

---

## Current gRPC Readiness Note

The current gRPC process-host validation uses a temporary readiness bypass for provider-specific readiness.

Reason:

```text
The old readiness probe path was HTTP-command-endpoint oriented.
A gRPC-only runtime command service does not answer the same HTTP readiness URL.
```

Current scenario setting:

```text
AiGrpcRuntimeScaleOut:RequireReadiness = false
```

This does not bypass runtime registration and capacity visibility.

The runtime still must register and publish capacity before dispatch.

Future hardening should replace this with:

```text
gRPC health check
or
provider-neutral registry/capacity readiness waiter
```

---

## What This Proves

Provider-agnostic process-host recovery proves that:

```text
Real runtime process recovery is not HTTP-specific.
Real runtime process recovery works through gRPC as well.
Runtime recovery does not belong inside the provider.
Health reconciliation and execution recovery remain separate.
Runtime Host Manager can be reused across transports.
Assigned work recovery is durable-state driven.
Tenant isolation survives process death and replacement.
Replay / ledger / trace / forensics proof is transport-neutral.
```

This is a major architecture milestone because it shows the runtime provider model is real, not just an HTTP-specific path renamed as a provider abstraction.

---

## Runtime Pool Scope and Remaining Work

The process-host Runtime Pool proof establishes child-local failure isolation inside one live pool host.

It does not yet prove:

- complete pool-host loss;
- Kubernetes Pod-wide suppression;
- distributed claim ownership across control planes;
- durable failure/safety/claim stores;
- Redis Cluster failover;
- hierarchical runtime/Pod/node scale-out.

Those boundaries are documented in the Runtime Pool roadmap.

---

## What This Does Not Yet Prove

This document does not claim that every future provider is complete.

Still separate validation targets:

```text
Kubernetes pod crash recovery
Attach mode recovery
Redis command queue provider recovery
production multi-control-plane recovery leadership
provider-neutral readiness API hardening
provider capability negotiation
production autoscaling policy quality
```

The current validated claim is:

```text
HTTP and gRPC process-host runtime crash recovery are validated through the same provider-agnostic recovery contract and shared scenario base.
```

---

## Current Status

| Capability | Status |
|---|---|
| Provider-neutral process-host recovery base | Implemented / validated |
| HTTP recovery wrapper | Implemented / validated |
| gRPC recovery wrapper | Implemented / validated |
| Runtime profile abstraction | Implemented / validated |
| Real RuntimeInstanceOnly process launch | Implemented / validated |
| HTTP process-host dispatch | Implemented / validated |
| gRPC process-host dispatch | Implemented / validated |
| Runtime Host Manager shared lifecycle boundary | Implemented / validated |
| Runtime health vs execution recovery separation | Implemented / validated |
| Unsafe capacity suppression | Implemented / validated |
| In-flight DAG resume with same ExecutionId | Implemented / validated |
| Local-queued redispatch through SharedRunId | Implemented / validated |
| Tenant-scoped replacement capacity | Implemented / validated |
| Safe-tenant non-impact | Implemented / validated |
| Replay / ledger / trace proof | Implemented / validated |
| Runtime recovery forensics | Implemented / validated |
| Provider-neutral readiness hardening | Planned |
| Kubernetes provider recovery | Planned |
| Redis command queue provider recovery | Planned |

---

## Documentation Rule

Do not describe process-host crash recovery as HTTP-only.

The correct current statement is:

```text
Process-host runtime crash recovery is provider-agnostic and validated for HTTP and gRPC through the same shared recovery base.
```

Do not describe providers as recovery owners.

The correct responsibility split is:

```text
Provider = transport dispatch and scale-out capability
Health reconciler = unsafe capacity suppression
Execution recovery reconciler = assigned-work recovery
Runtime Host Manager = lifecycle creation / attachment
```
