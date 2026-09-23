export const AI_SDK_EXECUTION_CONTROL_STATUSES = [
  "None",
  "Running",
  "Pausing",
  "Paused",
  "Resuming",
  "Cancelling",
  "Cancelled",
  "WaitingForInput",
] as const;

export type AiSdkExecutionControlStatus =
  (typeof AI_SDK_EXECUTION_CONTROL_STATUSES)[number];
