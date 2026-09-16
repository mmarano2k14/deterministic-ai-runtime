import assert from "node:assert/strict";
import test from "node:test";
import {
  AiSdkClient,
  AiSdkException,
  AI_SDK_OPERATIONS,
  AI_SDK_PROTOCOL_VERSION,
} from "../dist/index.js";

class RecordingTransport {
  constructor(responses) {
    this.responses = [...responses];
    this.requests = [];
    this.signals = [];
  }

  async invoke(request, signal) {
    this.requests.push(request);
    this.signals.push(signal);
    const response = this.responses.shift();
    if (response === undefined) throw new Error("No test response configured.");
    return response;
  }
}

test("publishPipeline emits the canonical wire defaults without runtime identities", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        publicationRef: "pub-1",
        publicationSha256: "abc",
        pipelineName: "demo",
        pipelineVersion: "v1",
      },
    },
  ]);
  const client = new AiSdkClient(transport);

  const response = await client.publishPipeline({
    definition: {
      name: "demo",
      steps: [
        {
          name: "one",
          stepKey: "one",
          order: 0,
        },
      ],
    },
  });

  assert.equal(response.publicationRef, "pub-1");
  assert.deepEqual(transport.requests, [
    {
      protocolVersion: AI_SDK_PROTOCOL_VERSION,
      operation: AI_SDK_OPERATIONS.publishPipeline,
      arguments: {
        request: {
          schemaVersion: 1,
          definition: {
            schemaVersion: 1,
            name: "demo",
            executionMode: "Sequential",
            steps: [
              {
                name: "one",
                stepKey: "one",
                order: 0,
                dependsOn: [],
                input: {},
                config: {},
              },
            ],
            config: {},
          },
          functions: [],
        },
      },
    },
  ]);
});

test("submitExecution materializes schemaVersion and metadata defaults", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-1",
        publicationRef: "pub-1",
        status: "Pending",
        acceptedAtUtc: "2026-09-16T00:00:00Z",
      },
    },
  ]);
  const client = new AiSdkClient(transport);

  await client.submitExecution({ publicationRef: "pub-1" });

  assert.deepEqual(transport.requests[0].arguments, {
    request: {
      schemaVersion: 1,
      publicationRef: "pub-1",
      metadata: {},
    },
  });
});

test("observeExecution forwards AbortSignal and uses the safe-read operation", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-1",
        publicationRef: "pub-1",
        pipelineName: "demo",
        pipelineVersion: "v1",
        status: "Running",
        createdAtUtc: "2026-09-16T00:00:00Z",
        updatedAtUtc: "2026-09-16T00:00:01Z",
        steps: [],
      },
    },
  ]);
  const controller = new AbortController();
  const client = new AiSdkClient(transport);

  await client.observeExecution("exec-1", controller.signal);

  assert.equal(transport.requests[0].operation, AI_SDK_OPERATIONS.observeExecution);
  assert.equal(transport.signals[0], controller.signal);
});

test("unsupported response schema fails closed", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 2,
        executionId: "exec-1",
        publicationRef: "pub-1",
        pipelineName: "demo",
        pipelineVersion: "v1",
        status: "Running",
        createdAtUtc: "2026-09-16T00:00:00Z",
        updatedAtUtc: "2026-09-16T00:00:01Z",
        steps: [],
      },
    },
  ]);
  const client = new AiSdkClient(transport);

  await assert.rejects(
    () => client.observeExecution("exec-1"),
    (error) =>
      error instanceof AiSdkException &&
      error.sdkError.kind === "unsupported_schema" &&
      error.sdkError.code === "unsupported_schema",
  );
});

test("remote normalized errors are surfaced as AiSdkException", async () => {
  const transport = new RecordingTransport([
    {
      error: {
        kind: "conflict",
        code: "run_key_conflict",
        message: "Conflict",
        retryable: false,
        details: {},
      },
    },
  ]);
  const client = new AiSdkClient(transport);

  await assert.rejects(
    () => client.submitExecution({ publicationRef: "pub-1", idempotencyKey: "same-key" }),
    (error) =>
      error instanceof AiSdkException && error.sdkError.code === "run_key_conflict",
  );
});

test("blank execution ids fail before transport invocation", async () => {
  const transport = new RecordingTransport([]);
  const client = new AiSdkClient(transport);

  await assert.rejects(
    () => client.observeExecution("   "),
    (error) =>
      error instanceof AiSdkException && error.sdkError.code === "execution_id_required",
  );
  assert.equal(transport.requests.length, 0);
});
