import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";

export interface AiSdkExecutionControlRequest {
  readonly schemaVersion?: typeof AI_SDK_SCHEMA_VERSIONS.executionControlRequest;
  readonly reason?: string;
  readonly correlationId?: string;
}
