import { JSX } from "react";
import { LogBadge } from "./LogBadge";
import { RealtimeLogHelper } from "../helpers/RealtimeLogHelper";
import { RealtimeLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";

export type RealtimeLogCardProps = {
  log: RealtimeLogEntry;
};

export function RealtimeLogCard(props: RealtimeLogCardProps): JSX.Element {
  const { log: l } = props;

  const levelTone = RealtimeLogHelper.getLevelTone(l.level);
  const badges = RealtimeLogHelper.getBadges(l);

  return (
    <div
      className="realtime-log-card"
      data-tone={levelTone}
    >
      <div className="realtime-log-card__header">
        <div className="realtime-log-card__identity">
          {badges.map((badge) => (
            <LogBadge key={badge.label} badge={badge} />
          ))}

          <span>{l.eventName ?? "realtime-event"}</span>

          {l.level ? (
            <code
              className="realtime-log-card__level"
              data-tone={levelTone}
            >
              {l.level}
            </code>
          ) : null}
        </div>

        <div className="realtime-log-card__timestamp">{l.t}</div>
      </div>

      <div className="realtime-log-card__body">
        {l.category ? (
          <div>
            <b>Category:</b> <code>{l.category}</code>
          </div>
        ) : null}

        {l.message ? (
          <div className="realtime-log-card__spaced">
            <b>Message:</b> <code>{l.message}</code>
          </div>
        ) : null}

        {typeof l.payload !== "undefined" ? (
          <details className="realtime-log-card__details">
            <summary className="realtime-log-card__summary">
              Payload
            </summary>
            <pre className="realtime-log-card__pre">
              {JSON.stringify(l.payload, null, 2)}
            </pre>
          </details>
        ) : null}
      </div>
    </div>
  );
}
