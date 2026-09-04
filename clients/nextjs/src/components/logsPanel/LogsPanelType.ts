export type LogBadge = {
  label: string;
  color: string;
  background: string;
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