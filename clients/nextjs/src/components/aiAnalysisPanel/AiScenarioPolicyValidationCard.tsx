import { JSX } from "react";
import type {
  RuntimeAnalysisScenarioPolicyValidationResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import styles from "./AiAnalysisPanel.module.css";

export type AiScenarioPolicyValidationCardProps = {
  validation: RuntimeAnalysisScenarioPolicyValidationResult;
};

export function AiScenarioPolicyValidationCard(
  props: AiScenarioPolicyValidationCardProps
): JSX.Element {
  const { validation } = props;

  return (
    <div className={styles.policyValidation}>
      <div className={styles.policyValidationHeader}>
        <div>
          <div className={styles.resultSubheading}>
            Automatic policy validation
          </div>
          <div className={styles.policyPipeline}>
            Same runtime-analysis DAG · dynamic pipeline policy config
          </div>
        </div>

        <div
          className={styles.policyDecisionBadge}
          data-allowed={validation.allowed}
        >
          {validation.allowed ? "ALLOWED" : "DENIED"}
        </div>
      </div>

      <div className={styles.policyDecisionList}>
        {validation.policyDecisions.map((decision) => (
          <div
            key={decision.policyKey}
            className={styles.policyDecision}
            data-allowed={decision.allowed}
          >
            <div className={styles.policyDecisionTopline}>
              <strong>{shortPolicyKey(decision.policyKey)}</strong>
              <span>{decision.allowed ? "Allowed" : "Denied"}</span>
            </div>
            <div className={styles.policyDecisionMessage}>
              {decision.message}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function shortPolicyKey(policyKey: string): string {
  const parts = policyKey.split(".");
  return parts[parts.length - 1] || policyKey;
}
