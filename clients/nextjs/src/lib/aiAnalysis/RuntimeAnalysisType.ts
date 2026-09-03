import type { AiAnalysisScope } from "./AiAnalysisType";

export type RuntimeAnalysisScenarioInput = {
  name?: string;
  dispatchMode?: string;
  planKey?: string;
  totalRequests: number;
  concurrency?: number;
  batchSize?: number;
  delayMs?: number;
  wavePauseMs?: number;
  maxInFlight?: number;
  rotationOverlapMs?: number;
};

export type RuntimeAnalysisMetricsInput = {
  completed: number;
  inFlight: number;
  ok: number;
  unauthorized: number;
  forbidden: number;
  tooManyRequests: number;
  otherHttp: number;
  errors: number;
  p50Ms?: number;
  p95Ms?: number;
  elapsedMs?: number;
  liveLogCount: number;
};

export type RuntimeAnalysisEvidenceInput = {
  timestampUtc: string;
  category: string;
  eventType: string;
  message?: string;
  statusCode?: number;
  durationMs?: number;
  correlationId?: string;
  sharedRunId?: string;
  executionId?: string;
  dagId?: string;
  stepId?: string;
  childExecutionId?: string;
  policyKey?: string;
  metadata?: Record<string, string | null | undefined>;
};

export type RuntimeAnalysisSnapshotRequest = {
  scope: AiAnalysisScope;
  capturedAtUtc: string;
  scenario?: RuntimeAnalysisScenarioInput;
  metrics: RuntimeAnalysisMetricsInput;
  evidence: RuntimeAnalysisEvidenceInput[];
};

export type RuntimeAnalysisScenarioSnapshot = RuntimeAnalysisScenarioInput;

export type RuntimeAnalysisMetricsSnapshot = RuntimeAnalysisMetricsInput;

export type RuntimeAnalysisEvidence = RuntimeAnalysisEvidenceInput;

export type RuntimeAnalysisEvidenceSummary = {
  byCategory: Record<string, number>;
  byEventType: Record<string, number>;
  httpErrorCount: number;
  dagRelatedCount: number;
  policyRelatedCount: number;
  recoveryRelatedCount: number;
};

export type RuntimeAnalysisSnapshot = {
  scope: AiAnalysisScope;
  capturedAtUtc: string;
  scenario?: RuntimeAnalysisScenarioSnapshot;
  metrics: RuntimeAnalysisMetricsSnapshot;
  evidence: RuntimeAnalysisEvidence[];
  evidenceSummary: RuntimeAnalysisEvidenceSummary;
  evidenceReceivedCount: number;
  evidenceTruncated: boolean;
};
