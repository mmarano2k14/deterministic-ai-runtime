"use client";

import { JSX, useEffect, useMemo, useRef, useState } from "react";
import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import type { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import type { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import type {
  AiAnalysisScope,
  AiAnalysisStatus,
} from "@/lib/aiAnalysis/AiAnalysisType";
import { AiAnalysisContextSnapshotBuilder } from "@/lib/aiAnalysis/AiAnalysisContextSnapshotBuilder";
import { AiAnalysisUxModel } from "@/lib/aiAnalysis/AiAnalysisUxModel";
import { RuntimeAnalysisAnalysisService } from "@/lib/aiAnalysis/RuntimeAnalysisAnalysisService";
import { RuntimeAnalysisSnapshotService } from "@/lib/aiAnalysis/RuntimeAnalysisSnapshotService";
import { RuntimeAnalysisScenarioObservationBuilder } from "@/lib/aiAnalysis/RuntimeAnalysisScenarioObservationBuilder";
import type {
  RuntimeAnalysisChildDagRelationResult,
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisInvestigationMode,
  RuntimeAnalysisPreparedContext,
  RuntimeAnalysisProviderStatus,
  RuntimeAnalysisRuntimeExecutionResult,
  RuntimeAnalysisSuggestedScenario,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiAnalysisContextCard } from "./AiAnalysisContextCard";
import {
  AiInvestigationActivityDock,
  type AiOnlyActivity,
} from "./AiInvestigationActivityDock";
import { AiAnalysisPromptPanel } from "./AiAnalysisPromptPanel";
import { AiAnalysisResultCard } from "./AiAnalysisResultCard";
import { AiRuntimeExecutionCard } from "./AiRuntimeExecutionCard";
import { AiAnalysisSnapshotCard } from "./AiAnalysisSnapshotCard";
import { AiAnalysisStatusBadge } from "./AiAnalysisStatusBadge";
import styles from "./AiAnalysisPanel.module.css";

export type AiAnalysisPanelProps = {
  model: BurstRuntime;
  logs: readonly ConsoleLogEntry[];
  maxInFlight: string;
  rotationOverlapMs: string;
  api: MultiplexedRbacApi;
  onExecuteScenario: (
    scenario: RuntimeAnalysisSuggestedScenario,
    planKey: string
  ) => Promise<void>;
};

type PendingScenarioExecution =
  | {
      kind: "root";
      executionId: string;
      previousStartedAt: number | undefined;
    }
  | {
      kind: "child";
      rootExecutionId: string;
      childExecutionId: string;
      rootRunId: string;
      previousStartedAt: number | undefined;
    };

export function AiAnalysisPanel(props: AiAnalysisPanelProps): JSX.Element {
  const {
    model,
    logs,
    maxInFlight,
    rotationOverlapMs,
    api,
    onExecuteScenario,
  } = props;

  const [scope, setScope] = useState<AiAnalysisScope>("current-run");
  const [investigationMode, setInvestigationMode] =
    useState<RuntimeAnalysisInvestigationMode>(
      "stop-when-conclusive"
    );
  const [question, setQuestion] = useState(
    AiAnalysisUxModel.promptForAction("analyze")
  );
  const [providerStatus, setProviderStatus] =
    useState<RuntimeAnalysisProviderStatus | null>(null);
  const [isAnalyzing, setIsAnalyzing] = useState(false);
  const [isDecidingApproval, setIsDecidingApproval] = useState(false);
  const [decidingChildExecutionId, setDecidingChildExecutionId] =
    useState<string | null>(null);
  const [isExecutingScenario, setIsExecutingScenario] = useState(false);
  const [executingChildExecutionId, setExecutingChildExecutionId] =
    useState<string | null>(null);
  const [preparedContext, setPreparedContext] =
    useState<RuntimeAnalysisPreparedContext | null>(null);
  const [runtimeExecution, setRuntimeExecution] =
    useState<RuntimeAnalysisRuntimeExecutionResult | null>(null);
  const [snapshotError, setSnapshotError] = useState<string | null>(null);
  const [analysisError, setAnalysisError] = useState<string | null>(null);
  const [approvalError, setApprovalError] = useState<string | null>(null);
  const [scenarioExecutionError, setScenarioExecutionError] =
    useState<string | null>(null);
  const [childApprovalError, setChildApprovalError] =
    useState<string | null>(null);
  const [childScenarioExecutionError, setChildScenarioExecutionError] =
    useState<string | null>(null);
  const [pendingScenarioExecution, setPendingScenarioExecution] =
    useState<PendingScenarioExecution | null>(null);
  const reportingScenarioExecutionIdRef = useRef<string | null>(null);
  const approvedScenarioRunInProgressRef = useRef(false);
  const observedRunStartedAtRef = useRef<number | undefined>(
    model.report?.timing.startedAt
  );

  const contextSnapshot = useMemo(
    () => AiAnalysisContextSnapshotBuilder.build(model, logs.length),
    [model, logs.length]
  );

  const snapshotService = useMemo(
    () => new RuntimeAnalysisSnapshotService(api),
    [api]
  );

  const analysisService = useMemo(
    () => new RuntimeAnalysisAnalysisService(api),
    [api]
  );

  const runStartedAt = model.report?.timing.startedAt;

  useEffect(() => {
    const controller = new AbortController();

    analysisService
      .getProviderStatus(controller.signal)
      .then(setProviderStatus)
      .catch(() => setProviderStatus(null));

    return () => controller.abort();
  }, [analysisService]);

  useEffect(() => {
    setPreparedContext(null);
    setRuntimeExecution(null);
    setSnapshotError(null);
    setAnalysisError(null);
    setApprovalError(null);
    setScenarioExecutionError(null);
    setChildApprovalError(null);
    setChildScenarioExecutionError(null);
    setDecidingChildExecutionId(null);
    setExecutingChildExecutionId(null);
    setPendingScenarioExecution(null);
    reportingScenarioExecutionIdRef.current = null;
    approvedScenarioRunInProgressRef.current = false;
  }, [scope]);

  useEffect(() => {
    const previousStartedAt =
      observedRunStartedAtRef.current;

    observedRunStartedAtRef.current =
      runStartedAt;

    if (
      previousStartedAt === runStartedAt ||
      approvedScenarioRunInProgressRef.current
    ) {
      return;
    }

    // A new manually-started/external burst invalidates the previously
    // prepared analysis context. An AI-approved burst is deliberately excluded:
    // it is part of the same durable runtime-analysis workflow.
    setPreparedContext(null);
    setRuntimeExecution(null);
    setSnapshotError(null);
    setAnalysisError(null);
    setApprovalError(null);
    setScenarioExecutionError(null);
    setChildApprovalError(null);
    setChildScenarioExecutionError(null);
    setDecidingChildExecutionId(null);
    setExecutingChildExecutionId(null);
    setPendingScenarioExecution(null);
    reportingScenarioExecutionIdRef.current = null;
  }, [runStartedAt]);

  useEffect(() => {
    if (
      !runtimeExecution ||
      !shouldRefreshChildProgress(runtimeExecution) ||
      isAnalyzing ||
      isDecidingApproval ||
      decidingChildExecutionId !== null ||
      isExecutingScenario ||
      executingChildExecutionId !== null ||
      pendingScenarioExecution !== null
    ) {
      return;
    }

    const controller = new AbortController();
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const refresh = async (): Promise<void> => {
      try {
        const refreshed = await analysisService.getExecution(
          runtimeExecution.executionId,
          runtimeExecution.runId,
          controller.signal
        );

        if (disposed) {
          return;
        }

        setRuntimeExecution(refreshed);
      } catch {
        // The execution is durable and this is a best-effort read-only
        // projection refresh. Keep the last truthful UI snapshot and retry on
        // the next render/cycle instead of surfacing transient polling noise.
      }
    };

    timer = setTimeout(() => {
      void refresh();
    }, 750);

    return () => {
      disposed = true;
      controller.abort();

      if (timer !== null) {
        clearTimeout(timer);
      }
    };
  }, [
    analysisService,
    decidingChildExecutionId,
    executingChildExecutionId,
    isAnalyzing,
    isDecidingApproval,
    isExecutingScenario,
    pendingScenarioExecution,
    runtimeExecution,
  ]);

  useEffect(() => {
    if (!pendingScenarioExecution) {
      return;
    }

    const startedAt = model.report?.timing.startedAt;

    if (
      startedAt === undefined ||
      startedAt === pendingScenarioExecution.previousStartedAt
    ) {
      return;
    }

    if (model.state !== "Completed" && model.state !== "Error") {
      return;
    }

    const reportingKey = pendingScenarioExecution.kind === "root"
      ? `root:${pendingScenarioExecution.executionId}`
      : `child:${pendingScenarioExecution.childExecutionId}`;

    if (reportingScenarioExecutionIdRef.current === reportingKey) {
      return;
    }

    reportingScenarioExecutionIdRef.current = reportingKey;

    let observation;

    try {
      observation =
        RuntimeAnalysisScenarioObservationBuilder.build(model);
    } catch (error: unknown) {
      const message = errorMessage(error);

      if (pendingScenarioExecution.kind === "root") {
        setScenarioExecutionError(message);
      } else {
        setChildScenarioExecutionError(message);
        setExecutingChildExecutionId(null);
      }

      setIsExecutingScenario(false);
      reportingScenarioExecutionIdRef.current = null;
      return;
    }

    const completion = pendingScenarioExecution.kind === "root"
      ? analysisService.completeScenarioExecution(
          pendingScenarioExecution.executionId,
          observation
        )
      : analysisService.completeChildScenarioExecution(
          pendingScenarioExecution.rootExecutionId,
          pendingScenarioExecution.childExecutionId,
          pendingScenarioExecution.rootRunId,
          observation
        );

    void completion
      .then((execution) => {
        setRuntimeExecution(execution);
        setPendingScenarioExecution(null);
        setScenarioExecutionError(null);
        setChildScenarioExecutionError(null);
        setExecutingChildExecutionId(null);
        approvedScenarioRunInProgressRef.current = false;
      })
      .catch((error: unknown) => {
        const message = errorMessage(error);

        if (pendingScenarioExecution.kind === "root") {
          setScenarioExecutionError(message);
        } else {
          setChildScenarioExecutionError(message);
        }

        reportingScenarioExecutionIdRef.current = null;
        approvedScenarioRunInProgressRef.current = false;
      })
      .finally(() => {
        setIsExecutingScenario(false);
        setExecutingChildExecutionId(null);
      });
  }, [
    analysisService,
    model,
    pendingScenarioExecution,
  ]);

  const aiActivity = useMemo(
    () => resolveRealtimeAiActivity(logs),
    [logs]
  );

  const analysisStatus = resolveAnalysisStatus(
    providerStatus,
    isAnalyzing,
    runtimeExecution
  );

  const canAnalyze =
    providerStatus?.configured === true &&
    AiAnalysisUxModel.isScopeAvailable(scope) &&
    question.trim().length > 0 &&
    !isAnalyzing;

  async function handleAnalyze(): Promise<void> {
    if (!canAnalyze) {
      return;
    }

    setIsAnalyzing(true);
    setPreparedContext(null);
    setRuntimeExecution(null);
    setSnapshotError(null);
    setAnalysisError(null);
    setApprovalError(null);
    setScenarioExecutionError(null);
    setChildApprovalError(null);
    setChildScenarioExecutionError(null);
    setDecidingChildExecutionId(null);
    setExecutingChildExecutionId(null);

    let context: RuntimeAnalysisPreparedContext;

    try {
      context = await snapshotService.prepare({
        scope,
        model,
        logs,
        maxInFlight,
        rotationOverlapMs,
      });

      setPreparedContext(context);
    } catch (error: unknown) {
      setSnapshotError(errorMessage(error));
          setIsAnalyzing(false);
      return;
    }

    try {
      const execution = await analysisService.analyze(
        context,
        question.trim(),
        investigationMode
      );

      setRuntimeExecution(execution);
    } catch (error: unknown) {
      setAnalysisError(errorMessage(error));
    } finally {
          setIsAnalyzing(false);
    }
  }

  async function handleApprovalDecision(
    decision: RuntimeAnalysisHumanApprovalDecision
  ): Promise<void> {
    if (!runtimeExecution) {
      return;
    }

    setIsDecidingApproval(true);
    setApprovalError(null);
    setScenarioExecutionError(null);

    try {
      const execution = await analysisService.decideHumanApproval(
        runtimeExecution.executionId,
        decision
      );

      setRuntimeExecution(execution);

      if (
        decision === "approve" &&
        execution.scenarioExecution.status === "Pending"
      ) {
        await startApprovedScenario(execution);
      }
    } catch (error: unknown) {
      setApprovalError(errorMessage(error));
    } finally {
      setIsDecidingApproval(false);
    }
  }

  async function handleExecuteScenario(): Promise<void> {
    if (!runtimeExecution) {
      return;
    }

    await startApprovedScenario(runtimeExecution);
  }

  async function startApprovedScenario(
    execution: RuntimeAnalysisRuntimeExecutionResult
  ): Promise<void> {
    if (
      execution.scenarioExecution.status !== "Pending" ||
      isExecutingScenario
    ) {
      return;
    }

    const previousStartedAt =
      model.report?.timing.startedAt;

    approvedScenarioRunInProgressRef.current = true;
    setIsExecutingScenario(true);
    setScenarioExecutionError(null);
    reportingScenarioExecutionIdRef.current = null;
    setExecutingChildExecutionId(null);
    setPendingScenarioExecution({
      kind: "root",
      executionId: execution.executionId,
      previousStartedAt,
    });

    try {
      await onExecuteScenario(
        execution.scenarioExecution.scenario,
        execution.scenarioExecution.planKey
      );
    } catch (error: unknown) {
      approvedScenarioRunInProgressRef.current = false;
      setPendingScenarioExecution(null);
      setIsExecutingScenario(false);
      setScenarioExecutionError(errorMessage(error));
    }
  }

  async function handleChildApprovalDecision(
    relation: RuntimeAnalysisChildDagRelationResult,
    decision: RuntimeAnalysisHumanApprovalDecision
  ): Promise<void> {
    if (!runtimeExecution || !relation.childExecutionId) {
      return;
    }

    const childExecutionId = relation.childExecutionId;

    setDecidingChildExecutionId(childExecutionId);
    setChildApprovalError(null);
    setChildScenarioExecutionError(null);

    try {
      const execution = await analysisService.decideChildHumanApproval(
        runtimeExecution.executionId,
        childExecutionId,
        runtimeExecution.runId,
        decision
      );

      setRuntimeExecution(execution);

      const updatedRelation = execution.childDag?.relations.find(
        (candidate) => candidate.childExecutionId === childExecutionId
      );

      if (
        decision === "approve" &&
        updatedRelation?.scenarioExecution?.status === "Pending"
      ) {
        await startApprovedChildScenario(
          execution,
          updatedRelation
        );
      }
    } catch (error: unknown) {
      setChildApprovalError(errorMessage(error));
    } finally {
      setDecidingChildExecutionId(null);
    }
  }

  async function handleExecuteChildScenario(
    relation: RuntimeAnalysisChildDagRelationResult
  ): Promise<void> {
    if (!runtimeExecution) {
      return;
    }

    await startApprovedChildScenario(
      runtimeExecution,
      relation
    );
  }

  async function startApprovedChildScenario(
    execution: RuntimeAnalysisRuntimeExecutionResult,
    relation: RuntimeAnalysisChildDagRelationResult
  ): Promise<void> {
    const childExecutionId = relation.childExecutionId;
    const childScenarioExecution = relation.scenarioExecution;

    if (
      !childExecutionId ||
      childScenarioExecution?.status !== "Pending" ||
      isExecutingScenario
    ) {
      return;
    }

    const previousStartedAt = model.report?.timing.startedAt;

    approvedScenarioRunInProgressRef.current = true;
    setIsExecutingScenario(true);
    setExecutingChildExecutionId(childExecutionId);
    setChildScenarioExecutionError(null);
    reportingScenarioExecutionIdRef.current = null;
    setPendingScenarioExecution({
      kind: "child",
      rootExecutionId: execution.executionId,
      childExecutionId,
      rootRunId: execution.runId,
      previousStartedAt,
    });

    try {
      await onExecuteScenario(
        childScenarioExecution.scenario,
        childScenarioExecution.planKey
      );
    } catch (error: unknown) {
      approvedScenarioRunInProgressRef.current = false;
      setPendingScenarioExecution(null);
      setIsExecutingScenario(false);
      setExecutingChildExecutionId(null);
      setChildScenarioExecutionError(errorMessage(error));
    }
  }

  const hasDurableAnalysis = runtimeExecution !== null;
  const currentDepth =
    runtimeExecution?.childDag?.observedDepth ?? 0;
  const scopeLabel =
    AiAnalysisUxModel.scopes().find(
      (definition) => definition.key === scope
    )?.label ?? scope;
  const investigationModeLabel =
    investigationMode === "continue-useful-experiments"
      ? "Continue investigation"
      : "Stop when conclusive";

  return (
    <div className={styles.panel}>
      <section
        className={`${styles.hero} ${
          hasDurableAnalysis ? styles.heroCompact : ""
        }`}
      >
        <div className={styles.heroHeader}>
          <div className={styles.eyebrow}>Runtime intelligence</div>
          <AiAnalysisStatusBadge status={analysisStatus} />
        </div>

        {hasDurableAnalysis ? (
          <div className={styles.heroExecutionSummary}>
            <span>
              AI → Policy → Approval → Execute → Verify → Re-analyze
            </span>
            <div className={styles.heroExecutionBadges}>
              <strong>{runtimeExecution.runtimeStatus}</strong>
              <strong>Depth {currentDepth}</strong>
              <strong>{investigationModeLabel}</strong>
            </div>
          </div>
        ) : (
          <p className={styles.text}>
            Analyze evidence, validate the AI proposal with deterministic policies,
            require human approval, execute through the existing burst runner, then
            verify and re-analyze the outcome. Every approved follow-up creates exactly
            one deeper durable child execution.
          </p>
        )}
      </section>

      <div className={styles.contextSticky}>
        <AiAnalysisContextCard snapshot={contextSnapshot} />
      </div>

      {!hasDurableAnalysis ? (
        <AiAnalysisPromptPanel
          scope={scope}
          investigationMode={investigationMode}
          investigationModeLocked={false}
          question={question}
          isAnalyzing={isAnalyzing}
          canAnalyze={canAnalyze}
          providerHint={providerHint(providerStatus)}
          onScopeChange={setScope}
          onInvestigationModeChange={setInvestigationMode}
          onQuestionChange={setQuestion}
          onAnalyze={handleAnalyze}
        />
      ) : (
        <section
          className={`${styles.section} ${styles.analysisRequestSummary}`}
        >
          <div className={styles.analysisRequestHeader}>
            <div>
              <div className={styles.sectionTitle}>Analysis request</div>
              <div className={styles.analysisRequestMeta}>
                {scopeLabel} · {investigationModeLabel}
              </div>
            </div>

            <div className={styles.analysisRequestLocked}>
              Durable chain
            </div>
          </div>

          <div
            className={styles.analysisRequestQuestion}
            title={question}
          >
            {question}
          </div>

          <div className={styles.analysisRequestFooter}>
            <span>{providerHint(providerStatus)}</span>
            <span>
              A new manually-started scenario will reopen the analysis form.
            </span>
          </div>
        </section>
      )}

      <AiAnalysisSnapshotCard
        snapshot={preparedContext?.snapshot ?? null}
        error={snapshotError}
      />

      <AiAnalysisResultCard
        result={runtimeExecution?.result ?? null}
        policyValidation={runtimeExecution?.policyValidation ?? null}
        humanApproval={runtimeExecution?.humanApproval ?? null}
        scenarioExecution={runtimeExecution?.scenarioExecution ?? null}
        verification={runtimeExecution?.verification ?? null}
        error={analysisError}
        isDecidingApproval={isDecidingApproval}
        approvalError={approvalError}
        onApprovalDecision={handleApprovalDecision}
        isExecutingScenario={isExecutingScenario}
        scenarioExecutionError={scenarioExecutionError}
        onExecuteScenario={handleExecuteScenario}
      />

      <AiRuntimeExecutionCard
        execution={runtimeExecution}
        decidingChildExecutionId={decidingChildExecutionId}
        childApprovalError={childApprovalError}
        onChildApprovalDecision={handleChildApprovalDecision}
        executingChildExecutionId={executingChildExecutionId}
        childScenarioExecutionError={childScenarioExecutionError}
        onExecuteChildScenario={handleExecuteChildScenario}
      />

      <AiInvestigationActivityDock
        activity={aiActivity}
      />
    </div>
  );
}

function shouldRefreshChildProgress(
  execution: RuntimeAnalysisRuntimeExecutionResult
): boolean {
  const childDag = execution.childDag;

  if (!childDag) {
    return false;
  }

  if (childDag.status === "Failed") {
    return false;
  }

  const relations = childDag.relations ?? [];

  if (relations.length === 0) {
    return childDag.status === "Running";
  }

  if (
    childDag.status === "Completed" &&
    !childDag.allContinuationsResumed
  ) {
    const hasSuppressedContinuation = relations.some(
      (relation) =>
        relation.continuationStatus.trim().toLowerCase() === "suppressed"
    );

    return !hasSuppressedContinuation;
  }

  const latest = [...relations].sort(
    (left, right) => right.depth - left.depth
  )[0];

  if (!latest?.childExecutionId) {
    return true;
  }

  if (!latest.reanalysis || !latest.policyValidation) {
    return true;
  }

  if (!latest.reanalysis.shouldContinue) {
    return childDag.status !== "Completed";
  }

  if (!latest.policyValidation.allowed) {
    return childDag.status !== "Completed";
  }

  if (!latest.humanApproval) {
    return true;
  }

  if (latest.humanApproval.status === "Pending") {
    return false;
  }

  if (latest.humanApproval.status === "Rejected") {
    return childDag.status !== "Completed";
  }

  if (!latest.scenarioExecution) {
    return latest.humanApproval.status === "Approved";
  }

  if (latest.scenarioExecution.status === "Pending") {
    return false;
  }

  if (!latest.verification || latest.verification.status === "Pending") {
    return true;
  }

  // An approved + verified child experiment may be creating/re-analyzing the
  // next durable child. Keep refreshing until the next user boundary appears
  // or the approval-driven chain becomes terminal.
  return childDag.status !== "Completed";
}

type DemoUiAiActivityPayload = {
  activityId: string;
  activityKind: string;
  rootExecutionId: string | null;
  childExecutionId: string | null;
  depth: number | null;
  provider: string | null;
  model: string | null;
};

const DemoUiAiStartedCategory =
  "demo.ui.ai.started";
const DemoUiAiCompletedCategory =
  "demo.ui.ai.completed";
const DemoUiAiFailedCategory =
  "demo.ui.ai.failed";

function resolveRealtimeAiActivity(
  logs: readonly ConsoleLogEntry[]
): AiOnlyActivity | null {
  const terminalActivityIds =
    new Set<string>();

  for (const log of logs) {
    if (log.kind !== "realtime") {
      continue;
    }

    const category =
      log.category?.trim().toLowerCase();

    if (
      category !== DemoUiAiStartedCategory
      && category !== DemoUiAiCompletedCategory
      && category !== DemoUiAiFailedCategory
    ) {
      continue;
    }

    const payload =
      parseDemoUiAiActivityPayload(
        log.data ?? log.payload
      );

    if (!payload) {
      continue;
    }

    if (
      category === DemoUiAiCompletedCategory
      || category === DemoUiAiFailedCategory
    ) {
      terminalActivityIds.add(
        payload.activityId
      );

      continue;
    }

    if (
      terminalActivityIds.has(
        payload.activityId
      )
    ) {
      continue;
    }

    const childReanalysis =
      payload.activityKind === "child-reanalysis";

    return {
      key: payload.activityId,
      title: childReanalysis
        ? "AI is re-analyzing the verified outcome"
        : "AI is analyzing runtime evidence",
      detail: childReanalysis
        ? "Comparing deterministic verification with the previous hypothesis and deciding whether another materially distinct experiment is useful."
        : "The bounded runtime evidence is with the configured AI provider; waiting for the structured finding.",
      context:
        childReanalysis
        && typeof payload.depth === "number"
          ? `Depth ${payload.depth}`
          : "Root analysis",
      startedAtUtc:
        log.occurredAtUtc ?? log.t,
      provider: payload.provider,
      model: payload.model,
    };
  }

  return null;
}

function parseDemoUiAiActivityPayload(
  value: unknown
): DemoUiAiActivityPayload | null {
  let candidate: unknown = value;

  if (typeof candidate === "string") {
    try {
      candidate = JSON.parse(
        candidate
      );
    } catch {
      return null;
    }
  }

  if (
    typeof candidate !== "object"
    || candidate === null
    || Array.isArray(candidate)
  ) {
    return null;
  }

  const record =
    candidate as Record<string, unknown>;

  const activityId =
    readStringCaseInsensitive(
      record,
      "activityId"
    );

  const activityKind =
    readStringCaseInsensitive(
      record,
      "activityKind"
    );

  if (!activityId || !activityKind) {
    return null;
  }

  return {
    activityId,
    activityKind,
    rootExecutionId:
      readStringCaseInsensitive(
        record,
        "rootExecutionId"
      ) ?? null,
    childExecutionId:
      readStringCaseInsensitive(
        record,
        "childExecutionId"
      ) ?? null,
    depth:
      readNumberCaseInsensitive(
        record,
        "depth"
      ),
    provider:
      readStringCaseInsensitive(
        record,
        "provider"
      ) ?? null,
    model:
      readStringCaseInsensitive(
        record,
        "model"
      ) ?? null,
  };
}

function readStringCaseInsensitive(
  record: Record<string, unknown>,
  key: string
): string | undefined {
  const matchingKey =
    Object.keys(record).find(
      (candidate) =>
        candidate.toLowerCase()
        === key.toLowerCase()
    );

  if (!matchingKey) {
    return undefined;
  }

  const value =
    record[matchingKey];

  if (typeof value !== "string") {
    return undefined;
  }

  const normalized =
    value.trim();

  return normalized || undefined;
}

function readNumberCaseInsensitive(
  record: Record<string, unknown>,
  key: string
): number | null {
  const matchingKey =
    Object.keys(record).find(
      (candidate) =>
        candidate.toLowerCase()
        === key.toLowerCase()
    );

  if (!matchingKey) {
    return null;
  }

  const value =
    record[matchingKey];

  return typeof value === "number"
         && Number.isFinite(value)
    ? value
    : null;
}

function resolveAnalysisStatus(
  providerStatus: RuntimeAnalysisProviderStatus | null,
  isAnalyzing: boolean,
  result: RuntimeAnalysisRuntimeExecutionResult | null
): AiAnalysisStatus {
  if (isAnalyzing) {
    return "analyzing";
  }

  if (result) {
    return "finding-available";
  }

  return providerStatus?.configured === true
    ? "ready"
    : "provider-pending";
}

function providerHint(
  status: RuntimeAnalysisProviderStatus | null
): string {
  if (!status) {
    return "Provider status unavailable.";
  }

  if (!status.configured) {
    return `${status.provider} ${status.model} · server API key not configured`;
  }

  return `${status.provider} ${status.model} · structured output enabled`;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
