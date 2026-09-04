import { JSX } from "react";
import type { AiAnalysisContextSnapshot } from "@/lib/aiAnalysis/AiAnalysisType";

export type AiAnalysisContextCardProps = {
  snapshot: AiAnalysisContextSnapshot;
};

export function AiAnalysisContextCard(
  props: AiAnalysisContextCardProps
): JSX.Element {
  const { snapshot } = props;

  return (
    <section className={`${"ai-analysis-section"} ${"ai-analysis-context-section"}`}>
      <div className="ai-analysis-section-header">
        <div className="ai-analysis-section-title">Observed context</div>
        <div className="ai-analysis-status">
          <span className="ai-analysis-status-dot" />
          Live
        </div>
      </div>

      <div>
        <div className="ai-analysis-context-title">{snapshot.title}</div>
        <div className="ai-analysis-context-subtitle">{snapshot.subtitle}</div>
      </div>

      <div className="ai-analysis-metric-grid">
        {snapshot.metrics.map((metric) => (
          <div className="ai-analysis-metric" key={metric.label}>
            <div className="ai-analysis-metric-label">{metric.label}</div>
            <div className="ai-analysis-metric-value">{metric.value}</div>
          </div>
        ))}
      </div>
    </section>
  );
}
