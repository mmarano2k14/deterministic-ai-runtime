"use client";

import { JSX, useEffect, useMemo, useState } from "react";
import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import type { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import type { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import type {
  AiAnalysisScope,
  AiAnalysisStatus,
} from "@/lib/aiAnalysis/AiAnalysisType";
import { AiAnalysisContextSnapshotBuilder } from "@/lib/aiAnalysis/AiAnalysisContextSnapshotBuilder";
import { RuntimeAnalysisAnalysisService } from "@/lib/aiAnalysis/RuntimeAnalysisAnalysisService";
import { RuntimeAnalysisSnapshotService } from "@/lib/aiAnalysis/RuntimeAnalysisSnapshotService";
import type {
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisPreparedContext,
  RuntimeAnalysisProviderStatus,
  RuntimeAnalysisRuntimeExecutionResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiAnalysisContextCard } from "./AiAnalysisContextCard";
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
};

export function AiAnalysisPanel(props: AiAnalysisPanelProps): JSX.Element {
  const { model, logs, maxInFlight, rotationOverlapMs, api } = props;

  const [scope, setScope] = useState<AiAnalysisScope>("current-run");
  const [question, setQuestion] = useState("");
  const [providerStatus, setProviderStatus] =
    useState<RuntimeAnalysisProviderStatus | null>(null);
  const [isPreparingContext, setIsPreparingContext] = useState(false);
  const [isAnalyzing, setIsAnalyzing] = useState(false);
  const [isDecidingApproval, setIsDecidingApproval] = useState(false);
  const [preparedContext, setPreparedContext] =
    useState<RuntimeAnalysisPreparedContext | null>(null);
  const [runtimeExecution, setRuntimeExecution] =
    useState<RuntimeAnalysisRuntimeExecutionResult | null>(null);
  const [snapshotError, setSnapshotError] = useState<string | null>(null);
  const [analysisError, setAnalysisError] = useState<string | null>(null);
  const [approvalError, setApprovalError] = useState<string | null>(null);

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
  }, [scope, runStartedAt]);

  const analysisStatus = resolveAnalysisStatus(
    providerStatus,
    isAnalyzing,
    runtimeExecution
  );

  const canAnalyze =
    providerStatus?.configured === true &&
    preparedContext !== null &&
    question.trim().length > 0 &&
    !isPreparingContext &&
    !isAnalyzing;

  async function handlePrepareContext(): Promise<void> {
    setIsPreparingContext(true);
    setSnapshotError(null);
    setRuntimeExecution(null);
    setAnalysisError(null);
    setApprovalError(null);

    try {
      const context = await snapshotService.prepare({
        scope,
        model,
        logs,
        maxInFlight,
        rotationOverlapMs,
      });

      setPreparedContext(context);
    } catch (error: unknown) {
      setPreparedContext(null);
      setSnapshotError(errorMessage(error));
    } finally {
      setIsPreparingContext(false);
    }
  }

  async function handleAnalyze(): Promise<void> {
    if (!preparedContext) {
      return;
    }

    setIsAnalyzing(true);
    setRuntimeExecution(null);
    setAnalysisError(null);
    setApprovalError(null);

    try {
      const execution = await analysisService.analyze(
        preparedContext,
        question.trim()
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

    try {
      const execution = await analysisService.decideHumanApproval(
        runtimeExecution.executionId,
        decision
      );

      setRuntimeExecution(execution);
    } catch (error: unknown) {
      setApprovalError(errorMessage(error));
    } finally {
      setIsDecidingApproval(false);
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
          Analyze execution evidence, let deterministic policies validate the AI
          proposal automatically, then stop at a durable human-approval boundary.
        </p>
      </section>

      <div className={styles.contextSticky}>
        <AiAnalysisContextCard snapshot={contextSnapshot} />
      </div>

      <AiAnalysisPromptPanel
        scope={scope}
        question={question}
        isPreparingContext={isPreparingContext}
        isAnalyzing={isAnalyzing}
        canAnalyze={canAnalyze}
        providerHint={providerHint(providerStatus)}
        onScopeChange={setScope}
        onQuestionChange={setQuestion}
        onPrepareContext={handlePrepareContext}
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
        error={analysisError}
        isDecidingApproval={isDecidingApproval}
        approvalError={approvalError}
        onApprovalDecision={handleApprovalDecision}
      />
    </div>
  );
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
