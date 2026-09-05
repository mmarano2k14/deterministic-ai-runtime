"use client";

import { JSX, useEffect, useState } from "react";

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
      className="ai-analysis-ai-only-activity-dock"
      aria-live="polite"
      aria-busy="true"
      role="status"
    >
      <div
        className="ai-analysis-ai-only-activity-sweep"
        aria-hidden="true"
      />

      <div className="ai-analysis-ai-only-activity-main">
        <span
          className="ai-analysis-ai-only-activity-orb"
          aria-hidden="true"
        >
          <span />
          <span />
          <span />
        </span>

        <div className="ai-analysis-ai-only-activity-copy">
          <div className="ai-analysis-ai-only-activity-title-row">
            <strong>{activity.title}</strong>
            <span className="ai-analysis-ai-only-activity-context">
              {activity.context}
            </span>
          </div>

          <span>{activity.detail}</span>
        </div>

        <div className="ai-analysis-ai-only-activity-elapsed">
          {elapsedSeconds}s
        </div>
      </div>

      <div className="ai-analysis-ai-only-activity-runtime">
        <span className="ai-analysis-ai-only-activity-runtime-label">
          Live AI provider
        </span>

        <span className="ai-analysis-ai-only-activity-runtime-value">
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
