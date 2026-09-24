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


test("rotating access-context fetch applies the latest response handle to the next request", async () => {
  const { createRotatingAccessContextFetch } = await import(
    "../dist/transport/mcp-http-transport.js"
  );

  let current = "ctx-initial";
  const seen = [];
  let responseIndex = 0;

  const baseFetch = async (_input, init) => {
    const headers = new Headers(init?.headers);
    seen.push(headers.get("X-Access-Context"));
    responseIndex += 1;

    return new Response("{}", {
      status: 200,
      headers: {
        "X-Access-Context": `ctx-rotated-${responseIndex}`,
      },
    });
  };

  const rotatingFetch = createRotatingAccessContextFetch(
    baseFetch,
    "X-Access-Context",
    () => current,
    (value) => {
      current = value;
    },
  );

  await rotatingFetch(new URL("https://runtime.example/mcp"), {
    method: "POST",
  });
  await rotatingFetch(new URL("https://runtime.example/mcp"), {
    method: "POST",
  });

  assert.deepEqual(seen, ["ctx-initial", "ctx-rotated-1"]);
  assert.equal(current, "ctx-rotated-2");
});

test("MCP transport accepts a custom rotating access-context header name", () => {
  assert.doesNotThrow(
    () =>
      new AiSdkMcpHttpTransport(new URL("https://example.test/mcp"), {
        accessContextHeaderName: "X-Custom-Context",
        additionalHeaders: {
          "X-Custom-Context": "ctx-1",
        },
      }),
  );
});
