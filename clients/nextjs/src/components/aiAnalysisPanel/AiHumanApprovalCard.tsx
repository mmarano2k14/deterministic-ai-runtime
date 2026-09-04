import { JSX } from "react";
import { Button } from "@/components/ui/Button";
import type {
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisHumanApprovalResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";

export type AiHumanApprovalCardProps = {
  approval: RuntimeAnalysisHumanApprovalResult;
  isDeciding: boolean;
  error: string | null;
  onDecision: (decision: RuntimeAnalysisHumanApprovalDecision) => void;
};

export function AiHumanApprovalCard(
  props: AiHumanApprovalCardProps
): JSX.Element {
  const {
    approval,
    isDeciding,
    error,
    onDecision,
  } = props;

  const pending =
    approval.required && approval.status === "Pending";

  return (
    <div
      className="ai-analysis-human-approval-card"
      data-status={approval.status}
    >
      <div className="ai-analysis-human-approval-header">
        <div>
          <div className="ai-analysis-result-subheading">Human approval</div>
          <div className="ai-analysis-policy-pipeline">
            Durable external-wait boundary
          </div>
        </div>

        <div className="ai-analysis-human-approval-badge">
          {approvalLabel(approval)}
        </div>
      </div>

      <div className="ai-analysis-human-approval-message">
        {approval.message ?? defaultMessage(approval)}
      </div>

      {approval.decidedBy ? (
        <div className="ai-analysis-policy-runtime-identity">
          Decision by {approval.decidedBy}
        </div>
      ) : null}

      {pending ? (
        <div className="ai-analysis-approval-actions">
          <Button
            variant="neutral"
            disabled={isDeciding}
            onClick={() => onDecision("reject")}
            title="Reject the AI proposal and resume the same execution to a terminal non-executing state"
          >
            Reject
          </Button>
          <Button
            variant="primary"
            loading={isDeciding}
            disabled={isDeciding}
            onClick={() => onDecision("approve")}
            title="Approve the proposal; the same durable ExecutionId will continue to the approved-scenario execution boundary"
          >
            Approve &amp; run
          </Button>
        </div>
      ) : null}

      {error ? (
        <div className="ai-analysis-policy-validation-error">
          {error}
        </div>
      ) : null}
    </div>
  );
}

function approvalLabel(
  approval: RuntimeAnalysisHumanApprovalResult
): string {
  if (!approval.required) {
    return "NOT REQUIRED";
  }

  return approval.status.toUpperCase();
}

function defaultMessage(
  approval: RuntimeAnalysisHumanApprovalResult
): string {
  switch (approval.status) {
    case "Pending":
      return "Policies passed. The runtime execution is parked until an explicit human decision is durably recorded.";
    case "Approved":
      return "Approved. The same durable execution resumed through the runtime external-wait continuation path.";
    case "Rejected":
      return "Rejected. The proposal will not execute.";
    case "NotRequired":
      return "No human approval is available for this proposal.";
  }
}
