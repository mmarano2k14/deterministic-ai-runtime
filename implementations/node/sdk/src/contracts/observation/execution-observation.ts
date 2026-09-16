import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkExecutionStatus } from "../executions/execution-status.js";
import type { AiSdkExecutionStepObservation } from "./execution-step-observation.js";

export interface AiSdkExecutionObservation {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.executionObservation;
  readonly executionId: string;
  readonly publicationRef: string;
  readonly pipelineName: string;
  readonly pipelineVersion: string;
  readonly status: AiSdkExecutionStatus;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
  readonly completedAtUtc?: string;
  readonly steps: readonly AiSdkExecutionStepObservation[];
}
