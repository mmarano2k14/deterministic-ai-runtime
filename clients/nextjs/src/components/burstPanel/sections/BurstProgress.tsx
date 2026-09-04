"use client";

import { JSX } from "react";
import { UiHelpers } from "../helpers/uiHelpers";

export type BurstProgressProps = {
  started: number;
  completed: number;
  inFlight: number;
  total: number;
  durations?: number[];
  metricPoints: number;
};

/**
 * Progress bar + progress counters.
 */
export function BurstProgress(props: BurstProgressProps): JSX.Element {
  const { started, completed, inFlight, total, durations, metricPoints } = props;

  const ratio = UiHelpers.ratio(completed, total);

  const min =
    durations?.length
      ? Math.min(...durations).toFixed(2)
      : "0";

  const max =
    durations?.length
      ? Math.max(...durations).toFixed(2)
      : "0";

  return (
    <div className="burst-progress">
      <UiHelpers.ProgressBar value={ratio} />

      <div className="burst-progress__meta">
        <span>
          started: <b>{started}</b> 
          — completed: <b>{completed}</b> 
          — inFlight:{" "}<b>{inFlight}</b>
          — min latency: <b>{min}</b> ms
          — max latency: <b>{max}</b> ms
          — metric points: <b>{metricPoints}</b> ms

        </span>
        <span>{Math.round(ratio * 100)}%</span>
      </div>

    </div>
  );
}