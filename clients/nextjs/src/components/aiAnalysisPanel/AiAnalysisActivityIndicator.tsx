"use client";

import { JSX, useEffect, useState } from "react";

export type AiAnalysisActivityPhase =
  | "preparing-context"
  | "analyzing-evidence";

export type AiAnalysisActivityLog = {
  name: string;
  method: string;
  path: string;
  status: string;
};

export type AiAnalysisActivityIndicatorProps = {
  phase: AiAnalysisActivityPhase;
  startedAt: number;
  latestLog: AiAnalysisActivityLog | null;
};

export function AiAnalysisActivityIndicator(
  props: AiAnalysisActivityIndicatorProps
): JSX.Element {
  const { phase, startedAt, latestLog } = props;
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    setNow(Date.now());

    const timer = window.setInterval(() => {
      setNow(Date.now());
    }, 1000);

    return () => window.clearInterval(timer);
  }, [startedAt]);

  const elapsedSeconds = Math.max(
    0,
    Math.floor((now - startedAt) / 1000)
  );

  const preparingContext = phase === "preparing-context";

  return (
    <div
      className="ai-analysis-ai-activity"
      aria-live="polite"
      aria-busy="true"
      role="status"
    >
      <div className="ai-analysis-ai-activity-sweep" aria-hidden="true" />

      <div className="ai-analysis-ai-activity-header">
        <div className="ai-analysis-ai-activity-identity">
          <span className="ai-analysis-ai-activity-orb" aria-hidden="true">
            <span />
            <span />
            <span />
          </span>

          <div className="ai-analysis-ai-activity-heading">
            <strong>AI is working</strong>
            <span>
              {preparingContext
                ? "Preparing deterministic runtime context"
                : "Analyzing runtime evidence"}
            </span>
          </div>
        </div>

        <span className="ai-analysis-ai-activity-elapsed">{elapsedSeconds}s</span>
      </div>

      <div className="ai-analysis-ai-activity-steps">
        <div
          className="ai-analysis-ai-activity-step"
          data-state={preparingContext ? "active" : "done"}
        >
          <span className="ai-analysis-ai-activity-step-dot" aria-hidden="true" />
          <div>
            <strong>Prepare context</strong>
            <span>
              Build the bounded snapshot from this scenario, metrics, and logs.
            </span>
          </div>
        </div>

        <div
          className="ai-analysis-ai-activity-step"
          data-state={preparingContext ? "pending" : "active"}
        >
          <span className="ai-analysis-ai-activity-step-dot" aria-hidden="true" />
          <div>
            <strong>Analyze evidence</strong>
            <span>
              Send the bounded evidence to the configured AI provider and wait
              for the structured result.
            </span>
          </div>
        </div>
      </div>

      <div className="ai-analysis-ai-activity-log">
        <span className="ai-analysis-ai-activity-log-label">
          Latest runtime activity
        </span>

        {latestLog ? (
          <div className="ai-analysis-ai-activity-log-value">
            <strong>{latestLog.name}</strong>
            <span>
              {latestLog.method} {latestLog.path} · {latestLog.status}
            </span>
          </div>
        ) : (
          <div className="ai-analysis-ai-activity-log-value">
            <strong>Runtime analysis</strong>
            <span>Waiting for the current analysis request log…</span>
          </div>
        )}
      </div>
    </div>
  );
}
