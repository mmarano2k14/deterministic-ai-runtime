import { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";
import { JSX, useEffect, useMemo, useState } from "react";

export type LogsTimelineProps = {
  logs: ConsoleLogEntry[];
  windowSeconds?: number;
};

type TimelineTone =
  | "info"
  | "success"
  | "warning"
  | "danger";

export function LogsTimeline(props: LogsTimelineProps): JSX.Element {
  const { logs, windowSeconds = 10 } = props;

  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => {
      setNow(Date.now());
    }, 500);

    return () => {
      window.clearInterval(timer);
    };
  }, []);

  const points = useMemo(() => {
    const windowMs = windowSeconds * 1000;

    return logs
      .map((log) => {
        const ts = new Date(log.t).getTime();
        const age = now - ts;

        if (age > windowMs) {
          return null;
        }

        const position = 1 - age / windowMs;

        return {
          ...log,
          position,
        };
      })
      .filter(Boolean) as Array<ConsoleLogEntry & { position: number }>;
  }, [logs, now, windowSeconds]);

  function getTone(log: ConsoleLogEntry): TimelineTone {
    if (log.kind === "http") {
      const status = log.status ?? 0;

      if (status >= 200 && status < 300) return "success";
      if (status >= 400 && status < 500) return "warning";
      if (status >= 500) return "danger";

      return "info";
    }

    const level = (log.level ?? "").toLowerCase();

    if (level === "error") return "danger";
    if (level === "warning") return "warning";

    return "info";
  }

  return (
    <div className="logs-timeline">
      {points.map((point) => {
        const left = point.position * 100;

        return (
          <div
            key={point.id}
            className="logs-timeline__point"
            data-tone={getTone(point)}
            title={
              (point.kind === "http"
                ? point.name
                : point.eventName)
              ?? "event"
            }
            style={{
              left: `${left}%`,
            }}
          />
        );
      })}
    </div>
  );
}
