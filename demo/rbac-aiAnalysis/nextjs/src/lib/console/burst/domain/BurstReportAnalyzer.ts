import { BurstConfig, BurstReport } from "../runtime/BurstMachineType";

export type BurstReportAnalysis = {
  summary: string;
  rootCause: string;
  recommendations: Array<{
    config: BurstConfig;
    reason: string;
  }>;
};

export class BurstReportAnalyzer {
  static analyze(report: BurstReport): BurstReportAnalysis {
    const rejected = report.counters.rejected;
    const completed = report.progress.completed || 1;
    const rejectionRate = rejected / completed;

    if (rejectionRate > 0.2) {
      return {
        summary: "High rejection rate detected.",
        rootCause: "System likely overloaded (too much concurrency).",
        recommendations: [
          {
            config: {
              ...report.config,
              dispatchMode: "maintained-concurrency",
              concurrency: 5,
            },
            reason: "Reduce concurrency to stabilize throughput.",
          },
        ],
      };
    }

    return {
      summary: "Run appears stable.",
      rootCause: "No major issue detected.",
      recommendations: [],
    };
  }
}