import type { AiSdkJsonObject } from "../json/json-value.js";

export type AiSdkErrorKind =
  | "transport"
  | "authentication"
  | "authorization"
  | "invalid_request"
  | "unsupported_schema"
  | "not_found"
  | "conflict"
  | "result_unavailable"
  | "remote_failure"
  | "invalid_response"
  | "cancelled";

export interface AiSdkError {
  readonly kind: AiSdkErrorKind;
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
  readonly details: AiSdkJsonObject;
}
