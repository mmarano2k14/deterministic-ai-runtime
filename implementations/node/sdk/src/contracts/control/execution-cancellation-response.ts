import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkExecutionStatus } from "../executions/execution-status.js";

export interface AiSdkExecutionCancellationResponse {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.executionCancellationResponse;
  readonly executionId: string;
  readonly cancellationRequested: boolean;
  readonly status: AiSdkExecutionStatus;
  readonly requestedAtUtc?: string;
  readonly correlationId?: string;
}
