"use client";

import { JSX } from "react";

export type BurstFooterProps = {
  durations?: number[];
  metricPoints: number;
};

export function BurstFooter(props: BurstFooterProps): JSX.Element {
  const { durations, metricPoints } = props;

  const min =
    durations?.length
      ? Math.min(...durations).toFixed(2)
      : "-";

  const max =
    durations?.length
      ? Math.max(...durations).toFixed(2)
      : "-";

  return (
    <>
      <div className="burst-footer__tip">
        Tip: use Single burst for brute contention,
        Maintained concurrency for sustained load,
        and Wave batches for fixed-size packet testing.
      </div>

      <div className="burst-footer__metrics">
        min latency: <b>{min}</b> ms {" | "}
        max latency: <b>{max}</b> ms {" | "}
        metric points: <b>{metricPoints}</b>
      </div>
    </>
  );
}