import { MultiplexedRbacApi } from "../rbac/MultiplexedRbacApi";
import { ConsoleState, ConsoleEvent } from "./ConsoleType";
import { RealtimeClient } from "../infrastructure/realtime/RealtimeClient";
import { RealtimeConnectionState } from "../infrastructure/realtime/RealtimeType";


export type ConsoleActions = {
  getContextKey(): Promise<void>;
  readInvoice(): Promise<void>;
  refundInvoice(): Promise<void>;
  login(username: string): Promise<boolean | undefined>;
  bootstrap(): Promise<void>;
  clearLogs(): void;
  resetError(): void;
};

export type ConsoleRealtime = {
  client: RealtimeClient;
  connect(): Promise<void>;
  disconnect(): Promise<void>;
  getState(): RealtimeConnectionState;
};

export type ConsoleController = {
  state: ConsoleState;
  dispatch: (e: ConsoleEvent) => void;
  actions: ConsoleActions;
  api: MultiplexedRbacApi;
  realtime: ConsoleRealtime;
  readonly subscribe: (listener: () => void) => () => void;
};