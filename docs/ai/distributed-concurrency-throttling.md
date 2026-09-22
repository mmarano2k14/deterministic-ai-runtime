# Distributed Concurrency and Throttling

Status: Documentation split in progress.

This document describes the distributed concurrency and throttling model used by the Deterministic AI Runtime.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

Production AI workloads must protect shared infrastructure.

A runtime may need to limit:

- total workflow parallelism
- pipeline-level concurrency
- step-level concurrency
- execution-level concurrency
- runtime-instance concurrency
- provider concurrency
- model concurrency
- operation concurrency

Without distributed concurrency control, multiple workers or runtime instances can overload:

- LLM providers
- RAG backends
- vector databases
- relational databases
- external APIs
- internal services
- Redis and MongoDB infrastructure

The concurrency and throttling system exists to prevent overload while preserving deterministic distributed execution.

This document describes production concurrency policy and Redis-backed capacity enforcement.

The adversarial process-host validation campaign is documented separately:

- [Concurrency Hardening and Adversarial Validation](concurrency-hardening-and-adversarial-validation.md)

That document covers race-condition exposure, exact crash inventories, durable crash gates, process kills, recovery convergence, safe-tenant controls, P10–P35 validation, and the distinction between local saturation and protocol correctness.

---

## Core Idea

Concurrency is not only a local `Task.WhenAll` problem.

In a distributed runtime, concurrency must be coordinated across workers and runtime instances.

The runtime therefore separates:

```text
Local bounded parallelism
        +
Distributed admission control
        +
Policy-driven throttling
        +
Redis lease-based capacity enforcement
```

This allows a workflow to run in parallel while staying inside controlled limits.

---

## Configuration Location

Concurrency is configured through:

```text
config.concurrency
```

Configuration can exist at:

- pipeline level
- step level

Pipeline-level configuration provides shared defaults.

Step-level configuration can specialize or override behavior for a specific step.

---

## Local vs Distributed Concurrency

The runtime distinguishes between local and distributed concurrency.

### Local Concurrency

Local concurrency controls how much work one runtime instance can execute in parallel.

This is controlled by:

```text
maxDegreeOfParallelism
```

### Distributed Concurrency

Distributed concurrency controls how much work can execute across workers and runtime instances.

This is controlled through distributed limits such as:

- `maxGlobalConcurrency`
- `maxPipelineConcurrency`
- `maxStepConcurrency`
- `maxExecutionConcurrency`
- `maxInstanceConcurrency`
- `maxProviderConcurrency`
- `maxModelConcurrency`
- `maxOperationConcurrency`

Distributed limits are enforced through Redis-backed leases.

---

## Direct Concurrency Configuration

Direct values can be declared inside `config.concurrency`.

Example:

```json
{
  "config": {
    "concurrency": {
      "enabled": true,
      "maxDegreeOfParallelism": 8,

      "maxGlobalConcurrency": 100,
      "maxPipelineConcurrency": 20,
      "maxStepConcurrency": 5,
      "maxExecutionConcurrency": 10,
      "maxInstanceConcurrency": 8,

      "maxProviderConcurrency": 25,
      "maxModelConcurrency": 10,
      "maxOperationConcurrency": 15,

      "leaseSeconds": 300,
      "defaultRetryAfterMs": 250,
      "jitter": false
    }
  }
}
```

Direct `config.concurrency` values remain authoritative.

Generic throttle rules may fill missing limits, but they should not silently override explicit direct values.

---

## Policy-Driven Concurrency

Concurrency is part of the shared Policy Engine V2 model.

The runtime uses a unified policy model across:

- retry
- retention
- concurrency

Each engine resolves its own configuration section:

- `config.retry`
- `config.retention`
- `config.concurrency`

For concurrency, this means admission and throttling behavior can be driven by configured concurrency policies.

---

## Generic Throttle Policy

The generic throttle policy is:

```text
concurrency.throttle
```

It allows configuration to declare distributed throttling rules.

Example:

```json
{
  "config": {
    "concurrency": {
      "enabled": true,
      "policies": [
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
      ]
    }
  }
}
```

This means:

```text
scope  = provider
target = openai
limit  = 10
```

At runtime, matching steps with `provider = openai` receive an effective provider concurrency limit of `10`.

Redis then enforces that limit through the provider scope:

```text
ai:concurrency:scope:provider:openai
```

The `target` field is optional.

If omitted, the rule applies to all values for the selected scope.

---

## Multiple Throttle Rules

A single pipeline can define multiple throttle rules.

Example:

```json
{
  "config": {
    "concurrency": {
      "enabled": true,
      "policies": [
        {
          "name": "concurrency.throttle",
          "config": {
            "scope": "provider",
            "target": "openai",
            "limit": 10
          }
        },
        {
          "name": "concurrency.throttle",
          "config": {
            "scope": "model",
            "target": "openai:gpt-4.1",
            "limit": 5
          }
        },
        {
          "name": "concurrency.throttle",
          "config": {
            "scope": "operation",
            "target": "llm.chat",
            "limit": 20
          }
        }
      ]
    }
  }
}
```

A matching step must satisfy all effective Redis admission scopes before it can be claimed.

---

## Pipeline-Level vs Step-Level Configuration

Concurrency configuration can be declared at pipeline level or step level.

Pipeline-level configuration applies broadly.

Step-level configuration can specialize a specific step.

Example:

```json
{
  "name": "content-generation",
  "version": "1",
  "executionMode": "Dag",
  "config": {
    "concurrency": {
      "enabled": true,
      "policies": [
        {
          "name": "concurrency.throttle",
          "config": {
            "scope": "provider",
            "target": "openai",
            "limit": 10
          }
        }
      ]
    }
  },
  "steps": [
    {
      "name": "summarize",
      "stepKey": "llm.summary",
      "dependsOn": [],
      "config": {
        "provider": "openai",
        "model": "gpt-4.1",
        "operation": "llm.chat"
      }
    },
    {
      "name": "retrieve-context",
      "stepKey": "rag.retrieve",
      "dependsOn": [],
      "config": {
        "provider": "redis-vector",
        "operation": "rag.retrieve"
      }
    }
  ]
}
```

In this example:

```text
summarize         -> provider=openai       -> throttle applies
retrieve-context  -> provider=redis-vector -> throttle does not apply
```

A step can also define its own concurrency configuration:

```json
{
  "name": "summarize",
  "stepKey": "llm.summary",
  "config": {
    "provider": "openai",
    "model": "gpt-4.1",
    "operation": "llm.chat",
    "concurrency": {
      "enabled": true,
      "policies": [
        {
          "name": "concurrency.throttle",
          "config": {
            "scope": "operation",
            "target": "llm.chat",
            "limit": 3
          }
        }
      ]
    }
  }
}
```

---

## Supported Generic Throttle Scopes

The generic `concurrency.throttle` policy supports these rule scopes:

```text
provider
model
operation
step
step-type
pipeline
```

Scope behavior:

| Scope | Matching Behavior |
|---|---|
| `provider` | Matches `AiConcurrencyContext.Provider`. |
| `model` | Matches the normalized `{provider}:{model}` pair. |
| `operation` | Matches `AiConcurrencyContext.Operation`. |
| `step` | Matches the concrete step name / step id. |
| `step-type` | Matches the logical step key. |
| `pipeline` | Matches the stable pipeline key. |

Examples:

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

```json
{
  "name": "concurrency.throttle",
  "config": {
    "scope": "model",
    "target": "openai:gpt-4.1",
    "limit": 5
  }
}
```

```json
{
  "name": "concurrency.throttle",
  "config": {
    "scope": "operation",
    "target": "llm.chat",
    "limit": 20
  }
}
```

```json
{
  "name": "concurrency.throttle",
  "config": {
    "scope": "step",
    "target": "summarize-product-description",
    "limit": 1
  }
}
```

```json
{
  "name": "concurrency.throttle",
  "config": {
    "scope": "step-type",
    "target": "llm.summary",
    "limit": 3
  }
}
```

```json
{
  "name": "concurrency.throttle",
  "config": {
    "scope": "pipeline",
    "target": "content-generation:1",
    "limit": 20
  }
}
```

---

## Policy-Driven Admission

Concurrency policies can deny admission before Redis lease acquisition.

Supported admission policies include:

```text
concurrency.provider.admission
concurrency.model.admission
concurrency.operation.admission
```

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

If an admission policy denies execution:

```text
policy deny
        ↓
no Redis lease acquisition
        ↓
no DAG claim
        ↓
no step execution
```

This is different from throttling.

Admission policies decide whether a step is allowed to proceed at all.

Throttle policies provide distributed capacity limits that Redis enforces.

---

## Effective Configuration Priority

Direct concurrency values remain authoritative.

Generic throttle rules fill missing limits after the runtime creates the `AiConcurrencyContext`.

The practical flow is:

```text
pipeline config + step config
        ↓
resolved concurrency definition
        ↓
runtime concurrency context
        ↓
matching generic throttle rules
        ↓
effective concurrency definition
        ↓
policy admission
        ↓
Redis distributed gate
```

This keeps static pipeline configuration out of `AiExecutionState` while still allowing pipeline-level throttle rules to apply to matching steps.

---

## Runtime Concurrency Context

The runtime creates an `AiConcurrencyContext` before distributed admission.

This context may include:

- pipeline key
- step key
- execution id
- runtime instance id
- provider
- model
- operation
- step name
- logical step type

Throttle rules and admission policies are evaluated against this context.

This avoids hardcoding provider/model/operation throttling directly into the DAG engine.

---

## Redis ZSET Lease Model

Distributed concurrency is implemented with Redis sorted-set leases.

Each concurrency scope is stored as a Redis ZSET.

Each active lease is represented as:

```text
member = lease id
score  = expiration timestamp in Unix milliseconds
```

Before acquiring capacity, Redis Lua removes expired leases:

```text
ZREMRANGEBYSCORE scopeKey -inf now
```

Then it counts active leases:

```text
ZCARD scopeKey
```

If capacity is available, the lease is added:

```text
ZADD scopeKey expiresAt leaseId
```

This avoids counter drift.

If a worker crashes after acquiring capacity but before releasing it, the lease eventually expires.

A later acquisition attempt removes expired leases and restores capacity automatically.

---

## Supported Redis Concurrency Scopes

The Redis concurrency gate supports these distributed enforcement scopes:

```text
global
pipeline
pipeline-step
execution
instance
provider
model
operation
```

The Redis keys follow this structure:

```text
ai:concurrency:scope:global
ai:concurrency:scope:pipeline:{pipelineKey}
ai:concurrency:scope:pipeline-step:{pipelineKey}:{stepKey}
ai:concurrency:scope:execution:{executionId}
ai:concurrency:scope:instance:{runtimeInstanceId}
ai:concurrency:scope:provider:{provider}
ai:concurrency:scope:model:{provider}:{model}
ai:concurrency:scope:operation:{operation}
```

The `pipeline-step` scope intentionally combines the pipeline key and step key.

This prevents unrelated pipelines from throttling each other when they use the same logical step name.

The `model` scope intentionally combines provider and model.

This avoids collisions between different providers that may expose similarly named models.

---

## Lease-Based Safety

Concurrency slots are lease-based.

Each acquired slot has a TTL.

If a worker crashes, the slot eventually expires.

This provides crash recovery for concurrency control.

The runtime does not rely only on in-memory counters.

It uses distributed lease state so multiple workers can coordinate safely.

---

## Claim Flow with Concurrency

Concurrency admission is enforced before DAG step ownership is claimed.

The claim flow is:

```text
GetReadyStepsAsync
        ↓
Resolve pipeline + step config.concurrency
        ↓
Create AiConcurrencyContext
        ↓
Apply matching concurrency.throttle rules
        ↓
Evaluate concurrency admission policies
        ↓
ConcurrencyGate.TryAcquireAsync
        ↓
TryClaimStepAsync
        ↓
Execute step
        ↓
Release concurrency slot
```

Important rules:

- if policy admission is denied, no Redis lease is acquired
- if Redis capacity is denied, no DAG claim is attempted
- if Redis capacity is acquired but DAG claim fails, the lease is released immediately
- if DAG claim succeeds, the lease remains owned until step execution finishes

This prevents capacity leaks and avoids claiming work that should not execute.

---

## Release on Failed Claim

There is an important race condition:

```text
Worker A acquires concurrency lease
Worker B also tries to claim same step
Worker B wins DAG claim
Worker A loses DAG claim
```

If Worker A acquired a concurrency slot before losing the claim race, it must release the slot immediately.

Otherwise the system leaks distributed capacity.

This rule is required for correctness.

---

## Diagnostic Denial Reasons

When concurrency admission is denied, the runtime returns diagnostic reasons that identify the blocking scope or policy.

Examples:

```text
Concurrency limit reached for scope 'ai:concurrency:scope:provider:openai'. Current='10', Limit='10'.
```

```text
Provider 'openai' is blocked by concurrency policy.
```

This makes throttling and policy decisions visible through logs, tracing, and tests.

---

## Admission vs Throttling

Admission and throttling are related but not the same.

| Concept | Meaning |
|---|---|
| Admission | Decides whether a step is allowed to proceed at all. |
| Throttling | Enforces distributed capacity limits for allowed work. |

Example:

```text
Provider is not allowed
        ↓
Admission denies
        ↓
No lease
        ↓
No claim

Provider is allowed but limit reached
        ↓
Redis gate denies
        ↓
No claim
        ↓
Retry later
```

This distinction keeps policy governance separate from capacity enforcement.

---

## Interaction with Execution Control

Execution control should be checked before concurrency capacity is acquired.

If execution is paused, cancelling, cancelled, or waiting for input, the runtime should not acquire a concurrency lease.

Safe ordering:

```text
Check execution control gate
        ↓
Resolve concurrency config
        ↓
Evaluate policy admission
        ↓
Acquire Redis lease
        ↓
Claim step
```

This prevents capacity from being consumed by work that is blocked by control state.

---

## Interaction with Retry

A retry-ready step must still pass concurrency admission.

If a step is `WaitingForRetry` and its retry window opens, it is not automatically executed.

It must still pass:

- execution control gate
- concurrency policies
- Redis distributed capacity
- atomic DAG claim

If concurrency is denied, retry state remains unchanged.

---

## Interaction with Distributed Execution

Distributed concurrency protects distributed execution.

Multiple workers can evaluate ready steps, but they must satisfy the same distributed gates.

This ensures that one worker does not overload a provider while another worker is also consuming capacity.

The gate is shared through Redis.

---

## Interaction with Provider Governance

Provider/model/operation throttling is important for AI workloads.

It allows the runtime to enforce limits such as:

```text
OpenAI provider max 10 concurrent calls
Specific model max 5 concurrent calls
LLM chat operation max 20 concurrent calls
RAG retrieval operation max 15 concurrent calls
```

This provides a foundation for future cost and provider governance.

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Provider limit reached | Redis gate denies capacity. |
| Model limit reached | Redis gate denies capacity for the model scope. |
| Operation limit reached | Redis gate denies capacity for the operation scope. |
| Policy denies provider | No Redis lease and no DAG claim. |
| Worker crashes after acquiring lease | Lease expires automatically. |
| Worker wins lease but loses DAG claim | Lease is released immediately. |
| Multiple workers compete for capacity | Redis Lua enforces limit atomically. |
| Direct concurrency value exists | Direct value remains authoritative. |
| Throttle target does not match step context | Rule does not apply. |
| Execution is paused | No concurrency capacity should be acquired. |

---

## Current Status

| Capability | Status |
|---|---|
| `config.concurrency` resolution | Implemented |
| Local `maxDegreeOfParallelism` | Implemented |
| Distributed concurrency limits | Implemented |
| Policy-driven concurrency admission | Implemented |
| Generic `concurrency.throttle` policy | Implemented |
| Provider throttling | Implemented |
| Model throttling | Implemented |
| Operation throttling | Implemented |
| Step / step-type / pipeline throttle rules | Implemented |
| Redis ZSET lease model | Implemented |
| Lease expiration crash safety | Implemented |
| Redis Lua capacity enforcement | Implemented |
| Release on failed DAG claim | Implemented |
| Diagnostic denial reasons | Implemented |
| Integration with DAG claim flow | Implemented |
| Cost and provider governance | Planned |
| Advanced adaptive throttling | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Concurrency definition resolver | Resolves pipeline and step `config.concurrency`. |
| Concurrency engine | Evaluates admission and effective concurrency rules. |
| Policy engine | Executes concurrency policies by policy kind. |
| Generic throttle policy | Applies provider/model/operation/step/pipeline throttle rules. |
| Redis concurrency gate | Enforces distributed capacity using Redis leases. |
| DAG claim service | Claims work only after concurrency admission succeeds. |
| Execution control gate | Prevents concurrency acquisition when execution is blocked. |
| Observability layer | Records allowed/denied admission and diagnostic reasons. |

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Distributed Execution](distributed-execution.md)
- [Execution Control State](execution-control-state.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Observability](observability.md)
- [Testing Strategy](testing-strategy.md)
- [Concurrency Hardening and Adversarial Validation](concurrency-hardening-and-adversarial-validation.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
