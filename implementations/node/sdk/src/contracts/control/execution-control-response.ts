import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkExecutionControlOperation } from "./execution-control-operation.js";
import type { AiSdkExecutionControlState } from "./execution-control-state.js";

export interface AiSdkExecutionControlResponse {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.executionControlResponse;
  readonly executionId: string;
  readonly operation: AiSdkExecutionControlOperation;
  readonly accepted: boolean;
  readonly state?: AiSdkExecutionControlState;
  readonly acceptedAtUtc: string;
  readonly correlationId?: string;
}
