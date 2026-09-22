# RAG Retrieval Flow (rag.retrieval)
## Deep Technical Documentation — Runtime-Level Orchestration

---

## 1. Purpose

The `rag.retrieval` step is a core building block of the AI runtime.

It is NOT a simple data fetch.

It is a **deterministic orchestration bridge** between:
- The DAG execution engine
- External domain-driven logic (plugins)
- The RAG pipeline (context composition, prompting, etc.)

---

## 2. Core Problem This Solves

Traditional systems:
- Hardcode data access inside services
- Couple business logic with orchestration
- Are difficult to replay, debug, or scale

This system:

> Executes domain logic dynamically while preserving deterministic execution.

---

## 3. High-Level Architecture

```
Pipeline JSON
    ↓
DAG Engine
    ↓
RagRetrievalAiStep
    ↓
RagRetrievalStepDispatcher
    ↓
RagRetrievalStep<TExecutionContext>
    ↓
IRagOperation<TExecutionContext>
    ↓
RagRetrievalBatch
    ↓
IRagStepResultNormalizer
    ↓
Persisted Step State
```

---

## 4. Pipeline Definition

```json
{
  "pipelines": [
    {
      "name": "rag-retrieval-test",
      "executionMode": "Dag",
      "steps": [
        {
          "name": "retrieve",
          "stepKey": "rag.retrieval",
          "order": 1,
          "inputs": {
            "query": "state.query"
          },
          "config": {
            "operation": "test.operation"
          }
        }
      ]
    }
  ]
}
```

### Important

- `operation` is dynamic
- resolved at runtime
- no compile-time dependency

---

## 5. Step Layer (RagRetrievalAiStep)

### Role

- Entry point inside DAG
- Extracts config + inputs
- Passes execution to dispatcher

### Key Code

```csharp
var execution = context.Execution;
await _dispatcher.ExecuteAsync(execution, inputs, config, ct);
```

### Key Idea

The step **does not know anything about business logic**

---

## 6. Dispatcher Layer

### Class

`RagRetrievalStepDispatcher`

### Responsibilities

- Resolve operation from key
- Determine expected execution context type
- Execute strongly-typed path dynamically

### Critical Concept

```csharp
operation.ExecutionContextType
```

This defines the expected type at runtime.

### Performance

- Uses cached compiled delegates
- No reflection in hot path

---

## 7. Typed Execution Layer

### Class

`RagRetrievalStep<TExecutionContext>`

### Responsibilities

1. Validate execution context type
2. Build plugin execution context
3. Execute operation
4. Normalize result

---

## 8. Plugin Execution Context

### Interface

`IPluginExecutionContext<TExecutionContext>`

### Structure

- ExecutionContext → runtime state
- ExecutionContextSnapshot → RBAC snapshot
- Inputs → resolved step inputs

### Purpose

- Provide safe access to runtime
- Avoid exposing engine internals
- Keep plugin contract stable

---

## 9. Operation Layer

### Interface

`IRagOperation<TExecutionContext>`

### Purpose

Encapsulates **domain-specific retrieval logic**

### Example

```csharp
public sealed class GetCandidateOperation : RagOperationBase<AiExecutionContext>
{
    public override string Key => "getCandidate";

    public override Task<RagRetrievalBatch> ExecuteAsync(
        IPluginExecutionContext<AiExecutionContext> context,
        CancellationToken cancellationToken)
    {
        var query = context.Inputs["query"];

        return Task.FromResult(new RagRetrievalBatch());
    }
}
```

### Key Insight

Operations are:
- external
- replaceable
- dynamically discovered

---

## 10. Result Model

### RagRetrievalBatch

Represents the output of retrieval.

### RagNormalizedItem

Represents one normalized unit of data.

### Design Goal

Unify data from:
- SQL
- Vector DB
- API
- Runtime state

---

## 11. Normalization Layer

### Interface

`IRagStepResultNormalizer`

### Purpose

- Ensure stable output
- Guarantee serialization
- Enable replay

### Important Distinction

- Engine normalizer → execution state
- RAG normalizer → step output

---

## 12. Persistence

Result stored in:

```
AiStepResult.Data["batch"]
```

### Guarantees

- deterministic
- replay-safe
- distributed-safe

---

## 13. Dependency Injection

Required:

```csharp
services.AddRagCore();
services.AddTransient<IRagRetrievalStepDispatcher, RagRetrievalStepDispatcher>();
services.AddTransient<IRagStepResultNormalizer, DefaultRagStepResultNormalizer>();
services.AddRagOperationsFromAssemblies(...);
```

---

## 14. Determinism Guarantees

- Same input → same output
- No randomness
- Stable ordering
- Safe replay

---

## 15. Why This Architecture Matters

This enables:

- Plugin-based AI pipelines
- Domain-driven RAG
- External extensibility
- Distributed execution safety

---

## 16. Mental Model (IMPORTANT)

Think of `rag.retrieval` as:

> A dynamic execution bridge between orchestration and domain logic.

NOT as:
- a query
- a provider
- a simple step

---

## 17. Future Evolution

V2 will introduce:

- Multi-operation retrieval
- Ranking / scoring
- Hybrid providers
- Intelligent fallback

---

## 18. Final Summary

You built:

- a deterministic DAG engine
- extended it without breaking it
- added dynamic plugin execution
- integrated RAG cleanly

This is not a feature.

This is a **runtime architecture**.
