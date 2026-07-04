# Config-Driven Runtime

Status: Implemented / validated for declarative DAG pipeline definitions, step configuration, retry / retention / concurrency policy configuration, provider/model/operation metadata, structured policy definitions, plugin-oriented execution, replay/observability context, and runtime-provider configuration separation across local, HTTP, and gRPC process-host scenarios.

This document describes how the Deterministic AI Runtime uses declarative configuration to define pipeline structure, step behavior, retry, retention, concurrency, providers, models, operations, and runtime policies.

It also clarifies the boundary between **step provider metadata** such as `provider`, `providerKey`, `model`, and `operation`, and **runtime instance provider configuration** such as `provider.name = http` or `provider.name = grpc`. The former belongs to pipeline execution semantics. The latter belongs to control-plane dispatch, runtime hosting, scale-out, and process-host transport.

The complete technical reference is currently preserved in:

- [runtime-instance-provider-model.md](runtime-instance-provider-model.md)
- [http-runtime-provider.md](http-runtime-provider.md)
- [grpc-runtime-provider.md](grpc-runtime-provider.md)
- [shared-controller-usage.md](shared-controller-usage.md)
- [mcp-production-runtime-scenario-framework.md](mcp-production-runtime-scenario-framework.md)

---

## Purpose

The runtime is designed to execute AI workflows from explicit configuration rather than hidden orchestration code.

A pipeline definition describes:

- what steps exist
- how steps depend on each other
- which executor handles each step
- where input values come from
- which retry behavior applies
- which retention behavior applies
- which concurrency and throttling rules apply
- which provider/model/operation metadata applies
- which policies should be evaluated

The runtime then controls execution through state, Redis coordination, policies, and deterministic convergence rules.

The principle is:

```text
Configuration declares intent.
The runtime controls execution.
```

---

## Why Configuration Matters

Production AI workflows evolve quickly.

A runtime may need to change:

- providers
- models
- operations
- retry rules
- retention thresholds
- concurrency limits
- throttling policies
- RAG providers
- step dependencies
- execution behavior
- policy definitions
- plugin-specific settings

If these behaviors are hardcoded inside the execution engine, every change requires engine modification.

The config-driven model keeps the runtime core stable while allowing workflow behavior to evolve through pipeline definitions and structured configuration.

---

## Core Idea

The runtime is both:

```text
config-driven
policy-driven
plugin-driven
state-driven
```

These concepts work together.

```text
Configuration
        ↓
declares workflow and runtime behavior

Policies
        ↓
decide runtime governance behavior

Plugins
        ↓
execute domain-specific step behavior

Runtime state machine
        ↓
controls execution, ownership, retry, retention, convergence, and finalization
```

This separation prevents the DAG engine from becoming a monolith.

---

## Pipeline Definition

A pipeline definition is the top-level declarative description of a workflow.

It usually contains:

- name
- version
- execution mode
- pipeline-level configuration
- steps

A pipeline can be represented directly or as part of a larger document containing a `pipelines` array, depending on the loader being used.

Example single pipeline:

```json
{
  "name": "content-generation",
  "version": "1",
  "executionMode": "Dag",
  "config": {
    "concurrency": {
      "enabled": true,
      "maxDegreeOfParallelism": 4
    }
  },
  "steps": [
    {
      "name": "retrieve-context",
      "stepKey": "rag.retrieval",
      "dependsOn": [],
      "config": {
        "provider": "redis-vector",
        "operation": "rag.retrieve"
      }
    },
    {
      "name": "summarize",
      "stepKey": "llm.summary",
      "dependsOn": [
        "retrieve-context"
      ],
      "config": {
        "provider": "openai",
        "model": "gpt-4.1",
        "operation": "llm.chat"
      }
    }
  ]
}
```

Example pipeline collection:

```json
{
  "pipelines": [
    {
      "name": "content-generation",
      "version": "1",
      "executionMode": "Dag",
      "steps": []
    }
  ]
}
```

The pipeline is declarative.

The runtime decides how to execute it safely.

---

## DAG Execution Mode

The runtime uses DAG execution as the primary workflow model.

DAG execution allows:

- explicit dependencies
- parallel execution where safe
- dependency-driven scheduling
- deterministic convergence
- distributed step claiming
- retry-aware execution
- retention compatibility
- replay foundations

The pipeline defines dependencies through `dependsOn`.

The runtime uses those dependencies to determine when each step becomes eligible.

### About `order`

Some pipeline examples may contain an `order` field.

In DAG execution, dependency semantics come from `dependsOn`, not from a hidden linear order.

The `order` field may still be useful as metadata for readability, display, or legacy pipeline definitions, but the runtime should rely on explicit dependencies for safe execution.

---

## Step Definition

Each step typically defines:

- `name`
- `stepKey`
- `dependsOn`
- `input`
- `config`

Example:

```json
{
  "name": "compose",
  "stepKey": "rag.compose",
  "dependsOn": [
    "merge"
  ],
  "input": {
    "source": "steps.merge.result.data"
  },
  "config": {
    "sourceStep": "merge",
    "composer": "deterministic"
  }
}
```

The step definition does not execute itself.

The runtime resolves the step and dispatches it to the registered executor for its `stepKey`.

---

## Step Name

The step `name` identifies a specific step instance inside the pipeline.

It is used for:

- dependency references
- input bindings
- step result lookup
- observability
- tracing
- replay validation
- diagnostics

Step names must be stable and unique inside a pipeline.

---

## Step Key

The `stepKey` identifies which registered executor should handle the step.

Examples:

```text
rag.retrieval
rag.merge
rag.compose
llm.summary
tool.execute
decision.score
```

The DAG engine does not hardcode step logic.

It uses the `stepKey` to resolve the appropriate step executor.

This is what enables plugin-style extensibility.

---

## Dependencies

Dependencies are defined using `dependsOn`.

Example:

```json
{
  "name": "merge",
  "stepKey": "rag.merge",
  "dependsOn": [
    "candidate",
    "job"
  ]
}
```

This means `merge` cannot run until both `candidate` and `job` are completed.

Dependencies are enforced by the runtime, not by application code.

---

## Input Bindings

Input bindings describe where step inputs come from.

Examples:

```json
{
  "input": {
    "candidateId": "state.candidateId",
    "jobId": "state.jobId",
    "context": "steps.merge.result.data"
  }
}
```

Bindings may reference:

- initial execution state
- previous step results
- nested result data
- runtime metadata

This allows data flow to be declared without hardcoding object access inside the DAG engine.

---

## Step Configuration

The `config` section controls step-specific behavior.

It may include:

- provider
- provider key
- model
- operation
- retry
- retention
- concurrency
- composer
- source step
- source steps
- provider-specific metadata
- tool-specific settings
- plugin-specific settings

Example:

```json
{
  "config": {
    "provider": "openai",
    "model": "gpt-4.1",
    "operation": "llm.chat",
    "retry": {
      "policies": [
        "retry.transient.default"
      ],
      "maxRetries": 3,
      "baseDelayMs": 500,
      "jitter": false
    }
  }
}
```

The runtime interprets the configuration through resolvers, engines, plugins, and policies.

---

## Pipeline-Level Configuration

Pipeline-level configuration applies broadly to the workflow.

Example:

```json
{
  "config": {
    "concurrency": {
      "enabled": true,
      "maxDegreeOfParallelism": 8,
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
  }
}
```

This can define shared behavior such as:

- global concurrency limits
- provider throttling rules
- retention defaults
- policy configuration
- runtime-level behavior

---

## Step-Level Configuration

Step-level configuration specializes a specific step.

Example:

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

Step-level configuration can refine behavior without changing the whole pipeline.

---

## Configuration Resolution

The runtime resolves configuration before applying runtime behavior.

A typical resolution flow is:

```text
Pipeline definition
        ↓
Pipeline-level config
        ↓
Step-level config
        ↓
Runtime context
        ↓
Effective configuration
        ↓
Policy evaluation
        ↓
Runtime decision
```

Configuration resolution must be deterministic.

The same pipeline and input should produce the same effective runtime behavior.

---

## Retry Configuration

Retry behavior is configured through:

```text
config.retry
```

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
      "maxDelayMs": 5000,
      "jitter": false
    }
  }
}
```

Retry configuration controls:

- retry policies
- max retries
- delay strategy
- base delay
- maximum delay
- jitter

The retry engine resolves this configuration when a step fails.

The retry decision is then persisted through runtime-controlled state transitions.

---

## Retention Configuration

Retention behavior is configured through:

```text
config.retention
```

Retention configuration may define:

- retention policies
- compaction behavior
- eviction behavior
- thresholds
- strategy selection
- hot state limits
- payload externalization behavior

Retention belongs to the shared policy-driven runtime model.

The retention engine resolves `config.retention`, evaluates retention policies, computes retention decisions, and produces a retention execution plan.

The retention coordinator then applies compaction or eviction safely.

---

## Concurrency Configuration

Concurrency behavior is configured through:

```text
config.concurrency
```

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

Concurrency configuration controls both:

- local bounded parallelism
- distributed Redis-backed admission limits

Direct values in `config.concurrency` remain authoritative.

Generic throttle rules may fill missing limits but should not silently override explicit direct values.

---

## Generic Throttle Configuration

Distributed throttling can be configured through the generic policy:

```text
concurrency.throttle
```

Example:

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

Supported throttle scopes include:

```text
provider
model
operation
step
step-type
pipeline
```

Throttle rules are matched against the runtime concurrency context.

The throttle policy can configure provider, model, operation, step, step-type, and pipeline throttling without changing the pipeline model.

---

## Provider / ProviderKey / Model / Operation Metadata

AI workloads often need provider-aware governance.

Step configuration can include:

```json
{
  "provider": "openai",
  "model": "gpt-4.1",
  "operation": "llm.chat"
}
```

RAG or provider-driven steps can also include:

```json
{
  "operation": "candidate.byId",
  "provider": "relational",
  "providerKey": "sqlserver",
  "executionMode": "provider"
}
```

This metadata is used for:

- provider dispatch
- provider throttling
- model throttling
- operation throttling
- observability
- future cost governance
- policy evaluation
- diagnostics

Provider/model/operation metadata should be explicit, stable, and normalized.


---

## Runtime Provider Configuration Is a Separate Boundary

The runtime uses the word `provider` in two different layers.

They must not be confused.

### Step provider metadata

Step provider metadata belongs to the pipeline and step execution layer.

Examples:

```text
config.provider = openai
config.providerKey = sqlserver
config.model = gpt-4.1
config.operation = llm.chat
```

This metadata helps the runtime and policies understand which domain provider, model, operation, or plugin-specific behavior a step needs.

It can be used by:

```text
step executors
RAG providers
LLM/tool providers
policy evaluation
concurrency throttling
retry diagnostics
observability
future cost governance
```

### Runtime instance provider metadata

Runtime instance provider metadata belongs to the control-plane dispatch and runtime hosting layer.

Examples:

```text
provider.name = local
provider.name = http
provider.name = grpc
transport.name = http
transport.name = grpc
transport.endpoint = http://localhost:5800
```

This metadata tells the shared controller and runtime provider router how to contact a selected runtime instance.

It can be used by:

```text
runtime instance provider router
dispatch provider
status provider
control provider
scale-out provider
runtime host manager
registry / capacity descriptors
MCP runtime visibility
```

The runtime instance provider layer is configured through host/control-plane settings, not through step definitions.

Examples:

```text
AiMcpHost:Mode = ControlPlaneWithHttpRuntimeInstances
AiMcpHost:Mode = ControlPlaneWithGrpcRuntimeInstances

AiRuntimeInstanceRegistration:ProviderName = http
AiRuntimeInstanceRegistration:ProviderName = grpc

AiHttpRuntimeScaleOut:Enabled = true
AiGrpcRuntimeScaleOut:Enabled = true
AiGrpcRuntimeScaleOut:HostCreationMode = Process
```

The rule is:

```text
Pipeline config.provider
    = domain/provider semantics inside a workflow step

Runtime provider.name
    = transport/runtime-host semantics used by the control plane
```

Do not use pipeline `config.provider` as the durable runtime transport selector.

Do not use runtime `provider.name` as a substitute for step-level provider/model/operation metadata.

Both are valid, but they belong to different layers.

---

## Process-Host Runtime Configuration

The production scenario framework now validates real process-host runtime execution through provider-specific host configuration.

The HTTP and gRPC process-host paths reuse the same control-plane model:

```text
MCP submit
    ↓
Shared runtime controller
    ↓
Tenant-aware admission
    ↓
Redis scale-out request
    ↓
Scale-out watcher
    ↓
Runtime provider selected by providerHint
    ↓
Runtime Host Manager
    ↓
RuntimeInstanceOnly process
    ↓
runtime self-registration
    ↓
registry / capacity visibility
    ↓
provider dispatch
    ↓
DAG execution
```

The process-host settings are runtime infrastructure settings.

They should remain outside pipeline definitions.

Representative HTTP infrastructure settings:

```text
AiMcpHost:Mode = ControlPlaneWithHttpRuntimeInstances
AiRuntimeInstanceRegistration:ProviderName = http
AiHttpRuntimeScaleOut:Enabled = true
AiHttpRuntimeScaleOut:HostCreationMode = Process
AiHttpRuntimeScaleOut:EndpointTemplate = http://127.0.0.1:{port}
```

Representative gRPC infrastructure settings:

```text
AiMcpHost:Mode = ControlPlaneWithGrpcRuntimeInstances
AiRuntimeInstanceRegistration:ProviderName = grpc
AiGrpcRuntimeScaleOut:Enabled = true
AiGrpcRuntimeScaleOut:HostCreationMode = Process
AiGrpcRuntimeScaleOut:EndpointTemplate = http://127.0.0.1:{port}
Kestrel:EndpointDefaults:Protocols = Http2
```

The gRPC process-host path requires HTTP/2 support because the runtime command service is exposed through gRPC.

The validated gRPC process-host settings ensure that the child `RuntimeInstanceOnly` process registers with:

```text
provider.name = grpc
transport.name = grpc
transport.endpoint = http://localhost:{port}
```

This allows admission and provider routing to remain transport-neutral while the actual dispatch transport is gRPC.


---

## Plugin-Oriented Configuration

Step configuration can drive plugin behavior.

For example:

```json
{
  "name": "candidate",
  "stepKey": "rag.retrieval",
  "config": {
    "operation": "candidate.byId",
    "provider": "relational",
    "providerKey": "sqlserver",
    "executionMode": "provider"
  }
}
```

This means:

```text
stepKey
= which runtime executor handles the step

provider/providerKey/operation
= which provider or operation the executor should dispatch to
```

The DAG engine remains generic.

Step plugins and provider plugins handle domain-specific behavior.

---

## Policy Configuration

Policies can be declared in configuration sections such as:

- `config.retry.policies`
- `config.retention.policies`
- `config.concurrency.policies`

The runtime supports both legacy string policies and structured policy definitions.

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
      "name": "retry.timeout.default",
      "kind": "Retry",
      "config": {
        "code": "timeout"
      }
    }
  ]
}
```

The `name` field is used for policy registry lookup.

The `kind` field is optional inside typed sections when the section already determines the policy kind.

The `config` field carries policy-specific configuration.

---

## Policy Engine V2 Compatibility

Policy Engine V2 supports structured policy definitions while remaining backward compatible with the original string-based policy format.

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
        "maxRetries": 5
      }
    }
  ]
}
```

For backward compatibility, legacy JSON using `type` is still accepted during deserialization.

New JSON should prefer `kind` when a kind field is needed.

Internally, the runtime can normalize legacy strings into structured configured policy definitions.

---

## Used By Policy Engine V2

The shared policy model is used by:

- Retry Engine
- Retention Engine
- Concurrency Engine

Each engine resolves its own configuration section:

- `config.retry`
- `config.retention`
- `config.concurrency`

Each section may use legacy string policies or structured policy objects.

This creates a unified policy model across the runtime.

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

## Backward Compatibility

The runtime keeps backward compatibility with older policy formats.

Legacy string-based policies remain supported.

Structured policy definitions add richer configuration without breaking existing pipeline JSON.

Legacy `type` fields may still be accepted for compatibility, but new JSON should prefer `kind`.

This allows the runtime to evolve while preserving existing workflow definitions.

---

## Config-Driven Does Not Mean Config-Only

The runtime is config-driven, but not config-only.

Configuration declares behavior.

Runtime components enforce correctness.

For example:

- `config.retry` declares retry rules
- retry engine computes decisions
- Redis DAG store persists retry transitions atomically

- `config.concurrency` declares limits
- concurrency engine evaluates admission
- Redis gate enforces distributed capacity

- `config.retention` declares retention behavior
- retention engine computes decisions
- retention coordinator applies compaction/eviction safely

The runtime must still control state transitions.

---

## Configuration and Determinism

Configuration must support deterministic execution.

This means:

- pipeline definitions should be stable
- step names should be stable
- dependencies should be explicit
- policies should be deterministic for the same context
- retry behavior should be predictable
- concurrency denial should not mutate step state
- retention should preserve resolvable data
- configuration should be versioned where needed

A workflow should not depend on hidden runtime magic.

---

## Configuration and Replay

Replay foundations require configuration context.

To explain or restore an execution, the runtime may need to know:

- pipeline name
- pipeline version
- step definitions
- step keys
- dependencies
- retry configuration
- retention configuration
- concurrency configuration
- policy definitions
- provider/model/operation metadata

Without configuration context, a replay can restore state but may not fully explain why execution behaved the way it did.

---

## Configuration and Observability

Configuration should appear in observability context where safe.

Useful diagnostic context includes:

- pipeline name
- pipeline version
- step name
- step key
- provider
- provider key
- model
- operation
- runtime provider name when relevant
- runtime transport name when relevant
- policy key
- policy kind
- concurrency scope
- retry strategy
- retention strategy

This allows operators to understand why the runtime made a decision.

---

## Configuration Validation

The runtime should validate pipeline configuration before execution where possible.

Validation may include:

- pipeline name is present
- version is present
- step names are unique
- dependencies reference existing steps
- no circular dependencies exist
- step keys are registered
- required config values exist
- policy names are registered
- policy kinds match expected sections
- concurrency values are valid
- retry values are valid
- retention values are valid
- provider metadata is present when required by policies
- provider keys are resolvable when provider dispatch is used
- operations are valid for the selected provider/plugin

Invalid configuration should fail early.

---

## Example: RAG Pipeline Configuration

```json
{
  "pipelines": [
    {
      "name": "rag-final-test",
      "version": "1",
      "executionMode": "Dag",
      "steps": [
        {
          "name": "candidate",
          "stepKey": "rag.retrieval",
          "order": 1,
          "input": {
            "candidateId": "state.candidateId"
          },
          "config": {
            "operation": "candidate.byId",
            "provider": "relational",
            "providerKey": "sqlserver",
            "executionMode": "provider"
          }
        },
        {
          "name": "job",
          "stepKey": "rag.retrieval",
          "order": 2,
          "input": {
            "jobId": "state.jobId"
          },
          "config": {
            "operation": "job.byId",
            "provider": "relational",
            "providerKey": "postgres",
            "executionMode": "provider"
          }
        },
        {
          "name": "merge",
          "stepKey": "rag.merge",
          "order": 3,
          "dependsOn": [
            "candidate",
            "job"
          ],
          "config": {
            "sourceSteps": [
              "candidate",
              "job"
            ]
          }
        },
        {
          "name": "compose",
          "stepKey": "rag.compose",
          "order": 4,
          "dependsOn": [
            "merge"
          ],
          "config": {
            "sourceStep": "merge",
            "composer": "deterministic"
          }
        }
      ]
    }
  ]
}
```

This configuration describes a workflow.

The runtime controls:

- parallel retrieval
- provider dispatch
- operation dispatch
- dependency ordering
- step claiming
- retry behavior
- retention behavior
- concurrency behavior
- final convergence

---

## Configuration Safety Rules

Configuration should follow these safety rules:

1. Prefer explicit dependencies over implicit execution order.
2. Keep step names stable.
3. Keep step keys stable and registered.
4. Avoid hiding retry loops inside step executors.
5. Declare retry behavior through `config.retry`.
6. Declare retention behavior through `config.retention`.
7. Declare concurrency behavior through `config.concurrency`.
8. Use provider/model/operation metadata when throttling or governance is needed.
9. Use provider/providerKey/operation metadata when dispatching to pluggable providers.
10. Use structured policies for advanced behavior.
11. Prefer `kind` over legacy `type` in new policy JSON.
12. Version pipeline definitions when behavior changes.

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Invalid dependency | Pipeline validation should reject the definition. |
| Unknown step key | Runtime should fail before unsafe execution. |
| Invalid retry config | Runtime should reject or default safely depending on contract. |
| Invalid retention config | Retention should fail safely without destructive cleanup. |
| Invalid concurrency config | Runtime should avoid acquiring unsafe capacity. |
| Missing provider metadata | Admission policy may deny if provider is required. |
| Unknown providerKey | Provider resolver should fail safely. |
| Unknown operation | Operation dispatcher should fail safely. |
| Legacy policy format | Runtime converts or resolves it compatibly. |
| Legacy `type` field | Runtime may accept it for compatibility; new JSON should use `kind`. |
| Structured policy config | Runtime passes policy-specific config to the policy. |
| Step-level override exists | Effective configuration should be deterministic. |

---

## Validated Production Configuration Paths

The config-driven runtime model is now validated together with provider-hosted runtime infrastructure.

The pipeline remains declarative and transport-neutral.

The runtime infrastructure decides whether the execution is delivered through local, HTTP, or gRPC runtime instances.

Validated separation:

```text
Pipeline definition
    declares DAG, steps, dependencies, inputs, retry, retention, concurrency, provider/model/operation metadata

Runtime host configuration
    declares control-plane mode, runtime instance provider, transport, scale-out provider, host creation mode, and process-host settings
```

Validated runtime provider paths include:

```text
local runtime provider
HTTP pooled runtime provider
HTTP process-host runtime provider
gRPC process-host runtime provider
```

Validated process-host recovery paths include:

```text
HTTP RuntimeInstanceOnly process crash recovery
gRPC RuntimeInstanceOnly process crash recovery
provider-agnostic process-host crash recovery test base
```

The same pipeline shape can be executed through different runtime providers without changing the DAG definition.

That is the main architectural value of separating pipeline configuration from runtime provider configuration.

---

## Validated Behavior

The config-driven runtime model is validated through tests and runtime usage covering:

- declarative pipeline definitions
- DAG execution mode
- dependency-driven step eligibility
- step definitions with `stepKey`
- input bindings
- step-level configuration
- pipeline-level configuration
- `config.retry`
- `config.retention`
- `config.concurrency`
- provider/model/operation metadata
- providerKey and operation-based RAG configuration
- legacy string policies
- structured policy definitions
- policy-specific configuration payloads
- backward-compatible policy deserialization
- plugin-style step execution
- separation between pipeline provider metadata and runtime instance provider metadata
- local / HTTP / gRPC runtime provider hosting without changing pipeline definitions
- HTTP process-host execution with real `RuntimeInstanceOnly` processes
- gRPC process-host execution with real `RuntimeInstanceOnly` processes
- provider-agnostic process-host crash recovery proofs using the same pipeline/recovery shape

Public schema documentation and a versioned pipeline registry remain planned work.

---

## Current Status

| Capability | Status |
|---|---|
| Declarative pipeline definitions | Implemented / validated |
| DAG execution mode | Implemented / validated |
| Step definitions with `stepKey` | Implemented / validated |
| Dependency-driven execution | Implemented / validated |
| Input bindings | Implemented / foundation available |
| Step-level configuration | Implemented / validated |
| Pipeline-level configuration | Implemented / validated |
| `config.retry` | Implemented / validated |
| `config.retention` | Implemented / validated |
| `config.concurrency` | Implemented / validated |
| Provider/model/operation metadata | Implemented / validated |
| ProviderKey / operation-based provider dispatch | Implemented / foundation available |
| Legacy string policies | Implemented / validated |
| Legacy `type` compatibility | Implemented / compatibility support |
| Structured policy definitions | Implemented / validated |
| Policy-specific config payloads | Implemented / validated |
| Pipeline provider metadata vs runtime provider metadata separation | Implemented / validated |
| Runtime provider host configuration separation | Implemented / validated |
| HTTP process-host runtime configuration path | Implemented / validated |
| gRPC process-host runtime configuration path | Implemented / validated |
| Advanced configuration validation | In progress / planned |
| Public schema documentation | Planned |
| Versioned pipeline registry | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Pipeline loader | Loads pipeline definitions. |
| Pipeline resolver | Validates and normalizes executable pipeline structure. |
| Step registry | Maps `stepKey` values to registered executors. |
| Step executor / plugin | Executes domain-specific behavior for a configured step. |
| Provider resolver / dispatcher | Resolves provider/providerKey/operation-specific behavior where applicable inside workflow steps. |
| Runtime instance provider router | Resolves local, HTTP, or gRPC runtime instance transport from registry/capacity metadata. |
| Retry engine | Resolves and applies `config.retry`. |
| Retention engine | Resolves and applies `config.retention`. |
| Concurrency engine | Resolves and applies `config.concurrency`. |
| Policy engine | Executes configured policies by policy kind. |
| DAG engine | Executes resolved workflow state deterministically. |
| Observability layer | Records configuration context and decisions. |

---

## Summary

The config-driven runtime model provides:

- clear workflow definitions
- dynamic data flow
- extensible step types
- step provider flexibility
- runtime provider transport separation
- policy-driven runtime governance
- safe DAG execution
- replayable and observable behavior

This is what allows the runtime to support real AI workflows instead of simple one-off AI calls.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Step Plugins](step-plugins.md)
- [RAG Pipelines](rag-pipelines.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)


