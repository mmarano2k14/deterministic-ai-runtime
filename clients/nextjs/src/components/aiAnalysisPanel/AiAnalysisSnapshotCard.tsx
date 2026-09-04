import { JSX } from "react";
import type { RuntimeAnalysisSnapshot } from "@/lib/aiAnalysis/RuntimeAnalysisType";
import styles from "./AiAnalysisPanel.module.css";

export type AiAnalysisSnapshotCardProps = {
  snapshot: RuntimeAnalysisSnapshot | null;
  error: string | null;
};

export function AiAnalysisSnapshotCard(
  props: AiAnalysisSnapshotCardProps
): JSX.Element {
  const { snapshot, error } = props;

  if (error) {
    return (
      <section className={`${styles.placeholder} ${styles.snapshotError}`}>
        <div className={styles.placeholderTitle}>Analysis context error</div>
        <p className={styles.placeholderText}>{error}</p>
      </section>
    );
  }

  if (!snapshot) {
    return <></>;
  }

  return (
    <details
      className={`${styles.section} ${styles.snapshotCompact}`}
    >
      <summary className={styles.snapshotCompactSummary}>
        <div className={styles.snapshotCompactTitle}>
          <div className={styles.sectionTitle}>Analysis context</div>
          <div className={styles.snapshotReady}>Ready</div>
        </div>

        <div className={styles.snapshotCompactFacts}>
          <span>
            Evidence {snapshot.evidence.length}/{snapshot.evidenceReceivedCount}
          </span>
          <span>
            HTTP errors {snapshot.evidenceSummary.httpErrorCount}
          </span>
          <span>
            {snapshot.evidenceTruncated ? "Truncated" : "Not truncated"}
          </span>
          <span className={styles.snapshotCompactTime}>
            {new Date(snapshot.capturedAtUtc).toLocaleTimeString()}
          </span>
        </div>
      </summary>

      <div className={styles.snapshotCompactDetails}>
        <div className={styles.metricGrid}>
          <div className={styles.metric}>
            <div className={styles.metricLabel}>DAG related</div>
            <div className={styles.metricValue}>
              {snapshot.evidenceSummary.dagRelatedCount}
            </div>
          </div>

          <div className={styles.metric}>
            <div className={styles.metricLabel}>Policy related</div>
            <div className={styles.metricValue}>
              {snapshot.evidenceSummary.policyRelatedCount}
            </div>
          </div>

          <div className={styles.metric}>
            <div className={styles.metricLabel}>Recovery / replay</div>
            <div className={styles.metricValue}>
              {snapshot.evidenceSummary.recoveryRelatedCount}
            </div>
          </div>

          <div className={styles.metric}>
            <div className={styles.metricLabel}>Captured</div>
            <div className={styles.metricValue}>
              {new Date(snapshot.capturedAtUtc).toLocaleTimeString()}
            </div>
          </div>
        </div>
      </div>
    </details>
  );
}
