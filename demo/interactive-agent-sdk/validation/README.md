# Interactive Agent SDK Validation Contract

This directory freezes the canonical logical scenario used by the interactive SDK topology validation campaign.

## Source of truth

`interactive-agent-scenario.json` defines the business-neutral execution shape and durable invariants shared by the .NET, TypeScript, and Python consumers.

`evidence.schema.json` defines the minimum machine-readable evidence required for a scenario to be accepted as PASS.

## Canonical root flow

```text
plan
  -> delegate-analysis
  -> await-review
  -> final-answer
  -> publish-result
```

The delegated Child DAG is:

```text
child-analysis
  -> publish-child-result
```

## Acceptance boundary

A topology run is PASS only when all of the following are true:

```text
publication accepted
execution submitted
human-review boundary reached
human input accepted
ExecutionId unchanged across human input
terminal execution status == Completed
all five root steps == Completed
public result available
provider == openai
model metadata present
response payload present
replay succeeded == true
replay deterministic == true
```

The generated OpenAI text is intentionally not compared for equality. The model output is probabilistic; the execution lifecycle and durable evidence are the deterministic validation target.

## Public-boundary rule

The validation runner must use public SDK operations only. It must not use matrix endpoints, internal stores, internal controller APIs, or direct `IAiPublicSdkBoundary` calls to manufacture a PASS.

## Language-specific names

Pipeline publication names may remain language-specific. Semantic step names, waiting keys, durable assertions, and evidence fields are shared.
