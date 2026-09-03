"use client";

import { JSX, useEffect, useState } from "react";
import styles from "./AiAnalysisPanel.module.css";

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
      className={styles.aiActivity}
      aria-live="polite"
      aria-busy="true"
      role="status"
    >
      <div className={styles.aiActivitySweep} aria-hidden="true" />

      <div className={styles.aiActivityHeader}>
        <div className={styles.aiActivityIdentity}>
          <span className={styles.aiActivityOrb} aria-hidden="true">
            <span />
            <span />
            <span />
          </span>

          <div className={styles.aiActivityHeading}>
            <strong>AI is working</strong>
            <span>
              {preparingContext
                ? "Preparing deterministic runtime context"
                : "Analyzing runtime evidence"}
            </span>
          </div>
        </div>

        <span className={styles.aiActivityElapsed}>{elapsedSeconds}s</span>
      </div>

      <div className={styles.aiActivitySteps}>
        <div
          className={styles.aiActivityStep}
          data-state={preparingContext ? "active" : "done"}
        >
          <span className={styles.aiActivityStepDot} aria-hidden="true" />
          <div>
            <strong>Prepare context</strong>
            <span>
              Build the bounded snapshot from this scenario, metrics, and logs.
            </span>
          </div>
        </div>

        <div
          className={styles.aiActivityStep}
          data-state={preparingContext ? "pending" : "active"}
        >
          <span className={styles.aiActivityStepDot} aria-hidden="true" />
          <div>
            <strong>Analyze evidence</strong>
            <span>
              Send the bounded evidence to the configured AI provider and wait
              for the structured result.
            </span>
          </div>
        </div>
      </div>

      <div className={styles.aiActivityLog}>
        <span className={styles.aiActivityLogLabel}>
          Latest runtime activity
        </span>

        {latestLog ? (
          <div className={styles.aiActivityLogValue}>
            <strong>{latestLog.name}</strong>
            <span>
              {latestLog.method} {latestLog.path} · {latestLog.status}
            </span>
          </div>
        ) : (
          <div className={styles.aiActivityLogValue}>
            <strong>Runtime analysis</strong>
            <span>Waiting for the current analysis request log…</span>
          </div>
        )}
      </div>
    </div>
  );
}
