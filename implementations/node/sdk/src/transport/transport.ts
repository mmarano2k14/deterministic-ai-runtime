import type { AiSdkCredentialProvider } from "../authentication/credential.js";
import type { AiSdkError } from "../errors/sdk-error.js";
import type { AiSdkJsonObject, AiSdkJsonValue } from "../json/json-value.js";
import type { AiSdkOperationName } from "../protocol/operations.js";
import { AI_SDK_PROTOCOL_VERSION } from "../protocol/operations.js";

export interface AiSdkTransportRequest {
  readonly protocolVersion: typeof AI_SDK_PROTOCOL_VERSION;
  readonly operation: AiSdkOperationName;
  readonly arguments: AiSdkJsonObject;
}

export type AiSdkTransportResponse =
  | {
      readonly result: AiSdkJsonValue;
      readonly error?: never;
    }
  | {
      readonly result?: never;
      readonly error: AiSdkError;
    };

export interface AiSdkTransportOptions {
  readonly credentialProvider?: AiSdkCredentialProvider;
  /** Additional public-boundary headers. Authorization remains credential-provider owned. */
  readonly additionalHeaders?: Readonly<Record<string, string>>;
  readonly safeReadMaxAttempts?: number;
  readonly safeReadRetryDelayMs?: number;
  readonly clientName?: string;
  readonly clientVersion?: string;
}

export interface AiSdkTransport {
  invoke(request: AiSdkTransportRequest, signal?: AbortSignal): Promise<AiSdkTransportResponse>;
}
