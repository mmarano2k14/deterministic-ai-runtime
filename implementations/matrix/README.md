# Multilanguage Runtime Matrix

This directory is the executable validation surface for the external SDK-to-runtime matrix.

It does not introduce a scheduler, queue, recovery authority, result-acceptance path, publication authority, or worker transition authority. The matrix drives the public SDK boundary and records evidence from the runtime authorities that already own those concerns.

## Stable layout

```text
implementations/matrix/
├── multilanguage-runtime-matrix-v1.json
├── matrix_plan.py
├── matrix_cli.py
├── README.md
├── clients/       # language-specific executable consumers are added here
├── fixtures/      # immutable publication/dependency fixtures are added here
├── scenarios/     # live scenario adapters are added here
├── evidence/      # explicit executed-run evidence is added here
└── tests/
```

The directories listed above are the intended final layout. Later validation work should fill these locations rather than moving the matrix foundation.

## Core cross-language matrix

The core matrix is an exact 3 x 3 cross-product:

```text
.NET client       -> .NET worker
.NET client       -> TypeScript worker
.NET client       -> Python worker
TypeScript client -> .NET worker
TypeScript client -> TypeScript worker
TypeScript client -> Python worker
Python client     -> .NET worker
Python client     -> TypeScript worker
Python client     -> Python worker
```

Each core scenario must eventually produce evidence for:

```text
publish
submit
observe
terminal result
public executionId
```

An executor being present in the plan does not make the scenario passed. Pass/fail/skipped outcomes belong only to executed evidence.

## Targeted coverage

The plan separately requires coverage for:

```text
immutable publication/run pinning
PythonWheelBundle / NodeLockedBundle / DotNetAssemblyClosure
hosted concurrency / retry / delegation policy families
nested Child DAGs
explicit durable cancellation from all three SDKs
in-flight recovery and local-queued redispatch
MCP durable effect evidence
journal result acceptance and duplicate convergence
trusted-process and sandboxed-container isolation profiles
HostRuntime and OciImage artifact selection
external-client engine dependency firewalls
```

Feature scenarios are intentionally bound later, after the exact supported combination is proven. Unsupported combinations must not be inferred merely to make the matrix rectangular.

## Validation commands

From the repository root:

```cmd
python .\implementations\matrix\matrix_cli.py validate
```

List the planned scenarios:

```cmd
python .\implementations\matrix\matrix_cli.py list
```

Inspect required coverage:

```cmd
python .\implementations\matrix\matrix_cli.py coverage
```

Run the foundation tests:

```cmd
python -m unittest discover -s .\implementations\matrix\tests -p "test_*.py" -v
```

These commands validate the matrix definition only. They do not claim that a live runtime scenario has executed.

## Execution evidence rule

The final matrix must record exactly what ran. A live result must distinguish at least:

```text
passed
failed
skipped
```

A skipped scenario is not a pass. The evidence must identify the exact client language, worker language, scenario capability, provider/profile where relevant, and command that produced the outcome.
