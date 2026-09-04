import { JSX } from "react";
import type { RuntimeAnalysisRuntimeExecutionResult } from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiChildDagEvidenceCard } from "./AiChildDagEvidenceCard";
import styles from "./AiAnalysisPanel.module.css";

export type AiRuntimeExecutionCardProps = {
  execution: RuntimeAnalysisRuntimeExecutionResult | null;
};

export function AiRuntimeExecutionCard(
  props: AiRuntimeExecutionCardProps
): JSX.Element | null {
  const { execution } = props;

  if (!execution) {
    return null;
  }

  const runtimeStatus =
    typeof execution.runtimeStatus === "string" &&
    execution.runtimeStatus.trim().length > 0
      ? execution.runtimeStatus.trim()
      : "Unknown";

  return (
    <section className={`${styles.section} ${styles.runtimeExecution}`}>
      <div className={styles.sectionHeader}>
        <div className={styles.sectionTitle}>Runtime DAG</div>
        <div
          className={styles.runtimeStatus}
          data-status={runtimeStatus.toLowerCase()}
        >
          {runtimeStatus}
        </div>
      </div>

      <div className={styles.runtimeIdentityGrid}>
        <div>
          <span>Pipeline</span>
          <strong>{execution.pipelineName}</strong>
        </div>
        <div>
          <span>Current step</span>
          <strong>{execution.stepName}</strong>
        </div>
        <div>
          <span>Initial RunId</span>
          <strong title={execution.runId}>
            {execution.runId}
          </strong>
        </div>
        <div>
          <span>ExecutionId</span>
          <strong title={execution.executionId}>
            {execution.executionId}
          </strong>
        </div>
        {execution.continuationRunId ? (
          <div>
            <span>Continuation RunId</span>
            <strong title={execution.continuationRunId}>
              {execution.continuationRunId}
            </strong>
          </div>
        ) : null}
      </div>

      <AiChildDagEvidenceCard
        childDag={execution.childDag}
        rootExecutionId={execution.executionId}
      />
    </section>
  );
}
