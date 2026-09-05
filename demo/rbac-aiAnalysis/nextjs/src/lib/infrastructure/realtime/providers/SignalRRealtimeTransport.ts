import {
  RealtimeLifecycleHandlers,
  RealtimeConnectionState,
  RealtimeClientOptions,
  RealtimeDisconnectReason,
  RealtimeEventHandler,
  RealtimeSubscription,
} from "../RealtimeType";
import { IRealtimeTransport } from "./IRealtimeTransport";
import * as signalR from "@microsoft/signalr";

/**
 * Internal representation of a subscription registered
 * before the SignalR connection is created.
 *
 * We intentionally store handlers as unknown because this list
 * acts only as an internal buffer before binding to SignalR.
 */
type PendingSubscription = {
  eventName: string;
  handler: RealtimeEventHandler<unknown>;
};

/**
 * SignalR realtime transport.
 *
 * Responsibilities:
 * - establish SignalR hub connection
 * - manage lifecycle state
 * - subscribe to named realtime events
 * - expose a generic transport compatible with RealtimeClient
 */
export class SignalRRealtimeTransport implements IRealtimeTransport {
  /**
   * Active SignalR connection instance.
   * Undefined until connect() is called.
   */
  private connection?: signalR.HubConnection;

  /**
   * Lifecycle callbacks provided by the consumer.
   */
  private lifecycle?: RealtimeLifecycleHandlers;

  /**
   * Subscriptions registered BEFORE the connection exists.
   * These are replayed once the hub connection is built.
   */
  private readonly pendingSubscriptions: PendingSubscription[] = [];

  /**
   * Current transport state.
   */
  public state: RealtimeConnectionState = "idle";

  /**
   * Establish the SignalR connection.
   */
  public async connect(
    options: RealtimeClientOptions,
    lifecycle?: RealtimeLifecycleHandlers
  ): Promise<void> {
    this.lifecycle = lifecycle;

    // Prevent duplicate connections.
    if (this.state === "connected") {
      return;
    }

    this.updateState("connecting");

    const url = this.buildUrl(options);

    // Build SignalR connection.
    const builder = new signalR.HubConnectionBuilder().withUrl(url);

    if (options.autoReconnect ?? true) {
      builder.withAutomaticReconnect();
    }

    this.connection = builder.build();

    /**
     * Lifecycle hooks
     */

    this.connection.onreconnecting((error) => {
      lifecycle?.onError?.(error);
      this.updateState("connecting");
    });

    this.connection.onreconnected(() => {
      this.updateState("connected");
    });

    this.connection.onclose((error) => {
      if (error) {
        lifecycle?.onError?.(error);
      }

      this.updateState("disconnected");
      lifecycle?.onDisconnected?.("transport-closed");
    });

    /**
     * Bind subscriptions that were registered
     * before the connection existed.
     */
    for (const sub of this.pendingSubscriptions) {
      const wrapped = (payload: unknown) => {
        void sub.handler(payload, {
          type: sub.eventName,
          payload,
        });
      };

      this.connection.on(sub.eventName, wrapped);
    }

    this.connection.on("runtime-log", (payload) => {
        console.log("EVENT runtime-log RECEIVED", payload);
    });

    /**
     * Start SignalR connection.
     */
    await this.connection.start();

    this.updateState("connected");

    /**
     * Join initial groups if provided.
     */
    if (options.groups?.length) {
      for (const group of options.groups) {
        await this.connection.invoke("JoinGroup", group);
      }
    }
  }

  /**
   * Stop the connection.
   */
  public async disconnect(reason?: RealtimeDisconnectReason): Promise<void> {
    if (!this.connection) {
      return;
    }

    await this.connection.stop();
    this.connection = undefined;

    this.updateState("disconnected");
    this.lifecycle?.onDisconnected?.(reason ?? "client-stop");
  }

  /**
   * Subscribe to a realtime event.
   *
   * If the connection does not exist yet,
   * the subscription is stored and applied later
   * when connect() is called.
   */
  public subscribe<TPayload = unknown>(
    eventName: string,
    handler: RealtimeEventHandler<TPayload>
  ): RealtimeSubscription {
    /**
     * Connection not yet established.
     * Store subscription for later.
     */
    if (!this.connection) {
      const pending: PendingSubscription = {
        eventName,
        handler: handler as RealtimeEventHandler<unknown>,
      };

      this.pendingSubscriptions.push(pending);

      return {
        unsubscribe: () => {
          const index = this.pendingSubscriptions.indexOf(pending);

          if (index >= 0) {
            this.pendingSubscriptions.splice(index, 1);
          }
        },
      };
    }

    /**
     * Connection already exists.
     * Bind handler directly to SignalR.
     */
    const wrapped = (payload: TPayload) => {
      void handler(payload, {
        type: eventName,
        payload,
      });
    };

    this.connection.on(eventName, wrapped);

    return {
      unsubscribe: () => {
        this.connection?.off(eventName, wrapped);
      },
    };
  }

  /**
   * Send a message to the server hub.
   */
  public async send(message: string | object): Promise<void> {
    if (!this.connection || this.state !== "connected") {
      return;
    }

    const payload =
      typeof message === "string"
        ? { type: "raw", payload: message }
        : message;

    await this.connection.invoke("ClientMessage", payload);
  }

  /**
   * Build hub connection URL with optional query parameters.
   */
  private buildUrl(options: RealtimeClientOptions): string {
    const base = options.url;
    const params = new URLSearchParams();

    if (options.userId) {
      params.append("userId", options.userId);
    }

    if (options.accessToken) {
      params.append("token", options.accessToken);
    }

    const query = params.toString();

    return query ? `${base}?${query}` : base;
  }

  /**
   * Update local state and notify lifecycle listeners.
   */
  private updateState(state: RealtimeConnectionState): void {
    this.state = state;
    this.lifecycle?.onStateChanged?.(state);
  }
}