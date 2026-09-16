# Step Plugins

Status: Documentation split in progress.

This document describes the native step plugin and executor extension model used by the Deterministic AI Runtime, and its boundary with hosted functions and MCP invocations.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

The runtime is designed to orchestrate execution without hardcoding every possible AI, RAG, tool, or decision behavior inside the DAG engine.

Instead, runtime behavior is extended through step plugins.

A step plugin allows the runtime to support new step types while keeping the execution engine focused on:

- DAG orchestration
- dependency evaluation
- distributed claiming
- retry and recovery
- retention and compaction
- concurrency admission
- execution control
- deterministic convergence

The plugin owns domain-specific execution logic.

The runtime owns execution guarantees.

---

## Core Principle

The main principle is:

```text
The DAG engine coordinates execution.
Step plugins execute domain behavior.
```

This keeps responsibilities clean.

The engine should not need to know how to:

- call a specific LLM provider
- query a specific RAG backend
- merge a specific document format
- score a business decision
- call a domain API
- write to an external system

The engine only needs to know how to safely run a step.

---

## Native Plugins, Hosted Functions, and MCP

Assembly registration in this document concerns native plugins. Published Python/TypeScript functions and .NET assemblies use contextual hosted adapters, not native discovery of tenant code. An outbound MCP invocation uses a separate tool adapter. Both paths remain under the existing runtime's orchestration and authorization.

A missing hosted or MCP capability fails explicitly rather than selecting a native executor with the same key. Contextual step adapters are not discovered as attributed native plugins. The runtime-side adapter contract does not require the public SDK contract assembly, external SDK clients, or the published function to reference engine DLLs.

Hosted custom functions use immutable publication, run pinning, the durable invocation journal, and the existing DAG continuation path. Actual language loaders, execution requirements, supported dependencies, and limits are documented in [Hosted Multilanguage Execution](hosted-multilanguage-execution.md).

---

## What Is a Step Plugin?

A step plugin is a registered execution component that can handle a specific `stepKey`.

A pipeline step declares:

```json
{
  "name": "retrieve-context",
  "stepKey": "rag.retrieval",
  "config": {
    "provider": "postgres",
    "operation": "candidate.byId"
  }
}
```

The runtime uses `stepKey` to resolve the correct executor.

The executor receives resolved input and configuration, then returns a structured result.

---

## Step Key

The `stepKey` is the stable identifier for a step executor.

Examples:

```text
rag.retrieval
rag.merge
rag.compose
llm.summary
tool.execute
decision.score
```

The `stepKey` should be:

- stable
- explicit
- unique enough to avoid ambiguity
- registered with the runtime
- meaningful for diagnostics and observability

A step cannot execute safely if its `stepKey` cannot be resolved.

---

## Step Name vs Step Key

The runtime separates step name from step key.

```text
step name
= unique step instance inside a pipeline

stepKey
= registered executor type / behavior
```

Example:

```json
{
  "name": "candidate",
  "stepKey": "rag.retrieval"
}
```

Here:

```text
candidate
= this specific step inside the pipeline

rag.retrieval
= the executor behavior used to run it
```

This separation allows multiple steps to use the same executor type.

---

## Step Executor Responsibility

A step executor is responsible for executing domain logic.

It may:

- read resolved input
- read step configuration
- call a provider
- run custom logic
- return a structured result
- emit diagnostics
- throw controlled failures when required

A step executor should not directly own global orchestration.

It should not decide DAG convergence.

It should not bypass runtime state transitions.

---

## Runtime Responsibility

The runtime is responsible for:

- validating the pipeline
- resolving step dependencies
- determining step readiness
- claiming the step atomically
- applying retry rules
- applying concurrency admission
- applying execution control
- persisting results
- applying retention
- finalizing the execution

The runtime decides when and whether a step can run.

The plugin decides what happens inside the step.

---

## Plugin Execution Flow

A simplified execution flow is:

```text
DAG engine finds eligible step
        ↓
Execution control gate allows advancement
        ↓
Concurrency admission succeeds
        ↓
Redis Lua claim succeeds
        ↓
Runtime resolves step executor by stepKey
        ↓
Runtime resolves inputs and config
        ↓
Executor runs domain logic
        ↓
Executor returns result or failure
        ↓
Runtime persists completion/failure transition
```

This keeps plugin execution inside the runtime safety boundary.

---

## Config-Driven Plugins

Step plugins are configured through the pipeline definition.

A plugin can receive configuration such as:

- provider
- provider key
- model
- operation
- source step
- source steps
- composer
- retry configuration
- retention configuration
- concurrency configuration
- plugin-specific options

Example:

```json
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
```

The plugin should treat configuration as its execution contract.

---

## Class Attribute Registration

Step plugins can be declared using a class-level attribute.

The attribute binds a concrete executor class to a stable `stepKey`.

Example:

```csharp
[AiStep("decision.score")]
public sealed class CandidateScoreStep : IAiStepExecutor
{
    public Task<AiStepExecutionResult> ExecuteAsync(
        AiStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Domain-specific logic belongs here.
        // Runtime guarantees are still handled by the runtime.
        return Task.FromResult(
            AiStepExecutionResult.Success(new
            {
                score = 92,
                reason = "Strong profile match"
            }));
    }
}
```

The important idea is:

```text
Class attribute
        ↓
declares the stepKey handled by the executor

Assembly scanning
        ↓
discovers attributed executor classes

Step registry
        ↓
maps stepKey to executor implementation

Pipeline JSON
        ↓
references stepKey

Runtime
        ↓
resolves the executor automatically
```

The exact attribute class name should match the implementation in the repository.

The documentation should keep the concept clear:

```text
A step executor should be discoverable from its class metadata.
The pipeline should not manually construct executor instances.
```

---

## Assembly-Based Auto Registration

The runtime can register step plugins by scanning one or more assemblies.

A typical registration pattern is:

```csharp
services.AddAiStepsFromAssemblies(
    typeof(AiRuntimeAssemblyMarker).Assembly,
    typeof(MyApplicationAssemblyMarker).Assembly);
```

The assembly scanner should discover step executors that are marked with the step class attribute.

This allows new plugins to be added without modifying the DAG engine core.

The high-level flow is:

```text
Assembly contains attributed step classes
        ↓
AddAiStepsFromAssemblies scans assembly
        ↓
Step metadata is read from class attributes
        ↓
Executors are registered in DI
        ↓
Step registry maps stepKey to executor
        ↓
Pipeline can use the stepKey
```

This makes the extension model scalable:

- runtime-owned steps can live in runtime assemblies
- test steps can live in test assemblies
- application steps can live in application assemblies
- domain-specific plugins can live in separate packages

---

## Attribute-Based Discovery Rules

A safe attribute-based discovery model should ensure:

- each discovered step has a stable `stepKey`
- the executor implements the required step executor contract
- duplicate `stepKey` registrations are rejected
- missing step keys fail before unsafe execution
- only intended assemblies are scanned
- step metadata is explicit and observable
- registration is deterministic

Duplicate step keys must not silently override each other.

A duplicate registration should fail fast because it creates ambiguity.

---

## Plugin Registration

Plugins must be registered with the runtime.

The registration mechanism may use dependency injection, class attributes, and assembly scanning.

A typical pattern is:

```text
Executor class has step attribute
        ↓
Assembly is scanned
        ↓
Runtime reads stepKey from attribute
        ↓
Executor is registered in DI
        ↓
Step registry binds stepKey to executor
        ↓
Pipeline references stepKey
        ↓
Runtime resolves executor at execution time
```

The exact registration implementation may evolve, but the principle remains:

```text
No registered executor
        ↓
step cannot execute safely
```

---

## Plugin Discovery

The runtime supports or can support assembly-based discovery of step plugins.

This allows new step executors to be added without modifying the DAG engine core.

A safe discovery model should ensure:

- only intended assemblies are scanned
- duplicate step keys are detected
- missing step keys fail early
- attributed classes are validated
- plugin registration is explicit enough for production debugging
- the final registry can be inspected or logged

Assembly scanning is a convenience mechanism.

It must not weaken runtime safety.

---

## Provider Abstractions

Many plugins should not hardcode a specific provider.

Instead, a step may use provider abstractions.

Examples:

```text
rag.retrieval
        ↓
provider = relational
providerKey = sqlserver

rag.retrieval
        ↓
provider = relational
providerKey = postgres

rag.retrieval
        ↓
provider = vector
providerKey = redis-vector
```

This allows the same step type to work with different backends.

---

## RAG Step Plugins

RAG-oriented steps are a natural fit for the plugin model.

Examples include:

- `rag.retrieval`
- `rag.merge`
- `rag.compose`

A retrieval plugin may retrieve data from:

- SQL Server
- PostgreSQL
- MongoDB
- Redis vector
- vector databases
- external APIs
- document indexes

A merge plugin may combine upstream results.

A compose plugin may transform merged data into deterministic context.

The DAG engine does not need to know the internals of these operations.

---

## LLM Step Plugins

LLM or prompt steps can also be implemented as plugins.

An LLM plugin may:

- build a prompt
- resolve context
- call an AI provider
- parse a response
- return structured output
- emit provider/model/operation metadata

The runtime can then apply:

- retry policies
- provider throttling
- model throttling
- operation throttling
- observability
- retention

without the LLM plugin controlling those runtime guarantees directly.

---

## Tool and Action Plugins

Tool or action plugins can execute application-specific behavior.

Examples:

- call an external API
- send a notification
- write to a database
- update a CRM
- run a calculation
- execute a domain command

Tool plugins should still run inside the runtime execution model.

They should respect:

- runtime retry behavior
- cancellation boundaries
- concurrency admission
- observability
- idempotency expectations where required

---

## Decision Plugins

Decision plugins can evaluate structured data and return a deterministic decision.

Examples:

- score a candidate
- classify a document
- decide whether to continue
- select a downstream path
- validate a business rule

Decision plugins are important because AI workflows often combine probabilistic AI outputs with deterministic business logic.

---

## Plugin Inputs

Step plugins should receive resolved inputs from the runtime.

Inputs may come from:

- initial execution state
- previous step outputs
- nested result data
- configuration
- external payload resolver

Example binding:

```json
{
  "input": {
    "candidateId": "state.candidateId",
    "context": "steps.merge.result.data"
  }
}
```

The plugin should not manually scan unrelated runtime state if the input resolver can provide the required input.

---

## Plugin Outputs

Step plugins should return structured outputs.

A plugin output may include:

- status
- data
- metadata
- diagnostics
- provider information
- payload information

The runtime is responsible for persisting the result through controlled state transitions.

Large outputs may later be externalized by retention.

---

## Plugins and Retry

Plugins should not implement hidden retry loops that bypass runtime retry state.

Instead:

```text
Plugin fails
        ↓
Runtime captures failure
        ↓
Retry engine resolves config.retry
        ↓
Retry policies decide
        ↓
Runtime schedules retry through state
```

This keeps retry behavior:

- observable
- deterministic
- policy-driven
- distributed-safe

Plugins may classify or expose failure metadata, but the runtime owns retry scheduling.

---

## Plugins and Concurrency

Plugins should not bypass runtime concurrency control.

Before a plugin executes, the runtime may apply:

- execution control gate
- concurrency policies
- provider/model/operation throttling
- Redis lease acquisition
- atomic DAG claim

Provider/model/operation metadata in step config helps the runtime apply correct concurrency rules.

---

## Plugins and Retention

Plugins may produce large outputs.

The runtime retention system decides whether to:

- keep output inline
- compact payload
- externalize payload
- evict hot data later
- preserve resolver references

A plugin should return structured data and let the runtime manage memory boundaries.

---

## Plugins and Replay

Plugins should support replay-safe execution patterns where possible.

Replay foundations rely on:

- stable step keys
- stable step names
- deterministic result representation
- durable payload references
- configuration context
- replay-safe metadata

A plugin should avoid hiding important execution context outside runtime state.

---

## Plugins and Observability

Plugins should emit or expose useful diagnostics.

Useful plugin diagnostics include:

- provider
- provider key
- model
- operation
- input source
- output size
- failure reason
- external call latency
- result metadata

The runtime should correlate this with:

- `ExecutionId`
- step name
- step key
- retry state
- concurrency decision
- trace/timeline events

---

## Plugin Safety Boundaries

Plugins must not bypass the runtime safety model.

A plugin should not directly:

- claim steps
- complete steps
- fail steps
- mutate Redis DAG state
- mutate execution control state
- schedule retries independently
- acquire distributed concurrency leases independently
- finalize executions
- delete runtime payloads outside retention rules

The plugin performs domain work.

The runtime controls execution state.

---

## Idempotency Considerations

Some plugins may call external systems.

Those operations may not be naturally idempotent.

The runtime can prevent duplicate ownership, but plugin authors should still design external side effects carefully.

For example:

- use idempotency keys where possible
- include `ExecutionId` and step name in external calls
- avoid irreversible side effects without safeguards
- make retries safe when possible
- distinguish read-only steps from mutating actions

This is especially important for tool/action steps.

---

## Plugin Versioning

Plugins may evolve over time.

Future runtime hardening may require:

- versioned step keys
- versioned pipeline definitions
- compatibility rules
- migration guidance
- replay-safe plugin metadata

For now, stable `stepKey` and pipeline versioning are the main foundations.

---

## Example: Adding a New Step Plugin

A simplified process for adding a new plugin is:

```text
1. Define a stable stepKey
2. Implement the executor
3. Add the step class attribute with the stepKey
4. Ensure the executor implements the runtime step executor contract
5. Register the assembly with AddAiStepsFromAssemblies
6. Add step config schema/expectations
7. Add tests for success/failure behavior
8. Add retry/concurrency/retention compatibility tests if needed
9. Reference the stepKey in a pipeline definition
```

Example executor:

```csharp
[AiStep("decision.score")]
public sealed class CandidateScoreStep : IAiStepExecutor
{
    public Task<AiStepExecutionResult> ExecuteAsync(
        AiStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(
            AiStepExecutionResult.Success(new
            {
                score = 92
            }));
    }
}
```

Example service registration:

```csharp
services.AddAiStepsFromAssemblies(
    typeof(AiRuntimeAssemblyMarker).Assembly,
    typeof(CandidateScoreStep).Assembly);
```

Example pipeline step:

```json
{
  "name": "score-candidate",
  "stepKey": "decision.score",
  "dependsOn": [
    "compose"
  ],
  "config": {
    "operation": "candidate.score",
    "strategy": "deterministic"
  }
}
```

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Unknown `stepKey` | Runtime should fail safely before execution. |
| Missing step class attribute | Assembly scanning should not register the executor. |
| Duplicate step plugin registration | Runtime should detect ambiguity and fail fast. |
| Executor does not implement required contract | Registration should reject it or fail clearly. |
| Plugin throws exception | Runtime captures failure and applies retry rules. |
| Plugin returns large payload | Retention can externalize and compact output. |
| Plugin calls provider | Concurrency and provider throttling can apply before execution. |
| Plugin is slow or worker crashes | Recovery can release stale ownership. |
| Plugin output is needed later | Resolver and retention preserve access. |
| Plugin has side effects | Plugin should use idempotency safeguards where possible. |

---

## Validated Behavior

The step plugin model is validated through runtime usage and tests covering:

- step execution by `stepKey`
- registered step executors
- class-attribute-based step metadata
- assembly-based discovery
- config-driven step behavior
- RAG retrieval step pattern
- RAG merge step pattern
- RAG compose step pattern
- provider-based retrieval abstraction
- runtime-controlled retry around plugins
- runtime-controlled concurrency around plugins
- runtime-controlled retention around plugin outputs
- failure when a step key cannot be resolved

Public plugin SDK polish and versioned plugin contracts remain planned work.

---

## Current Status

| Capability | Status |
|---|---|
| Step execution by `stepKey` | Implemented / validated |
| Registered step executors | Implemented / validated |
| Class-attribute-based step metadata | Implemented / validated |
| Assembly-based step discovery | Implemented / validated |
| `AddAiStepsFromAssemblies` registration pattern | Implemented / validated |
| Config-driven step behavior | Implemented / validated |
| RAG retrieval step pattern | Implemented / foundation available |
| RAG merge step pattern | Implemented / foundation available |
| RAG compose step pattern | Implemented / foundation available |
| Provider-based retrieval abstraction | Implemented / foundation available |
| LLM/prompt step extension model | Foundation available |
| Tool/action step extension model | Foundation available |
| Decision step extension model | Foundation available |
| Runtime-controlled retry around plugins | Implemented / validated |
| Runtime-controlled concurrency around plugins | Implemented / validated |
| Runtime-controlled retention around plugin outputs | Implemented / validated |
| Published hosted function adapters | Implemented / validated for the supported DAG path; separate from native discovery |
| Outbound MCP invocation adapter | Implemented / validated; server-owned target and existing RBAC |
| Public plugin SDK polish | Planned |
| Versioned plugin contract | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| DAG engine | Coordinates step eligibility and execution flow. |
| Step attribute | Declares the stable `stepKey` handled by an executor class. |
| Assembly scanner | Discovers attributed step executor classes. |
| Step registry | Maps `stepKey` values to step executors. |
| Step executor | Executes domain-specific behavior. |
| Input resolver | Resolves step inputs from state and previous results. |
| Retry engine | Handles plugin failures through runtime retry state. |
| Concurrency engine | Applies admission and throttling before plugin execution. |
| Retention system | Handles large plugin outputs safely. |
| Observability layer | Records plugin execution diagnostics. |

---

## Summary

Step plugins allow the runtime to stay generic while supporting specialized behavior.

The DAG engine does not need to know how RAG retrieval, LLM calls, tools, or domain decisions work.

Native step executors are discovered through explicit metadata, registered through assembly scanning, resolved by `stepKey`, and executed inside the runtime safety boundary. Hosted functions use the separate contextual adapter path described above.

This keeps the runtime extensible without weakening deterministic execution guarantees.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [RAG Pipelines](rag-pipelines.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
