# Invocation performance measurement

This measurement pack establishes a baseline before changing the durable Invocation Mongo codec or indexes.

## Codec baseline

```powershell
dotnet test `
  .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~AiDurableInvocationPerformanceMeasurementTests.Codec_Benchmark" `
  --logger "console;verbosity=detailed" `
  --nologo
```

Optional iteration override:

```powershell
$env:MULTIPLEXED_INVOCATION_CODEC_BENCHMARK_ITERATIONS = "25000"
```

The output reports encode/decode time, allocations, throughput, JSON snapshot size and BSON size. There is no universal pass/fail threshold; compare measurements on the same target environment.

## Mongo query plans

Set the existing isolated integration-test connection variable:

```powershell
$env:MULTIPLEXED_TEST_MONGO_INVOCATION_CONNECTION_STRING = "mongodb://..."
```

Then run:

```powershell
dotnet test `
  .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~AiDurableInvocationPerformanceMeasurementTests.Mongo_Query_Explain" `
  --logger "console;verbosity=detailed" `
  --nologo
```

Optional seeded-document override:

```powershell
$env:MULTIPLEXED_INVOCATION_EXPLAIN_DOCUMENTS = "10000"
```

The test creates a random temporary database, uses the production store query paths, captures the actual Mongo `find` commands, runs `explain("executionStats")`, prints the results, and drops the database.

Review at minimum:

```text
nReturned
totalKeysExamined
totalDocsExamined
executionTimeMillis
winning plan stages
SORT / COLLSCAN presence
index bounds
```

Do not change index ordering from static inspection alone. Preserve the output before implementing any follow-up optimization.

## Production promotion after measured index experiments

The production dispatch query uses a hybrid strategy because one global index ordering was not robust across lease distributions:

```text
Prepared
    -> ordered Prepared index
    -> sort key: updatedAt, _id

Expired Leased
    -> expiry-oriented dispatch index
    -> leaseExpiresAt <= now

bounded branch pages
    -> merge by updatedAt, _id
    -> take requested page size
```

The prepared branch uses `ix_durable_invocation_dispatch_prepared_v2`:

```text
controlPlaneId
tenantId
tenantGroupId
language
status
updatedAt
_id
```

The expired-lease branch retains `ix_durable_invocation_dispatch`:

```text
controlPlaneId
tenantId
tenantGroupId
language
status
leaseExpiresAt
updatedAt
```

The split preserves the expiry-oriented behavior that remained superior when live leases dominated, while avoiding the full blocking sort/scan for dense Prepared populations. The merged page remains discovery-only; the worker-lease CAS remains authoritative.

Continuation paging uses `ix_durable_invocation_continuation_v2`:

```text
controlPlaneId
tenantId
tenantGroupId
continuationStatus
status
updatedAt
_id
```

The production query explicitly selects the promoted index. The legacy continuation index remains created during the compatibility window so an existing deployment can add the v2 index without an index-name/key-spec migration conflict.

Targeted Mongo validation:

```powershell
dotnet test `
  .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~AiDurableInvocationMongoIndexPromotionTests|FullyQualifiedName~AiWorkerDispatchMongoTests|FullyQualifiedName~AiDurableInvocationDagReconciliationTests" `
  --logger "console;verbosity=minimal" `
  --nologo
```

The production promotion must preserve keyset ordering, cursor exclusivity, live-lease exclusion, continuation semantics, and CAS authority. Index measurements are evidence for query planning only; they do not replace correctness tests.
