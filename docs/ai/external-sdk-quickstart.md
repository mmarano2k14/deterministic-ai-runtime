# External SDK Quickstart

**Status:** Initial repository quickstart for the implemented .NET, TypeScript/JavaScript, and Python SDK clients.

## Goal

This quickstart shows the complete external-client flow:

```text
application
    -> external SDK client
    -> publish user code
    -> submit immutable publication
    -> observe durable execution
    -> read terminal result
```

The examples deliberately publish the same Python user function from all three client languages. The SDK client language and the hosted execution language are independent concerns.

The reusable sample source is:

```text
implementations/sdk/samples/published-functions/python/functions.py
```

That file represents **user code supplied through the SDK**. It is not a runtime worker. Execution still occurs through the production Python hosted worker.

## Prerequisites

You need:

- an MCP public SDK endpoint, for example `https://runtime.example/mcp`;
- a valid bearer token when authentication is enabled;
- any public-boundary access-context header required by the deployment;
- a server-configured Python hosted environment reference, represented below as `<python-environment-ref>`.

The `environmentRef` identifies server-approved execution material. It is not a worker id, runtime instance id, credential, or client-selected scheduling authority.

## Shared pipeline shape

All three examples publish one custom step:

```text
pipeline: sdk-quickstart
step:     work
stepKey:  custom
language: python
invocation.kind: Custom
entry point: functions.py::run
```

The sample function returns a payload containing the worker language and the input marker.

## .NET

```csharp
using System.Text.Json;
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Sdk.Transport;

var endpoint = new Uri("https://runtime.example/mcp");
var token = Environment.GetEnvironmentVariable("MULTIPLEXED_TOKEN")!;
var accessContext = Environment.GetEnvironmentVariable("MULTIPLEXED_ACCESS_CONTEXT");
var environmentRef = "<python-environment-ref>";

var options = new AiSdkTransportOptions
{
    CredentialProvider = new AiSdkStaticCredentialProvider(
        new AiSdkCredential("Bearer", token)),
    AdditionalHeaders = string.IsNullOrWhiteSpace(accessContext)
        ? null
        : new Dictionary<string, string>
        {
            ["X-Access-Context"] = accessContext
        }
};

IAiSdkClient client = new AiSdkClient(
    new AiSdkMcpHttpTransport(endpoint, options));

var sourceBytes = await File.ReadAllBytesAsync(
    "implementations/sdk/samples/published-functions/python/functions.py");

var publication = await client.PublishPipelineAsync(
    new AiSdkPipelinePublicationRequest
    {
        Definition = new AiSdkPipelineDefinition
        {
            Name = "sdk-quickstart",
            Version = "1",
            ExecutionLanguage = "python",
            ExecutionMode = AiSdkExecutionMode.Dag,
            Steps = new[]
            {
                new AiSdkPipelineStepDefinition
                {
                    Name = "work",
                    StepKey = "custom",
                    Order = 0,
                    ExecutionLanguage = "python",
                    Invocation = new AiSdkInvocationDefinition
                    {
                        Kind = AiSdkInvocationKind.Custom
                    }
                }
            }
        },
        Functions = new[]
        {
            new AiSdkPublicationFunctionUpload
            {
                Site = new AiSdkPublicationCallSite
                {
                    Kind = AiSdkPublicationFunctionKind.Step,
                    StepName = "work"
                },
                EnvironmentRef = environmentRef,
                EntryPointPath = "functions.py",
                EntryPointSymbol = "run",
                Sources = new[]
                {
                    new AiSdkPublicationFileUpload
                    {
                        Path = "functions.py",
                        ContentBase64 = Convert.ToBase64String(sourceBytes)
                    }
                }
            }
        }
    });

var submitted = await client.SubmitExecutionAsync(
    new AiSdkExecutionSubmissionRequest
    {
        PublicationRef = publication.PublicationRef,
        IdempotencyKey = $"quickstart-{Guid.NewGuid():N}",
        Input = JsonSerializer.SerializeToElement(new { marker = "hello" })
    });

AiSdkExecutionStatus status;
do
{
    await Task.Delay(250);
    status = (await client.ObserveExecutionAsync(submitted.ExecutionId)).Status;
}
while (status is AiSdkExecutionStatus.Pending
    or AiSdkExecutionStatus.Running
    or AiSdkExecutionStatus.Waiting);

var result = await client.GetExecutionResultAsync(submitted.ExecutionId);
Console.WriteLine($"{result.ExecutionId}: {result.Status}");
```

## TypeScript / JavaScript

```ts
import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import {
  AiSdkClient,
  AiSdkMcpHttpTransport,
  AiSdkStaticCredentialProvider,
  type AiSdkPipelinePublicationRequest,
} from "@multiplexed/ai-sdk";

const endpoint = new URL("https://runtime.example/mcp");
const token = process.env.MULTIPLEXED_TOKEN!;
const accessContext = process.env.MULTIPLEXED_ACCESS_CONTEXT;
const environmentRef = "<python-environment-ref>";

const transport = new AiSdkMcpHttpTransport(endpoint, {
  credentialProvider: new AiSdkStaticCredentialProvider({
    scheme: "Bearer",
    value: token,
  }),
  ...(accessContext === undefined
    ? {}
    : { additionalHeaders: { "X-Access-Context": accessContext } }),
});

const client = new AiSdkClient(transport);
const source = await readFile(
  "implementations/sdk/samples/published-functions/python/functions.py",
);

const request: AiSdkPipelinePublicationRequest = {
  definition: {
    name: "sdk-quickstart",
    version: "1",
    executionLanguage: "python",
    executionMode: "Dag",
    steps: [
      {
        name: "work",
        stepKey: "custom",
        order: 0,
        executionLanguage: "python",
        invocation: { kind: "Custom" },
      },
    ],
  },
  functions: [
    {
      site: { kind: "Step", stepName: "work" },
      environmentRef,
      entryPointPath: "functions.py",
      entryPointSymbol: "run",
      sources: [
        {
          path: "functions.py",
          contentBase64: source.toString("base64"),
        },
      ],
    },
  ],
};

const publication = await client.publishPipeline(request);
const submitted = await client.submitExecution({
  publicationRef: publication.publicationRef,
  idempotencyKey: `quickstart-${randomUUID()}`,
  input: { marker: "hello" },
});

let observation = await client.observeExecution(submitted.executionId);
while (["Pending", "Running", "Waiting"].includes(observation.status)) {
  await new Promise((resolve) => setTimeout(resolve, 250));
  observation = await client.observeExecution(submitted.executionId);
}

const result = await client.getExecutionResult(submitted.executionId);
console.log(result.executionId, result.status);
```

## Python

```python
import asyncio
import base64
import os
import uuid

from multiplexed_ai_sdk import (
    AiSdkClient,
    AiSdkCredential,
    AiSdkExecutionMode,
    AiSdkExecutionStatus,
    AiSdkExecutionSubmissionRequest,
    AiSdkInvocationDefinition,
    AiSdkInvocationKind,
    AiSdkMcpHttpTransport,
    AiSdkPipelineDefinition,
    AiSdkPipelinePublicationRequest,
    AiSdkPipelineStepDefinition,
    AiSdkPublicationCallSite,
    AiSdkPublicationFileUpload,
    AiSdkPublicationFunctionKind,
    AiSdkPublicationFunctionUpload,
    AiSdkStaticCredentialProvider,
    AiSdkTransportOptions,
)


async def main() -> None:
    token = os.environ["MULTIPLEXED_TOKEN"]
    access_context = os.getenv("MULTIPLEXED_ACCESS_CONTEXT")
    environment_ref = "<python-environment-ref>"

    options = AiSdkTransportOptions(
        credential_provider=AiSdkStaticCredentialProvider(
            AiSdkCredential("Bearer", token)
        ),
        additional_headers=(
            {"X-Access-Context": access_context}
            if access_context
            else None
        ),
    )

    client = AiSdkClient(
        AiSdkMcpHttpTransport("https://runtime.example/mcp", options)
    )

    source = open(
        "implementations/sdk/samples/published-functions/python/functions.py",
        "rb",
    ).read()

    publication = await client.publish_pipeline(
        AiSdkPipelinePublicationRequest(
            definition=AiSdkPipelineDefinition(
                name="sdk-quickstart",
                version="1",
                execution_language="python",
                execution_mode=AiSdkExecutionMode.DAG,
                steps=(
                    AiSdkPipelineStepDefinition(
                        name="work",
                        step_key="custom",
                        order=0,
                        execution_language="python",
                        invocation=AiSdkInvocationDefinition(
                            kind=AiSdkInvocationKind.CUSTOM
                        ),
                    ),
                ),
            ),
            functions=(
                AiSdkPublicationFunctionUpload(
                    site=AiSdkPublicationCallSite(
                        kind=AiSdkPublicationFunctionKind.STEP,
                        step_name="work",
                    ),
                    environment_ref=environment_ref,
                    entry_point_path="functions.py",
                    entry_point_symbol="run",
                    sources=(
                        AiSdkPublicationFileUpload(
                            path="functions.py",
                            content_base64=base64.b64encode(source).decode("ascii"),
                        ),
                    ),
                ),
            ),
        )
    )

    submitted = await client.submit_execution(
        AiSdkExecutionSubmissionRequest(
            publication_ref=publication.publication_ref,
            idempotency_key=f"quickstart-{uuid.uuid4().hex}",
            input={"marker": "hello"},
        )
    )

    observation = await client.observe_execution(submitted.execution_id)
    while observation.status in {
        AiSdkExecutionStatus.PENDING,
        AiSdkExecutionStatus.RUNNING,
        AiSdkExecutionStatus.WAITING,
    }:
        await asyncio.sleep(0.25)
        observation = await client.observe_execution(submitted.execution_id)

    result = await client.get_execution_result(submitted.execution_id)
    print(result.execution_id, result.status.value)


asyncio.run(main())
```

## Cancellation

Cancelling the client task, `.NET CancellationToken`, or JavaScript `AbortSignal` cancels only the in-flight SDK request. Durable execution cancellation is explicit:

```text
.NET        CancelExecutionAsync(executionId, request)
TypeScript  cancelExecution(executionId, request)
Python      cancel_execution(execution_id, request)
```

## Retry boundary

SDK transport retry remains limited to safe reads:

```text
publish  -> no automatic retry
submit   -> no automatic retry
observe  -> bounded safe-read retry
result   -> bounded safe-read retry
cancel   -> no automatic retry
```

The server remains authoritative for durable idempotency, publication pinning, scheduling, recovery, journal leases/epochs, result acceptance, and finalization.

## Run the KubernetesPool Python scenario

The validated Kubernetes SDK path publishes the Python sample through MCP, submits with `QueueFirst`, runs it with the production Python `TrustedProcess` worker inside a Runtime Pool Pod, and checks the uploaded marker in the public `Completed` result.

From the repository root, with the existing Minikube/Gateway and Redis/MongoDB environment ready:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& .\implementations\matrix\runtime\kubernetes\run-kubernetes-pool-sdk-execution.ps1
```

Use `-SkipImageBuild` only when the SDK Runtime Pool image already includes the current host and production workers. The runner still loads and probes it and generates a fresh control-plane profile. The generic historical Kubernetes test image is not a substitute for this SDK image.

See [KubernetesPool Matrix Validation](kubernetes-pool-matrix-validation.md) for prerequisites, the distinct routing/recovery evidence inputs, bootstrap configuration, and diagnostic collection. This one Python-to-Python scenario does not validate the full cross-language Kubernetes matrix.

## Validation reference

The external SDK path represented here is exercised by the fixture-free Docker runtime matrix. See [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md) for the exact 37/37 executed coverage, including the bounded `ProcessHostPool` / `ContainerIsolationProvider` provider-selection closure and its non-claims.

The separate KubernetesPool closure is **3/3**: live HTTP routing, hierarchical runtime/Pod failure recovery, and external Python SDK publication/execution with a public `Completed` result and the uploaded-function marker verified. The combined record is **40 validated scenarios across two topologies (37 Docker + 3 Kubernetes)**, not a homogeneous `40/40` matrix. The final SDK invocation revalidated retained routing/recovery evidence; it did not rerun those campaigns. See [KubernetesPool Matrix Validation](kubernetes-pool-matrix-validation.md).