export const AI_SDK_EXECUTION_WATCH_EVENT_KINDS = [
  "Snapshot",
  "Event",
  "ResyncRequired",
] as const;

export type AiSdkExecutionWatchEventKind =
  (typeof AI_SDK_EXECUTION_WATCH_EVENT_KINDS)[number];
