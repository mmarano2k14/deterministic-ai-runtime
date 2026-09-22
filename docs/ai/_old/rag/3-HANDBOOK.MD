# Deterministic RAG Runtime — Developer Handbook (V2)

## 1. Purpose of this handbook

This file is the practical companion to the architecture and deep implementation guides.

Its purpose is to help future-you answer these questions quickly:

- Where does `rag.retrieval` start and end?
- Where does `rag.compose` start and end?
- What should happen inside each step?
- What should NOT happen inside each step?
- Where do new providers fit?
- Where do new composers fit?
- What can safely evolve without breaking the runtime model?

This document is intentionally hands-on.

It is written as a **developer handbook**, not as a high-level concept note.

---

## 2. One-sentence mental model

The simplest correct mental model is:

> `rag.retrieval` gathers and normalizes data, `rag.compose` transforms normalized data into deterministic prompt context, and `ai.prompt` consumes that final context.

If you remember this line, you can reconstruct almost everything else.

---

## 3. Full execution story

Here is the execution story from start to finish.

```text
State / Execution Input
        ↓
rag.retrieval
        ↓
RagRetrievalBatch
        ↓
rag.compose
        ↓
RagComposedContext<TContext>
        ↓
ai.prompt
        ↓
LLM output / downstream step
```

This flow is deliberately split into **two RAG steps** before the prompt step.

That split is not accidental.

It gives:

- visibility
- traceability
- intermediate state inspection
- better retries
- safer recovery
- easier testing
- future ranking support
- future hybrid retrieval support

---

## 4. Step boundaries — the most important rule

This is one of the most important rules in the whole design.

## `rag.retrieval` must stop at normalized retrieval output

It should produce:

- `RagNormalizedItem[]`
- wrapped into `RagRetrievalBatch`

It should **not**:
- build prompt strings
- do LLM formatting
- decide final fragment presentation
- mix business prompt strategy with provider retrieval

## `rag.compose` must start from normalized retrieval output

It should:
- read a `RagRetrievalBatch`
- select / order / group content
- create `RagContextFragment[]`
- build `RagComposedContext<TContext>`

It should **not**:
- directly query providers
- know low-level provider details
- perform raw source-specific parsing if that should already be normalized

This boundary is what keeps the design clean.

---

## 5. Pseudo-code — `rag.retrieval`

The pseudo-code below is not tied to exact class names, but it captures the correct design behavior.

```csharp
public async Task<RagRetrievalBatch> ExecuteRetrievalAsync(RagExecutionContext context, RagRetrievalConfig config)
{
    // 1. Resolve retrieval strategy
    var retrieval = retrievalRegistry.Get(config.RetrievalKey);

    // 2. Resolve providers
    var providers = providerRegistry.GetMany(config.ProviderKeys);

    // 3. Execute provider logic through retrieval strategy
    var rawResults = await retrieval.ExecuteAsync(context, providers, config);

    // 4. Normalize all returned values
    var normalizedItems = normalizer.Normalize(rawResults);

    // 5. Enforce deterministic ordering
    var orderedItems = normalizedItems
        .OrderBy(x => x.ProviderKey)
        .ThenBy(x => x.SourceId)
        .ThenBy(x => x.Id)
        .ToList();

    // 6. Build batch
    return new RagRetrievalBatch
    {
        Items = orderedItems,
        Metadata = BuildMetadata(config, orderedItems)
    };
}
```

### What matters in this pseudo-code

The important thing is not the exact syntax.

The important thing is the sequence:

1. resolve
2. execute
3. normalize
4. order
5. package

That is the correct retrieval shape.

---

## 6. What retrieval strategy should do

A retrieval strategy defines **how retrieval is orchestrated**.

Examples:
- single provider retrieval
- multi-provider retrieval
- hybrid retrieval
- runtime-only retrieval
- SQL + vector retrieval

### Responsibilities of a retrieval strategy

- interpret config
- decide which providers to call
- aggregate provider outputs
- keep orchestration deterministic

### What it should not do

- final prompt formatting
- rich final context composition
- LLM-specific output shaping

A retrieval strategy is still part of **retrieval**, not composition.

---

## 7. What a provider should do

A provider defines **where data comes from**.

Examples:
- SQL query
- Redis lookup
- runtime state read
- API fetch
- vector index search

### Provider responsibilities

- read source-specific data
- return provider output in a consistent intermediate shape
- preserve source identity
- avoid leaking storage complexity upward

### Provider should not do

- build prompt context
- know final expert/compact strategy
- contain business prompt templates
- own final composition logic

If a provider starts building final prompt text, the separation is already breaking.

---

## 8. Pseudo-code — provider contract

```csharp
public interface IRagProvider
{
    Task<IReadOnlyList<object>> RetrieveAsync(
        RagExecutionContext context,
        RagProviderRequest request,
        CancellationToken cancellationToken);
}
```

This is conceptual.

In practice, the provider may return stronger types, but the design idea is:

- provider retrieves source-specific results
- another layer normalizes them

That normalization boundary is extremely important.

---

## 9. Why normalization deserves its own section

Normalization is one of the highest-value pieces in the design.

Without it, the composer would need to understand:
- SQL rows
- Redis hashes
- vector hits
- runtime objects
- API payloads

That would be a disaster over time.

Instead, the flow is:

```text
source-specific data
    ↓
normalize
    ↓
RagNormalizedItem
    ↓
compose
```

This means the composer only deals with one stable retrieval shape.

That is what makes the system extensible.

---

## 10. Pseudo-code — normalization

```csharp
public RagNormalizedItem Normalize(object raw)
{
    switch (raw)
    {
        case SqlCandidateRow sql:
            return new RagNormalizedItem
            {
                Id = sql.Id,
                SourceId = "sql:candidate",
                ProviderKey = "sql",
                Title = sql.Name,
                Text = sql.ProfileSummary,
                Metadata = new Dictionary<string, object>
                {
                    ["source"] = "sql",
                    ["entityType"] = "candidate"
                }
            };

        case VectorHit hit:
            return new RagNormalizedItem
            {
                Id = hit.DocumentId,
                SourceId = "vector:document",
                ProviderKey = "vector",
                Text = hit.Text,
                Score = hit.Score,
                Metadata = new Dictionary<string, object>
                {
                    ["source"] = "vector"
                }
            };

        default:
            throw new NotSupportedException($"Unsupported raw type: {raw.GetType().Name}");
    }
}
```

### Why this is good

Because all special-case logic is isolated at normalization time.

After that, the rest of the pipeline is cleaner.

---

## 11. What `RagNormalizedItem` should mean to you

When you see a `RagNormalizedItem`, you should think:

> this is a provider-independent knowledge unit that is safe to compose

That is the best memory shortcut.

It is no longer raw source data.
It is now runtime-usable retrieval data.

---

## 12. `RagRetrievalBatch` — practical role

`RagRetrievalBatch` is the output envelope of retrieval.

You should think of it as:

> the handoff object between retrieval and composition

It exists so composition does not need to know:
- how many providers were called
- which retrieval strategy ran
- how raw data was fetched

The composer only needs the normalized batch.

That is excellent separation of concerns.

---

## 13. Pseudo-code — `rag.compose`

```csharp
public RagComposedContext<TContext> ExecuteCompose<TContext>(
    RagRetrievalBatch batch,
    IRagComposer<TContext> composer,
    RagComposeConfig config)
{
    // 1. Validate input batch
    if (batch == null || batch.Items == null || batch.Items.Count == 0)
    {
        return composer.ComposeEmpty(config);
    }

    // 2. Apply any deterministic filtering
    var filtered = batch.Items
        .Where(x => IsAllowed(x, config))
        .ToList();

    // 3. Order items deterministically
    var ordered = filtered
        .OrderBy(x => x.Score ?? 0)
        .ThenBy(x => x.ProviderKey)
        .ThenBy(x => x.SourceId)
        .ThenBy(x => x.Id)
        .ToList();

    // 4. Convert items to fragments
    var fragments = composer.BuildFragments(ordered, config);

    // 5. Build final typed context
    var context = composer.BuildContext(fragments, config);

    // 6. Return composed result
    return new RagComposedContext<TContext>
    {
        Context = context,
        Fragments = fragments,
        Metadata = composer.BuildMetadata(ordered, fragments, config)
    };
}
```

### What matters here

Again, not the exact syntax.

The key flow is:

1. validate
2. filter
3. order
4. fragment
5. build typed context
6. return metadata

That is a correct composition pipeline.

---

## 14. What a composer should do

A composer defines **how normalized retrieval data becomes final context**.

This is where compact vs expert behavior belongs.

### Responsibilities

- decide which items matter
- determine ordering
- group by meaning
- create fragments
- build final typed context object
- preserve traceability

### A composer should not

- call database providers directly
- know low-level retrieval internals
- mix raw provider-specific parsing that should already be normalized

Think of the composer as a **context strategy component**.

---

## 15. Compact composer — what it really does

A compact composer should optimize for:

- fewer fragments
- short text
- essential evidence only
- low token cost
- stable, simple context

### Compact composer typical rules

- keep only top items
- merge similar evidence
- reduce metadata noise
- use short summaries
- limit fragment count aggressively

### Pseudo-code — compact composer

```csharp
public IReadOnlyList<RagContextFragment> BuildFragments(
    IReadOnlyList<RagNormalizedItem> items,
    RagComposeConfig config)
{
    var top = items.Take(config.MaxFragments ?? 4).ToList();

    var fragments = new List<RagContextFragment>();
    var order = 1;

    foreach (var item in top)
    {
        fragments.Add(new RagContextFragment
        {
            Order = order++,
            FragmentKind = "summary",
            SourceIds = new[] { item.SourceId },
            Text = BuildShortSummary(item)
        });
    }

    return fragments;
}
```

This is not final production code, but it captures the right idea.

---

## 16. Expert composer — what it really does

An expert composer should optimize for:

- richer structure
- grouped evidence
- explicit traceability
- stronger reasoning context
- better auditability

### Expert composer typical rules

- preserve more source distinctions
- classify fragments by semantic kind
- keep more evidence blocks
- include useful metadata
- separate profile / experience / requirement / gap / match fragments

### Pseudo-code — expert composer

```csharp
public IReadOnlyList<RagContextFragment> BuildFragments(
    IReadOnlyList<RagNormalizedItem> items,
    RagComposeConfig config)
{
    var fragments = new List<RagContextFragment>();
    var order = 1;

    foreach (var group in GroupItemsBySemanticKind(items))
    {
        foreach (var item in group.Items)
        {
            fragments.Add(new RagContextFragment
            {
                Order = order++,
                FragmentKind = group.Kind,
                SourceIds = new[] { item.SourceId },
                Text = BuildDetailedSummary(item)
            });
        }
    }

    return fragments;
}
```

The key idea is that expert mode is not just “longer”.
It is **more structured**.

---

## 17. `RagContextFragment` — practical developer meaning

Whenever you see a fragment, think:

> this is one exact block of context that may appear in the prompt

That is why the fragment type matters so much.

### Important fields

#### `Order`
The final prompt order must be stable.

#### `Text`
This is the actual content that downstream prompt steps see.

#### `SourceIds`
This preserves auditability.

#### `FragmentKind`
This helps with semantic grouping and better context reasoning.

---

## 18. `RagComposedContext<TContext>` — practical developer meaning

This is the final product of composition.

You should think of it as having 3 layers:

### Layer 1 — typed context
Good for prompt binding and strongly typed downstream logic.

### Layer 2 — fragments
Good for traceability and prompt debugging.

### Layer 3 — metadata
Good for diagnostics and future tuning.

This model is one of the reasons the design is strong.

It supports both:
- machine-friendly processing
- human-friendly inspection

---

## 19. JSON example — compact mode

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
        "retrievalKey": "hybrid-basic",
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

### How to read this

- retrieval uses runtime + SQL
- result becomes batch
- batch becomes compact context
- prompt consumes compact context only

This is efficient and cheap.

---

## 20. JSON example — expert mode

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
        "retrievalKey": "hybrid-expert",
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

### How to read this

- retrieval uses more providers
- composition is richer
- prompt gets both context and fragments
- better for deeper review flows

---

## 21. What can break determinism

This section is important for future maintenance.

The following things are dangerous:

### Unstable provider ordering
If a provider returns arbitrary order and you do not re-order, context may drift.

### Hidden time-based formatting
If text changes depending on current date/time inside composition, replay becomes weak.

### Random truncation
If item selection is not stable, prompt behavior may vary.

### Provider-specific composition leakage
If composers start treating each provider with ad hoc random rules, evolution becomes fragile.

### Mixing raw JSON everywhere
If too much downstream logic depends on untyped dynamic payloads, long-term safety drops.

---

## 22. Safe extension points

These are the best places to extend the subsystem.

### Add a new provider
Do this when you need a new data source.

Good examples:
- file provider
- document provider
- API provider
- vector engine provider

### Add a new retrieval strategy
Do this when orchestration changes.

Good examples:
- runtime-only retrieval
- SQL + vector hybrid
- score-first retrieval
- staged retrieval

### Add a new composer
Do this when the context strategy changes.

Good examples:
- compact composer
- expert composer
- token-budget composer
- audit composer

### Add new fragment policies
Do this when you want better final context control.

Good examples:
- group-by-kind
- top-evidence-first
- merge-similar-fragments
- preserve-source-blocks

---

## 23. Unsafe extension patterns

Avoid these if possible.

### Anti-pattern 1 — provider builds prompt text
This couples retrieval to prompt strategy.

### Anti-pattern 2 — retrieval step does final composition
This destroys the clean boundary with `rag.compose`.

### Anti-pattern 3 — composer directly queries sources
This mixes composition and retrieval concerns.

### Anti-pattern 4 — prompt step does missing composition work
This makes debugging much harder.

### Anti-pattern 5 — everything becomes arbitrary JSON
This destroys readability over time.

---

## 24. Suggested folder / code organization mindset

Even if actual folder names evolve, the organization should remain conceptually clear.

```text
Rag/
  Abstractions/
    Models/
    Contracts/
    Attributes/
  Discovery/
    Descriptors/
    Registry/
  Runtime/
    Retrieval/
    Composition/
    Steps/
  Providers/
    Sql/
    Redis/
    Runtime/
    Vector/
  Tests/
    Unit/
    Integration/
```

The point is not exact naming.

The point is preserving:
- contracts
- discovery
- execution
- providers
- tests

as distinct concerns.

---

## 25. Testing checklist for future-you

When adding anything new, test at these levels.

### Provider tests
- retrieves expected source data
- preserves source ids
- does not leak unstable ordering unexpectedly

### Normalization tests
- maps raw result to valid `RagNormalizedItem`
- keeps required metadata
- serializes safely

### Retrieval tests
- builds `RagRetrievalBatch`
- preserves deterministic order
- handles multiple providers safely

### Composer tests
- compact mode produces expected fragments
- expert mode produces expected fragments
- fragment order is stable
- metadata is correct

### Runtime integration tests
- `rag.retrieval` works as DAG step
- `rag.compose` works as DAG step
- `ai.prompt` can consume composed output

### Replay / persistence tests
- outputs serialize correctly
- restored values still make sense
- no unreadable blob regression appears

---

## 26. Example debugging workflow

Suppose the prompt output looks wrong.

The correct debugging path should be:

### Milestone 1
Inspect `rag.retrieval` output:
- did providers return the right knowledge?
- were items normalized correctly?
- was ordering stable?

### Milestone 2
Inspect `rag.compose` output:
- which fragments were created?
- which items were filtered out?
- did compact/expert strategy behave correctly?

### Milestone 3
Inspect `ai.prompt` input:
- what exact context was sent?
- were fragments included or not?
- was the template correct?

This split makes debugging much stronger than a one-shot RAG service.

---

## 27. Migration memory note

If in the future you forget why two steps exist instead of one, remember this:

The split was done to preserve:
- observability
- deterministic boundaries
- intermediate inspectability
- future ranking insertion
- safer retries
- better code clarity

If everything is re-merged into one giant step later, you lose many of those benefits.

---

## 28. The likely next evolution path

The current design naturally supports the next features.

### Next likely step 1 — scoring/ranking
Insert ranking after retrieval, before composition.

```text
retrieve -> normalize -> score -> compose -> prompt
```

### Next likely step 2 — hybrid strategies
Allow vector + SQL + runtime blending with clearer weighting.

### Next likely step 3 — token-budget composition
Make the composer select fragments with token awareness.

### Next likely step 4 — reusable expert schemas
Define stable composed context types for recurring scenarios.

That is why the foundation matters.

It is already shaped to accept these improvements.

---

## 29. Final memory anchors

If you only remember a few things later, remember these:

1. `rag.retrieval` fetches and normalizes
2. `rag.compose` turns normalized data into deterministic context
3. providers are source-specific
4. normalized items are provider-independent
5. fragments are prompt-ready context units
6. compact mode is minimal
7. expert mode is rich and traceable
8. the step split exists for debugging, safety, and extensibility
9. determinism requires stable ordering
10. this design is meant to survive replay, persistence, and future growth

---

## 30. Final conclusion

What was built is not just “RAG support”.

It is a proper runtime subsystem with clean boundaries.

That matters because it allows the system to grow into:
- hybrid retrieval
- ranking
- better composition policies
- token-aware orchestration
- stronger AI workflows

In practical developer terms:

- retrieval gathers knowledge
- composition shapes knowledge
- prompt consumes shaped knowledge

That is the right separation.
And that is the reason this design will still be understandable months later.
