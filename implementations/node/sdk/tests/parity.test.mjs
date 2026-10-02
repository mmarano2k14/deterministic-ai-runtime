import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  AI_SDK_EXECUTION_CONTROL_ACTIONS,
  AI_SDK_EXECUTION_CONTROL_OPERATIONS,
  AI_SDK_EXECUTION_CONTROL_STATUSES,
  AI_SDK_EXECUTION_MODES,
  AI_SDK_EXECUTION_STATUSES,
  AI_SDK_EXECUTION_STEP_STATUSES,
  AI_SDK_EXECUTION_WATCH_CHANNELS,
  AI_SDK_EXECUTION_WATCH_EVENT_KINDS,
  AI_SDK_EXECUTION_WATCH_RESYNC_REASONS,
  AI_SDK_INVOCATION_KINDS,
  AI_SDK_OPERATIONS,
  AI_SDK_PROTOCOL_VERSION,
  AI_SDK_PUBLICATION_DEPENDENCY_PACKAGE_KINDS,
  AI_SDK_PUBLICATION_FUNCTION_KINDS,
  AI_SDK_SCHEMA_VERSIONS,
  AiSdkClient,
  AiSdkException,
} from "../dist/index.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(
  fs.readFileSync(path.resolve(here, "../../../sdk/parity/fixtures/sdk-parity-v1.json"), "utf8"),
);

class FixtureTransport {
  constructor(responses) {
    this.responses = responses;
    this.requests = [];
  }

  async invoke(request) {
    this.requests.push(structuredClone(request));
    const name = Object.entries(fixture.requests)
      .find(([, value]) => value.operation === request.operation)?.[0];
    if (name === undefined) throw new Error(`Unexpected operation '${request.operation}'.`);
    return { result: structuredClone(this.responses[name]) };
  }
}

test("protocol, schema versions and enum literals match the shared fixture", () => {
  assert.equal(AI_SDK_PROTOCOL_VERSION, fixture.protocolVersion);
  assert.deepEqual(AI_SDK_OPERATIONS, fixture.operations);
  assert.deepEqual(AI_SDK_SCHEMA_VERSIONS, fixture.schemaVersions);
  assert.deepEqual(AI_SDK_EXECUTION_MODES, fixture.enumValues.executionMode);
  assert.deepEqual(AI_SDK_INVOCATION_KINDS, fixture.enumValues.invocationKind);
  assert.deepEqual(AI_SDK_PUBLICATION_FUNCTION_KINDS, fixture.enumValues.publicationFunctionKind);
  assert.deepEqual(
    AI_SDK_PUBLICATION_DEPENDENCY_PACKAGE_KINDS,
    fixture.enumValues.publicationDependencyPackageKind,
  );
  assert.deepEqual(AI_SDK_EXECUTION_STATUSES, fixture.enumValues.executionStatus);
  assert.deepEqual(AI_SDK_EXECUTION_CONTROL_OPERATIONS, fixture.enumValues.executionControlOperation);
  assert.deepEqual(AI_SDK_EXECUTION_CONTROL_STATUSES, fixture.enumValues.executionControlStatus);
  assert.deepEqual(AI_SDK_EXECUTION_CONTROL_ACTIONS, fixture.enumValues.executionControlAction);
  assert.deepEqual(AI_SDK_EXECUTION_STEP_STATUSES, fixture.enumValues.executionStepStatus);
  assert.deepEqual(AI_SDK_EXECUTION_WATCH_CHANNELS, fixture.enumValues.executionWatchChannel);
  assert.deepEqual(AI_SDK_EXECUTION_WATCH_EVENT_KINDS, fixture.enumValues.executionWatchEventKind);
  assert.deepEqual(AI_SDK_EXECUTION_WATCH_RESYNC_REASONS, fixture.enumValues.executionWatchResyncReason);
  assert.equal(AI_SDK_SCHEMA_VERSIONS.executionWatchRequest, fixture.schemaVersions.executionWatchRequest);
  assert.equal(AI_SDK_SCHEMA_VERSIONS.executionWatchEvent, fixture.schemaVersions.executionWatchEvent);
  assert.equal(AI_SDK_SCHEMA_VERSIONS.executionWatchResyncRequired, fixture.schemaVersions.executionWatchResyncRequired);
});

test("all public non-streaming request wire shapes match the shared fixture", async () => {
  const transport = new FixtureTransport(fixture.responses);
  const client = new AiSdkClient(transport);

  const publishResponse = await client.publishPipeline(fixture.requests.publish.arguments.request);
  const submitResponse = await client.submitExecution(fixture.requests.submit.arguments.request);
  const observeResponse = await client.observeExecution(fixture.requests.observe.arguments.executionId);
  const resultResponse = await client.getExecutionResult(fixture.requests.result.arguments.executionId);
  const cancelResponse = await client.cancelExecution(
    fixture.requests.cancel.arguments.executionId,
    fixture.requests.cancel.arguments.request,
  );
  const pauseResponse = await client.pauseExecution(
    fixture.requests.pause.arguments.executionId,
    fixture.requests.pause.arguments.request,
  );
  const resumeResponse = await client.resumeExecution(
    fixture.requests.resume.arguments.executionId,
    fixture.requests.resume.arguments.request,
  );
  const inputResponse = await client.submitExecutionInput(
    fixture.requests.submitInput.arguments.executionId,
    fixture.requests.submitInput.arguments.request,
  );
  const replayResponse = await client.replayExecution(
    fixture.requests.replay.arguments.executionId,
    fixture.requests.replay.arguments.request,
  );

  assert.equal(publishResponse.publicationRef, "pub-parity");
  assert.equal(submitResponse.status, "Pending");
  assert.equal(observeResponse.status, "Running");
  assert.equal(resultResponse.status, "Completed");
  assert.equal(cancelResponse.cancellationRequested, true);
  assert.equal(pauseResponse.operation, "Pause");
  assert.equal(resumeResponse.operation, "Resume");
  assert.equal(inputResponse.operation, "SubmitInput");
  assert.equal(replayResponse.deterministic, true);

  for (const [index, name] of ["publish", "submit", "observe", "result", "cancel", "pause", "resume", "submitInput", "replay"].entries()) {
    assert.equal(transport.requests[index].protocolVersion, fixture.protocolVersion);
    assert.equal(transport.requests[index].operation, fixture.requests[name].operation);
    assert.deepEqual(transport.requests[index].arguments, fixture.requests[name].arguments);
  }
});

test("minimal defaults and omission semantics match the shared fixture", async () => {
  const transport = new FixtureTransport({
    publish: fixture.responses.publish,
    submit: fixture.responses.submit,
    cancel: fixture.responses.cancel,
    pause: fixture.responses.pause,
    resume: fixture.responses.resume,
    replay: fixture.responses.replay,
  });
  const client = new AiSdkClient(transport);

  await client.publishPipeline({ definition: { name: "minimal" } });
  await client.submitExecution({ publicationRef: "pub-minimal", input: null });
  await client.cancelExecution("exec-minimal", {});
  await client.pauseExecution("exec-minimal");
  await client.resumeExecution("exec-minimal");
  await client.replayExecution("exec-minimal");

  assert.deepEqual(transport.requests[0].arguments, fixture.minimalRequests.publish.arguments);
  assert.deepEqual(transport.requests[1].arguments, fixture.minimalRequests.submit.arguments);
  assert.deepEqual(transport.requests[2].arguments, fixture.minimalRequests.cancel.arguments);
  assert.deepEqual(transport.requests[3].arguments, fixture.minimalRequests.pause.arguments);
  assert.deepEqual(transport.requests[4].arguments, fixture.minimalRequests.resume.arguments);
  assert.deepEqual(transport.requests[5].arguments, fixture.minimalRequests.replay.arguments);
});

test("normalized authorization error preserves the shared failure semantics", async () => {
  const errorTransport = {
    async invoke() {
      return { error: structuredClone(fixture.normalizedError) };
    },
  };
  const client = new AiSdkClient(errorTransport);

  await assert.rejects(
    () => client.observeExecution("exec-parity"),
    (error) => error instanceof AiSdkException && assert.deepEqual(error.sdkError, fixture.normalizedError) === undefined,
  );
});
