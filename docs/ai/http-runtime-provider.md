# HTTP Runtime Provider

Status: Implemented foundation / validated for hardened HTTP dispatch, provider-based HTTP scale-out, Redis-backed scale-out request fulfillment, and tenant-aware shared, dedicated, and hybrid scale-out policies.

This document describes the **HTTP runtime provider** for the Deterministic AI Runtime control plane.

The HTTP provider is part of the runtime instance provider model. It allows the control plane to communicate with runtime instances over HTTP and to participate in provider-based scale-out through the same `IAiRuntimeScaleOutProvider` routing model used by local and future providers.

The general provider model is described in:

- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)

---

## Purpose

The HTTP runtime provider exists to support runtime instances that are reachable through HTTP endpoints.

It currently supports or prepares the following responsibilities:

- dispatching shared runs to HTTP runtime instances
- querying runtime status through HTTP transport
- sending runtime control operations through HTTP transport
- applying HTTP dispatch timeout protection
- applying retry and backoff behavior
- applying circuit breaker protection
- returning structured dispatch failure reasons
- persisting dispatch failure state through the shared run store
- participating in provider-based scale-out
- delegating HTTP scale-out to `IAiHttpRuntimeScaleOutProvisioner`
- publishing HTTP runtime registry and capacity metadata during scale-out foundation scenarios
- preserving tenant-aware runtime settings during HTTP scale-out
- validating shared, dedicated, and hybrid runtime isolation behavior

The HTTP provider must not execute DAG steps directly.

The target runtime instance remains responsible for:

```text
local runtime queue
worker execution
DAG execution
runtime-local status
runtime-local cancellation
```

---

## Provider Responsibilities

The HTTP provider has two related but separate responsibilities.

### 1. HTTP Dispatch Transport

The HTTP provider can dispatch a run to an already registered runtime instance whose capacity descriptor contains HTTP transport metadata.

```text
Shared queue dispatcher
    ↓
Admission assigns runtime instance
    ↓
Capacity descriptor contains provider.name=http
    ↓
HttpAiRuntimeInstanceProvider
    ↓
HTTP runtime endpoint
    ↓
Runtime instance local queue
    ↓
DAG execution
```

Dispatch is transport-specific, but execution remains runtime-local.

### 2. HTTP Scale-Out Capability

The HTTP provider also implements `IAiRuntimeScaleOutProvider`.

This means it can be selected by the scale-out provider selector when a persisted scale-out request has:

```text
providerHint = http
```

The provider delegates scale-out to:

```text
IAiHttpRuntimeScaleOutProvisioner
```

Current foundation flow:

```text
AiRuntimeScaleOutRequestWatcherHostedService
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
providerHint = http
    ↓
HttpAiRuntimeInstanceProvider
    ↓
IAiHttpRuntimeScaleOutProvisioner
    ↓
HTTP runtime registry/capacity metadata materialized
    ↓
ScaleOutRequest.Status = Fulfilled
```

---

## Provider vs Transport

Provider identity and transport identity must remain separate.

```text
providerHint=http
```

means the HTTP provider handles the scale-out request.

```text
transport.name=http
```

means the resulting runtime capacity is contacted through HTTP.

For the current HTTP provider foundation, these values are usually the same.

For Kubernetes, they may diverge:

```text
providerHint=kubernetes
transport.name=http
```

or:

```text
providerHint=kubernetes
transport.name=grpc
```

This separation allows Kubernetes, Docker, ECS, Nomad, or a remote MCP host manager to create runtime capacity while dispatch still uses HTTP or gRPC.

---

## HTTP Dispatch Hardening

HTTP dispatch is hardened with provider-level options.

Current options:

```text
AiHttpRuntimeInstanceProvider:DispatchTimeout
AiHttpRuntimeInstanceProvider:EnableRetry
AiHttpRuntimeInstanceProvider:MaxRetryAttempts
AiHttpRuntimeInstanceProvider:RetryBaseDelay
AiHttpRuntimeInstanceProvider:RetryMaxDelay
AiHttpRuntimeInstanceProvider:RetryTimeouts
AiHttpRuntimeInstanceProvider:EnableCircuitBreaker
AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold
AiHttpRuntimeInstanceProvider:CircuitBreakerBreakDuration
```

Default intent:

```text
DispatchTimeout = 30 seconds
EnableRetry = true
MaxRetryAttempts = 1
RetryBaseDelay = 200 milliseconds
RetryMaxDelay = 2 seconds
RetryTimeouts = false
EnableCircuitBreaker = true
CircuitBreakerFailureThreshold = 5
CircuitBreakerBreakDuration = 30 seconds
```

The provider should fail fast and return structured failure results when an HTTP runtime endpoint is unavailable, invalid, timing out, repeatedly failing, or circuit-open.

---

## Structured Failure Reasons

HTTP dispatch failures use stable failure reason constants.

Current failure reasons include:

```text
http-endpoint-missing
http-endpoint-invalid
http-provider-unavailable
http-dispatch-timeout
http-command-failed
http-command-non-retryable
http-command-invalid-response
http-circuit-open
http-command-cancelled
http-command-exception
```

These failure reasons should be persisted and observable through:

```text
SharedRunStore
logs
runtime control-plane events
decision ledger
trace timeline
future dashboards
```

The HTTP provider should report endpoint failure reasons, but it should not directly mark runtime instances unhealthy or restart instances.

Health management and runtime restart policy should remain provider-agnostic or be handled by a higher-level health manager.

---

## Retry, Timeout, and Circuit Breaker Behavior

The provider applies timeout protection around HTTP dispatch operations.

Retry behavior is intended for transient failures.

Non-retryable HTTP failures should not be retried.

Circuit breaker behavior prevents repeated dispatch attempts against a runtime endpoint that is already known to be failing.

Conceptual behavior:

```text
dispatch request
    ↓
endpoint metadata validated
    ↓
circuit state checked
    ↓
HTTP request executed with timeout
    ↓
retry applied when allowed
    ↓
failure reason mapped
    ↓
result returned to dispatcher
    ↓
shared run failure persisted when dispatch cannot proceed
```

Timeout, retry, and circuit breaker behavior must remain transport-level hardening.

They must not bypass admission, shared queue state, runtime local queue ownership, or DAG execution ownership.

---

## HTTP Scale-Out Foundation

The HTTP provider now participates in provider-based scale-out.

The provider implements:

```text
IAiRuntimeScaleOutProvider
```

and delegates to:

```text
IAiHttpRuntimeScaleOutProvisioner
```

Current foundation behavior:

```text
RequestScaleOutAsync(request)
    ↓
IAiHttpRuntimeScaleOutProvisioner.ProvisionAsync(request)
    ↓
runtime instance registration is created
    ↓
runtime capacity descriptor is published
    ↓
HTTP transport metadata is attached
    ↓
scale-out provider result is returned
```

This validates the control-plane scale-out loop for HTTP.

It proves that HTTP is no longer only a dispatch transport. It can also participate in scale-out provider routing.

---

## Current HTTP Scale-Out Provisioner

The current HTTP scale-out provisioner is a metadata-first foundation.

It publishes registry and capacity metadata directly.

It does not yet start a real remote HTTP runtime process.

This is intentional for the current phase.

It validates:

- provider selector resolution
- `providerHint=http`
- watcher-to-provider flow
- Redis scale-out request fulfillment
- tenant-aware runtime settings propagation
- registry/capacity publication
- tenant visibility filtering
- shared, dedicated, and hybrid runtime policies

Current foundation flow:

```text
MCP submit
    ↓
Admission sees no tenant-visible capacity
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Redis scale-out request created
    ↓
Scale-out watcher observes request
    ↓
Selector resolves HTTP provider
    ↓
HTTP provider delegates to provisioner
    ↓
Provisioner publishes HTTP registry/capacity
    ↓
Scale-out request marked Fulfilled
```

---

## Production Direction: Remote MCP Runtime Host Manager

The production direction is not for the HTTP provisioner to be only metadata-first.

For non-Kubernetes HTTP runtime scale-out, the control plane should call a remote MCP host manager.

Target flow:

```text
MCP Control Plane
    ↓
Admission requests scale-out
    ↓
Scale-out watcher
    ↓
HTTP provider
    ↓
MCP HTTP runtime scale-out provisioner
    ↓
Remote MCP Runtime Host Manager
    ↓
RuntimeInstanceOnly HTTP runtime starts
    ↓
Runtime self-registers in Redis
    ↓
Runtime publishes capacity heartbeat
    ↓
Readiness waiter observes registry/capacity Ready
    ↓
Scale-out request marked Fulfilled
    ↓
Shared run requeued
    ↓
Normal dispatch through HTTP provider
```

In this model, MCP is the remote host-management control interface.

A remote MCP runtime host manager may expose tools such as:

```text
runtime.host.createInstance
runtime.host.stopInstance
runtime.host.listInstances
runtime.host.getInstanceStatus
runtime.host.getCapacity
```

The runtime instance should self-register.

The provisioner should not mark a scale-out request fulfilled until the runtime is visible and ready.

---

## Readiness Requirement

The HTTP scale-out provider should eventually wait for readiness before reporting fulfillment.

Readiness should require:

```text
registry contains runtimeInstanceId
capacity store contains runtimeInstanceId
runtime status is Ready
CanAcceptRun = true
transport.name = http
transport.endpoint is present
tenant metadata matches request
isolation mode matches request
runtime instance prefix matches request
```

This readiness behavior should be provider-reusable.

A future abstraction can support HTTP, gRPC, Kubernetes, ECS, Docker, Nomad, and other providers.

Example target abstraction:

```csharp
public interface IAiRuntimeInstanceReadinessWaiter
{
    Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
        AiRuntimeInstanceReadinessRequest request,
        CancellationToken cancellationToken = default);
}
```

---

## Tenant-Aware HTTP Scale-Out

HTTP scale-out requests preserve tenant runtime settings.

The following fields must flow from admission into the Redis scale-out request, watcher provider request, HTTP provisioner, registry metadata, and capacity metadata:

```text
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
MaxRuntimeInstances
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
```

Validated runtime modes:

```text
default/test-tenant
    IsolationMode = Shared
    PreferDedicatedCapacity = false
    AllowSharedFallback = true
    RuntimeInstanceIdPrefix = runtime-instance
    MaxRuntimeInstances = 1
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 3

tenant-a
    IsolationMode = Dedicated
    PreferDedicatedCapacity = true
    AllowSharedFallback = false
    RuntimeInstanceIdPrefix = tenant-a-runtime
    MaxRuntimeInstances = 3
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 5
    LocalQueueCapacity = 500

tenant-b
    IsolationMode = Hybrid
    PreferDedicatedCapacity = true
    AllowSharedFallback = true
    RuntimeInstanceIdPrefix = tenant-b-runtime
    MaxRuntimeInstances = 2
    WorkerCountPerInstance = 5
    MaxConcurrentRunsPerInstance = 3
    LocalQueueCapacity = 250
```

Important behavior:

```text
Dedicated tenants must not silently use shared HTTP capacity.

Hybrid tenants may use shared HTTP capacity when shared fallback is enabled.
```

---

## Tenant Visibility and HTTP Capacity

HTTP capacity descriptors are filtered by the same tenant visibility rules as local capacity.

Shared HTTP runtime capacity is visible to:

```text
Shared tenants
Hybrid tenants when shared fallback is enabled
Dedicated tenants only if shared fallback is enabled
```

Dedicated HTTP runtime capacity is visible only to:

```text
matching TenantId
or matching TenantGroupId
```

Hybrid HTTP runtime capacity is visible only to:

```text
matching TenantId
or matching TenantGroupId
```

Hybrid fallback means:

```text
a Hybrid tenant may use Shared runtime capacity
```

It does not mean:

```text
every Hybrid tenant can see every Hybrid runtime capacity
```

This prevents cross-tenant capacity leakage.

---

## Metadata and Transport Keys

HTTP provisioned registry and capacity descriptors should include provider metadata, transport metadata, tenant metadata, and scale-out metadata.

Provider metadata:

```text
provider.name = http
provider = http
```

Transport metadata:

```text
transport.name = http
transport.endpoint = http://...
runtime.instance.id = ...
```

Tenant metadata:

```text
tenant.id = tenant-a
tenant.group.id = tenant-group-id-xxx
runtime.isolationMode = Dedicated
runtime.preferDedicatedCapacity = True
runtime.allowSharedFallback = False
runtime.maxRuntimeInstances = 3
runtime.instanceIdPrefix = tenant-a-runtime
runtime.workerCountPerInstance = 10
runtime.maxConcurrentRunsPerInstance = 5
runtime.localQueueCapacity = 500
```

Scale-out metadata:

```text
scaleout.provider = http
scaleout.requestId = scale-out-...
scaleout.sharedRunId = ...
controlPlaneId = ...
```

Metadata is useful for diagnostics and compatibility, but tenant isolation must remain based on execution context, tenant runtime settings, and tenant-visible registry/capacity filtering.

---

## Configuration

HTTP dispatch hardening options:

```text
AiHttpRuntimeInstanceProvider:DispatchTimeout
AiHttpRuntimeInstanceProvider:EnableRetry
AiHttpRuntimeInstanceProvider:MaxRetryAttempts
AiHttpRuntimeInstanceProvider:RetryBaseDelay
AiHttpRuntimeInstanceProvider:RetryMaxDelay
AiHttpRuntimeInstanceProvider:RetryTimeouts
AiHttpRuntimeInstanceProvider:EnableCircuitBreaker
AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold
AiHttpRuntimeInstanceProvider:CircuitBreakerBreakDuration
```

HTTP scale-out options:

```text
AiHttpRuntimeScaleOut:Enabled
AiHttpRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix
AiHttpRuntimeScaleOut:EndpointTemplate
```

Example test configuration:

```text
AiMcpHost:Mode = ControlPlaneWithHttpRuntimeInstances
AiRuntimeInstanceRegistration:ProviderName = http
AiRunAdmission:EnableScaleOutRequest = true
AiRunAdmission:EnableGlobalQueueFallback = false
AiRunAdmission:RejectWhenNoCapacity = false
AiRuntimeScaleOutRequestWatcher:Enabled = true
AiRuntimeScaleOutRequestWatcher:WatcherId = mcp-scaleout-watcher
AiHttpRuntimeScaleOut:Enabled = true
AiHttpRuntimeScaleOut:EndpointTemplate = http://runtime-host/{runtimeInstanceId}
```

---

## Validated Scenarios

The HTTP provider has been validated through unit and integration scenarios.

Validated dispatch hardening behavior:

```text
provider unavailable -> http-provider-unavailable
timeout -> http-dispatch-timeout
circuit open -> http-circuit-open
retry success -> dispatch succeeds
retry exhausted -> http-command-failed
non-retryable HTTP 4xx -> http-command-non-retryable
options binding -> configured provider options loaded
```

Validated scale-out behavior:

```text
Shared HTTP scale-out -> runtime-instance-* capacity
Dedicated HTTP scale-out -> tenant-a-runtime-* capacity
Hybrid HTTP scale-out -> tenant-b-runtime-* capacity
Dedicated tenant does not fallback to shared HTTP capacity
Hybrid tenant falls back to shared HTTP capacity when shared fallback is enabled
```

Validated control-plane flow:

```text
MCP submit
    ↓
RBAC tenant context
    ↓
admission tenant-aware
    ↓
no tenant-visible capacity
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Redis scale-out request
    ↓
providerHint = http
    ↓
scale-out watcher
    ↓
provider selector
    ↓
HttpAiRuntimeInstanceProvider
    ↓
IAiHttpRuntimeScaleOutProvisioner
    ↓
registry/capacity HTTP metadata
    ↓
tenant visibility
    ↓
ScaleOutRequest.Status = Fulfilled
```

---

## Current Limitations

The current HTTP scale-out foundation is not yet the final production runtime lifecycle.

Current limitations:

- current HTTP scale-out provisioner publishes registry/capacity metadata directly
- current HTTP scale-out provisioner does not yet start a real remote HTTP runtime process
- readiness waiter is not implemented yet
- Remote MCP Runtime Host Manager client is not implemented yet
- runtime self-registration is not yet part of HTTP scale-out fulfillment
- HTTP scale-out followed by real HTTP dispatch is not yet validated as one end-to-end lifecycle
- provider health management is still separate from endpoint dispatch failure reporting

These limitations are intentional boundaries for the current phase.

---

## Future Implementation Targets

Recommended next steps:

```text
1. Add runtime instance readiness waiter.
2. Add Remote MCP Runtime Host Manager contract.
3. Add MCP-backed HTTP runtime scale-out provisioner.
4. Keep metadata-only HTTP provisioner for tests/dev foundation.
5. Wait for runtime self-registration and capacity readiness before fulfillment.
6. Validate HTTP scale-out followed by real HTTP dispatch.
7. Reuse the same readiness model for gRPC and Kubernetes.
```

Future production flow:

```text
ScaleOutRequested
    ↓
providerHint = http
    ↓
MCP-backed HTTP provisioner
    ↓
Remote MCP host manager starts RuntimeInstanceOnly HTTP runtime
    ↓
runtime self-registers
    ↓
capacity heartbeat Ready
    ↓
readiness waiter succeeds
    ↓
scale-out request Fulfilled
    ↓
shared run requeued
    ↓
normal HTTP dispatch
    ↓
runtime local queue
    ↓
DAG execution
```

---

## Documentation Rule

Do not present the current HTTP scale-out provisioner as a production runtime launcher.

Current completed capability:

```text
HTTP provider can participate in provider-based scale-out and materialize tenant-aware HTTP registry/capacity metadata.
```

Future capability:

```text
HTTP provider can call a Remote MCP Runtime Host Manager, wait for runtime self-registration/readiness, and then mark scale-out fulfilled.
```

The runtime boundaries must remain unchanged:

```text
Admission decides.
Providers transport or scale.
Remote MCP host manager starts runtime instances.
Runtime instances self-register.
Registry/capacity stores expose readiness.
Local runtime queues own RunId.
DAG engine owns ExecutionId.
ExecutionContextSnapshot carries durable tenant context.
```
