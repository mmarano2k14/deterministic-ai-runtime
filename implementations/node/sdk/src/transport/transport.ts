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

export interface AiSdkTransportResponse {
  readonly result?: AiSdkJsonValue;
  readonly error?: AiSdkError;
}

export interface AiSdkTransportOptions {
  readonly credentialProvider?: AiSdkCredentialProvider;
}

export interface AiSdkTransport {
  invoke(request: AiSdkTransportRequest, signal?: AbortSignal): Promise<AiSdkTransportResponse>;
}
