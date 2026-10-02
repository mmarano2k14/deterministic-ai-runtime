export const AI_SDK_EXECUTION_STATUSES = [
  "Pending",
  "Running",
  "Waiting",
  "Completed",
  "Failed",
  "Cancelled",
] as const;

export type AiSdkExecutionStatus = (typeof AI_SDK_EXECUTION_STATUSES)[number];
