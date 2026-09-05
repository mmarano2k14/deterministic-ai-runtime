import type { IConsoleRealtimeRuntime } from "./IConsoleRealtimeRuntime";
import { RealtimeClientFactory } from "../../infrastructure/realtime/RealtimeClientFactory";
import { RealtimeConnectionState, RuntimeLogEvent, ContextRotatedEvent } from "../../infrastructure/realtime/RealtimeType";
import { RealtimeLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";


type ConsoleRealtimeRuntimeDependencies = {
  getBaseUrl: () => string;
  getUserId: () => string | null | undefined;
  onContextRotated: (contextKey: string) => void;
  appendLog: (entry: RealtimeLogEntry) => void;
};

export class ConsoleRealtimeRuntime implements IConsoleRealtimeRuntime {
  private readonly deps: ConsoleRealtimeRuntimeDependencies;
  private readonly subscriptions: { unsubscribe(): void }[] = [];
  private readonly bound = { value: false };

  public readonly client = RealtimeClientFactory.create({
    transport: "signalr",
    endPoint: "/runtime/live"
  });

  public constructor(deps: ConsoleRealtimeRuntimeDependencies) {
    this.deps = deps;
  }

  public getState(): RealtimeConnectionState {
    return this.client.state;
  }

  public async connect(userId?: string | null): Promise<void> {
    this.bindHandlers();

    if (this.client.state === "connected" || this.client.state === "connecting") {
      return;
    }

    await this.client.connect({
      url: `${this.deps.getBaseUrl()}${this.client.endPoint}`,
      userId: userId ?? this.deps.getUserId() ?? undefined,
      groups: ["runtime-console"],
      autoReconnect: true,
    });
  }

  public async disconnect(): Promise<void> {
    try {
      await this.client.disconnect();
    } catch {
      // ignored
    }
  }

  public async dispose(): Promise<void> {
    for (const sub of this.subscriptions) {
      sub.unsubscribe();
    }

    this.subscriptions.length = 0;
    this.bound.value = false;

    await this.disconnect();
  }

  private bindHandlers(): void {
    if (this.bound.value) { 
      return;
    }

    this.bound.value = true;

    const logSub = this.client.onRuntimeLog((event: RuntimeLogEvent) => {
      this.deps.appendLog(this.toLogEntry(event));
    });

    const rotatedSub = this.client.onContextRotated((event: ContextRotatedEvent) => {
      this.deps.onContextRotated(event.newContextKey);
    });

    this.subscriptions.push(logSub, rotatedSub);
  }

  private toLogEntry(event: RuntimeLogEvent): RealtimeLogEntry {
    return {
      id:
        typeof crypto !== "undefined" && "randomUUID" in crypto
          ? crypto.randomUUID()
          : `${Date.now()}-${Math.random()}`,
      t:event.occurredAtUtc,
      eventName: "",
      kind: "realtime",
      updatedAt: Date.parse(event.occurredAtUtc),
      occurredAtUtc : event.occurredAtUtc,
      level: event.level,
      category: event.category ?? "Runtime",
      message: event.message,
      data: event.data ? JSON.stringify(event.data) : undefined,
      payload: event.data ? JSON.stringify(event.data) : undefined,
    };
  }
}