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

export type RuntimeAnalysisPreparedContext = {
  request: RuntimeAnalysisSnapshotRequest;
  snapshot: RuntimeAnalysisSnapshot;
};

export type RuntimeAnalysisProviderStatus = {
  provider: string;
  model: string;
  configured: boolean;
};

export type RuntimeAnalysisObservation = {
  title: string;
  detail: string;
  evidenceIndexes: number[];
};

export type RuntimeAnalysisSuggestedScenario = {
  name: string;
  rationale: string;
  scenarioType:
    | "single-burst"
    | "maintained-concurrency"
    | "wave-batches"
    | "wave-batches-staggered"
    | "custom";
  totalRequests: number;
  concurrency: number | null;
  batchSize: number | null;
  delayMs: number;
  wavePauseMs: number | null;
  maxInFlight: number;
  rotationOverlapMs: number;
  durationSeconds: number | null;
};

export type RuntimeAnalysisResult = {
  answer: string;
  summary: string;
  severity: "info" | "low" | "medium" | "high" | "critical";
  confidence: number;
  observations: RuntimeAnalysisObservation[];
  suggestedScenario: RuntimeAnalysisSuggestedScenario;
};

export type RuntimeAnalysisAnalyzeRequest = {
  question: string;
  snapshotRequest: RuntimeAnalysisSnapshotRequest;
};

export type RuntimeAnalysisScenarioPolicyDecision = {
  policyKey: string;
  resultKind: string;
  allowed: boolean;
  message: string;
};

export type RuntimeAnalysisScenarioPolicyValidationResult = {
  allowed: boolean;
  requiresHumanApproval: boolean;
  planKey: string;
  scenario: RuntimeAnalysisSuggestedScenario;
  policyDecisions: RuntimeAnalysisScenarioPolicyDecision[];
};

export type RuntimeAnalysisHumanApprovalStatus =
  | "Pending"
  | "Approved"
  | "Rejected"
  | "NotRequired";

export type RuntimeAnalysisHumanApprovalResult = {
  required: boolean;
  status: RuntimeAnalysisHumanApprovalStatus;
  continuationId: string | null;
  requestedAtUtc: string | null;
  decidedAtUtc: string | null;
  decidedBy: string | null;
  message: string | null;
};

export type RuntimeAnalysisHumanApprovalDecision =
  | "approve"
  | "reject";

export type RuntimeAnalysisRuntimeExecutionResult = {
  runId: string;
  continuationRunId: string | null;
  executionId: string;
  pipelineName: string;
  stepName: string;
  runtimeStatus: string;
  result: RuntimeAnalysisResult;
  policyValidation: RuntimeAnalysisScenarioPolicyValidationResult;
  humanApproval: RuntimeAnalysisHumanApprovalResult;
  scenarioExecution: RuntimeAnalysisScenarioExecutionResult;
  verification: RuntimeAnalysisVerificationResult;
};


export type RuntimeAnalysisScenarioExecutionStatus =
  | "NotStarted"
  | "Pending"
  | "Completed"
  | "Failed"
  | "NotExecuted";

export type RuntimeAnalysisScenarioExecutionObservation = {
  clientState: string;
  startedAtUtc: string;
  finishedAtUtc: string;
  completed: number;
  inFlight: number;
  ok: number;
  unauthorized: number;
  forbidden: number;
  tooManyRequests: number;
  otherHttp: number;
  errors: number;
  p50Ms: number | null;
  p95Ms: number | null;
  elapsedMs: number | null;
  error: string | null;
};

export type RuntimeAnalysisScenarioExecutionResult = {
  required: boolean;
  status: RuntimeAnalysisScenarioExecutionStatus;
  continuationId: string | null;
  requestedAtUtc: string | null;
  completedAtUtc: string | null;
  scenario: RuntimeAnalysisSuggestedScenario;
  planKey: string;
  observation: RuntimeAnalysisScenarioExecutionObservation | null;
  completedBy: string | null;
  message: string | null;
};

export type RuntimeAnalysisVerificationStatus =
  | "Pending"
  | "Verified"
  | "Skipped";

export type RuntimeAnalysisVerificationResult = {
  status: RuntimeAnalysisVerificationStatus;
  executed: boolean;
  completedMatchesPlan: boolean;
  noResidualInFlight: boolean;
  outcomeCountConsistent: boolean;
  expectedRequests: number;
  observedCompleted: number;
  observedOk: number;
  observedHttpNonOk: number;
  observedErrors: number;
  baselineP50Ms: number | null;
  observedP50Ms: number | null;
  p50DeltaMs: number | null;
  baselineP95Ms: number | null;
  observedP95Ms: number | null;
  p95DeltaMs: number | null;
  summary: string;
};
