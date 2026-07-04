# gRPC Runtime Provider

Status: Implemented / validated for gRPC runtime dispatch, gRPC scale-out provider selection, Runtime Host Manager process-host provisioning, real `RuntimeInstanceOnly` process launch, HTTP/2 gRPC command transport, tenant-aware runtime registration, and real process-host runtime crash recovery with strict DAG resume, replay, ledger, trace, and forensics proof.

This document describes the gRPC runtime provider used by the Deterministic AI Runtime control plane.

It focuses on the provider path where the control plane communicates with runtime instances through gRPC transport, while keeping runtime execution, tenant isolation, shared queue dispatch, scale-out, health reconciliation, and execution recovery responsibilities separated.

Related documents:

- [Architecture Overview](architecture-overview.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Testing Strategy](testing-strategy.md)

---

## Purpose

The gRPC runtime provider exists to allow the control plane to dispatch work, query status, and participate in runtime scale-out through a gRPC transport boundary.

It gives the runtime provider model a second production transport beside HTTP.

The purpose is not to change DAG execution.

The purpose is to prove that the runtime architecture is provider-neutral:

```text
Shared controller
    -> shared queue
    -> dispatch-time admission
    -> tenant-visible registry / capacity
    -> provider router
    -> gRPC runtime provider
    -> RuntimeInstanceOnly process
    -> local runtime queue
    -> DAG engine
```

The gRPC provider proves that the deterministic execution model does not depend on HTTP-specific behavior.

The DAG engine remains the same.

The shared queue remains the same.

The runtime process recovery model remains the same.

Only the transport and provider-specific scale-out path change.

---

## Core Contract

The gRPC provider must obey the same runtime provider contract as other providers.

It must:

- dispatch work to the selected runtime instance;
- preserve `ExecutionContextSnapshot` during dispatch;
- preserve tenant and runtime metadata;
- return structured provider outcomes;
- report transport failures as provider failure reasons;
- participate in provider-based scale-out when selected;
- delegate process lifecycle to the Runtime Host Manager;
- allow runtime instances to self-register and publish capacity;
- keep recovery ownership outside the provider;
- deliver work into the runtime local queue, not directly into the DAG engine.

It must not:

- execute DAG steps inside the control plane;
- bypass admission;
- bypass tenant visibility;
- bypass the shared queue lifecycle;
- own runtime crash recovery;
- own runtime health reconciliation;
- treat replacement capacity creation as recovery completion;
- treat transport failures as business execution failures.

The provider is transport infrastructure.

The runtime instance owns the local queue and workers.

The DAG engine owns durable execution.

---

## Provider Identity

The gRPC provider uses explicit provider and transport metadata.

Canonical metadata:

```text
provider.name = grpc
transport.name = grpc
```

A runtime instance reachable through gRPC should publish provider metadata that allows the provider router to contact it correctly.

Example capacity metadata:

```text
RuntimeInstanceId = grpc-runtime-1
ProviderName = grpc
ProviderEndpoint = http://127.0.0.1:50051/grpc-runtime-1
provider.name = grpc
transport.name = grpc
transport.endpoint = http://127.0.0.1:50051/grpc-runtime-1
```

Important distinction:

```text
provider.name
    = which runtime provider handles dispatch / scale-out

transport.name
    = which transport the runtime endpoint exposes

RuntimeInstanceId
    = dispatchable runtime capacity identity
```

The provider name and transport name are not tenant boundaries.

Tenant isolation must still come from strong tenant fields and `ExecutionContextSnapshot`.

---

## Host Mode

The control-plane host mode for gRPC runtime instances is:

```text
ControlPlaneWithGrpcRuntimeInstances
```

This mode means:

```text
MCP/control-plane host
    = accepts requests, stores shared runs, dispatches through providers, observes state

gRPC RuntimeInstanceOnly host
    = owns local queue, workers, heartbeat, capacity, DAG execution
```

The control plane does not execute DAG steps directly when operating as a remote-provider control plane.

It selects runtime capacity and calls the gRPC provider.

The gRPC runtime process receives the command and enqueues work into its local runtime queue.

---

## Runtime Host Requirements

A gRPC runtime host is a real `RuntimeInstanceOnly` process.

It must:

- start outside the parent MCP/control-plane process;
- resolve or receive the correct control-plane id;
- register as a runtime instance;
- publish heartbeat and capacity;
- expose gRPC runtime command endpoints;
- use HTTP/2 for gRPC transport;
- preserve tenant-aware registration metadata;
- execute DAG work locally through the runtime background controller.

For local process-host validation, the important Kestrel setting is:

```text
Kestrel:EndpointDefaults:Protocols = Http2
```

Without HTTP/2, the gRPC client path cannot reliably dispatch to the runtime command service.

---

## High-Level Dispatch Flow

The validated gRPC dispatch path is:

```text
Shared queue dispatcher
    ↓
Restore ExecutionContextSnapshot
    ↓
Dispatch-time admission
    ↓
Tenant-visible registry / capacity lookup
    ↓
Selected runtime capacity has provider.name = grpc
    ↓
Runtime provider router selects gRPC provider
    ↓
gRPC provider sends command to runtime endpoint
    ↓
RuntimeInstanceOnly gRPC process receives command
    ↓
Runtime local queue receives run request
    ↓
Background controller restores ExecutionContextSnapshot
    ↓
DAG execution starts or resumes
```

The selected runtime instance must already be visible through registry and capacity.

The provider does not invent dispatch targets.

Admission decides which runtime instance receives the run.

The provider decides how to contact that runtime instance.

---

## Scale-Out Flow

The gRPC provider also participates in the same scale-out capability model as local and HTTP providers.

Validated scale-out shape:

```text
Shared run submitted
    ↓
Admission finds no tenant-visible gRPC runtime capacity
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Redis scale-out request persisted
    ↓
AiRuntimeScaleOutRequestWatcherHostedService observes request
    ↓
AiRuntimeScaleOutProviderSelector resolves providerHint = grpc
    ↓
gRPC runtime provider handles scale-out request
    ↓
IAiGrpcRuntimeScaleOutProvisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
Real RuntimeInstanceOnly process starts
    ↓
Runtime registers with provider.name = grpc and transport.name = grpc
    ↓
Runtime publishes heartbeat and capacity
    ↓
Scale-out request is fulfilled
    ↓
Shared run is requeued
    ↓
Shared queue pump dispatches through gRPC provider
```

Important rule:

```text
Scale-out fulfillment is not dispatch.
```

The watcher creates or requests capacity.

The fulfilled run is requeued.

The pump still owns claim, context restore, admission, provider dispatch, and queue/run state transitions.

---

## Runtime Host Manager Boundary

The gRPC provider must not directly own process lifecycle.

The process-host path is:

```text
gRPC provider
    ↓
IAiGrpcRuntimeScaleOutProvisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly process
```

The provider owns transport and provider-specific scale-out behavior.

The Runtime Host Manager owns host creation or attachment mechanics.

The runtime process owns registration, heartbeat, capacity, local queue, workers, and DAG execution.

This keeps gRPC aligned with the HTTP process-host architecture.

---

## Configuration

Typical control-plane configuration for gRPC process-host scenarios:

```text
AiMcpHost:Mode = ControlPlaneWithGrpcRuntimeInstances
AiRuntimeInstanceRegistration:ProviderName = grpc
AiRuntimeInstanceRegistration:ProviderMetadata:provider.name = grpc
AiRuntimeInstanceRegistration:ProviderMetadata:transport.name = grpc
AiRuntimeInstanceRegistration:Metadata:provider.name = grpc
AiRuntimeInstanceRegistration:Metadata:transport.name = grpc
```

Scale-out configuration:

```text
AiHttpRuntimeScaleOut:Enabled = false
AiGrpcRuntimeScaleOut:Enabled = true
AiGrpcRuntimeScaleOut:Mode = HostManager
AiGrpcRuntimeScaleOut:HostCreationMode = Process
AiGrpcRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix = grpc-runtime
AiGrpcRuntimeScaleOut:EndpointTemplate = http://127.0.0.1:{port}/{runtimeInstanceId}
```

Dispatch hardening configuration used in deterministic process-host tests:

```text
AiGrpcRuntimeInstanceProvider:DispatchTimeout = 00:00:30
AiGrpcRuntimeInstanceProvider:EnableCircuitBreaker = false
AiGrpcRuntimeInstanceProvider:CircuitBreakerFailureThreshold = 100
```

Child process environment should preserve provider identity:

```text
AiRuntimeInstanceRegistration__ProviderName = grpc
AiRuntimeInstanceRegistration__ProviderMetadata__provider.name = grpc
AiRuntimeInstanceRegistration__ProviderMetadata__transport.name = grpc
AiRuntimeInstanceRegistration__Metadata__provider.name = grpc
AiRuntimeInstanceRegistration__Metadata__transport.name = grpc
AiRuntimeInstanceRegistration__Metadata__hostType = runtime-instance-only-grpc
AiRuntimeInstanceRegistration__Metadata__deployment = test-grpc-runtime-process
Kestrel__EndpointDefaults__Protocols = Http2
```

---

## Readiness

The current gRPC process-host scenario uses runtime registration and capacity visibility as the practical readiness signal.

A temporary scenario setting may disable the old HTTP command readiness probe:

```text
AiGrpcRuntimeScaleOut:RequireReadiness = false
```

Reason:

```text
The older readiness path probes HTTP command endpoints such as /runtime-instance/commands.
A gRPC-only runtime command surface does not expose that HTTP route.
```

This is not a runtime architecture limitation.

The correct future readiness model should be provider-aware:

```text
Option 1: gRPC health check
Option 2: provider-neutral registry / capacity readiness
Option 3: runtime command service readiness over gRPC
```

Until that is hardened, process-host gRPC validation relies on the runtime self-registering and publishing capacity before dispatch.

---

## Transport Failure Boundary

gRPC transport failures are provider outcomes.

They are not business step failures.

They are also not recovery commands.

Possible provider failure categories include:

```text
grpc-provider-unavailable
grpc-dispatch-timeout
grpc-circuit-open
grpc-command-failed
grpc-command-cancelled
grpc-command-exception
```

A transport failure may contribute to runtime health signals.

The boundary remains:

```text
gRPC provider
    reports transport / endpoint failure

RuntimeInstanceHealthReconciler
    decides whether runtime capacity is unsafe

ExecutionRecoveryReconciler
    recovers work already assigned to unsafe runtime capacity

Runtime Host Manager / lifecycle owner
    creates or attaches replacement runtime capacity when required
```

The gRPC provider must not become the runtime recovery owner.

---

## Runtime Crash Recovery

The gRPC provider has been validated against the same real process-host crash recovery model used by HTTP.

Validated recovery flow:

```text
Real RuntimeInstanceOnly gRPC process receives work
    ↓
DAG execution starts
    ↓
Runtime process is killed
    ↓
Heartbeat becomes stale / runtime becomes unsafe
    ↓
Unsafe capacity is suppressed from admission
    ↓
Execution recovery reconciler enumerates assigned work
    ↓
In-flight execution is requeued for resume
    ↓
Replacement gRPC runtime capacity is selected or created
    ↓
Recovered local run is registered
    ↓
Original ExecutionId is resumed
    ↓
DAG completes
    ↓
Replay / ledger / trace / forensics proof is validated
```

Critical invariant:

```text
ExecutionIdBefore == ExecutionIdAfter
```

For local-queued work, the invariant is different:

```text
SharedRunId is preserved
new LocalRunId may be created on replacement runtime
ExecutionId is created only when replacement runtime starts execution
```

This proves that gRPC recovery is not a restart disguised as recovery.

It is the same durable recovery model transported through gRPC.

---

## Multi-Tenant Crash Isolation

The gRPC provider is validated through the provider-agnostic multi-tenant process-host recovery scenario.

Scenario shape:

```text
One shared control plane
Tenant A runtime process killed
Tenant B runtime process killed
Tenant C runtime process not killed
Three runs per tenant
Fifty DAG steps per run
Replay / ledger / trace / forensics proof after convergence
```

Expected outcome:

```text
Tenant A recovered work = 3
Tenant B recovered work = 3
Tenant C recovered work = 0
Tenant C recovery forensics = 0
SafeTenantRecoveryLeakDetected = false
SafeTenantNonImpactValidated = true
CrossTenantLedgerLeakDetected = false
Strict replay validation = 9/9
```

This proves that gRPC provider integration does not weaken tenant-scoped recovery.

Recovery remains scoped to unsafe runtime instances and assigned work.

Safe tenant execution remains normal execution, not recovery execution.

---

## Provider-Agnostic Test Base

The HTTP and gRPC process-host crash recovery scenarios share the same provider-agnostic base.

The provider profile supplies:

- provider name;
- provider label;
- log prefix;
- requested-by/source metadata;
- process-host settings;
- transport-specific registration metadata.

This is important because it proves the recovery model is not duplicated per provider.

The common recovery contract is validated once, then exercised through both HTTP and gRPC transport profiles.

---

## Validated Test Names

Primary gRPC process-host crash recovery tests:

```text
Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

These validate:

```text
real RuntimeInstanceOnly gRPC process launch
providerHint = grpc scale-out
Runtime Host Manager process-host creation
runtime self-registration with provider.name = grpc
runtime capacity publication
HTTP/2 gRPC command transport
real process kill
unsafe runtime suppression
in-flight DAG resume with same ExecutionId
local-queued shared-run redispatch
multi-tenant recovery isolation
safe-tenant non-impact
replay / ledger / trace / forensics proof
```

---

## What gRPC Adds

gRPC adds a second remote transport to the runtime provider model.

That matters because it proves:

```text
The runtime is not an HTTP-only orchestration design.
```

The same control-plane model works with:

```text
local provider
HTTP provider
gRPC provider
future Kubernetes provider
future Redis command queue provider
```

The durable execution model does not change.

Only transport-specific dispatch and scale-out plumbing changes.

---

## What gRPC Does Not Change

gRPC does not change:

- DAG execution semantics;
- Redis Lua step ownership;
- retry rules;
- recovery rules;
- `ExecutionId` identity semantics;
- `SharedRunId` redispatch semantics;
- tenant visibility rules;
- shared queue ownership;
- scale-out fulfilled-run requeue;
- replay / ledger / trace requirements;
- runtime recovery forensics requirements.

This is the architecture win.

Transport can change without rewriting the engine.

---

## Current Status

| Capability | Status |
|---|---|
| `ControlPlaneWithGrpcRuntimeInstances` host mode | Implemented / validated |
| gRPC runtime provider dispatch | Implemented / validated |
| gRPC runtime scale-out provider path | Implemented / validated |
| gRPC process-host Runtime Host Manager path | Implemented / validated |
| Real `RuntimeInstanceOnly` gRPC process launch | Implemented / validated |
| Runtime registration with `provider.name = grpc` | Implemented / validated |
| Runtime capacity publication with gRPC metadata | Implemented / validated |
| Kestrel HTTP/2 gRPC command transport | Implemented / validated |
| gRPC single-tenant process crash recovery | Implemented / validated |
| gRPC multi-tenant process crash recovery | Implemented / validated |
| gRPC safe-tenant non-impact proof | Implemented / validated |
| gRPC replay / ledger / trace / forensics proof | Implemented / validated |
| Provider-agnostic HTTP/gRPC crash recovery base | Implemented / validated |
| gRPC provider-aware readiness probe | Planned / hardening |
| gRPC Kubernetes pod deployment | Planned |
| gRPC production multi-control-plane leadership | Planned |

---

## Current Limitations

Current known limitations:

```text
Provider-aware gRPC readiness is not fully hardened yet.
The current process-host scenario can rely on registration/capacity visibility instead of the old HTTP command readiness probe.
```

```text
Kubernetes gRPC pod creation is not validated yet.
The validated gRPC host creation mode is Process.
```

```text
The process-host scenario may disable provider circuit breaker behavior for deterministic crash recovery tests.
Provider circuit breaker behavior should remain covered by focused provider hardening tests.
```

These limitations do not invalidate the gRPC provider path.

They identify the next hardening work.

---

## Design Rules

### Do

```text
Use provider.name = grpc for gRPC provider selection.
Use transport.name = grpc for gRPC endpoint metadata.
Use HTTP/2 for gRPC RuntimeInstanceOnly process hosts.
Let the runtime process self-register and publish capacity.
Use registry/capacity visibility before dispatch.
Preserve ExecutionContextSnapshot across shared queue and gRPC dispatch.
Delegate process lifecycle to Runtime Host Manager.
Keep health reconciliation separate from execution recovery.
Validate replay, ledger, trace, and forensics after recovery.
Validate safe tenant non-impact in multi-tenant crash scenarios.
```

### Do Not

```text
Do not let the gRPC provider execute DAG steps directly.
Do not bypass shared queue dispatch-time admission.
Do not treat providerHint = grpc as a tenant boundary.
Do not treat transport failure as business retry failure.
Do not let gRPC provider own runtime restart or recovery.
Do not mark recovery complete only because a replacement runtime was created.
Do not rely on HTTP command readiness probes for gRPC-only runtime hosts.
Do not document gRPC provider as future-only capability.
```

---

## Documentation Rule

Do not describe the gRPC runtime provider as planned or future-only.

The validated capability is:

```text
gRPC runtime provider dispatch, gRPC provider-based scale-out, Runtime Host Manager process-host provisioning, real RuntimeInstanceOnly gRPC process launch, and real process-host crash recovery with replay / ledger / trace / forensics proof.
```

Still-planned gRPC work should remain explicit:

```text
gRPC provider-aware readiness hardening
gRPC Kubernetes pod deployment
gRPC production multi-control-plane leadership hardening
```
