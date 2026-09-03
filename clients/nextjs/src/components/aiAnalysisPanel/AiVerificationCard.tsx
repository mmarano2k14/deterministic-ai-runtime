import { JSX } from "react";
import type {
  RuntimeAnalysisVerificationResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import styles from "./AiAnalysisPanel.module.css";

export type AiVerificationCardProps = {
  verification: RuntimeAnalysisVerificationResult;
};

export function AiVerificationCard(
  props: AiVerificationCardProps
): JSX.Element {
  const { verification } = props;

  return (
    <div
      className={styles.verificationCard}
      data-status={verification.status}
    >
      <div className={styles.humanApprovalHeader}>
        <div>
          <div className={styles.resultSubheading}>
            Deterministic verification
          </div>
          <div className={styles.policyPipeline}>
            Prediction context vs observed execution
          </div>
        </div>

        <div className={styles.humanApprovalBadge}>
          {verification.status.toUpperCase()}
        </div>
      </div>

      <div className={styles.humanApprovalMessage}>
        {verification.summary}
      </div>

      {verification.executed ? (
        <div className={styles.verificationGrid}>
          <VerificationItem
            label="Plan completed"
            value={verification.completedMatchesPlan}
          />
          <VerificationItem
            label="No residual"
            value={verification.noResidualInFlight}
          />
          <VerificationItem
            label="Outcomes consistent"
            value={verification.outcomeCountConsistent}
          />
          <div>
            <span>P50 Δ</span>
            <strong>{formatDelta(verification.p50DeltaMs)}</strong>
          </div>
          <div>
            <span>P95 Δ</span>
            <strong>{formatDelta(verification.p95DeltaMs)}</strong>
          </div>
        </div>
      ) : null}
    </div>
  );
}

function VerificationItem(
  props: {
    label: string;
    value: boolean;
  }
): JSX.Element {
  return (
    <div>
      <span>{props.label}</span>
      <strong>{props.value ? "YES" : "NO"}</strong>
    </div>
  );
}

function formatDelta(
  value: number | null
): string {
  if (value === null) {
    return "—";
  }

  const sign = value > 0 ? "+" : "";
  return `${sign}${value.toFixed(1)} ms`;
}
