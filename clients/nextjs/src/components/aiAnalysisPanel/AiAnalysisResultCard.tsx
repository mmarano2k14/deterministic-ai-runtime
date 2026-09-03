import { JSX } from "react";
import type {
  RuntimeAnalysisResult,
  RuntimeAnalysisScenarioPolicyRuntimeExecutionResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiAnalysisObservations } from "./AiAnalysisObservations";
import { AiSuggestedScenarioCard } from "./AiSuggestedScenarioCard";
import styles from "./AiAnalysisPanel.module.css";

export type AiAnalysisResultCardProps = {
  result: RuntimeAnalysisResult | null;
  error: string | null;
  isValidatingScenario: boolean;
  policyExecution: RuntimeAnalysisScenarioPolicyRuntimeExecutionResult | null;
  policyError: string | null;
  onValidateScenario: () => void;
};

export function AiAnalysisResultCard(
  props: AiAnalysisResultCardProps
): JSX.Element {
  const {
    result,
    error,
    isValidatingScenario,
    policyExecution,
    policyError,
    onValidateScenario,
  } = props;

  if (error) {
    return (
      <section className={`${styles.placeholder} ${styles.snapshotError}`}>
        <div className={styles.placeholderTitle}>AI analysis error</div>
        <p className={styles.placeholderText}>{error}</p>
      </section>
    );
  }

  if (!result) {
    return (
      <section className={styles.placeholder}>
        <div className={styles.placeholderTitle}>AI analysis output</div>
        <p className={styles.placeholderText}>
          Prepare the context, ask a runtime question, then request a structured
          AI analysis.
        </p>
      </section>
    );
  }

  return (
    <section className={`${styles.section} ${styles.resultSection}`}>
      <div className={styles.resultHeader}>
        <div>
          <div className={styles.sectionTitle}>AI finding</div>
          <div className={styles.confidence}>
            Confidence {Math.round(result.confidence * 100)}%
          </div>
        </div>

        <div
          className={styles.severity}
          data-severity={result.severity}
        >
          {result.severity}
        </div>
      </div>

      <div className={styles.answer}>{result.answer}</div>

      <div className={styles.summary}>{result.summary}</div>

      <AiAnalysisObservations observations={result.observations} />

      <AiSuggestedScenarioCard
        scenario={result.suggestedScenario}
        isValidating={isValidatingScenario}
        policyExecution={policyExecution}
        policyError={policyError}
        onValidate={onValidateScenario}
      />
    </section>
  );
}
