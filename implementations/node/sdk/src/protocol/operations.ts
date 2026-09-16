export const AI_SDK_PROTOCOL_VERSION = 1 as const;

export const AI_SDK_OPERATIONS = {
  publishPipeline: "sdk.publish_pipeline",
  submitExecution: "sdk.execution.submit",
  observeExecution: "sdk.execution.observe",
  getExecutionResult: "sdk.execution.result",
  cancelExecution: "sdk.execution.cancel",
} as const;

export type AiSdkOperationName =
  (typeof AI_SDK_OPERATIONS)[keyof typeof AI_SDK_OPERATIONS];

export type AiSdkTransportRetryMode = "never" | "safe-read";

export const AI_SDK_OPERATION_RETRY: Readonly<Record<AiSdkOperationName, AiSdkTransportRetryMode>> = {
  [AI_SDK_OPERATIONS.publishPipeline]: "never",
  [AI_SDK_OPERATIONS.submitExecution]: "never",
  [AI_SDK_OPERATIONS.observeExecution]: "safe-read",
  [AI_SDK_OPERATIONS.getExecutionResult]: "safe-read",
  [AI_SDK_OPERATIONS.cancelExecution]: "never",
};

export function isAiSdkOperationName(value: string): value is AiSdkOperationName {
  return Object.values(AI_SDK_OPERATIONS).some((operation) => operation === value);
}
