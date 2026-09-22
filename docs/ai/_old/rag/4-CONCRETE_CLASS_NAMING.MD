# Deterministic RAG Runtime — Internal Repo Guide (Concrete Classes & Naming)

## 1. Why this file exists

This file is intentionally more concrete than the architecture note and the developer handbook.

Its goal is to reconnect future-you with the **actual repo mental model**:

- the names you used
- the runtime shape you built
- the boundaries between abstractions
- where RAG fits in the existing deterministic engine
- what likely belongs where in the codebase
- how to reason about the current implementation without starting from zero again

This is meant to be a practical internal note for the repository.

It is written for the codebase that already contains:

- deterministic DAG execution
- retry
- recovery
- replay
- snapshot persistence
- prompt orchestration
- provider discovery / registry patterns
- the new RAG foundation

---

## 2. The most important repo-level idea

The RAG subsystem was not built as an isolated helper library.

It was designed to follow the same engineering principles already used in the runtime:

- strong abstractions
- deterministic execution
- registry-driven resolution
- provider-agnostic extension
- replay compatibility
- serializable runtime models
- step-based orchestration

So the RAG work should be understood as **an extension of the runtime architecture**, not as a separate mini-project.

---

## 3. Existing runtime foundation that RAG plugs into

Before RAG, the runtime already had these core ideas:

### 3.1 Pipeline model split
There is a separation between declarative pipeline definitions and runtime-resolved forms.

Examples already established in the repo mental model include:
- `AiPipelineDefinition`
- `ResolvedAiPipeline`
- `ResolvedAiPipelineStep`

That matters because RAG follows the same philosophy:
configuration is declarative, execution is resolved.

### 3.2 Step execution model
Execution happens as runtime steps inside the deterministic engine.

This already existed for things like:
- prompt steps
- decision-like routing
- DAG-managed execution

RAG follows that same step model.

### 3.3 Step state and execution truth
The runtime already treats step state as a first-class truth source.

Examples from the engine:
- `AiStepState`
- `AiStepResult`
- `AiStepExecutionStatus`

This matters because RAG outputs should remain compatible with:
- step result persistence
- serialization
- replay inspection
- deterministic convergence

### 3.4 Multi-worker deterministic execution
The engine already has:
- claim logic
- Redis/Lua coordination
- retry state
- recovery behavior
- terminal convergence

RAG inherits those guarantees because it runs as steps.

---

## 4. Where RAG fits in that existing model

Conceptually, the RAG flow should be seen as:

```text
state / execution context
    ↓
rag.retrieval
    ↓
rag.compose
    ↓
ai.prompt
```

This means RAG is not “one magic service”.

It is a sequence of runtime-compatible responsibilities.

The most important practical split is:

- `rag.retrieval` = fetch + normalize
- `rag.compose` = structure + format + order
- `ai.prompt` = consume final context

That split is the main design anchor.

---

## 5. Concrete class names you should remember

This section lists the names that matter most from the current implementation direction.

## 5.1 Execution context

### `RagExecutionContext`
The generic runtime input object for RAG operations.

Use this when:
- dynamic execution is enough
- you want simple orchestration
- you do not need a strongly typed snapshot

### `RagExecutionContext<TContextSnapshot>`
The typed version.

Use this when:
- you want stronger context structure
- you care about long-term replay clarity
- the retrieval/composition flow benefits from typed snapshot data

### Why this matters
This mirrors the broader runtime philosophy:
keep things compatible with persistence and replay, not just immediate execution.

---

## 5.2 Retrieval output models

### `RagNormalizedItem`
This is one of the most important classes in the subsystem.

It is the universal shape of retrieved knowledge after normalization.

Future-you should remember this exact sentence:

> `RagNormalizedItem` is the provider-independent unit of retrieved data.

Without it:
- providers stay inconsistent
- composers become messy
- hybrid retrieval becomes fragile

### `RagRetrievalBatch`
This is the envelope returned by retrieval.

It is the clean handoff object between:
- retrieval
- composition

Future-you should remember this sentence too:

> `RagRetrievalBatch` is the boundary object between raw retrieval and context composition.

---

## 5.3 Composition output models

### `RagContextFragment`
This is the atomic prompt-context block.

Important fields conceptually include:
- `Order`
- `Text`
- `FragmentKind`
- `SourceIds`

Future-you should remember:

> `RagContextFragment` is the smallest deterministic prompt-ready context unit.

### `RagComposedContext<TContext>`
This is the final output of composition.

Conceptually it contains:
- `Context`
- `Fragments`
- `Metadata`

Future-you should remember:

> `RagComposedContext<TContext>` is the final structured context product consumed by prompt steps or downstream logic.

---

## 5.4 Structured context example

You already encountered a concrete structured context shape during testing:

### `RagStructuredContext`

This matters because it shows the design is not just about text blobs.

It is about building a strongly structured final context model.

One important memory from the test work:

- you hit a case where the result type became `JsonElement`
- but the test expected `RagStructuredContext`

That is a valuable implementation lesson:
**typed context restoration and normalization matter a lot**.

This is one of the reasons the normalizer and structured remapping are important.

---

## 6. Why the normalizer became important

A very practical problem appeared during testing:

the runtime could store or surface structured data as generic JSON-like values instead of the expected strong type.

Example symptom:
- expected `RagStructuredContext`
- actual value ended up as `JsonElement`

That problem is not cosmetic.

It affects:
- test correctness
- downstream type safety
- replay readability
- long-term maintainability

So the normalizer/remapper layer exists for a serious reason:
to prevent structured runtime outputs from degrading into “opaque JSON blobs”.

This is one of the most important implementation lessons from the RAG work.

---

## 7. What the RAG normalizer/remapper conceptually does

Even if class names evolve, the core responsibility is stable:

### Responsibilities
- inspect result payloads
- detect JSON-like dynamic structures
- restore them into runtime-usable CLR shapes
- preserve deterministic structure
- keep final output compatible with strong types

### Why this matters
Because once RAG composition returns a structured context object, the runtime should preserve that shape as much as possible.

Otherwise future code has to constantly deal with:
- `JsonElement`
- dynamic traversal
- fragile casting
- unclear replay output

That is exactly the kind of technical debt the runtime architecture is trying to avoid.

---

## 8. How the RAG foundation mirrors the prompt/provider architecture

You already had a provider/resolution pattern in the prompt subsystem.

Examples from the repo mental model include:
- `IAiPromptExecutor`
- provider discovery
- provider resolver patterns
- registry-driven runtime resolution

RAG follows a very similar philosophy:

- discovery
- descriptor
- registry
- runtime resolution
- step execution

This is good because the repo stays conceptually consistent.

Future-you should remember:

> RAG was designed to feel native to the engine, not bolted on beside it.

---

## 9. Discovery / descriptor / registry mental model

The RAG subsystem follows a pattern like this:

```text
attribute
   ↓
discovery service
   ↓
descriptor
   ↓
registry
   ↓
runtime resolution
```

This matters because it avoids hardcoded switching logic.

### The practical benefits
- new providers can be added safely
- new composers can be added safely
- runtime lookup remains fast
- reflection stays out of hot execution paths
- uniqueness can be validated at startup

This is one of the strongest architectural choices in the system.

---

## 10. Concrete responsibilities by area

## 10.1 Retrieval area
This area should own:
- retrieval orchestration
- provider calling
- normalization
- deterministic ordering before packaging
- returning `RagRetrievalBatch`

It should **not** own:
- final prompt formatting
- expert prompt layout decisions
- LLM-oriented context assembly

## 10.2 Provider area
This area should own:
- source-specific fetching
- source mapping
- preservation of source identity
- source-specific raw result handling

It should **not** own:
- final fragment building
- prompt context structure
- cross-provider final composition

## 10.3 Composition area
This area should own:
- transforming normalized items into fragments
- stable ordering
- grouping and summarization strategy
- final `RagComposedContext<TContext>` creation

It should **not** own:
- raw DB or Redis calls
- provider-specific retrieval concerns
- low-level retrieval orchestration

---

## 11. Concrete meaning of compact mode in repo terms

Compact mode should be remembered as:

> the low-token, low-noise composition strategy

It is useful when:
- you want fast prompt enrichment
- the prompt does not need full evidence detail
- you want cheaper or smaller prompt context
- you are doing shortlisting or first-pass evaluation

### Compact mode should typically do
- keep fewer fragments
- prefer summaries
- remove unnecessary metadata noise
- preserve only essential source references
- produce a small, stable context object

### Compact mode should not do
- create giant evidence trees
- preserve every detail by default
- behave differently on each run

---

## 12. Concrete meaning of expert mode in repo terms

Expert mode should be remembered as:

> the rich, traceable, structured composition strategy

It is useful when:
- you want deeper reasoning support
- you want auditability
- you want stronger review context
- you want categorized evidence

### Expert mode should typically do
- keep more fragments
- separate fragments by semantic kind
- preserve more traceability
- include more contextual metadata
- produce a richer structured final context

### Expert mode should not do
- collapse everything into one short summary
- lose important source distinctions
- become unstable in ordering

---

## 13. Concrete JSON example — compact mode

This is the kind of JSON you should remember conceptually for compact composition.

```json
{
  "pipelineId": "rag-compact-pipeline",
  "executionMode": "Dag",
  "steps": [
    {
      "id": "retrieve",
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
      "id": "compose",
      "stepType": "rag.compose",
      "inputs": {
        "batch": "steps.retrieve.result.data"
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
        "context": "steps.compose.result.data.context"
      },
      "config": {
        "provider": "openai",
        "model": "gpt-4.1-mini",
        "responseFormat": "json",
        "template": "Evaluate this compact context: {{context}}"
      }
    }
  ]
}
```

### Repo-level reading of this JSON
- `rag.retrieval` produces a normalized batch
- `rag.compose` produces a compact structured context
- `ai.prompt` consumes the result

That is the clean runtime story.

---

## 14. Concrete JSON example — expert mode

```json
{
  "pipelineId": "rag-expert-pipeline",
  "executionMode": "Dag",
  "steps": [
    {
      "id": "retrieve",
      "stepType": "rag.retrieval",
      "inputs": {
        "query": "state.query",
        "candidate": "state.candidate",
        "job": "state.job"
      },
      "config": {
        "retrievalKey": "hybrid-search",
        "providerKeys": ["runtime", "sql", "vector"],
        "mode": "expert"
      }
    },
    {
      "id": "compose",
      "stepType": "rag.compose",
      "inputs": {
        "batch": "steps.retrieve.result.data"
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
        "context": "steps.compose.result.data.context",
        "fragments": "steps.compose.result.data.fragments"
      },
      "config": {
        "provider": "openai",
        "model": "gpt-4.1-mini",
        "responseFormat": "json",
        "template": "Using this expert context and fragments, evaluate fit, strengths, gaps, and concerns. Context: {{context}} Fragments: {{fragments}}"
      }
    }
  ]
}
```

### Repo-level reading of this JSON
- more providers
- richer composition
- fragments stay visible
- prompt step gets stronger structured evidence

This is a natural evolution path for deeper evaluation pipelines.

---

## 15. Pseudo-code with repo naming mindset — retrieval step

This pseudo-code is intentionally close to the way the repo thinks, not to any exact file implementation.

```csharp
public async Task<RagRetrievalBatch> ExecuteAsync(
    RagExecutionContext context,
    RagRetrievalStepConfig config,
    CancellationToken cancellationToken)
{
    var retrieval = _retrievalRegistry.Get(config.RetrievalKey);
    var providers = _providerRegistry.GetMany(config.ProviderKeys);

    var rawResults = await retrieval.ExecuteAsync(context, providers, config, cancellationToken);

    var normalizedItems = _normalizer.NormalizeMany(rawResults);

    var orderedItems = normalizedItems
        .OrderBy(x => x.ProviderKey)
        .ThenBy(x => x.SourceId)
        .ThenBy(x => x.Id)
        .ToList();

    return new RagRetrievalBatch
    {
        Items = orderedItems,
        Metadata = BuildBatchMetadata(config, orderedItems)
    };
}
```

### What future-you must remember
The important sequence is:
- resolve
- execute
- normalize
- order
- package

If that sequence is preserved, the design remains healthy.

---

## 16. Pseudo-code with repo naming mindset — compose step

```csharp
public RagComposedContext<RagStructuredContext> Execute(
    RagRetrievalBatch batch,
    RagComposeStepConfig config)
{
    var composer = _composerRegistry.Get<RagStructuredContext>(config.ComposerKey);

    var filteredItems = ApplyDeterministicFiltering(batch.Items, config);
    var orderedItems = ApplyDeterministicOrdering(filteredItems, config);

    var fragments = composer.BuildFragments(orderedItems, config);
    var context = composer.BuildContext(fragments, config);
    var metadata = composer.BuildMetadata(orderedItems, fragments, config);

    return new RagComposedContext<RagStructuredContext>
    {
        Context = context,
        Fragments = fragments,
        Metadata = metadata
    };
}
```

### What future-you must remember
Composition starts from the batch, not from providers.

That is one of the most important boundaries in the entire subsystem.

---

## 17. How this integrates with `AiStepResult`

The runtime already uses step results to carry outputs.

That means RAG outputs should be thought of as values flowing through something conceptually like:

- `AiStepResult.Data`
- `AiStepState.Result`
- bindings such as `steps.step-id.result.data`

This matters because the output of:
- `rag.retrieval`
- `rag.compose`

must remain usable by:
- downstream prompt steps
- runtime bindings
- persistence
- replay

That is why serializable structured models matter so much.

---

## 18. The test lesson you should not forget

One of the strongest lessons from the RAG test phase is:

> if you do not preserve structured output types carefully, they collapse into generic JSON shapes and become much harder to use.

That lesson should influence future design decisions.

Whenever you add a new structured RAG model, ask:

- will this survive serialization clearly?
- will replay show a readable object?
- will this come back as a strong type or a `JsonElement`?
- do I need a remapper / normalizer for this type?

These questions matter a lot.

---

## 19. Safe extension points in the current repo style

## Add a provider
Do this when the data source changes.

Examples:
- SQL provider
- runtime provider
- vector provider
- file provider
- API provider

## Add a retrieval strategy
Do this when orchestration changes.

Examples:
- single-source retrieval
- hybrid retrieval
- staged retrieval
- weighted retrieval

## Add a composer
Do this when context strategy changes.

Examples:
- compact composer
- expert composer
- audit composer
- token-budget composer

## Add a typed context
Do this when a repeated scenario needs stronger structure.

Examples:
- `RagStructuredContext`
- job matching context
- review context
- risk analysis context

This is the healthy way to grow the subsystem.

---

## 20. Dangerous anti-patterns in this repo

These are the kinds of mistakes that would weaken the design.

### Anti-pattern 1
A provider directly builds prompt strings.

### Anti-pattern 2
`rag.retrieval` starts doing final prompt composition.

### Anti-pattern 3
`rag.compose` starts performing database retrieval.

### Anti-pattern 4
Everything is stored and restored as weak JSON without reconstruction.

### Anti-pattern 5
Ordering becomes implicit or provider-dependent.

### Anti-pattern 6
Different modes produce unstable fragment layout across identical runs.

If any of these happen, determinism and maintainability start to degrade.

---

## 21. How to debug the subsystem in the repo

If something looks wrong, inspect it in this order:

### 1. Retrieval output
Check:
- provider selection
- raw retrieval success
- normalized item correctness
- deterministic ordering

### 2. Batch boundary
Check:
- `RagRetrievalBatch`
- item count
- metadata
- source identity preservation

### 3. Composition output
Check:
- fragment count
- fragment kinds
- fragment ordering
- final typed context shape

### 4. Step result payload
Check:
- what was stored into runtime step result
- whether shape stayed strong or degraded to JSON
- whether downstream bindings match expected paths

### 5. Prompt input
Check:
- exact bound `context`
- exact bound `fragments`
- template correctness

This debugging order is extremely useful and should save time later.

---

## 22. What a likely next class evolution looks like

Given the current direction, the most natural future evolution would be:

### Retrieval side
- hybrid retrieval config model
- ranking/scoring model
- normalized scoring enrichment

### Composition side
- token-aware fragment selector
- explicit grouping policies
- specialized structured contexts per scenario

### Runtime side
- stronger result remapping hooks
- explicit structured output restoration
- more reusable bindings for composed context

This means the current foundation is already pointing in the right direction.

---

## 23. Final concrete memory anchors

If you read only one section months later, read this one.

1. `RagExecutionContext` is the runtime input container  
2. `RagExecutionContext<TContextSnapshot>` is the typed version  
3. `RagNormalizedItem` is the provider-independent knowledge unit  
4. `RagRetrievalBatch` is the handoff object from retrieval to composition  
5. `RagContextFragment` is the atomic prompt-ready context unit  
6. `RagComposedContext<TContext>` is the final structured composition result  
7. `RagStructuredContext` is an example of a real typed context you already used  
8. structured outputs must not degrade into `JsonElement` silently  
9. compact mode is short and efficient  
10. expert mode is rich and traceable  
11. `rag.retrieval` fetches + normalizes  
12. `rag.compose` structures + orders + shapes  
13. `ai.prompt` consumes the final result  
14. the whole design exists to stay deterministic, replay-safe, and extensible  

---

## 24. Final summary

The RAG work is best understood as:

- a natural extension of the deterministic engine
- a typed retrieval/composition subsystem
- a replay-aware structured context pipeline
- a provider-agnostic architecture following the same design discipline as the rest of the runtime

The most important implementation lesson from this phase is simple:

**strong boundaries and strong models matter.**

That means:
- normalize early
- compose explicitly
- preserve structure
- keep ordering deterministic
- do not let final outputs collapse into unreadable dynamic JSON

If future-you remembers that, the rest of the subsystem will make sense again quickly.
