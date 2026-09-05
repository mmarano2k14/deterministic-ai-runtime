**# AI Runtime Analysis Demo**

> A focused interactive application built ****on top of the Deterministic AI Runtime**** to demonstrate runtime observability, RBAC context rotation, in-flight coordination, AI-assisted investigation, policy-gated execution, explicit human approval, durable Child DAGs, and deterministic verification.

> ****Important:**** this demo is ****not the runtime itself****. It consumes the Deterministic AI Runtime through its public primitives and extension points.

**---**

**## Overview**

The demo brings several distributed-runtime concerns into one observable workflow:

```text

Observe

  ↓

Analyze

  ↓

AI proposes

  ↓

Deterministic policy gates

  ↓

Human approves / rejects

  ↓

Runtime executes durably

  ↓

Evidence verifies

  ↓

Re-analyze

  ↓

Stop OR propose another experiment

```

It deliberately exercises:

- RBAC and deterministic access context;

- atomic `ContextKey` rotation;

- request continuity while work remains in flight;

- Redis/Lua-backed atomic coordination;

- concurrent burst and wave scenarios;

- realtime metrics, logs, and runtime evidence;

- AI-assisted analysis of bounded evidence;

- pluggable runtime steps and policies;

- AI-generated follow-up proposals;

- deterministic policy eligibility decisions;

- explicit human approval or rejection;

- durable Child DAG execution;

- deterministic post-execution verification;

- bounded iterative investigation.

The goal is not simply to show successful HTTP calls. The goal is to make normally invisible distributed execution behavior ****observable, explainable, controllable, and verifiable****.

**---**

**## Demo vs. Runtime**

This distinction is fundamental.

**### The demo application owns**

```text

UI and visualization

traffic/scenario generation

AI analysis UX

bounded evidence presentation

policy workflow presentation

human approval UX

investigation controls

```

**### The Deterministic AI Runtime owns**

```text

durable execution

execution lifecycle

DAG / Child DAG semantics

runtime state

distributed coordination

Redis-backed atomic transitions / queues / claims

MongoDB-backed durable persistence

recovery

verification primitives

public execution and extension points

```

The demo therefore ****uses the runtime; it does not reimplement it****.

It demonstrates how the runtime can be extended through ****pluggable steps and policies****, durable Child DAGs, AI-generated proposals, deterministic policy decisions, and explicit human approval — while the runtime remains responsible for durable execution, lifecycle, recovery, and verification.

**---**

**## Decision Model**

The demo intentionally separates analysis from authority.

```text

AI analyzes and proposes.

Policy decides whether the proposal may proceed.

Human approves or rejects execution.

Runtime executes durably.

Evidence verifies the result.

```

AI does ****not**** own execution authority.

A generated proposal must pass deterministic policy evaluation first. If it is eligible, a human must explicitly approve it before execution can continue.

**---**

**## Investigation Modes**

After execution and verification, the AI can re-analyze the new evidence.

**### Stop when conclusion is strong**

The default mode.

```text

Evidence

  ↓

AI re-analysis

  ↓

Conclusion sufficiently strong

  ↓

STOP

```

No additional experiment is created once the available evidence is conclusive.

**### Continue with another useful experiment**

The AI actively searches for one ****materially different**** bounded follow-up experiment.

```text

Evidence

  ↓

AI re-analysis

  ↓

New materially different proposal

  ↓

Deterministic policy

  ↓

Human approval

  ↓

Next durable Child DAG

```

Continuation never means automatic execution.

Every new experiment must pass through ****policy and explicit human approval again**** before the next durable Child DAG is created.

The demo also applies a deterministic maximum investigation depth to keep the workflow bounded.

**---**

**# What the Demo Exercises**

**## 1. RBAC and Access Context**

The sample backend maintains an authorization/execution context associated with the current demo user.

A rotating context key is sent by the client using:

```http

X-Access-Context: <context-key>

```

The client always adopts the latest valid key returned by the backend.

This allows the demo to visualize authorization and execution state as the active context evolves.

**---**

**## 2. Atomic ContextKey Rotation**

Context rotation is coordinated atomically rather than treated as a simple client-side token replacement.

```text

ContextKey A

    │

    ├── Request A

    ├── Request B

    └── Request C

          │

          ▼

   Atomic coordination

          │

          ▼

ContextKey B

```

The demo exercises:

- concurrent key transitions;

- bounded reuse;

- stale-key behavior;

- race conditions;

- overlap windows;

- rotation under real request load.

**---**

**## 3. In-Flight Request Continuity**

Requests can remain active while the surrounding authorization context changes.

The demo exposes controls for generating real concurrency so this behavior can be observed rather than inferred.

```text

Request 1 ─┐

Request 2 ─┼── shared execution/access context

Request 3 ─┘

             │

             ▼

      in-flight coordination

             │

             ▼

      deterministic outcome

```

This makes it possible to inspect successful continuity, bounded concurrency, and intentional rejection behavior under contention.

**---**

**## 4. Redis / Lua Atomic Coordination**

Redis is used by the sample/runtime integration for distributed coordination.

Lua-backed atomic operations are used where multi-step state transitions must remain indivisible from the perspective of concurrent callers.

The demo makes this behavior visible through traffic, counters, context changes, and realtime evidence.

Typical scenarios include:

- context rotation;

- in-flight tracking;

- bounded concurrent use;

- distributed state transitions.

**---**

**## 5. Realtime Runtime Evidence**

The client receives realtime events from the backend and turns them into an engineering console.

The Live Log surface separates:

```text

HTTP

  ├── HTTP

  ├── HTTP Error

  └── Context

Realtime

  ├── Realtime

  ├── ContextKey

  ├── Runtime Engine

  └── AI

```

The client also keeps cumulative filter counters independently from the bounded retained log window. A noisy ring buffer can therefore remain bounded without making historical event counters appear to move backwards.

**---**

**## 6. Traffic Scenarios**

The demo can drive controlled traffic patterns against the sample API.

Examples include:

- bursts;

- maintained concurrency;

- wave batches;

- staggered waves.

The purpose is to create repeatable conditions that can later be analyzed and compared.

Runtime metrics include request outcomes, in-flight work, throughput, latency percentiles, and execution progress.

**---**

**## 7. AI Runtime Analysis**

The AI analysis layer receives a ****bounded evidence snapshot****, not unrestricted application state.

It can be used to:

- analyze current execution behavior;

- explain failures;

- identify anomalies;

- suggest a useful next experiment.

The structured result includes a finding, severity, confidence, observations, evidence, and — when appropriate — a proposed scenario.

The AI is an analysis and proposal layer. It is ****not**** the execution engine.

**---**

**## 8. Pluggable Steps and Policies**

The demo uses the runtime through public extension points.

That includes demo-specific steps and deterministic policies without modifying the runtime core to implement sample behavior.

```text

Demo

  │

  ├── pluggable steps

  ├── pluggable policies

  └── scenario-specific adapters

          │

          ▼

Deterministic AI Runtime

```

This is intentional.

The sample demonstrates how an application can host and extend the runtime rather than fork or reproduce its execution semantics.

**---**

**## 9. Human Approval**

When the AI proposes an executable follow-up:

```text

AI proposal

    ↓

Policy evaluation

    ↓

Eligible?

    ├── No  → do not proceed

    └── Yes

          ↓

     Human approval

       ├── Reject

       └── Approve

              ↓

        durable execution

```

Approval resumes the durable execution chain. The browser owns the human interaction and demo-specific launch UX; durable workflow state remains runtime-backed.

**---**

**## 10. Durable Child DAG Investigation**

An approved follow-up can create ****one durable Child DAG****.

The product invariant is:

```text

ONE HUMAN APPROVAL

        ↓

ONE DURABLE CHILD

```

Additional depth requires:

```text

Child evidence

   ↓

AI re-analysis

   ↓

new proposal

   ↓

policy

   ↓

human approval

   ↓

next durable Child

```

This produces a human-governed investigation tree instead of uncontrolled autonomous recursion.

**---**

**## 11. Deterministic Verification**

After a scenario executes, the demo verifies factual outcomes separately from the AI narrative.

Examples include:

- planned request count vs. completed count;

- successful vs. failed outcomes;

- residual in-flight work;

- latency changes;

- execution completion;

- Child relation / continuation state.

The AI can interpret the evidence, but deterministic verification remains a separate concern.

**---**

**# Runtime State, Coordination, and Persistence**

The demo does not keep durable execution semantics in the browser.

The underlying runtime uses ****Redis and MongoDB for different responsibilities****.

**## Redis**

Redis is used for distributed runtime coordination and hot-path state transitions.

Typical responsibilities include:

- atomic Lua-backed transitions;

- distributed claims and ownership coordination;

- shared queue / dispatch coordination;

- concurrency and in-flight coordination;

- fast runtime state used by distributed workers;

- synchronization across runtime instances.

Conceptually:

```text

Runtime Instance A ─┐

Runtime Instance B ─┼──► Redis

Runtime Instance C ─┘       │

                            ├── atomic Lua transitions

                            ├── claims / ownership

                            ├── shared queues

                            └── coordination state

```

The important property is that distributed state changes that must be atomic are decided at the Redis boundary rather than by trusting a stale client-side or worker-side view.

**## MongoDB**

MongoDB provides the durable document-oriented persistence layer used by the runtime and its execution model.

Typical responsibilities include durable storage for:

- execution/runtime documents;

- workflow and DAG-related persisted state;

- long-lived execution evidence;

- state required beyond the lifetime of an individual process;

- persisted information used by recovery, inspection, and replay-oriented workflows.

Conceptually:

```text

Durable Execution

       │

       ▼

    MongoDB

       │

       ├── persisted execution state

       ├── durable runtime documents

       ├── long-lived evidence

       └── recovery / replay inputs

```

Redis and MongoDB therefore complement each other:

```text

Redis

  = distributed coordination + atomic hot-path state

MongoDB

  = durable persistence + long-lived execution state

```

The demo surfaces the consequences of those runtime guarantees through metrics, realtime events, verification, recovery-aware execution, and durable Child DAG behavior.

**---**

**# Architecture**

```text

┌───────────────────────────────────────────────────────────────┐

│                     Next.js Demo Client                       │

│                                                               │

│  Traffic Controls   Metrics   Live Logs   AI Investigation    │

│        │                │         │              │             │

└────────┼────────────────┼─────────┼──────────────┼─────────────┘

         │                │         │              │

         ▼                ▼         ▼              ▼

┌───────────────────────────────────────────────────────────────┐

│                 .NET Demo / Sample API                        │

│                                                               │

│  RBAC / ContextKey      Realtime         AI Provider          │

│  Demo Scenarios         Evidence         Policy Adapters      │

│  Demo Steps / Policies  Verification     Approval Bridge      │

└──────────────────────────────┬────────────────────────────────┘

                               │ public runtime APIs /

                               │ extension points

                               ▼

┌───────────────────────────────────────────────────────────────┐

│                Deterministic AI Runtime                       │

│                                                               │

│  Durable execution      DAG / Child DAG     Lifecycle         │

│  Recovery               Coordination        Verification      │

│  Runtime state          Replay / evidence   Extensions        │

└──────────────────────────────┬────────────────────────────────┘

                               │

                    ┌──────────┴──────────┐

                    ▼                     ▼

                  Redis                MongoDB

          distributed coordination   durable runtime state

          atomic Lua transitions     execution documents

          claims / shared queues     long-lived evidence

          hot-path runtime state     recovery / replay inputs

```

**---**

**# User Interface**

The current UI is an engineering console rather than a production end-user product.

**## Runtime Controls**

- target selection;

- max in-flight configuration;

- rotation overlap;

- burst/scenario execution;

- reset and stop controls.

**## Runtime Metrics**

- status counts;

- HTTP outcomes;

- in-flight work;

- latency p50/p95;

- elapsed time;

- request throughput;

- latency histogram;

- context rotation timeline.

**## Live Log**

The log console provides:

- HTTP / HTTP Error / Context filters;

- Realtime / ContextKey / Runtime Engine / AI filters;

- search;

- cumulative event counters;

- bounded retained-window count;

- follow-live / paused mode;

- jump to latest;

- clear logs.

**## AI Runtime Analysis**

The AI workspace exposes:

- observed context;

- analysis request;

- analysis context;

- root AI finding;

- suggested scenario;

- deterministic policy decision;

- human approval;

- scenario execution;

- deterministic verification;

- Child DAG investigation tree;

- selected Child details;

- Child re-analysis;

- investigation mode;

- AI provider working status.

**---**

**# Dark Mode**

Dark mode is the default visual theme.

The console uses a graphite/navy palette with semantic status colors for long-running engineering sessions:

```text

blue / violet   runtime, analysis, actions

green           success, verified, completed

amber           warnings, DAG/execution identity

red             failure, rejection

```

The selected theme is persisted locally and can be switched from the UI.

**---**

**# Technology**

Client:

- Next.js 16

- React 19

- TypeScript

- SignalR

- Axios

- Recharts

- TanStack React Virtual

Demo/backend/runtime integration:

- .NET

- Redis — distributed coordination, claims, queues, atomic Lua transitions

- MongoDB — durable runtime/execution persistence

- realtime event infrastructure

- Deterministic AI Runtime

AI features use the provider configured by the demo API.

**---**

**# Quick Start**

**## Prerequisites**

You need:

- a supported .NET SDK;

- Node.js / npm compatible with the current Next.js version;

- Redis for distributed runtime coordination and atomic Lua-backed operations;

- MongoDB for durable runtime/execution persistence.

To use `Ask AI` and Child re-analysis, configure the AI provider expected by the sample API.

**---**

**## 1. Start Redis**

Start the Redis instance expected by the runtime/sample configuration.

Redis is part of the runtime's distributed coordination path. In this demo it is involved in behavior such as:

- atomic Lua-backed state transitions;

- distributed claims / ownership;

- shared queue and dispatch coordination;

- in-flight and concurrency coordination;

- ContextKey rotation scenarios.

**---**

**## 2. Start MongoDB**

Start the MongoDB instance expected by the runtime/sample configuration.

MongoDB provides durable persistence for runtime/execution data that must survive beyond an individual process lifetime.

The exact collections depend on the configured runtime features, but MongoDB is the durable counterpart to Redis' coordination role.

A useful mental model is:

```text

Redis   → coordinate distributed execution now

MongoDB → persist durable execution state over time

```

**---**

**## 3. Start the .NET Demo API

The demo backend project is:

```text
Multiplexed.Sample.Demo.Rbac.AiAnalysis
```

Repository location:

```text
implementations/dotnet/Samples/multiplexed-rbac/demo/rbac-aiAnalysis
```
**

From the repository root:

```bash

cd implementations/dotnet

dotnet run \\

  --project Samples/multiplexed-rbac/demo/rbac-aiAnalysis/Multiplexed.Sample.Demo.Rbac.AiAnalysis.csproj

```

The `Multiplexed.Sample.Demo.Rbac.AiAnalysis` API exposes the demo endpoints used by the Next.js client, including login, scenario execution, runtime analysis, approval, verification, Child DAG investigation, and realtime events.

**---**

**## 4. Start the Next.js Client**

From the repository root:

```bash

cd clients/nextjs

npm install

npm run dev

```

Open:

```text

http://localhost:3000

```

If dependencies are already installed:

```bash

cd clients/nextjs

npm run dev

```

**---**

**# Build and Lint**

```bash

cd clients/nextjs

npm run lint

npm run build

```

Production start:

```bash

npm run start

```

**---**

**# Recommended Demo Flow**

A concise end-to-end demonstration:

```text

1\. Start Redis and MongoDB

2\. Start the .NET sample API

3\. Start the Next.js client and login

4\. Start a controlled burst/wave scenario

5\. Observe RBAC ContextKey rotation

6\. Observe requests remaining in flight during rotation

7\. Inspect Redis/Lua-backed coordination through resulting evidence

8\. Watch metrics and realtime logs

9\. Ask AI to analyze the bounded runtime snapshot

10\. Review the AI finding and proposed experiment

11\. Observe deterministic policy evaluation

12\. Approve or reject as the human operator

13\. If approved, execute the follow-up through the runtime

14\. Verify the outcome deterministically

15\. Inspect the durable Child DAG and persisted runtime evidence

16\. Re-analyze Child evidence

17\. Stop when conclusive, or approve another materially different experiment

```

This sequence demonstrates the architectural boundary:

```text

OBSERVE

  ↓

AI ANALYZE

  ↓

PROPOSE

  ↓

POLICY

  ↓

HUMAN APPROVAL

  ↓

EXECUTE

  ↓

VERIFY

  ↓

CHILD RE-ANALYZE

```

**---**

**# Demo Safety and Scope**

This project is deliberately a bounded engineering demo.

It should not be interpreted as:

- the complete Deterministic AI Runtime UI;

- a production SIEM;

- a general-purpose security product;

- a cloud posture management system;

- an autonomous AI system with unrestricted execution authority.

Several controls exposed by the UI — such as concurrency and rotation timing — exist specifically to make distributed behavior reproducible during testing and demonstrations.

They are not recommendations for exposing equivalent controls directly to end users in a production security system.

**---**

**# Why This Demo Matters**

Distributed execution systems often fail in places that ordinary happy-path demos hide:

```text

concurrent state transitions

stale context

in-flight overlap

partial failure

recovery

policy boundaries

human decision points

AI uncertainty

```

This demo makes those boundaries visible.

It combines live traffic, deterministic coordination, runtime evidence, AI-assisted reasoning, policy evaluation, explicit human control, durable execution, and verification in a single observable workflow.

The result is not an AI chatbot attached to a dashboard.

It is a demonstration of a stronger pattern:

> ****AI helps understand. Policy determines eligibility. Humans authorize execution. The runtime owns durable semantics. Evidence proves what happened.****

**---**

**# Repository**

Main project:

```text

https://github.com/mmarano2k14/deterministic-ai-runtime

```

For the complete runtime architecture — including runtime pools, recovery, replay, lifecycle observation, Ledger, Forensics, Kubernetes execution, HTTP/gRPC transports, and durable DAG semantics — refer to the main repository documentation.

**---**

**# License**

This client is part of the Deterministic AI Runtime repository.

See the repository `LICENSE` file for the current licensing terms.
