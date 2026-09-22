# 🧠 Adaptive Retention Architecture (Redis + Mongo)

## 📌 Introduction

This architecture represents a **deterministic AI execution runtime** where memory is not managed through heuristics, but through **controlled state evolution**.

Instead of relying on:
- vector search guesses
- heuristic memory trimming
- implicit garbage collection

The system introduces a **Retention Pipeline** that explicitly decides:

WHEN to act → Trigger  
WHAT to do → Decision layer  
HOW to do it → Retention policies  

The result is a system that is:
- predictable
- testable
- lossless
- production-safe

---

## 🔧 Retention + Storage Flow

```text

                ┌────────────────────────────────────┐
                │   DAG Deterministic State Machine  │
                │                                    │
                │      (Steps in hot memory)         │
                └────────────────────────────────────┘
                                 │
                                 ▼
                    ┌──────────────────────────┐
                    │  Retention Trigger       │
                    │  (Threshold checks)      │
                    │                          │
                    │ - MaxStepsInState        │
                    │ - MaxInlinePayloadBytes  │
                    └────────────┬─────────────┘
                                 │
                                 ▼
                    ┌──────────────────────────┐
                    │ Decision Service         │
                    │ (Orchestrator)           │
                    └────────────┬─────────────┘
                                 │
                                 ▼
                    ┌──────────────────────────┐
                    │ Decision Evaluator       │
                    │ (Runs all policies)      │
                    └────────────┬─────────────┘
                                 │
        ┌────────────────────────┼────────────────────────┐
        ▼                        ▼                        ▼
┌──────────────────┐   ┌──────────────────┐   ┌──────────────────┐
│ Size Policy      │   │ (Future) Age     │   │ (Future) Usage   │
│ (current)        │   │ Policy           │   │ Policy           │
│                  │   │                  │   │                  │
│ payload > limit  │   │ old steps        │   │ low value steps  │
│ → COMPACT        │   │ → EVICT          │   │ → EVICT          │
└─────────┬────────┘   └─────────┬────────┘   └─────────┬────────┘
          │                      │                      │
          └──────────────┬───────┴──────────────┬───────┘
                         ▼                      ▼
              ┌────────────────────────────────────┐
              │  Retention Plan                    │
              │                                    │
              │ StepsToCompact                     │
              │ StepsToEvict                       │
              └────────────────┬───────────────────┘
                               │
                               ▼
                ┌────────────────────────────────┐
                │ Retention Service              │
                │ (Execution phase)              │
                └──────────────┬─────────────────┘
                               │
          ┌────────────────────┼────────────────────┐
          ▼                    ▼                    ▼

 ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────────┐
 │ COMPACT          │  │ EVICT            │  │ HYBRID               │
 │                  │  │                  │  │                      │
 │ Inline → external│  │ Persist payload  │  │ 1. Compact           │
 │ keep metadata    │  │ Write index      │  │ 2. Evict (old only)  │
 │                  │  │ Remove from state│  │                      │
 └─────────┬────────┘  └─────────┬────────┘  └─────────┬────────────┘
           │                     │                     │
           ▼                     ▼                     ▼

    ┌────────────────────────────────────────────────────────┐
    │ Updated AiExecutionState (bounded hot state)           │
    │                                                        │
    │ - Small / recent steps in memory                       │
    │ - Large / old steps externalized                       │
    └────────────────────────────────────────────────────────┘
                               │
                               ▼
                ┌────────────────────────────────┐
                │ Step Resolver                  │
                │ (rehydration layer)            │
                │                                │
                │ Hot state → Cache → Payload    │
                └────────────────────────────────┘
```
## 🧠 How It Works

### 1. Hot State (Redis)

- Active execution state lives in Redis  
- Steps are kept **bounded**  
- Only relevant / recent data stays in memory  

---

### 2. Adaptive Trigger

The system monitors:

- MaxStepsInState  
- MaxInlinePayloadBytes  

This determines **when retention must run**

---

### 3. Decision Layer (Core Innovation)

Instead of hardcoded rules, decisions are delegated to policies:

- Size-based (current)  
- Age-based (future)  
- Usage-based (future)  
- Supersession (future)  

This allows memory decisions to be composable and extensible.

---

### 4. Retention Execution

The system applies:

#### COMPACT
- Moves large payloads out of Redis  
- Keeps lightweight metadata inline  

#### EVICT
- Persists full step externally  
- Writes archive index  
- Removes from hot state  

#### HYBRID
- Compact first  
- Evict oldest / less relevant steps  

---

### 5. Storage Model

#### Redis (Hot Layer)
- Fast access  
- Active execution  
- Bounded memory  

#### Mongo (Cold Layer)

Payload Store:
- Full serialized step data  

Index Store:
- Pointer to payload (ExecutionId + StepName + ArtifactId)  

---

### 6. Resolver (Rehydration Layer)

Steps are resolved through:

1. Redis (hot state)  
2. Local cache  
3. Mongo index  
4. Mongo payload  

This ensures:

Evict is not deletion  
Evict is controlled relocation to cold storage  

---

### 7. Replay (Deterministic State Restoration)

The system supports replaying executions from persisted state.

After retention and eviction:

- Execution state is reloaded from Redis / Mongo  
- Missing steps are resolved through the resolver  
- Archived payloads are transparently rehydrated  
- Execution can continue or be inspected safely  

Replay flow:

1. Load execution state (Redis / Mongo)  
2. Detect missing steps (evicted from hot state)  
3. Resolve steps via index + payload store  
4. Reconstruct full step state  
5. Continue execution or perform diagnostics  

This guarantees:

- Full state reconstruction after retention  
- Deterministic replay behavior  
- Safe debugging and observability  
- No data loss despite aggressive memory optimization 

---

## 🔥 Key Advantages

### Deterministic Memory Management
No heuristics. Every decision is explicit and testable.

---

### Logically Lossless System
Data is never lost:
- persisted before removal  
- always resolvable  

---

### Bounded Memory
Hot state is always controlled and predictable.

---

### Production Safety
Validated with:
- idempotence tests  
- replay tests  
- resolver validation  
- hybrid retention scenarios  

---

### Extensible by Design
New policies can be added without changing the core:

- Temporal decay  
- Usage weighting  
- Supersession (memory evolution)  
- RAG-aware retention  

---

## 🚀 Why This Matters

Most AI systems treat memory as:

retrieve → guess → hope it's correct  

This system treats memory as:

state → evolve → control → guarantee  

---

## 🧠 Conclusion

This architecture transforms retention from a simple optimization into a **core runtime capability**.

It enables:

- deterministic AI execution  
- scalable memory control  
- safe distributed processing  
- future intelligent memory evolution  

---

Redis acts as the active brain  
Mongo acts as long-term memory  
Retention keeps them in sync