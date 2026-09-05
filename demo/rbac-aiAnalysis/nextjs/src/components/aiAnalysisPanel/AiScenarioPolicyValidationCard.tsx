import { JSX } from "react";
import type {
  RuntimeAnalysisScenarioPolicyValidationResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";

export type AiScenarioPolicyValidationCardProps = {
  validation: RuntimeAnalysisScenarioPolicyValidationResult;
};

export function AiScenarioPolicyValidationCard(
  props: AiScenarioPolicyValidationCardProps
): JSX.Element {
  const { validation } = props;

  return (
    <div className="ai-analysis-policy-validation">
      <div className="ai-analysis-policy-validation-header">
        <div>
          <div className="ai-analysis-result-subheading">
            Automatic policy validation
          </div>
          <div className="ai-analysis-policy-pipeline">
            Same runtime-analysis DAG · dynamic pipeline policy config
          </div>
        </div>

        <div
          className="ai-analysis-policy-decision-badge"
          data-allowed={validation.allowed}
        >
          {validation.allowed ? "ALLOWED" : "DENIED"}
        </div>
      </div>

      <div className="ai-analysis-policy-decision-list">
        {validation.policyDecisions.map((decision) => (
          <div
            key={decision.policyKey}
            className="ai-analysis-policy-decision"
            data-allowed={decision.allowed}
          >
            <div className="ai-analysis-policy-decision-topline">
              <strong>{shortPolicyKey(decision.policyKey)}</strong>
              <span>{decision.allowed ? "Allowed" : "Denied"}</span>
            </div>
            <div className="ai-analysis-policy-decision-message">
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
