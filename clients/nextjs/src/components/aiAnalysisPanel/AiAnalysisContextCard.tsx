import { JSX } from "react";
import type { AiAnalysisContextSnapshot } from "@/lib/aiAnalysis/AiAnalysisType";
import styles from "./AiAnalysisPanel.module.css";

export type AiAnalysisContextCardProps = {
  snapshot: AiAnalysisContextSnapshot;
};

export function AiAnalysisContextCard(
  props: AiAnalysisContextCardProps
): JSX.Element {
  const { snapshot } = props;

  return (
    <section className={`${styles.section} ${styles.contextSection}`}>
      <div className={styles.sectionHeader}>
        <div className={styles.sectionTitle}>Observed context</div>
        <div className={styles.status}>
          <span className={styles.statusDot} />
          Live
        </div>
      </div>

      <div>
        <div className={styles.contextTitle}>{snapshot.title}</div>
        <div className={styles.contextSubtitle}>{snapshot.subtitle}</div>
      </div>

      <div className={styles.metricGrid}>
        {snapshot.metrics.map((metric) => (
          <div className={styles.metric} key={metric.label}>
            <div className={styles.metricLabel}>{metric.label}</div>
            <div className={styles.metricValue}>{metric.value}</div>
          </div>
        ))}
      </div>
    </section>
  );
}
