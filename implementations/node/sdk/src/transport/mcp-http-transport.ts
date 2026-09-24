import { Client, StreamableHTTPClientTransport } from "@modelcontextprotocol/client";
import { createAiSdkError } from "../errors/sdk-error.js";
import { isAiSdkJsonObject } from "../json/json-value.js";
import {
  AI_SDK_OPERATION_RETRY,
  AI_SDK_PROTOCOL_VERSION,
} from "../protocol/operations.js";
import type {
  AiSdkTransport,
  AiSdkTransportOptions,
  AiSdkTransportRequest,
  AiSdkTransportResponse,
} from "./transport.js";

const DEFAULT_SAFE_READ_MAX_ATTEMPTS = 2;
const DEFAULT_SAFE_READ_RETRY_DELAY_MS = 100;
const DEFAULT_CLIENT_NAME = "Multiplexed.AI.Sdk.TypeScript";
const DEFAULT_CLIENT_VERSION = "0.0.0";
const DEFAULT_ACCESS_CONTEXT_HEADER = "X-Access-Context";

export class AiSdkMcpHttpTransport implements AiSdkTransport {
  readonly #endpoint: URL;
  readonly #options: Required<
    Pick<AiSdkTransportOptions, "safeReadMaxAttempts" | "safeReadRetryDelayMs" | "clientName" | "clientVersion">
  > & Pick<AiSdkTransportOptions, "credentialProvider" | "additionalHeaders" | "accessContextHeaderName">;
  readonly #accessContextHeaderName: string | undefined;
  #currentAccessContext: string | undefined;

  public constructor(endpoint: URL, options: AiSdkTransportOptions = {}) {
    if (!(endpoint instanceof URL) || !["http:", "https:"].includes(endpoint.protocol)) {
      throw new TypeError("The MCP endpoint must be an absolute HTTP or HTTPS URL.");
    }

    const safeReadMaxAttempts = options.safeReadMaxAttempts ?? DEFAULT_SAFE_READ_MAX_ATTEMPTS;
    const safeReadRetryDelayMs = options.safeReadRetryDelayMs ?? DEFAULT_SAFE_READ_RETRY_DELAY_MS;

    if (!Number.isInteger(safeReadMaxAttempts) || safeReadMaxAttempts < 1) {
      throw new RangeError("safeReadMaxAttempts must be an integer greater than or equal to 1.");
    }

    if (!Number.isFinite(safeReadRetryDelayMs) || safeReadRetryDelayMs < 0) {
      throw new RangeError("safeReadRetryDelayMs must be a non-negative finite number.");
    }

    this.#endpoint = new URL(endpoint.toString());

    this.#accessContextHeaderName = normalizeAccessContextHeaderName(
      options.accessContextHeaderName === undefined
        ? DEFAULT_ACCESS_CONTEXT_HEADER
        : options.accessContextHeaderName,
    );

    const validatedAdditionalHeaders =
      options.additionalHeaders === undefined
        ? undefined
        : validateAdditionalHeaders(options.additionalHeaders);

    this.#currentAccessContext =
      this.#accessContextHeaderName === undefined || validatedAdditionalHeaders === undefined
        ? undefined
        : headerValue(validatedAdditionalHeaders, this.#accessContextHeaderName);

    this.#options = {
      ...(options.credentialProvider === undefined
        ? {}
        : { credentialProvider: options.credentialProvider }),
      ...(validatedAdditionalHeaders === undefined
        ? {}
        : { additionalHeaders: validatedAdditionalHeaders }),
      ...(options.accessContextHeaderName === undefined
        ? {}
        : { accessContextHeaderName: options.accessContextHeaderName }),
      safeReadMaxAttempts,
      safeReadRetryDelayMs,
      clientName: options.clientName ?? DEFAULT_CLIENT_NAME,
      clientVersion: options.clientVersion ?? DEFAULT_CLIENT_VERSION,
    };
  }

  public async invoke(
    request: AiSdkTransportRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkTransportResponse> {
    signal?.throwIfAborted();

    if (request.protocolVersion !== AI_SDK_PROTOCOL_VERSION) {
      return {
        error: createAiSdkError(
          "unsupported_schema",
          "unsupported_protocol",
          `Unsupported SDK protocol version '${request.protocolVersion}'. Expected '${AI_SDK_PROTOCOL_VERSION}'.`,
        ),
      };
    }

    const safeRead = AI_SDK_OPERATION_RETRY[request.operation] === "safe-read";
    const attempts = safeRead ? this.#options.safeReadMaxAttempts : 1;

    for (let attempt = 1; attempt <= attempts; attempt++) {
      signal?.throwIfAborted();

      try {
        return await this.#invokeOnce(request, signal);
      } catch (error) {
        if (signal?.aborted === true) {
          signal.throwIfAborted();
        }

        const retryable = safeRead && isRetryableTransportFailure(error);
        if (!retryable || attempt >= attempts) {
          return { error: normalizeTransportFailure(error, retryable) };
        }

        await delay(this.#options.safeReadRetryDelayMs, signal);
      }
    }

    return {
      error: createAiSdkError(
        "transport",
        "transport_failure",
        "The MCP transport did not produce a response.",
      ),
    };
  }

  async #invokeOnce(
    request: AiSdkTransportRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkTransportResponse> {
    const headers = await this.#createHeaders(signal);
    if ("error" in headers) {
      return { error: headers.error };
    }

    const transport = new StreamableHTTPClientTransport(
      this.#endpoint,
      headers.value === undefined
        ? undefined
        : {
            requestInit: {
              headers: headers.value,
            },
            fetch: createRotatingAccessContextFetch(
              globalThis.fetch.bind(globalThis),
              this.#accessContextHeaderName,
              () => this.#currentAccessContext,
              (value) => {
                this.#currentAccessContext = value;
              },
            ),
          },
    );

    const client = new Client({
      name: this.#options.clientName,
      version: this.#options.clientVersion,
    });

    try {
      await client.connect(transport);
      signal?.throwIfAborted();

      const result = await client.callTool(
        {
          name: request.operation,
          arguments: request.arguments,
        },
        signal === undefined ? undefined : { signal },
      );

      if (result.isError === true) {
        return {
          error: createAiSdkError(
            "remote_failure",
            "remote_tool_error",
            `The remote SDK operation '${request.operation}' returned an error.`,
          ),
        };
      }

      if (!isAiSdkJsonObject(result.structuredContent)) {
        return {
          error: createAiSdkError(
            "invalid_response",
            "missing_structured_content",
            `The remote SDK operation '${request.operation}' did not return an object structured result.`,
          ),
        };
      }

      return { result: result.structuredContent };
    } finally {
      await closeMcpClient(client, transport);
    }
  }

  async #createHeaders(
    signal?: AbortSignal,
  ): Promise<{ readonly value?: Readonly<Record<string, string>> } | { readonly error: ReturnType<typeof createAiSdkError> }> {
    const headers: Record<string, string> = { ...(this.#options.additionalHeaders ?? {}) };

    if (this.#accessContextHeaderName !== undefined && this.#currentAccessContext !== undefined) {
      setHeaderCaseInsensitive(headers, this.#accessContextHeaderName, this.#currentAccessContext);
    }
    if (this.#options.credentialProvider === undefined) {
      return Object.keys(headers).length === 0 ? {} : { value: headers };
    }

    try {
      const credential = await this.#options.credentialProvider.getCredential(signal);
      signal?.throwIfAborted();

      if (credential === null) {
        return Object.keys(headers).length === 0 ? {} : { value: headers };
      }

      if (credential.scheme.trim().length === 0 || credential.value.trim().length === 0) {
        return {
          error: createAiSdkError(
            "authentication",
            "invalid_credential",
            "The SDK credential provider returned an invalid credential.",
          ),
        };
      }

      headers.Authorization = `${credential.scheme} ${credential.value}`;
      return { value: headers };
    } catch (error) {
      if (signal?.aborted === true) {
        signal.throwIfAborted();
      }

      return {
        error: createAiSdkError(
          "authentication",
          "credential_provider_failure",
          "The SDK credential provider failed to supply a transport credential.",
          false,
          { cause: errorMessage(error) },
        ),
      };
    }
  }
}


export function createRotatingAccessContextFetch(
  baseFetch: typeof fetch,
  headerName: string | undefined,
  current: () => string | undefined,
  observe: (value: string) => void,
): typeof fetch {
  if (headerName === undefined) {
    return baseFetch;
  }

  return async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const headers = new Headers(
      input instanceof Request ? input.headers : undefined,
    );

    if (init?.headers !== undefined) {
      new Headers(init.headers).forEach((value, name) => {
        headers.set(name, value);
      });
    }

    const accessContext = current();
    if (accessContext !== undefined && accessContext.trim().length > 0) {
      headers.set(headerName, accessContext);
    }

    const response = await baseFetch(input, {
      ...init,
      headers,
    });

    const rotated = response.headers.get(headerName);
    if (rotated !== null &&
        rotated.trim().length > 0 &&
        !rotated.includes("\r") &&
        !rotated.includes("\n")) {
      observe(rotated.trim());
    }

    return response;
  };
}

function normalizeAccessContextHeaderName(
  headerName: string | null,
): string | undefined {
  if (headerName === null) {
    return undefined;
  }

  const normalized = headerName.trim();
  if (normalized.length === 0 ||
      normalized.includes(":") ||
      normalized.includes("\r") ||
      normalized.includes("\n")) {
    throw new TypeError(
      "accessContextHeaderName must be a valid HTTP header name or null.",
    );
  }

  return normalized;
}

function headerValue(
  headers: Readonly<Record<string, string>>,
  name: string,
): string | undefined {
  const expected = name.toLowerCase();

  for (const [headerName, value] of Object.entries(headers)) {
    if (headerName.toLowerCase() === expected) {
      return value;
    }
  }

  return undefined;
}

function setHeaderCaseInsensitive(
  headers: Record<string, string>,
  name: string,
  value: string,
): void {
  const expected = name.toLowerCase();

  for (const headerName of Object.keys(headers)) {
    if (headerName.toLowerCase() === expected && headerName !== name) {
      delete headers[headerName];
    }
  }

  headers[name] = value;
}

function validateAdditionalHeaders(headers: Readonly<Record<string, string>>): Readonly<Record<string, string>> {
  const validated: Record<string, string> = {};
  for (const [name, value] of Object.entries(headers)) {
    if (name.trim().length === 0 || value.trim().length === 0 ||
        name.toLowerCase() === "authorization" || name.includes("\r") || name.includes("\n") ||
        name.includes(":") || value.includes("\r") || value.includes("\n")) {
      throw new TypeError("Additional transport headers must be non-empty, single-line headers and cannot override Authorization.");
    }
    validated[name] = value;
  }
  return Object.freeze(validated);
}

async function closeMcpClient(
  client: Client,
  transport: StreamableHTTPClientTransport,
): Promise<void> {
  try {
    await transport.terminateSession();
  } catch {
    // Cleanup is best effort after the operation outcome is already known.
  }

  try {
    await client.close();
  } catch {
    // Cleanup failure must not replace the operation outcome.
  }
}

function isRetryableTransportFailure(error: unknown): boolean {
  const status = statusCode(error);
  if (status !== undefined) {
    return status === 408 || status === 429 || status >= 500;
  }

  if (!(error instanceof Error)) {
    return false;
  }

  if (error.name === "ProtocolError") {
    return false;
  }

  if (error.name === "TimeoutError" || error.name === "TypeError") {
    return true;
  }

  const code = errorCode(error);
  return code !== undefined && [
    "ECONNABORTED",
    "ECONNREFUSED",
    "ECONNRESET",
    "EHOSTUNREACH",
    "ENETUNREACH",
    "ETIMEDOUT",
  ].includes(code);
}

function normalizeTransportFailure(error: unknown, retryable: boolean) {
  const status = statusCode(error);

  if (status === 401) {
    return createAiSdkError(
      "authentication",
      "authentication_failed",
      "The SDK transport was not authenticated.",
    );
  }

  if (status === 403) {
    return createAiSdkError(
      "authorization",
      "authorization_failed",
      "The SDK transport is not authorized for the requested operation.",
    );
  }

  return createAiSdkError(
    "transport",
    error instanceof Error && error.name === "TimeoutError"
      ? "transport_timeout"
      : "transport_failure",
    errorMessage(error),
    retryable,
  );
}

function statusCode(error: unknown): number | undefined {
  if (typeof error !== "object" || error === null) {
    return undefined;
  }

  const record = error as Record<string, unknown>;
  for (const key of ["status", "statusCode"]) {
    const value = record[key];
    if (typeof value === "number") {
      return value;
    }
  }

  const response = record.response;
  if (typeof response === "object" && response !== null) {
    const value = (response as Record<string, unknown>).status;
    if (typeof value === "number") {
      return value;
    }
  }

  return undefined;
}

function errorCode(error: Error): string | undefined {
  const value = (error as Error & { readonly code?: unknown }).code;
  return typeof value === "string" ? value : undefined;
}

function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message.trim().length > 0) {
    return error.message;
  }

  return "The MCP transport failed before a normalized response was returned.";
}

function delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  if (milliseconds <= 0) {
    signal?.throwIfAborted();
    return Promise.resolve();
  }

  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, milliseconds);

    const onAbort = (): void => {
      clearTimeout(timer);
      reject(signal?.reason ?? new DOMException("The operation was aborted.", "AbortError"));
    };

    if (signal === undefined) {
      return;
    }

    if (signal.aborted) {
      onAbort();
      return;
    }

    signal.addEventListener("abort", onAbort, { once: true });
  });
}
