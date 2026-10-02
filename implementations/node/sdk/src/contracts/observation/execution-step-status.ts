export const AI_SDK_EXECUTION_STEP_STATUSES = [
  "Pending",
  "Ready",
  "Running",
  "WaitingForRetry",
  "WaitingForExternal",
  "Completed",
  "Failed",
] as const;

export type AiSdkExecutionStepStatus = (typeof AI_SDK_EXECUTION_STEP_STATUSES)[number];
