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
import { RuntimeAnalysisSnapshotService } from "@/lib/aiAnalysis/RuntimeAnalysisSnapshotService";
import type { RuntimeAnalysisSnapshot } from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiAnalysisContextCard } from "./AiAnalysisContextCard";
import { AiAnalysisPromptPanel } from "./AiAnalysisPromptPanel";
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
  const [isPreparingContext, setIsPreparingContext] = useState(false);
  const [preparedSnapshot, setPreparedSnapshot] =
    useState<RuntimeAnalysisSnapshot | null>(null);
  const [snapshotError, setSnapshotError] = useState<string | null>(null);

  const contextSnapshot = useMemo(
    () => AiAnalysisContextSnapshotBuilder.build(model, logs.length),
    [model, logs.length]
  );

  const snapshotService = useMemo(
    () => new RuntimeAnalysisSnapshotService(api),
    [api]
  );

  const runStartedAt = model.report?.timing.startedAt;

  useEffect(() => {
    setPreparedSnapshot(null);
    setSnapshotError(null);
  }, [scope, runStartedAt]);

  const analysisStatus: AiAnalysisStatus = "provider-pending";

  async function handlePrepareContext(): Promise<void> {
    setIsPreparingContext(true);
    setSnapshotError(null);

    try {
      const snapshot = await snapshotService.prepare({
        scope,
        model,
        logs,
        maxInFlight,
        rotationOverlapMs,
      });

      setPreparedSnapshot(snapshot);
    } catch (error: unknown) {
      setPreparedSnapshot(null);
      setSnapshotError(
        error instanceof Error ? error.message : String(error)
      );
    } finally {
      setIsPreparingContext(false);
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
          Analyze the execution evidence already visible in graphs and live logs,
          with DAG and policy correlation as lifecycle events become available.
        </p>
      </section>

      <div className={styles.contextSticky}>
        <AiAnalysisContextCard snapshot={contextSnapshot} />
      </div>

      <AiAnalysisPromptPanel
        scope={scope}
        question={question}
        isPreparingContext={isPreparingContext}
        onScopeChange={setScope}
        onQuestionChange={setQuestion}
        onPrepareContext={handlePrepareContext}
      />

      <AiAnalysisSnapshotCard
        snapshot={preparedSnapshot}
        error={snapshotError}
      />

      <section className={styles.placeholder}>
        <div className={styles.placeholderTitle}>AI analysis output</div>
        <p className={styles.placeholderText}>
          The next pack will send this validated snapshot plus the user question
          to an AI provider and render a strict structured finding here.
        </p>
      </section>
    </div>
  );
}
