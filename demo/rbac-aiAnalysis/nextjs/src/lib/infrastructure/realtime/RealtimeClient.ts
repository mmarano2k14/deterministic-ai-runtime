

import { IRealtimeTransport } from "./providers/IRealtimeTransport";
import { RealtimeConnectionState, RealtimeClientOptions, RealtimeLifecycleHandlers, RuntimeLogEvent, RealtimeSubscription, ContextRotatedEvent } from "./RealtimeType";

/**
 * High-level realtime client used by the console runtime.
 *
 * Responsibilities:
 * - own the transport
 * - expose typed subscriptions
 * - expose connection lifecycle
 */
export class RealtimeClient {
  private readonly transport: IRealtimeTransport;
  public readonly endPoint: string

  public constructor(transport: IRealtimeTransport, endPoint: string) {
    this.transport = transport;
    this.endPoint = endPoint;
  }

  public get state(): RealtimeConnectionState {
    return this.transport.state;
  }

  public connect(
    options: RealtimeClientOptions,
    lifecycle?: RealtimeLifecycleHandlers
  ): Promise<void> {
    return this.transport.connect(options, lifecycle);
  }

  public disconnect(): Promise<void> {
    return this.transport.disconnect("client-stop");
  }

  public onRuntimeLog(
    handler: (event: RuntimeLogEvent) => void | Promise<void>
  ): RealtimeSubscription {
    return this.transport.subscribe<RuntimeLogEvent>("runtime-log", handler);
  }

  public onContextRotated(
    handler: (event: ContextRotatedEvent) => void | Promise<void>
  ): RealtimeSubscription {
    return this.transport.subscribe<ContextRotatedEvent>("context-rotated", handler);
  }

  public send(message: string | object): Promise<void> {
    if (!this.transport.send) {
      return Promise.resolve();
    }

    return this.transport.send(message);
  }
}