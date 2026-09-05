import { RealtimeConnectionState, RealtimeClientOptions, RealtimeLifecycleHandlers, RealtimeDisconnectReason, RealtimeEventHandler, RealtimeSubscription, RealtimeEventEnvelope } from "../RealtimeType";


export interface IRealtimeTransport {
  /**
   * Current transport connection state.
   */
  readonly state: RealtimeConnectionState;

  /**
   * Connects the transport to the backend realtime endpoint.
   */
  connect(
    options: RealtimeClientOptions,
    lifecycle?: RealtimeLifecycleHandlers
  ): Promise<void>;

  /**
   * Disconnects the transport from the backend realtime endpoint.
   */
  disconnect(reason?: RealtimeDisconnectReason): Promise<void>;

  /**
   * Subscribes to a specific public realtime event name.
   * Example:
   * - runtime-log
   * - context-rotated
   */
  subscribe<TPayload = unknown>(
    eventName: string,
    handler: RealtimeEventHandler<TPayload>
  ): RealtimeSubscription;

  /**
   * Sends a raw client message to the backend transport if supported.
   * This is useful for simple join commands in raw websocket mode.
   */
  send?(message: string | object): Promise<void>;

  /**
   * Optional hook called when a raw envelope is received.
   * Some transports may use this internally; exposing it keeps the contract flexible.
   */
  handleIncomingEnvelope?<TPayload = unknown>(
    envelope: RealtimeEventEnvelope<TPayload>
  ): Promise<void>;
}