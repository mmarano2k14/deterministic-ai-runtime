export const AI_SDK_EXECUTION_WATCH_CHANNELS = [
  "Lifecycle",
  "Steps",
  "Policies",
  "Children",
  "Effects",
  "Recovery",
] as const;

export type AiSdkExecutionWatchChannel =
  (typeof AI_SDK_EXECUTION_WATCH_CHANNELS)[number];
