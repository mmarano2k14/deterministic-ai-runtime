import { RealtimeClient } from "./RealtimeClient";
import { IRealtimeTransport } from "./providers/IRealtimeTransport";
import { SignalRRealtimeTransport } from "./providers/SignalRRealtimeTransport";
import { WebSocketRealtimeTransport } from "./providers/WebSocketRealtimeTransport";

export type RealtimeTransportKind = "websocket" | "signalr";

export type RealtimeFactoryOptions = {
  transport: RealtimeTransportKind;
  endPoint : string;
};

/**
 * Creates the appropriate realtime client depending on the selected transport.
 */
export class RealtimeClientFactory {
  public static create(options: RealtimeFactoryOptions): RealtimeClient {
    let transport: IRealtimeTransport;

    switch (options.transport) {
      case "signalr":
        transport = new SignalRRealtimeTransport();
        break;

      case "websocket":
      default:
        transport = new WebSocketRealtimeTransport();
        break;
    }

    return new RealtimeClient(transport, options.endPoint);
  }
}