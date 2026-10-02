import type { AiSdkJsonValue } from "../../json/json-value.js";
import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkExecutionFailure } from "./execution-failure.js";
import type { AiSdkExecutionStatus } from "./execution-status.js";

export interface AiSdkExecutionResult {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.executionResult;
  readonly executionId: string;
  readonly status: AiSdkExecutionStatus;
  readonly output?: AiSdkJsonValue;
  readonly failure?: AiSdkExecutionFailure;
  readonly completedAtUtc: string;
}
