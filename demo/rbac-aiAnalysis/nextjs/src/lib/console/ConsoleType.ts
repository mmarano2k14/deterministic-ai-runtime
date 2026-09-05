import { ConsoleLogEntry } from "../infrastructure/logs/inMemoryLogType";
import { MultiplexedRbacApi } from "../rbac/MultiplexedRbacApi";


export type ConsoleStatus = "Idle" | "Running" | "Expired" | "Error";

export type InFlightMaxValue =
  | "0"
  | "1"
  | "2"
  | "3"
  | "4"
  | "5"
  | "10"
  | "20"
  | "30"
  | "40"
  | "50"
  | "100"
  | "200";

export const maxInFlightOptions: Array<{
  value: InFlightMaxValue;
  label: string;
}> = [
  { value: "0", label: "Unlimited" },
  { value: "1", label: "1" },
  { value: "2", label: "2" },
  { value: "3", label: "3" },
  { value: "4", label: "4" },
  { value: "5", label: "5" },
  { value: "10", label: "10" },
  { value: "20", label: "20" },
  { value: "30", label: "30" },
  { value: "40", label: "40" },
  { value: "50", label: "50" },
  { value: "100", label: "100" },
  { value: "200", label: "200" },
];
  
/**
 * Preset values used by the overlap slider.
 * This keeps the demo guided and avoids random values that are less meaningful.
 */
export const rotationOverlapPresets = ["0", "50", "100", "200", "500", "1000", "5000", "10000"];

export type ConsoleState = {
  status: ConsoleStatus;

  // inputs
  baseUrl: string;
  demoUserId: string;
  contextKey: string;
  maxInFlight: InFlightMaxValue;
  rotationOverlapMs: string;

  invoiceId: string;
  amount: number;

  // runtime
  logs: ConsoleLogEntry[];
  lastError?: string;

  // derived flags
  busy: boolean;
};



export type ConsoleSession = {
  baseUrl?: string;
  demoUserId?: string;
  contextKey?: string;
};

export type ConsoleContextAccessor = {
  api: MultiplexedRbacApi
  dispatch: (e: ConsoleEvent) => void;
} 


export type ConsoleEvent =
  | { type: "TargetChanged"; baseUrl: string }
  | { type: "DemoUserChanged"; demoUserId: string }
  | { type: "ContextChanged"; contextKey: string }
  | { type: "InvoiceChanged"; invoiceId: string }
  | { type: "maxInFlightChanged"; maxInFlightValue: InFlightMaxValue }
  | { type: "rotationOverlapMsChange"; rotationOverlapMs: string }
  | { type: "AmountChanged"; amount: number }
  | { type: "LogsChanged"; logs: ConsoleLogEntry[] }
  | { type: "ClearLogs" }
  | { type: "StartCall" }
  | { type: "CallSucceeded"; rotatedContextKey?: string }
  | { type: "CallForbiddenOrUnauthorized"; httpStatus: number }
  | { type: "CallFailed"; message: string }
  | { type: "ResetError" }
  | { type: "LoginSucceeded"; demoUserId: string; contextKey: string }
  | { type: "BootStrapSucceeded"; demoUserId: string; contextKey: string };


  
