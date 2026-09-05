

import { RealtimeEventHandler, RealtimeLifecycleHandlers, RealtimeClientOptions, RealtimeConnectionState, RealtimeDisconnectReason, RealtimeSubscription, RealtimeEventEnvelope } from "../RealtimeType";
import { IRealtimeTransport } from "./IRealtimeTransport";

/**
 * Basic WebSocket realtime transport.
 *
 * Responsibilities:
 * - maintain websocket connection
 * - receive events from server
 * - route events to registered handlers
 *
 * The server is expected to send messages in the form:
 *
 * {
 *   type: "runtime-log",
 *   payload: { ... }
 * }
 */
export class WebSocketRealtimeTransport implements IRealtimeTransport {

  private socket?: WebSocket;

  private handlers = new Map<string, Set<RealtimeEventHandler>>();

  private lifecycle?: RealtimeLifecycleHandlers;

  private options?: RealtimeClientOptions;

  public state: RealtimeConnectionState = "idle";

  /**
   * Connect to realtime endpoint.
   */
  async connect(
    options: RealtimeClientOptions,
    lifecycle?: RealtimeLifecycleHandlers
  ): Promise<void> {

    this.options = options;
    this.lifecycle = lifecycle;

    if (this.state === "connected")
      return;

    this.updateState("connecting");

    const url = this.buildUrl(options);

    this.socket = new WebSocket(url);

    this.socket.onopen = () => {

      this.updateState("connected");

      // Auto join groups if specified
      if (options.groups?.length) {

        for (const group of options.groups) {

          this.send({
            type: "join-group",
            group
          });
        }
      }
    };

    this.socket.onmessage = (event) => {
      this.handleMessage(event.data);
    };

    this.socket.onerror = (error) => {

      lifecycle?.onError?.(error);

      this.updateState("error");
    };

    this.socket.onclose = () => {

      this.updateState("disconnected");

      lifecycle?.onDisconnected?.("transport-closed");
    };
  }

  /**
   * Disconnect websocket.
   */
  async disconnect(reason?: RealtimeDisconnectReason): Promise<void> {

    if (!this.socket)
      return;

    this.socket.close();

    this.socket = undefined;

    this.updateState("disconnected");

    this.lifecycle?.onDisconnected?.(reason ?? "client-stop");
  }

  /**
   * Subscribe to a realtime event.
   */
  subscribe<TPayload>(
    eventName: string,
    handler: RealtimeEventHandler<TPayload>
  ): RealtimeSubscription {

    if (!this.handlers.has(eventName)) {
      this.handlers.set(eventName, new Set());
    }

    const set = this.handlers.get(eventName)!;

    set.add(handler as RealtimeEventHandler);

    return {
      unsubscribe: () => {
        set.delete(handler as RealtimeEventHandler);
      }
    };
  }

  /**
   * Send a message to the server.
   */
  async send(message: string | object): Promise<void> {

    if (!this.socket || this.socket.readyState !== WebSocket.OPEN)
      return;

    const payload =
      typeof message === "string"
        ? message
        : JSON.stringify(message);

    this.socket.send(payload);
  }

  /**
   * Internal message handler.
   */
  private handleMessage(data: unknown) {

    try {

      const envelope: RealtimeEventEnvelope =
        typeof data === "string"
          ? JSON.parse(data)
          : data;

      const handlers = this.handlers.get(envelope.type);

      if (!handlers)
        return;

      for (const handler of handlers) {

        handler(envelope.payload, envelope);
      }

    } catch (error) {

      this.lifecycle?.onError?.(error);
    }
  }

  /**
   * Build websocket URL.
   */
  private buildUrl(options: RealtimeClientOptions): string {

    const base = options.url;

    const params = new URLSearchParams();

    if (options.userId)
      params.append("userId", options.userId);

    if (options.accessToken)
      params.append("token", options.accessToken);

    const query = params.toString();

    return query
      ? `${base}?${query}`
      : base;
  }

  /**
   * Update internal state and notify lifecycle handlers.
   */
  private updateState(state: RealtimeConnectionState) {

    this.state = state;

    this.lifecycle?.onStateChanged?.(state);
  }
}