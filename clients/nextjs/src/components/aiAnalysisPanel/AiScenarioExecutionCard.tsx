import { JSX } from "react";
import { Button } from "@/components/ui/Button";
import type {
  RuntimeAnalysisScenarioExecutionResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";

export type AiScenarioExecutionCardProps = {
  execution: RuntimeAnalysisScenarioExecutionResult;
  isExecuting: boolean;
  error: string | null;
  onExecute: () => void;
};

export function AiScenarioExecutionCard(
  props: AiScenarioExecutionCardProps
): JSX.Element {
  const {
    execution,
    isExecuting,
    error,
    onExecute,
  } = props;

  const pending =
    execution.required && execution.status === "Pending";

  return (
    <div
      className="ai-analysis-scenario-execution-card"
      data-status={execution.status}
    >
      <div className="ai-analysis-human-approval-header">
        <div>
          <div className="ai-analysis-result-subheading">
            Approved scenario execution
          </div>
          <div className="ai-analysis-policy-pipeline">
            Existing Next.js BurstController · durable external wait
          </div>
        </div>

        <div className="ai-analysis-human-approval-badge">
          {isExecuting && pending ? "CLIENT RUNNING" : execution.status.toUpperCase()}
        </div>
      </div>

      <div className="ai-analysis-human-approval-message">
        {execution.message ?? defaultMessage(execution.status)}
      </div>

      {execution.observation ? (
        <div className="ai-analysis-scenario-execution-metrics">
          <div>
            <span>Completed</span>
            <strong>{execution.observation.completed}</strong>
          </div>
          <div>
            <span>OK</span>
            <strong>{execution.observation.ok}</strong>
          </div>
          <div>
            <span>429</span>
            <strong>{execution.observation.tooManyRequests}</strong>
          </div>
          <div>
            <span>Errors</span>
            <strong>{execution.observation.errors}</strong>
          </div>
          <div>
            <span>P50</span>
            <strong>{formatMs(execution.observation.p50Ms)}</strong>
          </div>
          <div>
            <span>P95</span>
            <strong>{formatMs(execution.observation.p95Ms)}</strong>
          </div>
        </div>
      ) : null}

      {pending && !isExecuting ? (
        <Button
          variant="primary"
          onClick={onExecute}
          title="Execute the already-approved scenario through the existing BurstController"
        >
          Run approved scenario
        </Button>
      ) : null}

      {error ? (
        <div className="ai-analysis-policy-validation-error">
          {error}
        </div>
      ) : null}
    </div>
  );
}

function defaultMessage(
  status: RuntimeAnalysisScenarioExecutionResult["status"]
): string {
  switch (status) {
    case "NotStarted":
      return "The approved scenario execution boundary has not started yet.";
    case "Pending":
      return "The runtime is parked until the approved scenario is executed by the existing client runner.";
    case "Completed":
      return "Observed client execution was durably returned to the same runtime ExecutionId.";
    case "Failed":
      return "The approved scenario execution failed.";
    case "NotExecuted":
      return "The proposal did not cross the execution boundary.";
  }
}

function formatMs(
  value: number | null
): string {
  return value === null
    ? "—"
    : `${value.toFixed(1)} ms`;
}
