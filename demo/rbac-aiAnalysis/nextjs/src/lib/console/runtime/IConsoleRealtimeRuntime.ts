

import { RealtimeClient } from "../../infrastructure/realtime/RealtimeClient";
import { RealtimeConnectionState } from "../../infrastructure/realtime/RealtimeType";

export interface IConsoleRealtimeRuntime {
  readonly client: RealtimeClient;
  getState(): RealtimeConnectionState;
  connect(userId?: string | null): Promise<void>;
  disconnect(): Promise<void>;
  dispose(): Promise<void>;
}