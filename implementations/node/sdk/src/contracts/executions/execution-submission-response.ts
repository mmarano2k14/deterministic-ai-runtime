import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkExecutionStatus } from "./execution-status.js";

export interface AiSdkExecutionSubmissionResponse {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.executionSubmissionResponse;
  readonly executionId: string;
  readonly publicationRef: string;
  readonly status: AiSdkExecutionStatus;
  readonly acceptedAtUtc: string;
  readonly idempotencyKey?: string;
  readonly correlationId?: string;
}
