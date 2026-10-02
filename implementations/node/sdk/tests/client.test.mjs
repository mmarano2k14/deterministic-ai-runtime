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

test("watchExecution exposes an AsyncIterable and advances the stateless public cursor", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-watch",
        sequence: 10,
        kind: "Snapshot",
        occurredAtUtc: "2026-09-22T00:00:00Z",
        snapshot: {
          schemaVersion: 1,
          executionId: "exec-watch",
          publicationRef: "pub-watch",
          pipelineName: "demo",
          pipelineVersion: "v1",
          status: "Running",
          createdAtUtc: "2026-09-22T00:00:00Z",
          updatedAtUtc: "2026-09-22T00:00:00Z",
          steps: [],
        },
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-watch",
        sequence: 11,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:01Z",
        channel: "Steps",
        eventType: "step.progressed",
        payloadSchemaVersion: 1,
        payload: { status: "Running" },
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-watch",
        sequence: 12,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:02Z",
        channel: "Lifecycle",
        eventType: "execution.terminal",
        payloadSchemaVersion: 1,
        payload: { status: "Completed" },
      },
    },
  ]);
  const client = new AiSdkClient(transport);

  const events = [];
  for await (const event of client.watchExecution({ executionId: "exec-watch" })) {
    events.push(event);
  }

  assert.deepEqual(events.map((event) => event.sequence), [10, 11, 12]);
  assert.equal(transport.requests.length, 3);
  assert.deepEqual(
    transport.requests.map((request) => request.operation),
    [
      AI_SDK_OPERATIONS.watchExecution,
      AI_SDK_OPERATIONS.watchExecution,
      AI_SDK_OPERATIONS.watchExecution,
    ],
  );
  assert.deepEqual(transport.requests[0].arguments, {
    request: {
      schemaVersion: 1,
      executionId: "exec-watch",
      channels: [],
      includeInitialSnapshot: true,
    },
  });
  assert.deepEqual(transport.requests[1].arguments, {
    request: {
      schemaVersion: 1,
      executionId: "exec-watch",
      channels: [],
      afterSequence: 10,
      includeInitialSnapshot: false,
    },
  });
  assert.deepEqual(transport.requests[2].arguments, {
    request: {
      schemaVersion: 1,
      executionId: "exec-watch",
      channels: [],
      afterSequence: 11,
      includeInitialSnapshot: false,
    },
  });
});

test("watchExecution preserves explicit resume and channel arguments and auto-resyncs", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-resume",
        sequence: 879,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:00Z",
        channel: "Recovery",
        eventType: "recovery.resumed",
        payloadSchemaVersion: 1,
        payload: { status: "Running" },
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-resume",
        kind: "ResyncRequired",
        occurredAtUtc: "2026-09-22T00:00:01Z",
        resyncRequired: {
          schemaVersion: 1,
          reason: "HistoryUnavailable",
          requestedAfterSequence: 879,
          earliestAvailableSequence: 900,
          latestSequence: 901,
        },
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-resume",
        sequence: 901,
        kind: "Snapshot",
        occurredAtUtc: "2026-09-22T00:00:02Z",
        snapshot: {
          schemaVersion: 1,
          executionId: "exec-resume",
          publicationRef: "pub-watch",
          pipelineName: "demo",
          pipelineVersion: "v1",
          status: "Running",
          createdAtUtc: "2026-09-22T00:00:00Z",
          updatedAtUtc: "2026-09-22T00:00:02Z",
          steps: [],
        },
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-resume",
        sequence: 902,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:03Z",
        channel: "Lifecycle",
        eventType: "watch.test.terminal",
        payloadSchemaVersion: 1,
        payload: { status: "Completed" },
      },
    },
  ]);
  const client = new AiSdkClient(transport);

  const events = [];
  for await (const event of client.watchExecution({
    executionId: "exec-resume",
    channels: ["Recovery"],
    afterSequence: 878,
    includeInitialSnapshot: false,
  })) {
    events.push(event);
  }

  assert.equal(events.length, 4);
  assert.equal(events[1].kind, "ResyncRequired");
  assert.equal(events[2].kind, "Snapshot");
  assert.equal(events[2].sequence, 901);
  assert.equal(events[3].sequence, 902);
  assert.deepEqual(transport.requests[0].arguments, {
    request: {
      schemaVersion: 1,
      executionId: "exec-resume",
      channels: ["Recovery"],
      afterSequence: 878,
      includeInitialSnapshot: false,
    },
  });
  assert.deepEqual(transport.requests[1].arguments, {
    request: {
      schemaVersion: 1,
      executionId: "exec-resume",
      channels: ["Recovery"],
      afterSequence: 879,
      includeInitialSnapshot: false,
    },
  });
  assert.deepEqual(transport.requests[2].arguments, {
    request: {
      schemaVersion: 1,
      executionId: "exec-resume",
      channels: ["Recovery"],
      includeInitialSnapshot: true,
    },
  });
});

test("watchExecution ignores an exact duplicate cursor item", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-duplicate",
        sequence: 41,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:00Z",
        channel: "Steps",
        eventType: "watch.test.duplicate",
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-duplicate",
        sequence: 42,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:01Z",
        channel: "Lifecycle",
        eventType: "watch.test.terminal",
        payloadSchemaVersion: 1,
        payload: { status: "Completed" },
      },
    },
  ]);
  const client = new AiSdkClient(transport);
  const events = [];

  for await (const event of client.watchExecution({
    executionId: "exec-duplicate",
    afterSequence: 41,
    includeInitialSnapshot: false,
  })) {
    events.push(event);
  }

  assert.deepEqual(events.map((event) => event.sequence), [42]);
  assert.equal(transport.requests.length, 2);
  assert.equal(transport.requests[0].arguments.request.afterSequence, 41);
  assert.equal(transport.requests[1].arguments.request.afterSequence, 41);
});

test("watchExecution detects an unfiltered sequence gap and reestablishes a snapshot", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-gap",
        sequence: 42,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:00Z",
        channel: "Steps",
        eventType: "watch.test.gapped",
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-gap",
        sequence: 42,
        kind: "Snapshot",
        occurredAtUtc: "2026-09-22T00:00:01Z",
        snapshot: {
          schemaVersion: 1,
          executionId: "exec-gap",
          publicationRef: "pub-watch",
          pipelineName: "demo",
          pipelineVersion: "v1",
          status: "Running",
          createdAtUtc: "2026-09-22T00:00:00Z",
          updatedAtUtc: "2026-09-22T00:00:01Z",
          steps: [],
        },
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-gap",
        sequence: 43,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:02Z",
        channel: "Lifecycle",
        eventType: "watch.test.terminal",
        payloadSchemaVersion: 1,
        payload: { status: "Completed" },
      },
    },
  ]);
  const client = new AiSdkClient(transport);
  const events = [];

  for await (const event of client.watchExecution({
    executionId: "exec-gap",
    afterSequence: 40,
    includeInitialSnapshot: false,
  })) {
    events.push(event);
  }

  assert.equal(events.length, 3);
  assert.equal(events[0].kind, "ResyncRequired");
  assert.equal(events[0].resyncRequired.reason, "GapDetected");
  assert.equal(events[0].resyncRequired.requestedAfterSequence, 40);
  assert.equal(events[1].kind, "Snapshot");
  assert.equal(events[1].sequence, 42);
  assert.equal(events[2].sequence, 43);
  assert.equal(transport.requests[1].arguments.request.afterSequence, undefined);
  assert.equal(transport.requests[1].arguments.request.includeInitialSnapshot, true);
});

test("watchExecution rejects regressing public sequence values", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-regress",
        sequence: 39,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:00Z",
        channel: "Lifecycle",
        eventType: "execution.progressed",
        payloadSchemaVersion: 1,
        payload: { status: "Running" },
      },
    },
  ]);
  const client = new AiSdkClient(transport);
  const iterator = client.watchExecution({
    executionId: "exec-regress",
    afterSequence: 40,
    includeInitialSnapshot: false,
  })[Symbol.asyncIterator]();

  await assert.rejects(
    () => iterator.next(),
    (error) =>
      error instanceof AiSdkException &&
      error.sdkError.kind === "invalid_response" &&
      error.sdkError.code === "regressing_watch_sequence",
  );
});

test("watchExecution rejects mismatched execution responses", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-other",
        sequence: 1,
        kind: "Event",
        occurredAtUtc: "2026-09-22T00:00:00Z",
        channel: "Lifecycle",
        eventType: "execution.progressed",
        payloadSchemaVersion: 1,
        payload: { status: "Running" },
      },
    },
  ]);
  const client = new AiSdkClient(transport);
  const iterator = client.watchExecution({ executionId: "exec-requested" })[Symbol.asyncIterator]();

  await assert.rejects(
    () => iterator.next(),
    (error) =>
      error instanceof AiSdkException &&
      error.sdkError.kind === "invalid_response" &&
      error.sdkError.code === "watch_execution_mismatch",
  );
});

test("watchExecution validates channels before transport invocation", () => {
  const transport = new RecordingTransport([]);
  const client = new AiSdkClient(transport);

  assert.throws(
    () => client.watchExecution({ executionId: "exec-watch", channels: ["Internal"] }),
    (error) =>
      error instanceof AiSdkException &&
      error.sdkError.kind === "invalid_request" &&
      error.sdkError.code === "invalid_watch_channel",
  );
  assert.equal(transport.requests.length, 0);
});

test("aborting watch stops iteration without invoking durable execution cancellation", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-abort",
        sequence: 1,
        kind: "Snapshot",
        occurredAtUtc: "2026-09-22T00:00:00Z",
        snapshot: {
          schemaVersion: 1,
          executionId: "exec-abort",
          publicationRef: "pub-watch",
          pipelineName: "demo",
          pipelineVersion: "v1",
          status: "Running",
          createdAtUtc: "2026-09-22T00:00:00Z",
          updatedAtUtc: "2026-09-22T00:00:00Z",
          steps: [],
        },
      },
    },
  ]);
  const controller = new AbortController();
  const client = new AiSdkClient(transport);
  const iterator = client.watchExecution({ executionId: "exec-abort" }, controller.signal)[Symbol.asyncIterator]();

  const first = await iterator.next();
  assert.equal(first.value.kind, "Snapshot");

  controller.abort();
  await assert.rejects(() => iterator.next(), (error) => error?.name === "AbortError");
  assert.equal(transport.requests.length, 1);
  assert.equal(transport.requests[0].operation, AI_SDK_OPERATIONS.watchExecution);
  assert.equal(
    transport.requests.some((request) => request.operation === AI_SDK_OPERATIONS.cancelExecution),
    false,
  );
});

test("pause, resume, input and replay use public SDK operation contracts", async () => {
  const transport = new RecordingTransport([
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-control",
        operation: "Pause",
        accepted: true,
        state: {
          status: "Pausing",
          pendingAction: "Pause",
          updatedAtUtc: "2026-09-22T00:00:00Z",
        },
        acceptedAtUtc: "2026-09-22T00:00:00Z",
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-control",
        operation: "Resume",
        accepted: true,
        state: {
          status: "Resuming",
          pendingAction: "Resume",
          updatedAtUtc: "2026-09-22T00:00:01Z",
        },
        acceptedAtUtc: "2026-09-22T00:00:01Z",
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-control",
        operation: "SubmitInput",
        accepted: true,
        state: {
          status: "Resuming",
          pendingAction: "SubmitInput",
          waitingKey: "approval:test",
          updatedAtUtc: "2026-09-22T00:00:02Z",
        },
        acceptedAtUtc: "2026-09-22T00:00:02Z",
      },
    },
    {
      result: {
        schemaVersion: 1,
        executionId: "exec-control",
        succeeded: true,
        deterministic: true,
        diagnostics: [],
        startedAtUtc: "2026-09-22T00:00:03Z",
        completedAtUtc: "2026-09-22T00:00:04Z",
        durationMs: 1000,
      },
    },
  ]);
  const client = new AiSdkClient(transport);

  await client.pauseExecution("exec-control", { reason: "operator" });
  await client.resumeExecution("exec-control");
  await client.submitExecutionInput("exec-control", {
    waitingKey: "approval:test",
    input: { approved: true },
  });
  await client.replayExecution("exec-control");

  assert.deepEqual(
    transport.requests.map((item) => item.operation),
    [
      AI_SDK_OPERATIONS.pauseExecution,
      AI_SDK_OPERATIONS.resumeExecution,
      AI_SDK_OPERATIONS.submitExecutionInput,
      AI_SDK_OPERATIONS.replayExecution,
    ],
  );
  assert.deepEqual(transport.requests[0].arguments, {
    executionId: "exec-control",
    request: { schemaVersion: 1, reason: "operator" },
  });
  assert.deepEqual(transport.requests[1].arguments, {
    executionId: "exec-control",
    request: { schemaVersion: 1 },
  });
  assert.deepEqual(transport.requests[2].arguments, {
    executionId: "exec-control",
    request: {
      schemaVersion: 1,
      waitingKey: "approval:test",
      input: { approved: true },
    },
  });
  assert.deepEqual(transport.requests[3].arguments, {
    executionId: "exec-control",
    request: {
      schemaVersion: 1,
      strictDeterminism: true,
      includeDiagnostics: true,
    },
  });
});

test("submitExecutionInput validates waiting key and object payload before transport invocation", async () => {
  const transport = new RecordingTransport([]);
  const client = new AiSdkClient(transport);

  await assert.rejects(
    () => client.submitExecutionInput("exec-control", { waitingKey: " ", input: {} }),
    (error) => error instanceof AiSdkException && error.sdkError.code === "waiting_key_required",
  );
  await assert.rejects(
    () => client.submitExecutionInput("exec-control", { waitingKey: "approval:test", input: [] }),
    (error) => error instanceof AiSdkException && error.sdkError.code === "input_object_required",
  );
  assert.equal(transport.requests.length, 0);
});
