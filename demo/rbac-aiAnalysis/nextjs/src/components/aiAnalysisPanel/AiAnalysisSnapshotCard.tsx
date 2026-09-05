import { JSX } from "react";
import type { RuntimeAnalysisSnapshot } from "@/lib/aiAnalysis/RuntimeAnalysisType";

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
      <section className={`${"ai-analysis-placeholder"} ${"ai-analysis-snapshot-error"}`}>
        <div className="ai-analysis-placeholder-title">Analysis context error</div>
        <p className="ai-analysis-placeholder-text">{error}</p>
      </section>
    );
  }

  if (!snapshot) {
    return <></>;
  }

  return (
    <details
      className={`${"ai-analysis-section"} ${"ai-analysis-snapshot-compact"}`}
    >
      <summary className="ai-analysis-snapshot-compact-summary">
        <div className="ai-analysis-snapshot-compact-title">
          <div className="ai-analysis-section-title">Analysis context</div>
          <div className="ai-analysis-snapshot-ready">Ready</div>
        </div>

        <div className="ai-analysis-snapshot-compact-facts">
          <span>
            Evidence {snapshot.evidence.length}/{snapshot.evidenceReceivedCount}
          </span>
          <span>
            HTTP errors {snapshot.evidenceSummary.httpErrorCount}
          </span>
          <span>
            {snapshot.evidenceTruncated ? "Truncated" : "Not truncated"}
          </span>
          <span className="ai-analysis-snapshot-compact-time">
            {new Date(snapshot.capturedAtUtc).toLocaleTimeString()}
          </span>
        </div>
      </summary>

      <div className="ai-analysis-snapshot-compact-details">
        <div className="ai-analysis-metric-grid">
          <div className="ai-analysis-metric">
            <div className="ai-analysis-metric-label">DAG related</div>
            <div className="ai-analysis-metric-value">
              {snapshot.evidenceSummary.dagRelatedCount}
            </div>
          </div>

          <div className="ai-analysis-metric">
            <div className="ai-analysis-metric-label">Policy related</div>
            <div className="ai-analysis-metric-value">
              {snapshot.evidenceSummary.policyRelatedCount}
            </div>
          </div>

          <div className="ai-analysis-metric">
            <div className="ai-analysis-metric-label">Recovery / replay</div>
            <div className="ai-analysis-metric-value">
              {snapshot.evidenceSummary.recoveryRelatedCount}
            </div>
          </div>

          <div className="ai-analysis-metric">
            <div className="ai-analysis-metric-label">Captured</div>
            <div className="ai-analysis-metric-value">
              {new Date(snapshot.capturedAtUtc).toLocaleTimeString()}
            </div>
          </div>
        </div>
      </div>
    </details>
  );
}
