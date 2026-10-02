import type { AiSdkExecutionStepStatus } from "./execution-step-status.js";

export interface AiSdkExecutionStepObservation {
  readonly name: string;
  readonly stepKey: string;
  readonly status: AiSdkExecutionStepStatus;
  readonly startedAtUtc?: string;
  readonly updatedAtUtc?: string;
  readonly completedAtUtc?: string;
}
