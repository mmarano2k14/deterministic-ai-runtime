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
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisPreparedContext,
  RuntimeAnalysisProviderStatus,
  RuntimeAnalysisRuntimeExecutionResult,
  RuntimeAnalysisSuggestedScenario,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiAnalysisContextCard } from "./AiAnalysisContextCard";
import {
  AiAnalysisActivityIndicator,
  type AiAnalysisActivityLog,
  type AiAnalysisActivityPhase,
} from "./AiAnalysisActivityIndicator";
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
  const [question, setQuestion] = useState(
    AiAnalysisUxModel.promptForAction("analyze")
  );
  const [providerStatus, setProviderStatus] =
    useState<RuntimeAnalysisProviderStatus | null>(null);
  const [isAnalyzing, setIsAnalyzing] = useState(false);
  const [analysisPhase, setAnalysisPhase] =
    useState<AiAnalysisActivityPhase | null>(null);
  const [analysisStartedAt, setAnalysisStartedAt] =
    useState<number | null>(null);
  const [isDecidingApproval, setIsDecidingApproval] = useState(false);
  const [isExecutingScenario, setIsExecutingScenario] = useState(false);
  const [preparedContext, setPreparedContext] =
    useState<RuntimeAnalysisPreparedContext | null>(null);
  const [runtimeExecution, setRuntimeExecution] =
    useState<RuntimeAnalysisRuntimeExecutionResult | null>(null);
  const [snapshotError, setSnapshotError] = useState<string | null>(null);
  const [analysisError, setAnalysisError] = useState<string | null>(null);
  const [approvalError, setApprovalError] = useState<string | null>(null);
  const [scenarioExecutionError, setScenarioExecutionError] =
    useState<string | null>(null);
  const [pendingScenarioExecution, setPendingScenarioExecution] =
    useState<{
      executionId: string;
      previousStartedAt: number | undefined;
    } | null>(null);
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
    setAnalysisPhase(null);
    setAnalysisStartedAt(null);
    setSnapshotError(null);
    setAnalysisError(null);
    setApprovalError(null);
    setScenarioExecutionError(null);
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
    setAnalysisPhase(null);
    setAnalysisStartedAt(null);
    setSnapshotError(null);
    setAnalysisError(null);
    setApprovalError(null);
    setScenarioExecutionError(null);
    setPendingScenarioExecution(null);
    reportingScenarioExecutionIdRef.current = null;
  }, [runStartedAt]);

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

    if (
      reportingScenarioExecutionIdRef.current ===
      pendingScenarioExecution.executionId
    ) {
      return;
    }

    reportingScenarioExecutionIdRef.current =
      pendingScenarioExecution.executionId;

    let observation;

    try {
      observation =
        RuntimeAnalysisScenarioObservationBuilder.build(model);
    } catch (error: unknown) {
      setScenarioExecutionError(errorMessage(error));
      setIsExecutingScenario(false);
      reportingScenarioExecutionIdRef.current = null;
      return;
    }

    void analysisService
      .completeScenarioExecution(
        pendingScenarioExecution.executionId,
        observation
      )
      .then((execution) => {
        setRuntimeExecution(execution);
        setPendingScenarioExecution(null);
        setScenarioExecutionError(null);
        approvedScenarioRunInProgressRef.current = false;
      })
      .catch((error: unknown) => {
        setScenarioExecutionError(errorMessage(error));
        reportingScenarioExecutionIdRef.current = null;
        approvedScenarioRunInProgressRef.current = false;
      })
      .finally(() => {
        setIsExecutingScenario(false);
      });
  }, [
    analysisService,
    model,
    pendingScenarioExecution,
  ]);

  const latestAnalysisLog = useMemo(
    () => resolveLatestAnalysisLog(logs, analysisPhase, analysisStartedAt),
    [logs, analysisPhase, analysisStartedAt]
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
    setAnalysisStartedAt(Date.now());
    setAnalysisPhase("preparing-context");
    setPreparedContext(null);
    setRuntimeExecution(null);
    setSnapshotError(null);
    setAnalysisError(null);
    setApprovalError(null);
    setScenarioExecutionError(null);

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
      setAnalysisPhase(null);
      setAnalysisStartedAt(null);
      setIsAnalyzing(false);
      return;
    }

    setAnalysisPhase("analyzing-evidence");

    try {
      const execution = await analysisService.analyze(
        context,
        question.trim()
      );

      setRuntimeExecution(execution);
    } catch (error: unknown) {
      setAnalysisError(errorMessage(error));
    } finally {
      setAnalysisPhase(null);
      setAnalysisStartedAt(null);
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
    setPendingScenarioExecution({
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

  return (
    <div className={styles.panel}>
      <section className={styles.hero}>
        <div className={styles.heroHeader}>
          <div className={styles.eyebrow}>Runtime intelligence</div>
          <AiAnalysisStatusBadge status={analysisStatus} />
        </div>

        <p className={styles.text}>
          Analyze evidence, validate the AI proposal with deterministic policies,
          require human approval, execute through the existing burst runner, then
          verify the observed outcome in the same durable DAG.
        </p>
      </section>

      <div className={styles.contextSticky}>
        <AiAnalysisContextCard snapshot={contextSnapshot} />
      </div>

      <AiAnalysisPromptPanel
        scope={scope}
        question={question}
        isAnalyzing={isAnalyzing}
        canAnalyze={canAnalyze}
        providerHint={providerHint(providerStatus)}
        activityPhase={analysisPhase}
        activityStartedAt={analysisStartedAt}
        latestActivityLog={latestAnalysisLog}
        onScopeChange={setScope}
        onQuestionChange={setQuestion}
        onAnalyze={handleAnalyze}
      />

      <AiAnalysisSnapshotCard
        snapshot={preparedContext?.snapshot ?? null}
        error={snapshotError}
      />

      <AiRuntimeExecutionCard execution={runtimeExecution} />

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
    </div>
  );
}

function resolveLatestAnalysisLog(
  logs: readonly ConsoleLogEntry[],
  phase: AiAnalysisActivityPhase | null,
  analysisStartedAt: number | null
): AiAnalysisActivityLog | null {
  if (!phase || analysisStartedAt === null) {
    return null;
  }

  const expectedPath =
    phase === "preparing-context"
      ? "/runtime-analysis/snapshot"
      : "/runtime-analysis/analyze";

  const entry = logs.find((candidate) => {
    if (candidate.kind !== "http" || candidate.path !== expectedPath) {
      return false;
    }

    const activityAt =
      candidate.updatedAt ?? Date.parse(candidate.t);

    return (
      Number.isFinite(activityAt) &&
      activityAt >= analysisStartedAt - 250
    );
  });

  if (!entry || entry.kind !== "http") {
    return null;
  }

  let status = "waiting for response";

  if (entry.error) {
    status = "request failed";
  } else if (entry.status !== undefined) {
    status = entry.statusText
      ? `${entry.status} ${entry.statusText}`
      : String(entry.status);
  }

  return {
    name: entry.name,
    method: entry.method,
    path: entry.path,
    status,
  };
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
