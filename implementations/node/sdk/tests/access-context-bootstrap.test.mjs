import assert from "node:assert/strict";
import http from "node:http";
import test from "node:test";
import {
  AiSdkAccessContextBootstrapper,
  AiSdkException,
  AiSdkStaticCredentialProvider,
} from "../dist/index.js";

async function withServer(handler, action) {
  const server = http.createServer(handler);
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });

  const address = server.address();
  assert.ok(address && typeof address !== "string");
  const endpoint = new URL(`http://127.0.0.1:${address.port}/auth/access-context`);

  try {
    return await action(endpoint);
  } finally {
    await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
}

test("access-context bootstrap performs one authenticated POST and returns the handle", async () => {
  let calls = 0;

  const result = await withServer((request, response) => {
    calls += 1;
    assert.equal(request.method, "POST");
    assert.equal(request.url, "/auth/access-context");
    assert.equal(request.headers.authorization, "Bearer token-123");
    response.statusCode = 204;
    response.setHeader("X-Access-Context", "ctx_bootstrap_123");
    response.end();
  }, async (endpoint) => AiSdkAccessContextBootstrapper.create({
    endpoint,
    credentialProvider: new AiSdkStaticCredentialProvider({ scheme: "Bearer", value: "token-123" }),
  }));

  assert.equal(calls, 1);
  assert.equal(result.headerName, "X-Access-Context");
  assert.equal(result.accessContext, "ctx_bootstrap_123");
});

test("access-context bootstrap does not retry a failed state-changing request", async () => {
  let calls = 0;

  await assert.rejects(
    () => withServer((_request, response) => {
      calls += 1;
      response.statusCode = 503;
      response.end();
    }, async (endpoint) => AiSdkAccessContextBootstrapper.create({ endpoint })),
    (error) => {
      assert.ok(error instanceof AiSdkException);
      assert.equal(error.sdkError.kind, "remote_failure");
      assert.equal(error.sdkError.code, "access_context_bootstrap_failed");
      return true;
    },
  );

  assert.equal(calls, 1);
});

test("access-context bootstrap maps 401 to an authentication SDK error", async () => {
  await assert.rejects(
    () => withServer((_request, response) => {
      response.statusCode = 401;
      response.end();
    }, async (endpoint) => AiSdkAccessContextBootstrapper.create({ endpoint })),
    (error) => {
      assert.ok(error instanceof AiSdkException);
      assert.equal(error.sdkError.kind, "authentication");
      assert.equal(error.sdkError.code, "access_context_bootstrap_unauthenticated");
      return true;
    },
  );
});

test("access-context bootstrap rejects a successful response without an unambiguous handle", async () => {
  await assert.rejects(
    () => withServer((_request, response) => {
      response.statusCode = 204;
      response.end();
    }, async (endpoint) => AiSdkAccessContextBootstrapper.create({ endpoint })),
    (error) => {
      assert.ok(error instanceof AiSdkException);
      assert.equal(error.sdkError.kind, "invalid_response");
      assert.equal(error.sdkError.code, "access_context_bootstrap_missing_handle");
      return true;
    },
  );
});
