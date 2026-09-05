export type LogBadgeTone =
  | "neutral"
  | "info"
  | "success"
  | "warning"
  | "danger"
  | "purple";

export type LogBadge = {
  label: string;
  tone: LogBadgeTone;
};

export type LogFilterKind =
  | "all"
  | "http"
  | "rotation"
  | "http-error"
  | "realtime"
  | "context-key"
  | "runtime-engine"
  | "ai";