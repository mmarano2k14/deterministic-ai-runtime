# Policy-Driven Execution

Status: Documentation split in progress.

This document describes the shared policy-driven execution model used by the Deterministic AI Runtime.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

The runtime is designed to avoid hardcoding every execution decision directly inside the DAG engine.

Instead, important runtime behavior is delegated to policy engines.

Policy-driven execution allows the runtime to control behavior such as:

- retry decisions
- retention decisions
- concurrency admission
- distributed throttling
- future routing
- future validation
- future timeout or circuit-breaker behavior
- future provider and cost governance

The key principle is:

```text
Policies decide.
The runtime applies state transitions safely.
```

Policies can influence decisions, but they should not directly corrupt or mutate distributed execution state.

---

## Why Policy-Driven Execution Exists

Production AI workflows need configurable behavior.

Different pipelines may require different rules for:

- whether an error should retry
- how many retries are allowed
- whether a payload should be compacted
- whether a completed step can be evicted
- whether a provider is allowed
- how many concurrent calls a model can receive
- whether an operation should be throttled
- which runtime behavior is permitted under a given context

If all of this is hardcoded, the execution engine becomes rigid and unmaintainable.

Policy-driven execution keeps the runtime core stable while allowing runtime behavior to evolve.

---

## Why Policy Engine V2 Exists

The original policy model used simple policy keys:

```json
{
  "policies": [
    "retry.transient.default"
  ]
}
```

This is easy to use, but limited.

It does not allow policies to carry their own metadata or configuration.

For advanced runtime behavior, policies need to support structured configuration.

Examples include:

- provider-aware throttling
- tenant-specific concurrency limits
- model-level admission rules
- adaptive retry behavior
- dynamic retention strategies
- routing and validation policies

Policy Engine V2 adds this capability without breaking the old format.

---

## Shared Policy Model

The runtime uses a shared policy model across multiple engines.

The main policy-driven areas are:

```text
Retry
Retention
Concurrency
```

Each area has its own engine, but they use the same policy infrastructure.

This creates one consistent model for runtime decisions.

---

## Policy Kinds

Policies are grouped by kind.

Examples include:

```text
Retry
Retention
Concurrency
Timeout
CircuitBreaker
RateLimit
Validation
Routing
```

The currently important implemented policy areas are:

- Retry
- Retention
- Concurrency

Other policy kinds represent future extension directions.

The policy kind prevents a retry engine from accidentally executing a retention policy or a concurrency policy.

---

## Policy Registry

The policy registry stores registered policies.

A policy is typically identified by:

- policy key / name
- policy kind
- implementation

The registry allows engines to resolve policies dynamically by key and kind.

Example policy names may include:

```text
retry.transient.default
retry.timeout.default
retention.hybrid.default
concurrency.throttle
concurrency.provider.admission
concurrency.model.admission
concurrency.operation.admission
```

The exact registered native policies depend on the runtime configuration and assemblies. Custom policy implementations are resolved contextually from the run-pinned publication; they are not inserted into a mutable global tenant-policy registry. Contextual policy adapters are excluded from native singleton discovery.

---

## Policy Engine

The policy engine is responsible for executing policies.

A policy engine receives:

- runtime context
- policy kind
- configured policy definitions
- policy-specific configuration

It returns structured policy results.

The engine does not directly own distributed state transitions.

It produces decisions that the runtime interprets.

---

## Policy Result

Policies should return structured results.

A policy result may describe:

- allow
- deny
- retry allowed
- retry denied
- retention action selected
- throttle rule applied
- diagnostic reason
- retry-after value
- policy metadata

Structured results make policy decisions observable and testable.

---

## Configured Policy Definition

Policies can be configured in pipeline JSON.

The runtime supports both:

- legacy string policies
- structured policy definitions

This allows backward compatibility while enabling richer policy configuration.

---

## Legacy String Policy Format

The legacy policy format is a string array.

Example:

```json
{
  "policies": [
    "retry.transient.default"
  ]
}
```

This format is simple and remains supported.

Internally, the runtime can normalize it into a structured policy definition.

---

## Structured Policy Format

The structured policy format allows metadata and policy-specific configuration.

Example:

```json
{
  "policies": [
    {
      "name": "retry.timeout.default",
      "kind": "Retry",
      "config": {
        "code": "timeout"
      }
    }
  ]
}
```

A structured policy contains:

| Field | Purpose |
|---|---|
| `name` | Registered policy key used for lookup. |
| `kind` | Optional policy kind metadata. |
| `config` | Optional policy-specific configuration payload. |

Current runtime policy resolution uses the policy `name` and the engine kind that is currently evaluating the policy.

Inside typed sections such as `config.retry`, `config.retention`, or `config.concurrency`, the `kind` field is optional because the section already defines the policy kind.

---

## Backward Compatibility

The runtime preserves compatibility with existing policy configuration.

Both formats are supported.

Legacy format:

```json
{
  "policies": [
    "retry.transient.default"
  ]
}
```

Structured format:

```json
{
  "policies": [
    {
      "name": "retry.transient.default",
      "kind": "Retry",
      "config": {
        "maxRetries": 3
      }
    }
  ]
}
```

For backward compatibility, legacy JSON using `type` may still be accepted during deserialization.

New JSON should prefer `kind` when a kind field is needed.

The runtime automatically converts legacy string policies into structured policy definitions internally.

This means existing JSON pipeline definitions remain valid.

---

## Policy Sections

Policy definitions appear inside typed configuration sections.

Main sections include:

```text
config.retry.policies
config.retention.policies
config.concurrency.policies
```

Each engine resolves its own section.

This avoids one large untyped policy list and keeps behavior clear.

---

## Used By

Policy Engine V2 is used by:

- Retry Engine
- Retention Engine
- Concurrency Engine

This creates one unified policy model across the runtime.

Each engine resolves its own configuration section:

- `config.retry`
- `config.retention`
- `config.concurrency`

Each section may use legacy string policies or structured policy objects.

---

## Retry Policies

Retry policies influence retry decisions.

They may decide whether a failure is retryable based on:

- exception type
- error code
- provider response
- timeout classification
- operation type
- step metadata
- configured policy data

Example:

```json
{
  "config": {
    "retry": {
      "policies": [
        "retry.transient.default",
        {
          "name": "retry.timeout.default",
          "kind": "Retry",
          "config": {
            "code": "timeout"
          }
        }
      ],
      "maxRetries": 2,
      "strategy": "Fixed",
      "baseDelayMs": 500,
      "jitter": false
    }
  }
}
```

The retry engine resolves `config.retry`, executes retry policies, aggregates results, and computes the retry decision.

The Redis DAG store then persists the retry transition safely.

---

## Retention Policies

Retention policies influence retention decisions.

They may decide:

- whether compaction should run
- whether eviction should run
- whether hybrid retention should be used
- which completed steps should remain hot
- which payloads should be externalized
- whether retention is safe for the current execution state

Retention follows a structured flow:

```text
Retention trigger
        ↓
Retention engine
        ↓
Resolve config.retention
        ↓
Execute retention policies
        ↓
Compute retention decision
        ↓
Create retention execution plan
        ↓
Apply compaction / eviction safely
```

Policies influence the decision.

The runtime applies the retention plan safely.

---

## Concurrency Policies

Concurrency policies influence admission and throttling behavior.

They may:

- allow or deny provider usage
- allow or deny model usage
- allow or deny operation usage
- apply distributed throttle rules
- define provider/model/operation limits
- provide retry-after diagnostics

Example:

```json
{
  "config": {
    "concurrency": {
      "enabled": true,
      "policies": [
        {
          "name": "concurrency.provider.admission",
          "config": {
            "allowedProviders": [ "openai", "anthropic" ],
            "requireProvider": true,
            "retryAfterMs": 500
          }
        }
      ]
    }
  }
}
```

If a policy denies admission:

```text
policy deny
        ↓
no Redis lease acquisition
        ↓
no DAG claim
        ↓
no step execution
```

---

## Hosted Custom Policy Families

Custom `Concurrency`, `Retry`, and `Delegation` policies can execute through the hosted Python, TypeScript, and .NET backends while retaining their existing runtime checkpoints. Their code and environment come from the run-pinned publication, the original pipeline/local declaration scope is preserved, and a published Child DAG reuses its immutable child publication binding and exact `DefinitionPath`.

The existing RBAC engine authorizes the selected implementation before execution. Hosted policy evaluation is short and deadline-bounded; it does not prepare the durable custom-step journal operation or create a policy-specific scheduler/continuation path.

| Family | Contract | Hosted decision | Authority that remains in the runtime |
|---|---|---|---|
| `Concurrency` | `concurrency/v1` | Admission allow/deny evidence. | Existing concurrency engine, native guards, lease/admission path. |
| `Retry` | `retry/v1` | `pass`, `retry` with optional bounded delay suggestion, or `stop` with reason. | Retry budget, backoff, jitter, final delay, `WaitingForRetry`, terminal failure. |
| `Delegation` | `delegation/v1` | `approve` or `deny` with reason. | Durable delegation relation CAS, child allocation/dispatch, parent lifecycle, continuation and recovery. |

Technical output/transport failures remain technical failures. They cannot become implicit `Allow`, `Retry`, or `Approve`. No universal boolean policy protocol, replacement policy engine, or custom-policy lifecycle authority is introduced.

`Retention` remains native-only in the current capability matrix. `Timeout`, `CircuitBreaker`, `RateLimit`, `Validation`, and `Routing` do not currently have independent hosted policy checkpoints. `retry.timeout.default` and `retry.rate-limit.default` remain native `Retry` policies rather than separate family engines.

See [Hosted Multilanguage Execution](hosted-multilanguage-execution.md#hosted-custom-policy-families) and [Hosted Multilanguage Validation](hosted-multilanguage-validation.md) for the implemented boundary and validation evidence.

---

## Generic Throttle Policy

The generic concurrency throttle policy is:

```text
concurrency.throttle
```

It allows distributed throttling rules to be configured as policies.

Example:

```json
{
  "name": "concurrency.throttle",
  "config": {
    "scope": "provider",
    "target": "openai",
    "limit": 10,
    "leaseSeconds": 300,
    "defaultRetryAfterMs": 250
  }
}
```

Supported generic throttle scopes include:

```text
provider
model
operation
step
step-type
pipeline
```

Throttle policies produce effective distributed capacity limits.

Redis enforces those limits through lease-based scopes.

---

## Current Concurrency Policy Use Cases

Structured policy configuration currently supports:

- provider admission control
- model admission control
- operation admission control
- generic distributed throttling through `concurrency.throttle`
- provider throttle scopes
- model throttle scopes
- operation throttle scopes
- step throttle scopes
- step-type throttle scopes
- pipeline throttle scopes
- optional target matching for throttle rules
- Redis-backed distributed enforcement after policy evaluation

This gives the runtime policy-driven admission and policy-configured distributed throttling without changing the pipeline model.

---

## Admission vs Throttling

Admission and throttling are not the same.

| Concept | Meaning |
|---|---|
| Admission | Decides whether a step is allowed to proceed at all. |
| Throttling | Enforces distributed capacity limits for allowed work. |

Example:

```text
Provider is blocked by policy
        ↓
admission denies
        ↓
no Redis lease
        ↓
no DAG claim

Provider is allowed but limit is reached
        ↓
Redis gate denies capacity
        ↓
no DAG claim
        ↓
retry later
```

Policy-driven execution keeps these concepts separate.

---

## Policy Execution Flow

A generic policy execution flow is:

```text
Runtime engine receives context
        ↓
Resolve configured policies
        ↓
Look up policies by name and kind
        ↓
Execute policies
        ↓
Collect policy results
        ↓
Aggregate decision
        ↓
Runtime applies decision through safe state transition
```

The policy engine does not replace the runtime state machine.

It provides decision support.

---

## Policy Context

A policy needs context.

Depending on policy kind, context may include:

- execution id
- pipeline key
- step name
- step key
- provider
- model
- operation
- retry count
- failure reason
- payload size
- completed step count
- runtime instance id
- configured policy payload

The policy context should be explicit and testable.

---

## Policy-Specific Config

Structured policies allow policy-specific configuration.

Examples:

Retry timeout policy:

```json
{
  "name": "retry.timeout.default",
  "kind": "Retry",
  "config": {
    "code": "timeout"
  }
}
```

Provider admission policy:

```json
{
  "name": "concurrency.provider.admission",
  "config": {
    "allowedProviders": [ "openai", "anthropic" ],
    "requireProvider": true,
    "retryAfterMs": 500
  }
}
```

Provider throttle policy:

```json
{
  "name": "concurrency.throttle",
  "config": {
    "scope": "provider",
    "target": "openai",
    "limit": 10
  }
}
```

This makes policy behavior configurable without changing engine code.

---

## Policy Aggregation

Some engines may execute more than one policy.

The engine must aggregate policy results deterministically.

For example:

- any denial may deny admission
- any retryable classification may allow retry depending on engine rules
- retention policies may combine trigger and action results
- throttle policies may build effective concurrency definitions

Aggregation rules should be explicit and tested.

---

## Policies Do Not Own State Mutation

Policies should not directly mutate Redis DAG state.

They should not directly:

- claim steps
- complete steps
- fail steps
- finalize executions
- delete hot state
- write terminal snapshots
- acquire Redis leases outside the concurrency gate

Instead:

```text
Policy returns decision.
Runtime applies decision safely.
```

This keeps distributed state transitions centralized and protected.

---

## Policy-Driven but Deterministic

Policy-driven execution must remain deterministic.

For the same runtime context and configuration, policies should return the same decision.

Avoid policies that depend on hidden mutable global state unless that state is explicitly part of the runtime contract.

This matters because retry, retention, concurrency, replay, and auditability depend on predictable decisions.

---

## Observability of Policy Decisions

Policy decisions should be observable.

Useful observability data includes:

- policy name
- policy kind
- decision result
- diagnostic reason
- runtime context
- configured policy payload
- retry-after value
- selected retention action
- admission deny reason
- throttle scope

This is important for debugging and future audit/decision ledger work.

---

## Policy-Driven Execution and Replay

Replay and audit depend on understanding why runtime decisions happened.

A future audit system should be able to inspect:

- which policies were configured
- which policies ran
- what context they evaluated
- what decisions they returned
- how those decisions affected execution

The current policy-driven model creates the foundation for that future durable decision ledger.

---

## Policy-Driven Execution and Cost Governance

Policy-driven concurrency and throttling are foundations for future provider and cost governance.

The runtime can use policies to control:

- provider admission
- model admission
- operation admission
- provider concurrency
- model concurrency
- operation concurrency
- future per-tenant limits
- future cost budgets

Full cost governance is planned future work.

The current foundation is provider/model/operation-aware policy and throttle behavior.

---

## Policy Engine V2 Summary

Policy Engine V2 provides:

- backward-compatible policy configuration
- structured policy metadata
- policy-specific configuration
- one unified policy model across retry, retention, and concurrency
- a foundation for policy-driven throttling, routing, retry, and retention behavior

It allows runtime behavior to evolve without breaking existing pipeline definitions.

---

## Safety Rules

Policy-driven execution should follow these rules:

1. Policies decide; runtime mutates state.
2. Policies are resolved by kind.
3. Typed config sections should execute matching policy kinds.
4. Legacy string policies remain supported.
5. Structured policy definitions should be preferred for advanced behavior.
6. Policy-specific config should be explicit.
7. Policy results should be structured.
8. Policy aggregation should be deterministic.
9. Policy decisions should be observable.
10. Policies should not bypass Redis Lua state transitions.
11. Legacy `type` may be accepted for compatibility, but new JSON should prefer `kind`.

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Unknown policy key | Runtime should fail safely or report configuration error depending on contract. |
| Wrong policy kind | Engine should not execute a policy for the wrong kind. |
| Policy denies admission | No Redis lease and no DAG claim. |
| Retry policy denies retry | Step fails when no retry is allowed. |
| Retention policy selects compaction | Runtime externalizes payload before compacting hot state. |
| Throttle policy creates provider limit | Redis gate enforces provider scope capacity. |
| Policy config is malformed | Runtime should fail safely with diagnostics. |
| Legacy string policy used | Runtime normalizes or resolves it compatibly. |
| Legacy `type` field used | Runtime accepts it for compatibility; new config should prefer `kind`. |

---

## Validated Behavior

The policy-driven execution model is validated through runtime usage and tests covering:

- shared policy model across retry, retention, and concurrency
- policy kind resolution
- policy registry lookup
- legacy string policy compatibility
- structured policy definitions
- policy-specific configuration payloads
- legacy `type` compatibility
- retry policy execution
- retention policy execution
- concurrency policy execution
- generic `concurrency.throttle`
- provider/model/operation admission policies
- policy-driven denial before Redis lease acquisition
- policy decisions remaining separate from Redis DAG state mutation

Durable decision ledger, advanced cost governance, routing policies, and validation policy expansion remain planned work.

---

## Current Status

| Capability | Status |
|---|---|
| Shared policy model | Implemented / validated |
| Policy kinds | Implemented / validated |
| Policy registry | Implemented / validated |
| Policy engine abstraction | Implemented / validated |
| Retry policy execution | Implemented / validated |
| Retention policy execution | Implemented / validated |
| Concurrency policy execution | Implemented / validated |
| Hosted custom `Concurrency` policy execution | Implemented / validated at the existing admission checkpoint |
| Hosted custom `Retry` policy execution | Implemented; targeted target-environment validation reported passing |
| Hosted custom `Delegation` policy execution | Implemented; targeted target-environment validation reported passing |
| Hosted custom `Retention` policy execution | Native-only by current capability matrix |
| Hosted `Timeout` / `CircuitBreaker` / `RateLimit` / `Validation` / `Routing` families | No independent runtime checkpoint; not advertised as hosted |
| Legacy string policy support | Implemented / validated |
| Legacy `type` compatibility | Implemented / compatibility support |
| Structured policy definitions | Implemented / validated |
| Policy-specific config payloads | Implemented / validated |
| Generic `concurrency.throttle` policy | Implemented / validated |
| Provider/model/operation admission policies | Implemented / validated |
| Policy observability foundations | Foundation available |
| Durable decision ledger | Planned |
| Advanced cost governance policies | Planned |
| Routing / validation policy expansion | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Policy registry | Stores and resolves policies by name and kind. |
| Policy engine | Executes policies and returns structured results. |
| Retry engine | Resolves `config.retry`, executes retry policies, and computes retry decisions. |
| Retention engine | Resolves `config.retention`, executes retention policies, and computes retention decisions. |
| Concurrency engine | Resolves `config.concurrency`, executes concurrency policies, and computes admission/throttle decisions. |
| Redis DAG store | Applies state transitions safely after policy decisions. |
| Redis concurrency gate | Enforces distributed capacity after concurrency policy evaluation. |
| Observability layer | Records policy decisions and diagnostics. |
| Future decision ledger | Will persist policy decision evidence for audit. |

---

## Summary

Policy-driven execution allows the runtime to stay extensible without weakening its execution guarantees.

The policy layer controls decisions.

The runtime layer controls state.

This is what allows retry, retention, concurrency, and throttling behavior to evolve without turning the DAG engine into a hardcoded decision engine.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability](observability.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
