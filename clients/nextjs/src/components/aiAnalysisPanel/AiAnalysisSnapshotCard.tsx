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
    return (
      <section className={styles.placeholder}>
        <div className={styles.placeholderTitle}>Analysis context</div>
        <p className={styles.placeholderText}>
          Prepare a bounded snapshot to validate exactly which runtime evidence
          will be sent to the future AI provider.
        </p>
      </section>
    );
  }

  return (
    <section className={styles.section}>
      <div className={styles.sectionHeader}>
        <div className={styles.sectionTitle}>Analysis context</div>
        <div className={styles.snapshotReady}>Ready</div>
      </div>

      <div className={styles.snapshotMeta}>
        Captured {new Date(snapshot.capturedAtUtc).toLocaleTimeString()}
      </div>

      <div className={styles.metricGrid}>
        <div className={styles.metric}>
          <div className={styles.metricLabel}>Evidence</div>
          <div className={styles.metricValue}>
            {snapshot.evidence.length}/{snapshot.evidenceReceivedCount}
          </div>
        </div>

        <div className={styles.metric}>
          <div className={styles.metricLabel}>HTTP errors</div>
          <div className={styles.metricValue}>
            {snapshot.evidenceSummary.httpErrorCount}
          </div>
        </div>

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
          <div className={styles.metricLabel}>Truncated</div>
          <div className={styles.metricValue}>
            {snapshot.evidenceTruncated ? "Yes" : "No"}
          </div>
        </div>
      </div>
    </section>
  );
}
