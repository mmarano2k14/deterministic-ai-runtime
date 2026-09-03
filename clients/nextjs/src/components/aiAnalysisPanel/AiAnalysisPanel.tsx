"use client";

import { JSX, useMemo, useState } from "react";
import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import type {
  AiAnalysisScope,
  AiAnalysisStatus,
} from "@/lib/aiAnalysis/AiAnalysisType";
import { AiAnalysisContextSnapshotBuilder } from "@/lib/aiAnalysis/AiAnalysisContextSnapshotBuilder";
import { AiAnalysisContextCard } from "./AiAnalysisContextCard";
import { AiAnalysisPromptPanel } from "./AiAnalysisPromptPanel";
import { AiAnalysisStatusBadge } from "./AiAnalysisStatusBadge";
import styles from "./AiAnalysisPanel.module.css";

export type AiAnalysisPanelProps = {
  model: BurstRuntime;
  logCount: number;
};

export function AiAnalysisPanel(props: AiAnalysisPanelProps): JSX.Element {
  const { model, logCount } = props;

  const [scope, setScope] = useState<AiAnalysisScope>("current-run");
  const [question, setQuestion] = useState("");

  const snapshot = useMemo(
    () => AiAnalysisContextSnapshotBuilder.build(model, logCount),
    [model, logCount]
  );

  const analysisStatus: AiAnalysisStatus = "provider-pending";

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
        <AiAnalysisContextCard snapshot={snapshot} />
      </div>

      <AiAnalysisPromptPanel
        scope={scope}
        question={question}
        onScopeChange={setScope}
        onQuestionChange={setQuestion}
      />

      <section className={styles.placeholder}>
        <div className={styles.placeholderTitle}>Analysis output</div>
        <p className={styles.placeholderText}>
          Structured findings, evidence links, policy validation and suggested
          scenarios will appear here after the backend AI provider is connected.
          No synthetic AI result is rendered by this UX-only pack.
        </p>
      </section>
    </div>
  );
}
