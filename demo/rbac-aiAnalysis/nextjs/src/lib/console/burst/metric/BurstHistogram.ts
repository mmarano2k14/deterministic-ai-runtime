import { BurstHistogramBucket } from "./BurstMetricsType";

export class BurstHistogram {
  public static build(durations: number[]): BurstHistogramBucket[] {
    const buckets = [
      { max: 10, label: "0-10ms" },
      { max: 25, label: "11-25ms" },
      { max: 50, label: "26-50ms" },
      { max: 100, label: "51-100ms" },
      { max: 250, label: "101-250ms" },
      { max: 500, label: "251-500ms" },
      { max: 1000, label: "501ms-1s" },
      { max: Infinity, label: ">1s" },
    ];

    const result: BurstHistogramBucket[] = buckets.map((b) => ({
      label: b.label,
      count: 0,
    }));

    for (const d of durations) {
      const idx = buckets.findIndex((b) => d <= b.max);
      if (idx >= 0) result[idx].count++;
    }

    return result;
  }
}