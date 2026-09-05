import type { BurstReport } from "../runtime/BurstMachineType";

// Snapshot of a completed burst execution
export type BurstRun = {
  // Unique identifier
  id: string;

  // Creation timestamp (ISO string)
  createdAt: string;

  // The full execution report
  report: BurstReport;

  // Optional parent run (for replay / derivation)
  basedOnRunId?: string;
};