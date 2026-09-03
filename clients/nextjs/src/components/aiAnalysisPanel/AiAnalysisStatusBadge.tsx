import { JSX } from "react";
import type { AiAnalysisStatus } from "@/lib/aiAnalysis/AiAnalysisType";
import { AiAnalysisUxModel } from "@/lib/aiAnalysis/AiAnalysisUxModel";
import styles from "./AiAnalysisPanel.module.css";

export type AiAnalysisStatusBadgeProps = {
  status: AiAnalysisStatus;
};

export function AiAnalysisStatusBadge(
  props: AiAnalysisStatusBadgeProps
): JSX.Element {
  const definition = AiAnalysisUxModel.status(props.status);

  return (
    <div
      className={styles.analysisStatus}
      data-status={definition.key}
      title={definition.description}
    >
      <span className={styles.analysisStatusDot} />
      {definition.label}
    </div>
  );
}
