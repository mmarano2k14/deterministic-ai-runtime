import { JSX } from "react";
import type { RuntimeAnalysisObservation } from "@/lib/aiAnalysis/RuntimeAnalysisType";

export type AiAnalysisObservationsProps = {
  observations: readonly RuntimeAnalysisObservation[];
};

export function AiAnalysisObservations(
  props: AiAnalysisObservationsProps
): JSX.Element | null {
  if (props.observations.length === 0) {
    return null;
  }

  return (
    <div className="ai-analysis-observations">
      <div className="ai-analysis-result-subheading">Observations</div>

      {props.observations.map((observation, index) => (
        <div
          className="ai-analysis-observation"
          key={`${observation.title}-${index}`}
        >
          <div className="ai-analysis-observation-title">
            {observation.title}
          </div>

          <div className="ai-analysis-observation-detail">
            {observation.detail}
          </div>

          {observation.evidenceIndexes.length > 0 && (
            <div className="ai-analysis-evidence-references">
              Evidence{" "}
              {observation.evidenceIndexes
                .map((evidenceIndex) => `#${evidenceIndex}`)
                .join(", ")}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
