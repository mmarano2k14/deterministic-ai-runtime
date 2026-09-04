import { JSX } from "react";
import type { AiAnalysisStatus } from "@/lib/aiAnalysis/AiAnalysisType";
import { AiAnalysisUxModel } from "@/lib/aiAnalysis/AiAnalysisUxModel";

export type AiAnalysisStatusBadgeProps = {
  status: AiAnalysisStatus;
};

export function AiAnalysisStatusBadge(
  props: AiAnalysisStatusBadgeProps
): JSX.Element {
  const definition = AiAnalysisUxModel.status(props.status);

  return (
    <div
      className="ai-analysis-analysis-status"
      data-status={definition.key}
      title={definition.description}
    >
      <span className="ai-analysis-analysis-status-dot" />
      {definition.label}
    </div>
  );
}
