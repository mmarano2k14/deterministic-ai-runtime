"use client";

import { JSX } from "react";
import { BurstGraph } from "../charts/BurstGraph";
import { BurstHistogramChart } from "../charts/BurstHistogramChart";
import { BurstHistogramBucket, BurstMetricPoint } from "@/lib/console/burst/metric/BurstMetricsType";

export type BurstChartsProps = {
  showCharts: boolean;
  metrics: BurstMetricPoint[];
  histogram: BurstHistogramBucket[];
};

export function BurstCharts(props: BurstChartsProps): JSX.Element | null {
  const { showCharts, metrics, histogram } = props;

  if (!showCharts) {
    return null;
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div>
        <div style={{ fontWeight: 700, marginBottom: 8 }}>
          Throughput &amp; latency
        </div>
        <BurstGraph data={metrics} />
      </div>

      <div>
        <div style={{ fontWeight: 700, marginBottom: 8 }}>
          Latency Histogram
        </div>
        <BurstHistogramChart data={histogram} />
      </div>
    </div>
  );
}