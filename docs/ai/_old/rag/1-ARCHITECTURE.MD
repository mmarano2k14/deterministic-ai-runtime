# Deterministic RAG Runtime Architecture (V1 Foundation)

## 1. Overview

This document describes the architecture of the RAG (Retrieval-Augmented
Generation) subsystem integrated into the AI runtime.

The goal is to provide a **deterministic, extensible, and provider-agnostic**
retrieval and composition layer orchestrated through a DAG execution engine.

---

## 2. Core Principles

- Separation of concerns  
- Deterministic execution  
- Provider-agnostic design  
- Dynamic extensibility via discovery  

---

## 3. Architecture Layers

- **Retrieval** → defines how data is fetched  
- **Provider** → defines where data comes from  
- **Composer** → builds the final context  
- **Runtime** → orchestrates execution  

---

## 4. System Flow

```
Attribute
   ↓
Discovery Service
   ↓
Descriptor
   ↓
Registry
   ↓
Runtime Resolution
   ↓
Execution (DAG Step)
```

---

## 5. Taxonomy

### Retrieval
- Vector
- SQL
- Runtime
- Multi-provider

### Composer
- Structured
- Deterministic

### Providers
- Redis
- Postgres
- Runtime state

---

## 6. Execution Context

Supports both:

- Generic execution context (`RagExecutionContext`)
- Typed snapshot (`RagExecutionContext<TContextSnapshot>`)

This allows:
- simple orchestration
- strong typing when needed
- compatibility with replay / persistence

---

## 7. Core Data Flow (IMPORTANT)

```
RagExecutionContext
        ↓
Retrieval
        ↓
Providers
        ↓
RagNormalizedItem[]
        ↓
RagRetrievalBatch
        ↓
Composer
        ↓
RagContextFragment[]
        ↓
RagComposedContext<TContext>
        ↓
Prompt / LLM
```

---

## 8. Core Models (NEW)

### 8.1 RagNormalizedItem

Represents a **normalized unit of data** returned by providers.

Responsibilities:
- unify all data sources
- provide consistent structure
- enable cross-provider merging

---

### 8.2 RagRetrievalBatch

Represents:
- a collection of normalized items
- the result of a retrieval operation

---

### 8.3 RagContextFragment (CRITICAL)

Represents a **single piece of context used for prompt construction**

Key responsibilities:

- deterministic ordering (`Order`)
- traceability (`SourceIds`)
- semantic classification (`FragmentKind`)
- formatting for prompt (`Text`)

This is the **bridge between raw data and prompt context**.

---

### 8.4 RagComposedContext<TContext>

Final result of the composition layer.

Contains:

- `Context` → strongly typed final object
- `Fragments` → ordered context pieces
- `Metadata` → diagnostics / hints

This allows:
- structured prompt input
- debugging and replay
- full traceability

---

## 9. Discovery System

### Why

To allow:
- plug-and-play providers
- no hardcoded dependencies
- runtime extensibility

### Flow

```
[Attribute]
   ↓
Reflection
   ↓
Descriptor
   ↓
Registry
```

---

## 10. Descriptors

Descriptors are **runtime metadata objects**

They contain:
- Key
- Implementation type
- Kind (enum)

They allow:
- fast lookup
- no reflection at runtime
- clean DI integration

---

## 11. Registry

Registry is:

- immutable
- deterministic
- dictionary-based

Responsibilities:
- store descriptors
- validate uniqueness
- provide lookup

---

## 12. Determinism Rules (CRITICAL)

This system enforces:

- Stable ordering
- No randomness
- Pure transformations
- Same input → same output

This enables:
- replay
- debugging
- distributed safety

---

## 13. Why This Architecture is Different

Traditional RAG:
- loosely structured
- non-deterministic
- prompt-driven

This system:
- runtime-driven
- deterministic
- provider-driven
- replay-safe

---

## 14. Roadmap

V1:
- Core abstractions
- Discovery
- Registry
- Context models

V2:
- Hybrid retrieval
- Ranking / scoring

V3:
- Optimization
- Compression
- advanced orchestration

---

## 15. Summary

This RAG system is:

- deterministic
- extensible
- strongly structured
- built for distributed systems

It is not just a RAG pipeline.

👉 It is a **runtime-level AI data orchestration engine**
