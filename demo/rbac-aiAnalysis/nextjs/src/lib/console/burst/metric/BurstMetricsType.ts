export type BurstMetricPoint = {
  t: number;
  elapsedMs?: number;
  completed: number;
  ok: number;
  errors: number;
  p50?: number;
  p95?: number;
  rps?: number;
};

export type BurstHistogramBucket = {
  label: string;
  count: number;
};