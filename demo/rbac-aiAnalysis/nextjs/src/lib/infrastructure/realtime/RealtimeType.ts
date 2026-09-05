import { InMemoryLogSinkItem } from "../logs/inMemoryLogType";


export type RealtimeConnectionState =
  | "idle"
  | "connecting"
  | "connected"
  | "disconnected"
  | "error";

export type RealtimeEventEnvelope<TPayload = unknown> = {
  type: string;
  payload: TPayload;
};

export type RealtimeEventHandler<TPayload = unknown> = (
  payload: TPayload,
  envelope: RealtimeEventEnvelope<TPayload>
) => void | Promise<void>;

export type RealtimeDisconnectReason =
  | "client-stop"
  | "transport-closed"
  | "transport-error"
  | "authentication-failed"
  | "unknown";

export type RealtimeClientOptions = {
  /**
   * Base realtime endpoint.
   * Example:
   * - /runtime/live
   * - http://localhost:5000/runtime/live
   */
  url: string;

  /**
   * Optional user id used when the backend supports user-specific routing.
   * This matches the backend logic where events may target:
   * - RealtimeTarget.User(userId)
   * - RealtimeTarget.Group("runtime-console")
   */
  userId?: string | null;

  /**
   * Optional group subscriptions requested by the client after connection.
   * For now we already know we want "runtime-console".
   */
  groups?: string[];

  /**
   * Optional access token or context key if the transport needs it later.
   * Kept generic on purpose.
   */
  accessToken?: string | null;

  /**
   * Auto reconnect toggle.
   */
  autoReconnect?: boolean;

  /**
   * Delay in milliseconds before reconnect attempts.
   */
  reconnectDelayMs?: number;
};

export type RealtimeLifecycleHandlers = {
  onStateChanged?: (state: RealtimeConnectionState) => void;
  onError?: (error: unknown) => void;
  onDisconnected?: (reason: RealtimeDisconnectReason) => void;
};

export type RealtimeSubscription = {
  unsubscribe(): void;
};

export type RuntimeLogEvent = {
  kind : "realtime";
  level: string;
  message: string;
  category?: string | null;
  userId?: string | null;
  data?: unknown;
  occurredAtUtc: string;
};

export type ContextRotatedEvent = InMemoryLogSinkItem & {
  userId: string;
  oldContextKey: string;
  newContextKey: string;
  occurredAtUtc: string;
};