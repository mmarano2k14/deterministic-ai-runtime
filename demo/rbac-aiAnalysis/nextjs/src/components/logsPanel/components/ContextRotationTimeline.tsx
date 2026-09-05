"use client";

import { JSX, useMemo } from "react";
import { LogUiHelper } from "../helpers/LogUiHelper";
import {
  ContextRotationRealtimeEvent,
  ContextRotationRealtimeHelper,
} from "../helpers/ContextRotationRealtimeHelper";
import { ConsoleLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";

type Segment = {
  from: string;
  key: string;
  start: number;
  end: number;
  visualEnd: number;
  isActive: boolean;
  overlapWindowMs: number;
  method?: string;
  path?: string;
};

type RequestPoint = {
  id: string;
  at: number;
  method?: string;
  path?: string;
  status?: number;
  hasRotation: boolean;
  isError: boolean;
};

export type ContextRotationTimelineProps = {
  logs: ConsoleLogEntry[];
  activeContextKey?: string;
  showRequests?: boolean;
  maxItems?: number;
};

function shortKey(v: string): string {
  if (v.length <= 14) return v;
  return `${v.slice(0, 6)}...${v.slice(-4)}`;
}

function formatMs(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  return `${(ms / 1000).toFixed(2)} s`;
}

function LegendItem(props: {
  className: string;
  label: string;
}): JSX.Element {
  const { className, label } = props;

  return (
    <span className="context-rotation-timeline__legend-item">
      <span className={className} />
      <span>{label}</span>
    </span>
  );
}

function buildSegments(
  events: ContextRotationRealtimeEvent[],
  activeContextKey?: string
): Segment[] {
  if (events.length === 0) {
    return [];
  }

  const result: Segment[] = [];

  for (let i = 0; i < events.length; i++) {
    const current = events[i];
    const next = events[i + 1];

    const start = current.at;

    const end = next
      ? next.at
      : current.at + Math.max(current.overlapWindowMs, 1000);

    const visualEnd = Math.max(
      end,
      current.at + Math.max(current.overlapWindowMs, 0)
    );

    result.push({
      from: current.oldContextKey,
      key: current.newContextKey,
      start,
      end,
      visualEnd,
      isActive: current.newContextKey === activeContextKey,
      overlapWindowMs: current.overlapWindowMs,
      method: current.method,
      path: current.path,
    });
  }

  return result;
}

export function ContextRotationTimeline(
  props: ContextRotationTimelineProps
): JSX.Element {
  const {
    logs,
    activeContextKey,
    showRequests = false,
    maxItems = 10,
  } = props;

  const { segments, requestPoints, min, max } = useMemo(() => {
    const rotationEvents =
      ContextRotationRealtimeHelper.extractRotationEvents(logs);

    const allSegments = buildSegments(rotationEvents, activeContextKey);
    const segments = allSegments.slice(-maxItems);

    if (segments.length === 0) {
      return {
        segments: [] as Segment[],
        requestPoints: [] as RequestPoint[],
        min: 0,
        max: 1,
      };
    }

    const min = Math.min(...segments.map((s) => s.start));
    const max = Math.max(...segments.map((s) => s.visualEnd));

    const requestPoints: RequestPoint[] = logs
      .filter(LogUiHelper.isHttpLogEntry)
      .filter((l) => {
        const t = new Date(l.t).getTime();
        return t >= min && t <= max;
      })
      .map((l, index) => ({
        id: `${l.id ?? index}-${l.t}`,
        at: new Date(l.t).getTime(),
        method: l.method,
        path: l.path,
        status: l.status,
        hasRotation: !!l.rotation,
        isError:
          !!l.error || (typeof l.status === "number" && l.status >= 400),
      }));

    return { segments, requestPoints, min, max };
  }, [logs, activeContextKey, maxItems]);

  if (segments.length === 0) {
    return (
      <div className="context-rotation-timeline--empty">
        No context rotation data yet.
      </div>
    );
  }

  const range = Math.max(1, max - min);
  const ticks = [0, 0.25, 0.5, 0.75, 1];

  return (
    <div className="context-rotation-timeline">
      <div className="context-rotation-timeline__axis-row">
        <div className="context-rotation-timeline__axis-spacer" />

        <div className="context-rotation-timeline__axis">
          {ticks.map((t) => {
            const value = min + range * t;

            return (
              <div
                key={t}
                className="context-rotation-timeline__tick"
                style={{ left: `${t * 100}%` }}
              >
                {formatMs(value - min)}
              </div>
            );
          })}
        </div>
      </div>

      <div className="context-rotation-timeline__segments">
        {segments.map((s, index) => {
          const left = ((s.start - min) / range) * 100;
          const width = ((s.visualEnd - s.start) / range) * 100;

          const next = segments[index + 1];
          const overlapsNext = !!next && s.visualEnd > next.start;

          const barToneClassName = s.isActive
            ? "context-rotation-timeline__segment-bar--active"
            : overlapsNext
            ? "context-rotation-timeline__segment-bar--overlap"
            : "context-rotation-timeline__segment-bar--normal";

          const barClassName = [
            "context-rotation-timeline__segment-bar",
            barToneClassName,
            showRequests
              ? "context-rotation-timeline__segment-bar--with-requests"
              : "",
          ]
            .filter(Boolean)
            .join(" ");

          return (
            <div
              key={`${s.key}-${s.start}`}
              className="context-rotation-timeline__segment-row"
            >
              <div
                className={
                  s.isActive
                    ? "context-rotation-timeline__label context-rotation-timeline__label--active"
                    : "context-rotation-timeline__label"
                }
                title={`${s.from} -> ${s.key}`}
              >
                <div className="context-rotation-timeline__label-main">
                  {shortKey(s.from)} → {shortKey(s.key)}
                </div>

                <div className="context-rotation-timeline__label-sub">
                  {s.method ?? "?"} {s.path ?? ""}
                </div>
              </div>

              <div
                className={
                  showRequests
                    ? "context-rotation-timeline__track context-rotation-timeline__track--with-requests"
                    : "context-rotation-timeline__track"
                }
                title={`${s.key} | overlap=${s.overlapWindowMs} ms | duration=${formatMs(
                  s.visualEnd - s.start
                )}`}
              >
                <div
                  className={barClassName}
                  style={{
                    left: `${left}%`,
                    width: `${Math.max(width, 2)}%`,
                  }}
                />

                {showRequests &&
                  requestPoints.map((r) => {
                    const requestLeft = ((r.at - min) / range) * 100;

                    const requestClassName = r.isError
                      ? "context-rotation-timeline__request context-rotation-timeline__request--error"
                      : r.hasRotation
                      ? "context-rotation-timeline__request context-rotation-timeline__request--rotated"
                      : "context-rotation-timeline__request context-rotation-timeline__request--normal";

                    return (
                      <div
                        key={r.id}
                        title={`${r.method ?? "?"} ${r.path ?? ""} | ${
                          typeof r.status === "number" ? r.status : "-"
                        }${r.hasRotation ? " | rotated" : ""}`}
                        className={requestClassName}
                        style={{ left: `${requestLeft}%` }}
                      />
                    );
                  })}
              </div>
            </div>
          );
        })}
      </div>

      <div className="context-rotation-timeline__legend">
        <LegendItem
          className="context-rotation-timeline__legend-dot context-rotation-timeline__legend-dot--lifetime"
          label="Context lifetime"
        />
        <LegendItem
          className="context-rotation-timeline__legend-dot context-rotation-timeline__legend-dot--active"
          label="Active context"
        />
        <LegendItem
          className="context-rotation-timeline__legend-dot context-rotation-timeline__legend-dot--overlap"
          label="Overlap context"
        />

        {showRequests && (
          <>
            <LegendItem
              className="context-rotation-timeline__legend-dot context-rotation-timeline__legend-dot--request"
              label="HTTP request"
            />
            <LegendItem
              className="context-rotation-timeline__legend-dot context-rotation-timeline__legend-dot--error"
              label="HTTP error"
            />
          </>
        )}
      </div>
    </div>
  );
}