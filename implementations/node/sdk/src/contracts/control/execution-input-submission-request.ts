import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkJsonObject } from "../../json/json-value.js";

export interface AiSdkExecutionInputSubmissionRequest {
  readonly schemaVersion?: typeof AI_SDK_SCHEMA_VERSIONS.executionInputSubmissionRequest;
  readonly waitingKey: string;
  readonly waitingStepName?: string;
  readonly input: AiSdkJsonObject;
  readonly reason?: string;
  readonly correlationId?: string;
}
