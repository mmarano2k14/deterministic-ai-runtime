import assert from "node:assert/strict";
import test from "node:test";
import {
  AiSdkMcpHttpTransport,
  AI_SDK_OPERATIONS,
} from "../dist/index.js";

test("MCP transport rejects non HTTP endpoints", () => {
  assert.throws(
    () => new AiSdkMcpHttpTransport(new URL("file:///tmp/mcp")),
    /HTTP or HTTPS/,
  );
});

test("MCP transport validates safe-read attempt configuration", () => {
  assert.throws(
    () =>
      new AiSdkMcpHttpTransport(new URL("https://example.test/mcp"), {
        safeReadMaxAttempts: 0,
      }),
    /greater than or equal to 1/,
  );
});

test("MCP transport fails closed before I/O for unsupported protocol versions", async () => {
  const transport = new AiSdkMcpHttpTransport(new URL("https://example.test/mcp"));
  const response = await transport.invoke({
    protocolVersion: 999,
    operation: AI_SDK_OPERATIONS.observeExecution,
    arguments: { executionId: "exec-1" },
  });

  assert.equal(response.error?.kind, "unsupported_schema");
  assert.equal(response.error?.code, "unsupported_protocol");
});

test("Watch is classified as a safe-read transport operation", async () => {
  const { AI_SDK_OPERATION_RETRY } = await import("../dist/index.js");
  assert.equal(AI_SDK_OPERATION_RETRY[AI_SDK_OPERATIONS.watchExecution], "safe-read");
});

test("execution control and replay operations are never automatically retried", async () => {
  const { AI_SDK_OPERATION_RETRY } = await import("../dist/index.js");
  for (const operation of [
    AI_SDK_OPERATIONS.pauseExecution,
    AI_SDK_OPERATIONS.resumeExecution,
    AI_SDK_OPERATIONS.submitExecutionInput,
    AI_SDK_OPERATIONS.replayExecution,
  ]) {
    assert.equal(AI_SDK_OPERATION_RETRY[operation], "never");
  }
});
