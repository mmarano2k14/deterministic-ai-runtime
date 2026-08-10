# Deterministic RAG Runtime — Implementation

## 1. Why this document exists

This document is intentionally more detailed than the architecture note.

Its goal is simple:

**in 3 months, you should be able to reopen this file and immediately understand:**

- what was built
- why it was built this way
- how the pieces connect together
- what data flows through the system
- how to extend it safely
- how to configure it in JSON
- what “compact mode” and “expert mode” mean in practice

This is not just theory.

It is a **memory-safe implementation guide** for the RAG subsystem inside the deterministic AI runtime.

---

## 2. What was actually built

The RAG subsystem that was added is not a “chatbot helper” or a loose prompt trick.

It is a **runtime-level retrieval and composition layer** integrated into the DAG engine.

That means:

1. retrieval is executed as a runtime step
2. composition is executed as a runtime step
3. prompt execution can consume the composed result
4. the whole flow stays compatible with:
   - deterministic execution
   - retries
   - recovery
   - replay
   - persistence
   - multi-worker execution

So the important mental model is this:

```text
Traditional RAG:
query -> retrieve documents -> paste into prompt

This runtime RAG:
query/state -> retrieval step -> normalized batch -> composer step -> structured context -> prompt step
```

The second model is much stronger because the RAG flow becomes:

- observable
- testable
- traceable
- replayable
- extensible

---

## 3. The big idea

The key design decision is:

**RAG is split into 4 layers.**

### 3.1 Retrieval layer
Defines **how data is requested**.

Examples:
- vector retrieval
- SQL retrieval
- runtime-state retrieval
- hybrid retrieval

### 3.2 Provider layer
Defines **where data comes from**.

Examples:
- Redis
- Postgres
- runtime execution state
- API
- file/document source

### 3.3 Composition layer
Defines **how retrieved data becomes prompt-ready context**.

Examples:
- compact context
- structured expert context
- ordered fragments
- grouped summaries

### 3.4 Runtime layer
Defines **how the whole thing is executed** inside the DAG engine.

Examples:
- claim step
- execute step
- store outputs
- retry on failure
- recover if worker crashes
- replay from snapshot later

---

## 4. Why this architecture matters

If everything is mixed together in one “RAG service”, several problems appear quickly:

- retrieval logic becomes coupled to provider implementation
- prompt formatting becomes coupled to search logic
- replay becomes hard
- testing becomes weak
- changing one provider risks breaking the whole pipeline
- hybrid retrieval becomes messy
- outputs become inconsistent across providers

The architecture used here avoids that.

It introduces **normalized boundaries**.

That is the real value.

---

## 5. The most important flow to remember

This is the core flow of the implementation:

```text
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
ai.prompt
```

If you remember only one diagram, remember this one.

---

## 6. What each model means in simple words

## 6.1 `RagExecutionContext`

This is the runtime input container for RAG execution.

It carries the information needed by retrieval and composition.

Depending on the implementation, it can include things like:

- query text
- filters
- tenant information
- execution metadata
- state-derived input values
- typed snapshot data

There are two conceptual modes:

### Untyped mode
Useful when you only need simple values and dynamic state access.

```csharp
RagExecutionContext
```

### Typed mode
Useful when you want stronger structure and future replay/persistence compatibility.

```csharp
RagExecutionContext<TContextSnapshot>
```

This matters because typed context makes the final system safer when:
- restoring executions
- persisting snapshots
- debugging state later

---

## 6.2 `RagNormalizedItem`

This is one of the most important classes in the whole design.

A `RagNormalizedItem` represents **one normalized piece of data returned by a provider**.

Think of it as the universal shape of retrieval output.

Without normalization, each provider would return a different model:

- SQL row
- Redis hash
- API JSON
- runtime object
- vector search hit

That would make composition inconsistent and painful.

So instead:

```text
Provider-specific raw result
    ↓
Normalize
    ↓
RagNormalizedItem
```

### Why this is critical

Because once everything becomes `RagNormalizedItem`, the composer no longer cares where the data came from.

That enables:

- hybrid retrieval
- consistent merging
- consistent ordering
- deterministic output
- easier debugging

### Conceptual fields

A normalized item usually carries things like:

- id
- source id
- provider key
- text/content
- title
- metadata
- score
- raw payload or structured value

Even if exact fields evolve, the role stays the same:

**one provider-independent unit of retrieved knowledge**

---

## 6.3 `RagRetrievalBatch`

A `RagRetrievalBatch` is simply the output of a retrieval operation.

It usually contains:

- a list of normalized items
- metadata about retrieval
- provider or strategy hints
- optional diagnostics

Mentally:

```text
RagRetrievalBatch = collection of retrieved normalized items
```

This object is important because it creates a clean boundary between:

- retrieval world
- composition world

Retrieval ends there.
Composition starts there.

That separation is one of the strongest parts of the implementation.

---

## 6.4 `RagContextFragment`

This is the bridge between data retrieval and prompt construction.

A `RagContextFragment` is **one deterministic piece of prompt-ready context**.

It usually carries:

- `Order`
- `Text`
- `FragmentKind`
- `SourceIds`

### Why it exists

Raw retrieved data is not yet prompt context.

Even normalized items are still “data”.

A fragment is the first representation that says:

> this exact text block, in this exact order, with these exact sources, should become part of the final prompt context.

That makes it extremely valuable for:

- debugging
- replay
- traceability
- deterministic prompt construction

### The most important fields

#### `Order`
This matters a lot.

The fragment order must be stable.

If order changes randomly between executions, prompt output can change too.

So `Order` is one of the main determinism anchors.

#### `Text`
This is the actual context content that will be visible to the prompt step.

#### `SourceIds`
This gives traceability.

Later, if you inspect a prompt result and ask:
“where did this context come from?”
you can answer it.

#### `FragmentKind`
This lets the system classify fragment meaning.

Examples:
- summary
- evidence
- metadata
- instruction-support
- profile
- experience
- skills
- note

---

## 6.5 `RagComposedContext<TContext>`

This is the final output of the composition layer.

It usually contains three conceptual parts:

- `Context`
- `Fragments`
- `Metadata`

### `Context`
This is the final strongly typed structured object.

For example:
- compact prompt model
- rich expert prompt model
- categorized job matching context
- retrieval summary object

### `Fragments`
These are the actual ordered pieces used to build prompt context.

### `Metadata`
This is diagnostic and supporting information.

This can help with:
- debugging
- scoring insight
- provider visibility
- replay inspection

This model is powerful because it supports both:
- machine structure
- prompt-ready traceability

---

## 7. What “deterministic” really means here

The word deterministic is used a lot, so here is the practical meaning.

In this runtime, deterministic means:

> given the same execution state and the same retrieval inputs, the system should produce the same composed output.

That requires several rules.

### Rule 1 — Stable ordering
Results cannot be left in arbitrary provider order if that order is unstable.

If providers are not guaranteed stable, the retrieval/composer layer must enforce stable sorting.

### Rule 2 — No random formatting
No “best effort maybe shuffle” formatting logic.

### Rule 3 — Pure transformations
Normalization and composition should behave like pure functions as much as possible.

### Rule 4 — Explicit metadata
Context shape should be explicit, not guessed later.

### Rule 5 — Same inputs, same fragments
If the input and retrieved items are the same, fragment output should also be the same.

This matters because determinism is what makes:
- replay meaningful
- debugging credible
- distributed execution safe
- prompt analysis easier

---

## 8. Runtime integration with the DAG engine

RAG is not outside the engine.

It runs as steps.

That means the engine can treat RAG the same way it treats other steps.

### Example flow

```text
step-1: rag.retrieval
step-2: rag.compose
step-3: ai.prompt
step-4: decision / parser / next action
```

Each step:
- has inputs
- has config
- has output/result
- has state
- can be retried
- can be recovered
- can be replayed

This is a major advantage.

Because now:
- retrieval failures can be retried
- composition failures can be isolated
- prompt execution can be traced independently
- each stage becomes testable

---

## 9. Why this is stronger than “one RAG call”

A lot of systems do something like:

```text
user query -> SearchAndBuildPromptAsync() -> LLM
```

That is simple, but weak.

The runtime approach here is stronger because it separates the process into visible steps:

```text
retrieve -> inspect -> compose -> inspect -> prompt
```

That gives you:

- transparent intermediate state
- better tests
- easier failure recovery
- structured outputs
- future multi-strategy composition
- future scoring/ranking support

---

## 10. Discovery system

The subsystem uses a discovery/descriptor/registry approach.

This is important because you do not want hardcoded `switch` logic everywhere.

The general flow is:

```text
Attribute
   ↓
Discovery
   ↓
Descriptor
   ↓
Registry
   ↓
Runtime resolution
```

### Why this is useful

Because adding a new provider or composer should not require rewriting core runtime logic.

Instead:
- implementation gets marked/discovered
- descriptor is created
- registry stores it
- runtime resolves by key

This is cleaner and scales much better.

---

## 11. Descriptor role

Descriptors are runtime metadata objects.

They usually describe:
- key
- implementation type
- kind/category

Their purpose is to convert reflection-time discovery into runtime-safe metadata.

This means:
- reflection happens once
- runtime lookup stays fast
- runtime does not need repeated scanning

This is exactly the type of structure you want in a serious engine.

---

## 12. Registry role

The registry is the deterministic lookup container.

It is typically:

- immutable after build
- dictionary-based
- uniqueness-validated

Its job is to answer questions like:

- which retrieval implementation matches this key?
- which provider implementation matches this key?
- which composer implementation matches this key?

The registry is a major stability point because it avoids:
- repeated reflection
- ambiguous resolution
- accidental duplicate registration

---

## 13. What was done in V1 vs what is next

### V1 foundation focused on:
- abstractions
- discovery
- descriptors
- registries
- execution context
- normalized models
- retrieval batch
- fragments
- composed context
- deterministic structure

### V2 should naturally move toward:
- hybrid retrieval
- ranking/scoring
- provider blending
- more advanced JSON configuration
- better selection policies

### V3 can focus on:
- optimization
- compression
- token-aware context shaping
- adaptive retrieval strategy

---

## 14. Code-level mental model

Even if you do not remember every class name, the implementation should stay understandable through this mental map:

### Layer A — runtime contracts
Defines the interfaces and core models.

Examples:
- execution context
- retrieval contracts
- provider contracts
- composer contracts

### Layer B — discovery and registration
Finds implementations and stores descriptors.

Examples:
- attributes
- descriptor builders
- registries

### Layer C — runtime execution
Uses resolved implementations during actual step execution.

Examples:
- retrieval step executor
- composer step executor
- prompt step consuming composed context

### Layer D — persistence/replay compatibility
Ensures outputs remain serializable and stable.

Examples:
- normalized data
- typed context
- replay-safe structures

That is the code structure in practical terms.

---

## 15. Why normalization is not optional

I asked an important question earlier in the project around “why not just directly keep JSON?”

This file should record the answer clearly.

Direct raw JSON everywhere looks easy at first, but it causes several long-term issues:

- providers return inconsistent shapes
- downstream logic becomes dynamic and fragile
- replay/serialization becomes less predictable
- prompt composition becomes messy
- code becomes harder to reason about

Normalization solves that.

It creates a stable boundary.

That means the composer can rely on:

- known model shape
- known ordering logic
- known traceability structure

So even if raw JSON is still part of the raw provider world, the runtime world should move quickly toward normalized models.

That is the correct design choice.

---

## 16. Compact mode vs Expert mode

This is one of the most useful conceptual additions for future readability.

The idea is simple:

**the same retrieval output can be composed in different ways depending on the goal.**

That means the composer does not only “merge data”.
It can also decide **how rich or how minimal the final context should be**.

Two useful modes are:

- compact mode
- expert mode

---

## 17. Compact mode

Compact mode is optimized for:

- lower token usage
- faster prompt execution
- only essential evidence
- concise context blocks

Typical use cases:
- simple prompt enrichment
- shortlist decisions
- low-cost matching
- fast relevance pass

### Compact mode characteristics

- fewer fragments
- summarized text
- minimal metadata
- essential source references only
- stable and short prompt context

### Compact mode example — conceptual output

```json
{
  "mode": "compact",
  "fragments": [
    {
      "order": 1,
      "fragmentKind": "summary",
      "sourceIds": ["cv-123"],
      "text": "Senior .NET engineer with distributed systems, Redis, MongoDB, and workflow runtime experience."
    },
    {
      "order": 2,
      "fragmentKind": "summary",
      "sourceIds": ["job-456"],
      "text": "Staff engineer role focused on platform scalability, distributed services, and system design."
    },
    {
      "order": 3,
      "fragmentKind": "evidence",
      "sourceIds": ["cv-123", "job-456"],
      "text": "Strong overlap on distributed execution, reliability, and backend architecture."
    }
  ]
}
```

### Why compact mode is useful

Because not every prompt needs all details.

Sometimes you only want:
- a quick match
- a short ranking context
- a decision score input
- a reduced token footprint

---

## 18. Expert mode

Expert mode is optimized for:

- richer reasoning context
- stronger traceability
- advanced prompt analysis
- detailed evidence grouping

Typical use cases:
- deep job matching
- expert review prompts
- structured evaluation
- high-confidence reasoning
- audit-friendly prompts

### Expert mode characteristics

- more fragments
- grouped categories
- richer metadata
- explicit source mapping
- more detailed evidence blocks
- clearer separation by semantic type

### Expert mode example — conceptual output

```json
{
  "mode": "expert",
  "fragments": [
    {
      "order": 1,
      "fragmentKind": "profile",
      "sourceIds": ["cv-123"],
      "text": "Candidate profile: 25+ years in software engineering, strong .NET background, staff/principal-level architecture experience."
    },
    {
      "order": 2,
      "fragmentKind": "skills",
      "sourceIds": ["cv-123"],
      "text": "Core skills: C#, distributed systems, Redis Lua, MongoDB, DAG execution, multi-tenant SaaS, runtime determinism."
    },
    {
      "order": 3,
      "fragmentKind": "experience",
      "sourceIds": ["cv-123"],
      "text": "Recent work includes deterministic AI runtime, retry/recovery/replay, prompt orchestration, and runtime-safe RAG foundations."
    },
    {
      "order": 4,
      "fragmentKind": "job-requirement",
      "sourceIds": ["job-456"],
      "text": "Role requires strong backend architecture, scalable platform engineering, production reliability, and technical leadership."
    },
    {
      "order": 5,
      "fragmentKind": "match-evidence",
      "sourceIds": ["cv-123", "job-456"],
      "text": "High overlap on distributed systems, runtime architecture, platform leadership, and hands-on engineering depth."
    },
    {
      "order": 6,
      "fragmentKind": "gap-analysis",
      "sourceIds": ["cv-123", "job-456"],
      "text": "Potential gaps may include stack-specific tooling, but core architecture and distributed systems fit is strong."
    }
  ],
  "metadata": {
    "strategy": "expert",
    "providerKeys": ["runtime", "sql", "vector"],
    "traceabilityEnabled": true
  }
}
```

### Why expert mode is useful

Because some prompts benefit from more structure and more evidence.

Instead of just passing short summaries, the LLM receives:
- categorized context
- stronger reasoning support
- explicit evidence boundaries

That often improves output consistency and reviewability.

---

## 19. JSON pipeline examples

Below are conceptual JSON examples to remember how the runtime can be configured.

These examples are intentionally concrete enough to be remembered later, but still generic enough to adapt.

---

## 20. Compact mode pipeline example

```json
{
  "pipelineId": "rag-compact-matching",
  "executionMode": "Dag",
  "steps": [
    {
      "id": "retrieve-candidate",
      "stepType": "rag.retrieval",
      "inputs": {
        "query": "state.query"
      },
      "config": {
        "retrievalKey": "runtime-search",
        "providerKeys": ["runtime", "sql"],
        "mode": "compact"
      }
    },
    {
      "id": "compose-context",
      "stepType": "rag.compose",
      "inputs": {
        "batch": "steps.retrieve-candidate.result.data"
      },
      "config": {
        "composerKey": "compact",
        "maxFragments": 4,
        "includeMetadata": false
      }
    },
    {
      "id": "prompt",
      "stepType": "ai.prompt",
      "inputs": {
        "context": "steps.compose-context.result.data.context"
      },
      "config": {
        "provider": "openai",
        "model": "gpt-4.1-mini",
        "responseFormat": "json",
        "template": "Given this compact context, produce a short fit analysis: {{context}}"
      }
    }
  ]
}
```

### What this pipeline does

1. retrieves data from runtime and SQL providers
2. normalizes everything into a batch
3. composes a compact context
4. sends the compact context into the prompt step

This is good for:
- fast matching
- low token cost
- shortlist generation

---

## 21. Expert mode pipeline example

```json
{
  "pipelineId": "rag-expert-review",
  "executionMode": "Dag",
  "steps": [
    {
      "id": "retrieve-data",
      "stepType": "rag.retrieval",
      "inputs": {
        "query": "state.query",
        "job": "state.jobId",
        "candidate": "state.candidateId"
      },
      "config": {
        "retrievalKey": "hybrid-search",
        "providerKeys": ["runtime", "sql", "vector"],
        "mode": "expert"
      }
    },
    {
      "id": "compose-expert-context",
      "stepType": "rag.compose",
      "inputs": {
        "batch": "steps.retrieve-data.result.data"
      },
      "config": {
        "composerKey": "expert",
        "groupByKind": true,
        "includeMetadata": true,
        "includeSourceIds": true,
        "maxFragments": 12
      }
    },
    {
      "id": "prompt",
      "stepType": "ai.prompt",
      "inputs": {
        "context": "steps.compose-expert-context.result.data.context",
        "fragments": "steps.compose-expert-context.result.data.fragments"
      },
      "config": {
        "provider": "openai",
        "model": "gpt-4.1-mini",
        "responseFormat": "json",
        "template": "Using this expert-level structured context, evaluate candidate fit, strengths, gaps, and risk areas. Context: {{context}} Fragments: {{fragments}}"
      }
    }
  ]
}
```

### What this pipeline does

1. retrieves from multiple providers
2. composes a rich expert context
3. includes fragments and metadata
4. feeds a more powerful evidence structure to the prompt

This is better for:
- deep evaluations
- richer auditability
- stronger reasoning support

---

## 22. Practical difference between compact and expert

Here is the short memory version.

### Compact
Use when you want:
- speed
- cost reduction
- essential context only
- fewer tokens

### Expert
Use when you want:
- richer reasoning
- deeper evidence
- traceability
- structured analysis

So the composer is not just formatting text.
It is effectively a **context strategy layer**.

That is an important idea to remember.

---

## 23. Example execution story

This section is here so future-you can remember the runtime behavior more concretely.

Imagine a job matching execution.

### Milestone 1 — retrieval
The step reads:
- candidate data from runtime state
- job data from SQL
- supporting matches from vector search

Each provider returns different raw shapes.

The retrieval logic normalizes all of them into:

```text
RagNormalizedItem[]
```

and then packages them into:

```text
RagRetrievalBatch
```

### Milestone 2 — composition
The composer reads the batch.

If the chosen mode is compact:
- it keeps fewer fragments
- merges some evidence
- keeps prompt small

If the chosen mode is expert:
- it separates fragments by kind
- keeps more metadata
- builds a richer typed context

### Milestone 3 — prompt
The prompt step receives either:
- a compact context object
- an expert context object

Then the LLM works from a much stronger input than a raw retrieval dump.

That is the real implementation gain.

---

## 24. Retry and recovery implications for RAG

Because RAG runs inside the DAG engine, it inherits the engine guarantees.

That matters a lot.

### Retry
If retrieval fails transiently:
- the step can retry
- retry count is tracked
- delay can be applied

### Recovery
If a worker crashes while a RAG step is running:
- the step can be recovered
- another worker can reclaim it safely
- execution does not need to restart from zero

### Replay
If execution state was persisted:
- the RAG-related outputs can be restored and inspected
- deterministic context can be checked again later

This is one of the strongest differentiators of the whole design.

Most RAG systems do not have this level of operational safety.

---

## 25. Serialization and long-term safety

A hidden but important concern is serialization quality.

The reason the normalized/composed structures matter is not only prompt quality.

It is also because they need to survive:

- persistence
- logging
- replay
- debugging
- later inspection

That is why stable models matter more than loose ad hoc JSON.

If future-you reopens a replay snapshot, you want to clearly see:
- retrieval batch
- fragments
- composed context
- metadata

Not a random nested blob that nobody understands anymore.

---

## 26. Testing strategy you should remember

The right testing mindset for this subsystem is layered.

### Unit-level thinking
Test:
- normalization logic
- fragment ordering
- compact composer behavior
- expert composer behavior

### Integration-level thinking
Test:
- retrieval step through runtime
- composer step through runtime
- prompt integration with composed context

### Determinism testing
Test:
- same input -> same fragments
- same batch -> same composed context
- stable order across repeated runs

### Replay thinking
Test:
- serialized output remains readable
- restored state remains compatible

That test strategy matters as much as the code.

---

## 27. The value of expert-readable output

One subtle design benefit is this:

The models created here are not only machine-friendly.
They are also human-review-friendly.

That means when you inspect execution data later, you can understand:
- what was retrieved
- what was selected
- how it was composed
- what the prompt actually received

That is incredibly useful in production systems.

Especially when debugging AI behavior.

---

## 28. What to build next without breaking the foundation

The safest evolution path is:

### Next priority 1 — hybrid retrieval
Allow multiple strategies to contribute to one batch cleanly.

### Next priority 2 — ranking/scoring
Score normalized items before composition.

### Next priority 3 — richer fragment policies
Let the composer select fragments by strategy:
- top evidence
- grouped by type
- token budget aware

### Next priority 4 — token-aware compression
Add context compaction without losing traceability.

### Next priority 5 — more explicit config schemas
Make JSON definitions even more readable and validated.

These improvements fit naturally because the foundation is already properly separated.

---

## 29. Final memory anchors

Remember these 10 anchors.

1. **RAG is executed as DAG steps**
2. **retrieval and composition are separate**
3. **providers return raw data, then normalize**
4. **`RagNormalizedItem` is the universal retrieval unit**
5. **`RagRetrievalBatch` is the boundary between retrieval and composition**
6. **`RagContextFragment` is the atomic prompt context unit**
7. **`RagComposedContext<TContext>` is the final composed output**
8. **compact mode is short and efficient**
9. **expert mode is rich and traceable**
10. **the whole system is designed for determinism, replay, and distributed safety**

---

## 30. Final summary

What was built is much more than “RAG support”.

It is a **runtime-native retrieval and composition subsystem**.

Its main strengths are:

- clear separation of concerns
- deterministic data flow
- normalized boundaries
- prompt-ready structured composition
- DAG integration
- retry / recovery compatibility
- replay-friendly outputs
- future-ready support for hybrid retrieval and ranking

In short:

**this RAG implementation is not a prompt hack.**
It is a **serious execution-layer design**.

And that is exactly why it will still make sense when you read it again in 3 months.
