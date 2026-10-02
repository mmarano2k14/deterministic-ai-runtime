import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";

export interface AiSdkExecutionReplayRequest {
  readonly schemaVersion?: typeof AI_SDK_SCHEMA_VERSIONS.executionReplayRequest;
  readonly strictDeterminism?: boolean;
  readonly includeDiagnostics?: boolean;
  readonly reason?: string;
  readonly correlationId?: string;
}
