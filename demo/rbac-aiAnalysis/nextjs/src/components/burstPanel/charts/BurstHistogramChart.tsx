"use client";

import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

import { BurstHistogramBucket } from "../../../lib/console/burst/metric/BurstMetricsType";
import { JSX, useMemo } from "react";

type Props = {
  data: BurstHistogramBucket[];
};


export function BurstHistogramChart({ data }: Props): JSX.Element {
  const hasData = data.some((x) => x.count > 0);

  const chartKey = useMemo(() => {
    return data.map((x) => `${x.label}:${x.count}`).join("|");
  }, [data]);

  if (!hasData) {
    return (
      <div className="burst-chart burst-chart--empty">
        No latency data yet.
      </div>
    );
  }

  return (
    <div className="burst-chart burst-chart--histogram">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart
          className="burst-recharts"
          key={chartKey}
          data={data}
          margin={{ top: 8, right: 16, bottom: 8, left: 0 }}
          barCategoryGap="18%"
        >
          <CartesianGrid
            strokeDasharray="3 3"
            vertical={false}
            stroke="var(--chart-grid)"
          />

          <XAxis
            dataKey="label"
            tickLine={false}
            axisLine={false}
            interval={0}
            tick={{ fontSize: 12, fontWeight: 500 }}
          />

          <YAxis
            tickLine={false}
            axisLine={false}
            allowDecimals={false}
            width={40}
            tick={{ fontSize: 12, fontWeight: 500 }}
          />

          <Tooltip
            cursor={{ opacity: 0.08 }}
            formatter={(value) => [`${value} request(s)`, "Count"]}
            labelFormatter={(label) => `Latency: ${label}`}
          />

          <Bar
            dataKey="count"
            radius={[10, 10, 0, 0]}
            maxBarSize={72}
            isAnimationActive={false}
          >
            {data.map((entry, index) => (
              <Cell
                key={`${entry.label}-${entry.count}-${index}`}
                fill={
                  entry.count > 0
                    ? "var(--chart-bar)"
                    : "var(--chart-bar-empty)"
                }
              />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}