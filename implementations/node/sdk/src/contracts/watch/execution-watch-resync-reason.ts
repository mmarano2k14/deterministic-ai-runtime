export const AI_SDK_EXECUTION_WATCH_RESYNC_REASONS = [
  "HistoryUnavailable",
  "GapDetected",
  "InvalidCursor",
] as const;

export type AiSdkExecutionWatchResyncReason =
  (typeof AI_SDK_EXECUTION_WATCH_RESYNC_REASONS)[number];
