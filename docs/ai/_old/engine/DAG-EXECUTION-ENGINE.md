# 🧠 DAG Execution Engine  
## Atomic Convergence, Step-Oriented Persistence & Distributed Deterministic Runtime

---

# 1. Executive Summary

This document describes a production-grade distributed DAG execution engine designed for:

- AI pipelines
- high concurrency systems
- deterministic execution
- fault-tolerant distributed environments

Core innovations:
- Step-scoped state (AiStepState)
- Atomic coordination (Redis + Lua)
- Deterministic convergence model
- Retry-aware scheduling

---

# 2. System Philosophy

> “The engine does not drive execution — it derives truth from state.”

Key mindset:
- No hidden state
- No race-based logic
- No global mutable orchestration flags

---

# 3. Full Architecture

```
                +----------------------+
                | AiDagExecutionEngine |
                +----------+-----------+
                           |
      +--------------------+--------------------+
      |                                         |
+-------------+                        +----------------------+
| StepSelector|                        | ConvergenceEvaluator |
+-------------+                        +----------------------+
      |                                         |
      +--------------------+--------------------+
                           |
                +----------v-----------+
                | AiExecutionState     |
                | + AiStepState[]      |
                +----------+-----------+
                           |
                +----------v-----------+
                | Redis ExecutionStore |
                | Lua Atomic Scripts   |
                +----------------------+
```

---

# 4. Data Model (Complete)

## AiExecutionState

- ExecutionId
- PipelineName
- Mode (Sequential / DAG)
- Steps (AiStepState[])
- Global Status
- Metadata

## AiStepState (Extended)

```json
{
  "StepName": "string",
  "Status": "None | Ready | Running | WaitingForRetry | Completed | Failed",
  "DependsOn": ["stepA"],

  "ClaimedBy": "worker-id",
  "ClaimToken": "token",
  "ClaimedAtUtc": "datetime",

  "StartedAtUtc": "datetime",
  "CompletedAtUtc": "datetime",
  "Duration": "timespan",

  "RetryCount": 0,
  "MaxRetries": 3,
  "NextRetryAtUtc": "datetime",
  "RetryDelay": "timespan",

  "RecoveryCount": 0,

  "Error": "string",
  "Result": {},

  "Version": 1
}
```

---

# 5. Step Lifecycle (Full)

```
None → Ready → Running → Completed
                  ↓
               Failed
                  ↓
        WaitingForRetry → Ready
```

---

# 6. Execution Algorithm (Pseudo Code)

```
while (!execution.IsTerminal)
{
    RecoverTimeouts()
    PromoteRetries()

    step = TryClaimStep()

    if (step != null)
    {
        Execute(step)
        PersistResult(step)
    }

    convergence = Evaluate()

    if (convergence.IsTerminal)
        TryFinalizeExecution()
}
```

---

# 7. Distributed Sequence (Detailed)

```
Worker A        Redis           Worker B

   |              |               |
   |-- Claim ---->|               |
   |<-- OK -------|               |
   |              |-- Claim ----> |
   |              |<-- FAIL ----- |
   |              |               |
   |-- Complete ->|               |
   |              |               |
```

---

# 8. Retry Engine (Advanced)

## Retry State Machine

```
Fail → WaitingForRetry → Ready → Retry
                    ↘ MaxRetries → Failed
```

## Retry Logic

| Condition | Action |
|----------|--------|
| RetryCount < MaxRetries | schedule retry |
| RetryCount >= MaxRetries | fail |

## Delay Strategies (Future)

- fixed delay
- exponential backoff
- jitter

---

# 9. Timeout Recovery

## Problem
Worker crashes while step is Running.

## Solution

```
if (Now - ClaimedAtUtc > Timeout)
    → MarkRequeuedAfterTimeout
```

## Result
- step returns to Ready
- RecoveryCount++
- system continues safely

---

# 10. Atomic Convergence

## Why needed

Without atomic finalization:
- double completion
- inconsistent states
- race corruption

## Mechanism

```
TryFinalizeExecution()
```

## Guarantees

| Property | Guarantee |
|----------|----------|
| Single writer | YES |
| No overwrite | YES |
| Deterministic | YES |

---

# 11. Convergence Matrix

| Steps State | Execution |
|------------|----------|
| All Completed | Completed |
| Any Failed | Failed |
| Running exists | Running |
| None ready | Waiting |

---

# 12. Real World Scenarios

## Scenario 1 — High concurrency

100 workers start same execution  
→ only one claims each step  

## Scenario 2 — Crash recovery

Worker dies mid-step  
→ timeout → recovery → resume  

## Scenario 3 — Retry explosion

Failing step loops  
→ bounded by MaxRetries  

## Scenario 4 — Split brain finalize

Multiple workers finalize  
→ atomic lock → one wins  

---

# 13. Anti-Patterns Avoided

❌ Global mutable execution flags  
❌ Non-atomic step assignment  
❌ Retry at global level  
❌ State mutation without ownership  

---

# 14. Comparison

| Feature | This Engine | Airflow | Temporal |
|--------|------------|--------|---------|
| Deterministic | ✅ | ❌ | ✅ |
| Atomic step claim | ✅ | ❌ | ✅ |
| Retry per step | ✅ | ⚠️ | ✅ |
| Distributed safety | ✅ | ⚠️ | ✅ |
| Lightweight | ✅ | ❌ | ❌ |

---

# 15. Guarantees

✔ Deterministic execution  
✔ Strong consistency  
✔ Distributed-safe  
✔ Retry correctness  
✔ No infinite loops  
✔ Crash recovery  

---

# 16. Future Extensions

- Distributed retry scheduler (Lua)
- DAG prioritization
- RAG integration
- Step caching
- Execution replay / audit

---

# 17. Final Insight

> Most DAG engines work in demos.  
> This one is designed for production chaos.

---

# 🚀 Closing

This system is not just an orchestrator.

It is a **deterministic distributed execution runtime for AI systems**.
