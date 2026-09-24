import type { AiSdkCredential, AiSdkCredentialProvider } from "./credential.js";
import { createAiSdkError } from "../errors/sdk-error.js";
import { AiSdkException } from "../errors/sdk-exception.js";

const DEFAULT_ACCESS_CONTEXT_HEADER = "X-Access-Context";
const DEFAULT_TIMEOUT_MS = 30_000;

export interface AiSdkAccessContextBootstrapOptions {
  /** Protected HTTP endpoint that creates one access-context handle. */
  readonly endpoint: URL;
  /** Credential provider used only for this explicit bootstrap request. */
  readonly credentialProvider?: AiSdkCredentialProvider;
  /** Response header carrying the created access-context handle. */
  readonly accessContextHeaderName?: string;
  /** Maximum duration of this one non-retried state-changing request. */
  readonly timeoutMs?: number;
  /** Optional fetch implementation for host integration and deterministic tests. */
  readonly fetch?: typeof fetch;
}

export interface AiSdkAccessContextBootstrapResult {
  readonly accessContext: string;
  readonly headerName: string;
}

/**
 * Creates the first runtime access-context handle over the authenticated public HTTP boundary.
 * This state-changing operation is deliberately never retried automatically.
 */
export class AiSdkAccessContextBootstrapper {
  public static async create(
    options: AiSdkAccessContextBootstrapOptions,
    signal?: AbortSignal,
  ): Promise<AiSdkAccessContextBootstrapResult> {
    const validated = validateOptions(options);
    signal?.throwIfAborted();

    const credential = await getCredential(validated.credentialProvider, signal);
    const headers = new Headers();

    if (credential !== null) {
      headers.set("Authorization", formatAuthorization(credential));
    }

    const controller = new AbortController();
    const onAbort = (): void => controller.abort(signal?.reason);
    signal?.addEventListener("abort", onAbort, { once: true });
    const timeout = setTimeout(
      () => controller.abort(new DOMException("Access-context bootstrap timed out.", "TimeoutError")),
      validated.timeoutMs,
    );

    try {
      let response: Response;
      try {
        response = await validated.fetch(validated.endpoint, {
          method: "POST",
          headers,
          signal: controller.signal,
        });
      } catch (error) {
        if (signal?.aborted === true) {
          signal.throwIfAborted();
        }

        if (controller.signal.aborted) {
          throw sdkException(
            "transport",
            "access_context_bootstrap_timeout",
            "The access-context bootstrap request timed out.",
            error,
          );
        }

        throw sdkException(
          "transport",
          "access_context_bootstrap_transport_failure",
          errorMessage(error),
          error,
        );
      }

      if (response.status === 401) {
        throw sdkException(
          "authentication",
          "access_context_bootstrap_unauthenticated",
          "The access-context bootstrap request was not authenticated.",
        );
      }

      if (response.status === 403) {
        throw sdkException(
          "authorization",
          "access_context_bootstrap_forbidden",
          "The authenticated identity is not allowed to create an access context.",
        );
      }

      if (!response.ok) {
        throw sdkException(
          "remote_failure",
          "access_context_bootstrap_failed",
          `The access-context bootstrap endpoint returned HTTP ${response.status}${response.statusText ? ` (${response.statusText})` : ""}.`,
        );
      }

      const accessContext = response.headers.get(validated.headerName)?.trim();
      if (!isUnambiguousHeaderValue(accessContext)) {
        throw sdkException(
          "invalid_response",
          "access_context_bootstrap_missing_handle",
          `The access-context bootstrap response did not contain one unambiguous '${validated.headerName}' header.`,
        );
      }

      return {
        accessContext,
        headerName: validated.headerName,
      };
    } finally {
      clearTimeout(timeout);
      signal?.removeEventListener("abort", onAbort);
    }
  }
}

type ValidatedOptions = Readonly<{
  endpoint: URL;
  credentialProvider?: AiSdkCredentialProvider;
  headerName: string;
  timeoutMs: number;
  fetch: typeof fetch;
}>;

function validateOptions(options: AiSdkAccessContextBootstrapOptions): ValidatedOptions {
  if (!(options.endpoint instanceof URL) || !["http:", "https:"].includes(options.endpoint.protocol)) {
    throw new TypeError("The access-context bootstrap endpoint must be an absolute HTTP or HTTPS URL.");
  }

  const headerName = (options.accessContextHeaderName ?? DEFAULT_ACCESS_CONTEXT_HEADER).trim();
  if (!isValidHeaderName(headerName)) {
    throw new TypeError("accessContextHeaderName must be a valid non-empty HTTP header name.");
  }

  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    throw new RangeError("timeoutMs must be a positive finite number.");
  }

  return {
    endpoint: new URL(options.endpoint.toString()),
    ...(options.credentialProvider === undefined
      ? {}
      : { credentialProvider: options.credentialProvider }),
    headerName,
    timeoutMs,
    fetch: options.fetch ?? globalThis.fetch.bind(globalThis),
  };
}

async function getCredential(
  provider: AiSdkCredentialProvider | undefined,
  signal?: AbortSignal,
): Promise<AiSdkCredential | null> {
  if (provider === undefined) {
    return null;
  }

  try {
    const credential = await provider.getCredential(signal);
    signal?.throwIfAborted();
    return credential;
  } catch (error) {
    if (signal?.aborted === true) {
      signal.throwIfAborted();
    }

    throw sdkException(
      "authentication",
      "credential_provider_failure",
      "The SDK credential provider failed to supply a bootstrap credential.",
      error,
    );
  }
}

function formatAuthorization(credential: AiSdkCredential): string {
  const scheme = credential.scheme.trim();
  const value = credential.value.trim();

  if (
    scheme.length === 0 ||
    value.length === 0 ||
    /[\s:\u0000-\u001f\u007f]/u.test(scheme) ||
    /[\r\n]/u.test(value)
  ) {
    throw sdkException(
      "authentication",
      "invalid_credential",
      "The SDK credential provider returned an invalid bootstrap credential.",
    );
  }

  return `${scheme} ${value}`;
}

function isValidHeaderName(value: string): boolean {
  return value.length > 0 && !/[:\r\n\u0000-\u001f\u007f]/u.test(value);
}

function isUnambiguousHeaderValue(value: string | null | undefined): value is string {
  return value !== null &&
    value !== undefined &&
    value.length > 0 &&
    !/[\r\n,]/u.test(value);
}

function sdkException(
  kind: Parameters<typeof createAiSdkError>[0],
  code: string,
  message: string,
  cause?: unknown,
): AiSdkException {
  const details = cause === undefined ? {} : { cause: errorMessage(cause) };
  return new AiSdkException(createAiSdkError(kind, code, message, false, details),
    cause instanceof Error ? { cause } : undefined);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
