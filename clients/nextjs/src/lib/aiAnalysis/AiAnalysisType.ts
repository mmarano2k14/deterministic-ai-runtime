export type AiAnalysisScope =
  | "current-run"
  | "current-execution"
  | "selected-logs"
  | "last-30s";

export type AiAnalysisQuickActionKey =
  | "analyze"
  | "explain-failures"
  | "find-anomaly"
  | "suggest-scenario";

export type AiAnalysisStatus =
  | "provider-pending"
  | "ready"
  | "analyzing"
  | "finding-available";

export type AiAnalysisScopeDefinition = {
  key: AiAnalysisScope;
  label: string;
  description: string;
};

export type AiAnalysisQuickActionDefinition = {
  key: AiAnalysisQuickActionKey;
  label: string;
  prompt: string;
};

export type AiAnalysisStatusDefinition = {
  key: AiAnalysisStatus;
  label: string;
  description: string;
};

export type AiAnalysisContextMetric = {
  label: string;
  value: string;
};

export type AiAnalysisContextSnapshot = {
  title: string;
  subtitle: string;
  metrics: AiAnalysisContextMetric[];
};
