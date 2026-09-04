"use client";

import { JSX, useEffect, useState } from "react";
import styles from "./AiAnalysisPanel.module.css";

export type AiOnlyActivity = {
  key: string;
  title: string;
  detail: string;
  context: string;
  startedAtUtc: string;
  provider: string | null;
  model: string | null;
};

export type AiInvestigationActivityDockProps = {
  activity: AiOnlyActivity | null;
};

export function AiInvestigationActivityDock(
  props: AiInvestigationActivityDockProps
): JSX.Element | null {
  const { activity } = props;
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!activity) {
      return;
    }

    setNow(Date.now());

    const timer = window.setInterval(() => {
      setNow(Date.now());
    }, 1000);

    return () => window.clearInterval(timer);
  }, [activity?.key, activity]);

  if (!activity) {
    return null;
  }

  const startedAt = Date.parse(activity.startedAtUtc);
  const effectiveStartedAt =
    Number.isFinite(startedAt) ? startedAt : now;

  const elapsedSeconds = Math.max(
    0,
    Math.floor((now - effectiveStartedAt) / 1000)
  );

  return (
    <section
      className={styles.aiOnlyActivityDock}
      aria-live="polite"
      aria-busy="true"
      role="status"
    >
      <div
        className={styles.aiOnlyActivitySweep}
        aria-hidden="true"
      />

      <div className={styles.aiOnlyActivityMain}>
        <span
          className={styles.aiOnlyActivityOrb}
          aria-hidden="true"
        >
          <span />
          <span />
          <span />
        </span>

        <div className={styles.aiOnlyActivityCopy}>
          <div className={styles.aiOnlyActivityTitleRow}>
            <strong>{activity.title}</strong>
            <span className={styles.aiOnlyActivityContext}>
              {activity.context}
            </span>
          </div>

          <span>{activity.detail}</span>
        </div>

        <div className={styles.aiOnlyActivityElapsed}>
          {elapsedSeconds}s
        </div>
      </div>

      <div className={styles.aiOnlyActivityRuntime}>
        <span className={styles.aiOnlyActivityRuntimeLabel}>
          Live AI provider
        </span>

        <span className={styles.aiOnlyActivityRuntimeValue}>
          <strong>
            {activity.provider ?? "AI"}
          </strong>
          <span>
            {activity.model
              ? `${activity.model} · realtime backend signal`
              : "realtime backend signal"}
          </span>
        </span>
      </div>
    </section>
  );
}
