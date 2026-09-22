# 5 - RAG RETRIEVAL FLOW (MULTI-PROVIDER)

## Overview

This document explains the full RAG retrieval flow implemented in the system, including:

- Direct mode vs Provider mode
- Multi-provider architecture (SQL Server, PostgreSQL, etc.)
- Plugin-based extensibility
- Runtime orchestration via DAG engine

---

## High-Level Flow

```
RAG Step
   ↓
Operation (candidate.byId / job.byId)
   ↓
Datasource Resolver
   ↓
Datasource (SQL Server / Postgres)
   ↓
Execution Mode Switch
   ↓
DIRECT MODE
   → Store (EF / InMemory)

PROVIDER MODE
   → Runtime Connector (sqlserver / postgres)
        ↓
     Relational Query (plugin)
        ↓
     Store
```

---

## JSON Pipeline Example

```json
{
  "pipelines": [
    {
      "name": "candidate-by-id-test",
      "version": "1",
      "executionMode": "Dag",
      "steps": [
        {
          "name": "candidate.byId",
          "stepKey": "rag.retrieval",
          "order": 1,
          "input": {
            "candidateId": "state.candidateId"
          },
          "config": {
            "operation": "candidate.byId",
            "provider": "relational",
            "providerKey": "state.providerKey",
            "executionMode": "state.executionMode"
          }
        }
      ]
    }
  ]
}
```

---

## Execution Modes

### Direct Mode

- Calls the store directly
- No connector involved
- Used for testing, simple scenarios

### Provider Mode

- Goes through runtime connector
- Uses providerKey (sqlserver, postgres, etc.)
- Fully dynamic and extensible

---

## Multi-Provider Design

### Key Concepts

- `providerKey` → selects connector
- `entityType` → selects query
- `IRelationalRagQuery` → plugin entry point

---

## Connector Behavior (Runtime)

Connector does NOT know:

- Candidate
- Job

Connector ONLY knows:

```
(entityType, entityId)
```

And resolves:

```
entityType → IRelationalRagQuery
```

---

## Example: SQL Server Query Plugin

```csharp
public sealed class CandidateSqlServerRelationalRagQuery : IRelationalRagQuery
{
    public string ConnectorKey => "sqlserver";
    public string EntityType => "candidate";

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        string entityId,
        CancellationToken cancellationToken)
    {
        var entities = await _store.ReadByIdAsync(entityId, cancellationToken);
        return PropertyBasedRagRowMapper.MapMany(entities);
    }
}
```

---

## Adding a New Provider

Example: MySQL

1. Create connector
2. Implement IRelationalRagQuery
3. Register in DI
4. Use in pipeline:

```json
"providerKey": "mysql"
```

No runtime change needed.

---

## Strengths of This Architecture

- No duplication
- Fully extensible
- Runtime-driven
- Clean separation of concerns
- Works with DAG execution
- Deterministic

---

## Future Extensions

### Filters

```csharp
request.Filters["title"]
```

### Batch queries

```csharp
request.Ids = ["id1", "id2"]
```

### Multi-source merge

```json
sourceSteps: ["sql", "postgres", "vector"]
```

---

## Final Thought

This system is not just a RAG implementation.

It is a:

> Deterministic, multi-provider, plugin-based data orchestration engine.

