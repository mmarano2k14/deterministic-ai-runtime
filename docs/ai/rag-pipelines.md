# RAG Pipelines

Status: Documentation split in progress.

This document describes how the Deterministic AI Runtime supports RAG-oriented workflows through DAG execution, pluggable retrieval providers, operation-based dispatching, merge steps, compose steps, and runtime-controlled execution guarantees.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

RAG workflows are often treated as simple application logic:

```text
retrieve documents
        ↓
build prompt
        ↓
call model
```

That is enough for prototypes.

In production, RAG becomes more complex.

A real RAG workflow may need:

- multiple retrieval providers
- relational retrieval
- vector retrieval
- API retrieval
- document retrieval
- provider-specific operations
- operation-based dispatch
- providerKey-based dispatch
- parallel retrieval
- result merging
- deterministic composition
- retry and recovery
- provider/model/operation throttling
- payload externalization
- replayable execution state
- observability

The runtime treats RAG as an execution workflow, not as a hidden helper function.

---

## Core Idea

The core idea is:

```text
RAG is not only retrieval.
RAG is an execution pipeline.
```

A RAG pipeline can be represented as a DAG:

```text
candidate retrieval
        ↓
job retrieval
        ↓
merge
        ↓
compose
        ↓
LLM / decision / tool step
```

Each step is controlled by the runtime.

This means RAG workflows benefit from:

- deterministic DAG execution
- distributed step ownership
- retry and recovery
- retention and compaction
- resolver-backed rehydration
- observability
- replay foundations
- concurrency and throttling

---

## RAG Step Types

The runtime supports RAG-oriented step patterns such as:

```text
rag.retrieval
rag.merge
rag.compose
```

These step keys represent different responsibilities.

| Step Key | Responsibility |
|---|---|
| `rag.retrieval` | Retrieve data from a provider or operation. |
| `rag.merge` | Merge results from multiple upstream retrieval steps. |
| `rag.compose` | Compose merged data into deterministic context or final structured output. |

The DAG engine does not hardcode the internal behavior of these steps.

They are implemented as step plugins.

---

## RAG Steps Are Plugins

RAG steps are part of the same step plugin model used by the rest of the runtime.

A RAG step is resolved by:

```text
pipeline stepKey
        ↓
step registry
        ↓
registered executor
        ↓
runtime-controlled execution
```

Example:

```json
{
  "name": "candidate",
  "stepKey": "rag.retrieval",
  "config": {
    "operation": "candidate.byId",
    "provider": "relational",
    "providerKey": "sqlserver"
  }
}
```

The `stepKey` selects the RAG executor.

The `provider`, `providerKey`, and `operation` select the retrieval behavior inside the RAG provider/resolver layer.

---

## Class Attribute Registration for RAG Steps

RAG step executors can be registered with the runtime through class-level attributes, the same way as other step plugins.

Example:

```csharp
[AiStep("rag.retrieval")]
public sealed class RagRetrievalStep : IAiStepExecutor
{
    public Task<AiStepExecutionResult> ExecuteAsync(
        AiStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Resolve provider / providerKey / operation from context configuration.
        // Dispatch retrieval to the correct provider.
        // Return normalized RAG retrieval output.
        throw new NotImplementedException();
    }
}
```

Example merge step:

```csharp
[AiStep("rag.merge")]
public sealed class RagMergeStep : IAiStepExecutor
{
    public Task<AiStepExecutionResult> ExecuteAsync(
        AiStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Resolve sourceSteps from config.
        // Merge upstream results in deterministic order.
        throw new NotImplementedException();
    }
}
```

Example compose step:

```csharp
[AiStep("rag.compose")]
public sealed class RagComposeStep : IAiStepExecutor
{
    public Task<AiStepExecutionResult> ExecuteAsync(
        AiStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Resolve sourceStep and composer from config.
        // Produce deterministic context or structured output.
        throw new NotImplementedException();
    }
}
```

The exact attribute class name should match the implementation in the repository.

The important model is:

```text
Class attribute
        ↓
declares the stepKey

Assembly scanning
        ↓
discovers the executor

Step registry
        ↓
maps stepKey to executor

Pipeline JSON
        ↓
references stepKey
```

---

## Assembly-Based Auto Registration

RAG step executors can be auto-registered by scanning assemblies.

A typical registration pattern is:

```csharp
services.AddAiStepsFromAssemblies(
    typeof(AiRuntimeAssemblyMarker).Assembly,
    typeof(MyRagStepsAssemblyMarker).Assembly);
```

The scanner discovers attributed step executors such as:

```text
rag.retrieval
rag.merge
rag.compose
```

This means RAG support can be added without modifying the DAG engine core.

The high-level flow is:

```text
Assembly contains attributed RAG step classes
        ↓
AddAiStepsFromAssemblies scans assembly
        ↓
Step metadata is read from class attributes
        ↓
Executors are registered in DI
        ↓
Step registry maps stepKey to executor
        ↓
Pipeline can use rag.retrieval / rag.merge / rag.compose
```

This allows:

- runtime-provided RAG steps
- test RAG steps
- application-specific RAG steps
- custom provider-specific RAG extensions
- separate packages for domain-specific retrieval behavior

Duplicate `stepKey` registrations should fail fast.

Unknown `stepKey` values should fail before unsafe execution.

---

## Pluggable RAG Providers

RAG retrieval is designed to be provider-oriented and pluggable.

A retrieval step can be backed by different providers such as:

- SQL Server
- PostgreSQL
- MongoDB
- Redis vector
- vector databases
- document indexes
- external APIs
- custom provider plugins

The same logical `rag.retrieval` step can use different provider implementations depending on configuration.

Example:

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

Another step can use a different provider:

```json
{
  "name": "job",
  "stepKey": "rag.retrieval",
  "config": {
    "operation": "job.byId",
    "provider": "relational",
    "providerKey": "postgres",
    "executionMode": "provider"
  }
}
```

This allows multiple providers to participate in the same RAG pipeline.

---

## Provider Plugin Registration

RAG provider plugins can also be registered separately from the RAG step executor.

The distinction is:

```text
RAG step executor
= runtime step plugin resolved by stepKey

RAG provider plugin
= retrieval implementation selected by provider/providerKey/operation
```

A simplified model is:

```text
rag.retrieval step executor
        ↓
reads provider / providerKey / operation
        ↓
retrieval resolver / dispatcher
        ↓
selects provider plugin
        ↓
provider plugin executes retrieval
```

Provider plugins may be registered through dependency injection, explicit registry calls, or assembly scanning depending on the implementation.

The documentation should keep the distinction clear:

```text
stepKey selects the runtime step executor.
provider/providerKey/operation selects the retrieval behavior.
```

---

## Operation-Based Retrieval

Retrieval is not only provider-based.

It is also operation-based.

The `operation` field identifies what the retrieval should do.

Examples:

```text
candidate.byId
job.byId
documents.search
knowledge.semanticSearch
policy.lookup
profile.enrichment
history.byEntity
```

This means a retrieval step can be routed by:

- provider
- provider key
- operation
- execution mode
- step configuration

The runtime can dispatch retrieval to the correct provider/plugin based on the declared operation.

---

## Provider, ProviderKey, and Operation

A RAG retrieval step may use several configuration fields:

| Field | Meaning |
|---|---|
| `provider` | Logical provider category, such as `relational`, `vector`, `api`, or `document`. |
| `providerKey` | Specific provider implementation, such as `sqlserver`, `postgres`, or `redis-vector`. |
| `operation` | Logical operation to execute, such as `candidate.byId` or `documents.search`. |
| `executionMode` | Optional mode used by the retrieval dispatcher/provider. |

Example:

```json
{
  "operation": "candidate.byId",
  "provider": "relational",
  "providerKey": "sqlserver",
  "executionMode": "provider"
}
```

This keeps RAG behavior declarative.

The pipeline says what to retrieve.

The provider plugin decides how to retrieve it.

---

## Retrieval Resolver / Dispatcher

A RAG retrieval layer can use a resolver or dispatcher to select the correct retrieval implementation.

A simplified flow is:

```text
rag.retrieval step
        ↓
read operation/provider/providerKey
        ↓
build retrieval context
        ↓
resolve retrieval provider
        ↓
execute provider-specific retrieval
        ↓
return normalized result
```

This allows the runtime to support new providers without modifying the DAG engine.

The DAG engine only coordinates execution.

The retrieval dispatcher/provider handles retrieval behavior.

---

## Multiple Providers in One Pipeline

A single RAG pipeline can retrieve data from multiple providers in parallel.

Example:

```text
candidate step -> SQL Server
job step       -> PostgreSQL
history step   -> MongoDB
semantic step  -> vector database
policy step    -> API
```

These steps can run in parallel if they have no dependencies.

The runtime manages:

- parallel eligibility
- distributed claims
- retry behavior
- provider throttling
- result persistence
- retention and compaction

This is a major difference from hardcoded RAG logic inside application code.

---

## Parallel Retrieval

RAG pipelines often benefit from parallel retrieval.

Example:

```text
candidate retrieval   no dependencies
job retrieval         no dependencies
policy retrieval      no dependencies
        ↓
all can run in parallel
```

The runtime can execute these steps in parallel while preserving deterministic convergence.

Parallel retrieval remains controlled by:

- DAG dependencies
- local max degree of parallelism
- distributed concurrency limits
- provider/model/operation throttling
- claim ownership

---

## Merge Step

A `rag.merge` step combines results from multiple upstream retrieval steps.

Example:

```json
{
  "name": "merge",
  "stepKey": "rag.merge",
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
}
```

The merge step should wait until all required source steps are completed.

It can then combine their results into a normalized structure.

The merge step should preserve stable ordering where required for deterministic composition.

---

## Compose Step

A `rag.compose` step transforms merged data into deterministic context or structured output.

Example:

```json
{
  "name": "compose",
  "stepKey": "rag.compose",
  "dependsOn": [
    "merge"
  ],
  "config": {
    "sourceStep": "merge",
    "composer": "deterministic"
  }
}
```

The compose step may produce:

- prompt context
- structured evaluation input
- final normalized context
- deterministic summary
- input for an LLM step
- input for a decision step

Composition should be deterministic when the same source data is provided.

---

## Deterministic Composition

Deterministic composition is important because RAG retrieval can involve multiple sources.

The composer should avoid non-deterministic ordering unless explicitly required.

Stable composition may include:

- stable source ordering
- stable batch ordering
- stable merge order
- stable key ordering where relevant
- deterministic transformation rules

This makes replay and debugging easier.

---

## Example RAG Pipeline

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

The `order` field may be useful for readability or legacy definitions.

DAG execution should rely on explicit `dependsOn` dependencies.

---

## Execution Flow

The runtime interprets the example as a DAG.

```text
candidate
        ┐
        ├── merge ── compose
job     ┘
```

Execution flow:

```text
candidate and job have no dependencies
        ↓
both can run in parallel
        ↓
merge waits for candidate and job
        ↓
compose waits for merge
        ↓
execution converges
```

The application does not manually coordinate these steps.

The runtime does.

---

## Runtime Guarantees Applied to RAG

RAG steps benefit from the same runtime guarantees as other steps.

| Runtime Feature | RAG Benefit |
|---|---|
| DAG execution | Retrieval, merge, and compose steps are dependency-safe. |
| Distributed claiming | Multiple workers can process retrieval without duplicate ownership. |
| Retry engine | Provider failures can be retried deterministically. |
| Recovery | Crashed retrieval workers can be recovered. |
| Retention | Large retrieved batches can be compacted or externalized. |
| Resolver | Externalized payloads can be rehydrated. |
| Concurrency throttling | Providers and operations can be protected. |
| Observability | Retrieval and composition behavior can be inspected. |
| Replay foundations | Completed RAG executions can be restored and compared. |

---

## RAG and Retry

Retrieval providers can fail.

Examples:

- database timeout
- API timeout
- vector search failure
- provider unavailable
- malformed response

The plugin should surface failure to the runtime.

The runtime then applies:

```text
config.retry
        ↓
retry policies
        ↓
retry decision
        ↓
WaitingForRetry or Failed
```

The provider plugin should not hide uncontrolled retry loops inside itself.

---

## RAG and Provider Throttling

RAG providers may require throttling.

Examples:

```text
PostgreSQL retrieval max 20 operations
Redis vector retrieval max 15 operations
External API retrieval max 5 operations
LLM compose operation max 10 calls
```

Provider and operation metadata allow the concurrency engine to enforce limits.

Example:

```json
{
  "config": {
    "provider": "redis-vector",
    "operation": "rag.retrieve"
  }
}
```

This metadata can be used by:

- provider throttling
- operation throttling
- observability
- future cost governance

---

## RAG and Retention

RAG outputs can be large.

A retrieval step may return:

- many documents
- large text chunks
- embeddings metadata
- structured records
- merged context

Retention can externalize and compact these outputs.

Example:

```text
rag.retrieval returns large batch
        ↓
Retention trigger detects large payload
        ↓
Payload stored externally
        ↓
Redis keeps lightweight reference
        ↓
Resolver rehydrates when needed
```

This keeps Redis hot state bounded.

---

## RAG and Resolver Rehydration

The resolver is important for RAG because downstream steps may need data that was compacted.

Example:

```text
candidate retrieval completed
        ↓
payload compacted
        ↓
merge step needs candidate data
        ↓
resolver rehydrates payload
        ↓
merge continues
```

This allows memory control without breaking data flow.

---

## RAG and Replay

Replay foundations should preserve RAG execution outcomes.

Replay-relevant RAG data includes:

- retrieval step status
- source provider metadata
- operation metadata
- payload references
- merge output
- compose output
- retry counts
- terminal status
- deterministic fingerprint

RAG replay should avoid relying on hidden provider state that is not captured by the runtime.

---

## RAG and Observability

RAG workflows should expose diagnostics such as:

- provider
- provider key
- operation
- retrieval duration
- result count
- payload size
- merge source steps
- composer key
- retry count
- throttling decisions
- resolver hit/miss behavior

This makes RAG pipelines debuggable instead of opaque.

---

## RAG and Plugin Boundaries

RAG providers are plugins.

They should not bypass runtime guarantees.

A RAG provider should not directly:

- claim DAG steps
- complete or fail DAG steps
- mutate Redis execution state
- schedule runtime retries
- acquire distributed concurrency leases outside the runtime
- delete payload data outside retention rules

The provider retrieves data.

The runtime controls execution.

---

## Adding a New RAG Provider

A simplified process for adding a new RAG provider is:

```text
1. Define provider type and providerKey
2. Implement retrieval provider/plugin
3. Register provider with the retrieval resolver/dispatcher
4. If the provider is class-attribute based, add the provider metadata attribute
5. Register the provider assembly if assembly scanning is supported
6. Define supported operations
7. Add pipeline config using operation/provider/providerKey
8. Add tests for success, failure, retry, and throttling behavior
9. Validate payload externalization and resolver compatibility
```

Example new provider:

```json
{
  "name": "semantic-search",
  "stepKey": "rag.retrieval",
  "config": {
    "operation": "documents.semanticSearch",
    "provider": "vector",
    "providerKey": "redis-vector"
  }
}
```

---

## Adding a New RAG Operation

Operations should be explicit and stable.

A new operation may represent:

- entity lookup
- semantic search
- hybrid search
- policy lookup
- document retrieval
- relationship traversal
- profile enrichment

Example:

```text
documents.semanticSearch
candidate.byId
job.byId
policy.lookup
history.byEntity
```

The operation tells the provider what retrieval behavior is expected.

---

## Current and Future RAG Direction

The current RAG layer provides foundations for:

- operation-based retrieval
- provider-based retrieval
- providerKey-based dispatch
- multiple providers in one pipeline
- merge steps
- compose steps
- deterministic RAG pipelines
- provider-oriented workflow execution
- class-attribute-based step registration
- assembly-based step discovery

Future directions may include:

- deeper vector retrieval support
- hybrid retrieval
- graph/entity retrieval
- ranking and filtering
- result deduplication
- multi-provider retrieval plans
- richer composer plugins
- cost-aware retrieval governance

These should be treated as roadmap directions unless implemented.

---

## Safety Rules

RAG pipelines should follow these rules:

1. Keep provider and operation metadata explicit.
2. Keep providerKey metadata explicit when dispatch requires it.
3. Use stable step names.
4. Use stable operation names.
5. Use registered provider plugins.
6. Register RAG step executors through the step plugin model.
7. Prefer attribute-based step metadata when supported.
8. Avoid hidden retry loops inside providers.
9. Let the runtime manage retry, concurrency, retention, and replay.
10. Preserve deterministic ordering in merge and compose steps.
11. Externalize large retrieval payloads through retention.
12. Use resolver-backed rehydration for compacted payloads.
13. Keep provider-specific behavior behind plugin boundaries.

---

## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Retrieval provider fails | Runtime applies retry policy. |
| Retrieval worker crashes | Recovery can make the step claimable again. |
| Two workers try same retrieval step | Redis claim allows only one owner. |
| Provider concurrency limit reached | Redis concurrency gate denies capacity. |
| Retrieval payload is large | Retention externalizes and compacts payload. |
| Merge needs compacted retrieval result | Resolver rehydrates payload. |
| Compose receives merged data | Deterministic composer produces stable context. |
| Unknown RAG stepKey | Step registry should fail safely before execution. |
| RAG step executor is not registered | Runtime should fail before unsafe execution. |
| Duplicate RAG stepKey registration | Assembly scanning / registry should reject ambiguity. |
| Unknown providerKey | Runtime/provider resolver should fail safely. |
| Unknown operation | Retrieval dispatcher should fail safely. |
| Replay needs RAG output | Snapshot/payload references support reconstruction. |

---

## Validated Behavior

The RAG pipeline model is validated through runtime usage and tests covering:

- `rag.retrieval`
- `rag.merge`
- `rag.compose`
- operation-based retrieval configuration
- provider/providerKey configuration
- multiple providers in one pipeline
- parallel retrieval
- deterministic merge/compose behavior
- class-attribute-based step metadata
- assembly-based step discovery
- provider-oriented retrieval abstraction
- retry around RAG steps
- concurrency/throttling around RAG steps
- retention around large RAG payloads
- resolver rehydration for compacted payloads
- failure when a step key or provider operation cannot be resolved

Advanced vector/hybrid retrieval, ranking, deduplication, graph/entity retrieval, and cost-aware RAG governance remain future directions unless implemented.

---

## Current Status

| Capability | Status |
|---|---|
| RAG retrieval step pattern | Implemented / foundation available |
| RAG merge step pattern | Implemented / foundation available |
| RAG compose step pattern | Implemented / foundation available |
| Class-attribute-based RAG step metadata | Implemented / validated |
| Assembly-based RAG step discovery | Implemented / validated |
| Operation-based retrieval configuration | Implemented / foundation available |
| Provider/providerKey configuration | Implemented / foundation available |
| Multiple providers in one pipeline | Implemented / foundation available |
| Provider plugin model | Foundation available |
| Retrieval resolver / dispatcher pattern | Foundation available |
| Deterministic composition pattern | Implemented / foundation available |
| Retry around RAG steps | Implemented |
| Concurrency/throttling around RAG steps | Implemented |
| Retention around large RAG payloads | Implemented |
| Resolver rehydration for compacted payloads | Implemented |
| Advanced vector/hybrid retrieval | Planned / future direction |
| Ranking and deduplication | Planned / future direction |
| Graph/entity retrieval | Planned / future direction |
| Cost-aware RAG governance | Planned / future direction |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| DAG engine | Coordinates RAG step eligibility, dependencies, and convergence. |
| Step attribute | Declares stable `stepKey` metadata for RAG executors. |
| Assembly scanner | Discovers attributed RAG step executors. |
| Step registry | Maps RAG `stepKey` values to executors. |
| RAG retrieval executor | Executes retrieval step behavior. |
| Retrieval resolver / dispatcher | Selects provider implementation by operation/provider/providerKey. |
| RAG provider plugin | Performs provider-specific retrieval. |
| RAG merge executor | Combines upstream retrieval outputs. |
| RAG compose executor | Builds deterministic context from merged data. |
| Retry engine | Handles retrieval failures through runtime retry state. |
| Concurrency engine | Applies provider/operation throttling. |
| Retention system | Externalizes and compacts large RAG payloads. |
| Resolver | Rehydrates compacted or evicted RAG data. |
| Observability layer | Records provider, operation, payload, and execution diagnostics. |

---

## Summary

RAG pipelines in the Deterministic AI Runtime are not hidden application helpers.

They are explicit DAG-based execution workflows.

RAG steps are registered as plugins, discovered through step metadata and assembly scanning, configured through pipeline JSON, dispatched through provider/providerKey/operation metadata, and executed inside the same runtime safety model as every other step.

This gives RAG workflows deterministic execution, distributed safety, retry/recovery, retention, concurrency governance, replay foundations, and observability.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Step Plugins](step-plugins.md)
- [Distributed Execution](distributed-execution.md)
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
