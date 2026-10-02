import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";

export interface AiSdkExecutionCancellationRequest {
  readonly schemaVersion?: typeof AI_SDK_SCHEMA_VERSIONS.executionCancellationRequest;
  readonly reason?: string;
  readonly correlationId?: string;
}
