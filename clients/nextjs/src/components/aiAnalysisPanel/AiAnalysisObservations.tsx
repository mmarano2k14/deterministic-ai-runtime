import { JSX } from "react";
import type { RuntimeAnalysisObservation } from "@/lib/aiAnalysis/RuntimeAnalysisType";
import styles from "./AiAnalysisPanel.module.css";

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
    <div className={styles.observations}>
      <div className={styles.resultSubheading}>Observations</div>

      {props.observations.map((observation, index) => (
        <div
          className={styles.observation}
          key={`${observation.title}-${index}`}
        >
          <div className={styles.observationTitle}>
            {observation.title}
          </div>

          <div className={styles.observationDetail}>
            {observation.detail}
          </div>

          {observation.evidenceIndexes.length > 0 && (
            <div className={styles.evidenceReferences}>
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
