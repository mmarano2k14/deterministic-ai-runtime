import { randomUUID } from "node:crypto";
import { createInterface, type Interface } from "node:readline";
import type {
  AiSdkClient,
  AiSdkExecutionObservation,
  AiSdkExecutionResult,
  AiSdkExecutionStatus,
  AiSdkExecutionWatchEvent,
} from "@multiplexed/ai-sdk";

const OBSERVATION_INTERVAL_MS = 400;
const WATCH_TIMEOUT_MS = 20 * 60 * 1000;

export class ConsoleInputPump {
  readonly #readline: Interface;
  readonly #queued: Array<string | null> = [];
  readonly #waiters: Array<(value: string | null) => void> = [];
  #closed = false;

  public constructor() {
    this.#readline = createInterface({
      input: process.stdin,
      output: process.stdout,
      terminal: Boolean(process.stdin.isTTY && process.stdout.isTTY),
    });

    this.#readline.on("line", (line) => this.#push(line));
    this.#readline.on("close", () => {
      this.#closed = true;
      this.#push(null);
    });
  }

  public read(): Promise<string | null> {
    const queued = this.#queued.shift();
    if (queued !== undefined) {
      return Promise.resolve(queued);
    }
    if (this.#closed) {
      return Promise.resolve(null);
    }
    return new Promise((resolve) => this.#waiters.push(resolve));
  }

  public async prompt(text: string): Promise<string | null> {
    process.stdout.write(text);
    return this.read();
  }

  public close(): void {
    this.#readline.close();
  }

  #push(value: string | null): void {
    const waiter = this.#waiters.shift();
    if (waiter !== undefined) {
      waiter(value);
      return;
    }
    this.#queued.push(value);
  }
}

export class InteractiveExecutionConsole {
  readonly #client: AiSdkClient;
  readonly #executionId: string;
  readonly #waitingKey: string;
  readonly #waitingStepName: string;
  readonly #input: ConsoleInputPump;
  readonly #verbose: boolean;
  #waitingAnnounced = false;
  #pendingInput: Promise<string | null> | undefined;
  #lastObservation: AiSdkExecutionObservation | undefined;
  #reviewApproved: boolean | undefined;
  #reviewFeedback: string | undefined;

  public constructor(
    client: AiSdkClient,
    executionId: string,
    waitingKey: string,
    waitingStepName: string,
    input: ConsoleInputPump,
    verbose: boolean,
  ) {
    this.#client = client;
    this.#executionId = executionId;
    this.#waitingKey = waitingKey;
    this.#waitingStepName = waitingStepName;
    this.#input = input;
    this.#verbose = verbose;
  }

  public get lastObservation(): AiSdkExecutionObservation | undefined {
    return this.#lastObservation;
  }

  public get reviewApproved(): boolean | undefined {
    return this.#reviewApproved;
  }

  public get reviewFeedback(): string | undefined {
    return this.#reviewFeedback;
  }

  public async run(): Promise<AiSdkExecutionResult | null> {
    const watchController = new AbortController();
    const watchTimeout = setTimeout(
      () => watchController.abort(new DOMException("Watch timed out.", "TimeoutError")),
      WATCH_TIMEOUT_MS,
    );
    const watchTask = this.#watch(watchController.signal);

    printCommands();

    try {
      while (true) {
        const observation = await this.#client.observeExecution(this.#executionId);
        this.#lastObservation = observation;
        this.#announceInputBoundary(observation);

        if (isTerminal(observation.status)) {
          watchController.abort();
          await ignoreWatchTermination(watchTask);
          return this.#client.getExecutionResult(this.#executionId);
        }

        const commandTask = this.#getPendingInput();
        const completed = await Promise.race([
          commandTask.then(() => "input" as const),
          delay(OBSERVATION_INTERVAL_MS).then(() => "delay" as const),
        ]);

        if (completed !== "input") {
          continue;
        }

        const command = (await this.#consumePendingInput())?.trim().toLowerCase();
        if (command === "q") {
          watchController.abort();
          await ignoreWatchTermination(watchTask);
          return null;
        }

        await this.#handleCommand(command, observation);
      }
    } finally {
      clearTimeout(watchTimeout);
      watchController.abort();
    }
  }

  public async runPostTerminalCommands(terminalStatus: AiSdkExecutionStatus): Promise<void> {
    console.log();
    console.log("Post-terminal commands: [x] deterministic replay  [q] exit");

    while (true) {
      process.stdout.write("> ");
      const command = (await this.#consumePendingInput())?.trim().toLowerCase();

      switch (command) {
        case "x":
          await this.#replay();
          break;
        case "q":
        case "":
        case undefined:
          return;
        default:
          console.log(`Execution is already ${terminalStatus}. Use 'x' or 'q'.`);
          break;
      }
    }
  }

  async #watch(signal: AbortSignal): Promise<void> {
    try {
      for await (const item of this.#client.watchExecution(
        { executionId: this.#executionId, includeInitialSnapshot: true },
        signal,
      )) {
        if (this.#verbose) {
          printVerboseWatchItem(item);
        } else {
          printPresentationWatchItem(item);
        }
      }
    } catch (error) {
      if (signal.aborted) {
        return;
      }

      if (this.#verbose) {
        console.log(`[watch] stopped: ${errorMessage(error)}`);
      } else {
        console.log("[WARN] Live watch stopped; observation polling remains active.");
      }
    }
  }

  #announceInputBoundary(observation: AiSdkExecutionObservation): void {
    if (this.#waitingAnnounced) {
      return;
    }

    const waitingStep = observation.steps.find((step) => step.name === this.#waitingStepName);
    if (waitingStep?.status !== "WaitingForExternal") {
      return;
    }

    this.#waitingAnnounced = true;
    console.log();
    console.log("---------------- Human review ----------------");
    console.log("The durable execution is parked and waiting for approval.");
    console.log("Enter 'i' to approve/reject and optionally add feedback.");
    console.log("------------------------------------------------");
    console.log();
  }

  async #handleCommand(
    command: string | undefined,
    observation: AiSdkExecutionObservation,
  ): Promise<void> {
    switch (command) {
      case "p":
        await this.#pause();
        break;
      case "r":
        await this.#resume();
        break;
      case "i":
        await this.#submitInput(observation);
        break;
      case "c":
        await this.#cancel();
        break;
      case "s":
        printSnapshot(undefined, observation);
        break;
      case "x":
        console.log("Replay validation is available after terminal convergence.");
        break;
      case "":
      case undefined:
        break;
      default:
        console.log("Unknown command. Use p, r, i, c, s, or q.");
        break;
    }
  }

  async #pause(): Promise<void> {
    console.log();
    if (this.#verbose) {
      console.log(`SDK command: sdk.execution.pause(${this.#executionId})`);
    }
    const response = await this.#client.pauseExecution(this.#executionId, {
      reason: "interactive-agent-console-pause",
    });
    console.log(
      `Pause accepted=${response.accepted}; controlState=${response.state?.status ?? "unknown"}`,
    );
    console.log(`ExecutionId unchanged: ${response.executionId}`);
    console.log();
  }

  async #resume(): Promise<void> {
    console.log();
    if (this.#verbose) {
      console.log(`SDK command: sdk.execution.resume(${this.#executionId})`);
    }
    const response = await this.#client.resumeExecution(this.#executionId, {
      reason: "interactive-agent-console-resume",
    });
    console.log(
      `Resume accepted=${response.accepted}; controlState=${response.state?.status ?? "unknown"}`,
    );
    console.log(`ExecutionId unchanged: ${response.executionId}`);
    console.log();
  }

  async #submitInput(observation: AiSdkExecutionObservation): Promise<void> {
    const waitingStep = observation.steps.find((step) => step.name === this.#waitingStepName);
    if (waitingStep?.status !== "WaitingForExternal") {
      console.log("The execution is not currently parked at the human-input boundary.");
      return;
    }

    const approved = await this.#readApproval();
    const feedback = (await this.#input.prompt("Feedback (optional): "))?.trim() ?? "";

    console.log();
    if (this.#verbose) {
      console.log(`SDK command: sdk.execution.input.submit(${this.#executionId})`);
    }

    const response = await this.#client.submitExecutionInput(this.#executionId, {
      waitingKey: this.#waitingKey,
      waitingStepName: this.#waitingStepName,
      reason: "interactive-agent-human-review",
      correlationId: `interactive-agent-input-${randomUUID().replaceAll("-", "")}`,
      input: { approved, feedback },
    });

    if (response.accepted) {
      this.#reviewApproved = approved;
      this.#reviewFeedback = feedback;
    }

    console.log(
      `Human input accepted=${response.accepted}; controlState=${response.state?.status ?? "unknown"}`,
    );
    console.log(`ExecutionId unchanged: ${response.executionId}`);
    console.log();
  }

  async #readApproval(): Promise<boolean> {
    while (true) {
      const answer = (await this.#input.prompt("Approve the agent plan? [y/n]: "))?.trim().toLowerCase();
      if (answer === "y" || answer === "yes") {
        return true;
      }
      if (answer === "n" || answer === "no") {
        return false;
      }
      console.log("Enter 'y' or 'n'.");
    }
  }

  async #cancel(): Promise<void> {
    console.log();
    if (this.#verbose) {
      console.log(`SDK command: sdk.execution.cancel(${this.#executionId})`);
    }
    const response = await this.#client.cancelExecution(this.#executionId, {
      reason: "interactive-agent-console-cancel",
      correlationId: `interactive-agent-cancel-${randomUUID().replaceAll("-", "")}`,
    });
    console.log(
      `Cancellation requested=${response.cancellationRequested}; status=${response.status}`,
    );
    console.log();
  }

  async #replay(): Promise<void> {
    console.log();
    if (this.#verbose) {
      console.log(`SDK command: sdk.execution.replay(${this.#executionId})`);
    } else {
      console.log("Deterministic replay validation");
    }

    const replay = await this.#client.replayExecution(this.#executionId, {
      strictDeterminism: true,
      includeDiagnostics: true,
      reason: "interactive-agent-console-replay",
      correlationId: `interactive-agent-replay-${randomUUID().replaceAll("-", "")}`,
    });

    console.log(`  Succeeded:     ${replay.succeeded}`);
    console.log(`  Deterministic: ${replay.deterministic ?? "unknown"}`);
    if (this.#verbose && replay.message) {
      console.log(`  Message: ${replay.message}`);
    }
    if (replay.failureReason) {
      console.log(`  Failure: ${replay.failureReason}`);
    }
    if (this.#verbose) {
      for (const diagnostic of replay.diagnostics) {
        console.log(`  ${diagnostic}`);
      }
      console.log(
        "Replay validates the existing durable execution; it does not create a second execution.",
      );
    }
    console.log();
  }

  #getPendingInput(): Promise<string | null> {
    return this.#pendingInput ??= this.#input.read();
  }

  async #consumePendingInput(): Promise<string | null> {
    const pending = this.#getPendingInput();
    const value = await pending;
    this.#pendingInput = undefined;
    return value;
  }
}

function printCommands(): void {
  console.log("Commands: [p] pause  [r] resume  [i] human input  [s] status  [c] cancel  [q] detach");
  console.log();
}

function printVerboseWatchItem(item: AiSdkExecutionWatchEvent): void {
  if (item.kind === "Snapshot" && item.snapshot !== undefined) {
    printSnapshot(item.sequence, item.snapshot);
    return;
  }
  if (item.kind === "Event") {
    console.log(
      `[watch #${item.sequence ?? "-"}] ${item.channel ?? "event"} ${item.eventType ?? "event"}`,
    );
    return;
  }
  if (item.kind === "ResyncRequired") {
    console.log(`[watch] resync required: ${item.resyncRequired?.reason ?? "unknown"}`);
  }
}

function printPresentationWatchItem(item: AiSdkExecutionWatchEvent): void {
  if (item.kind === "ResyncRequired") {
    console.log(`[WARN] Watch resynchronization required: ${item.resyncRequired?.reason ?? "unknown"}`);
    return;
  }

  if (item.kind !== "Event" || !item.eventType) {
    return;
  }

  const name = readPayloadString(item.payload, "name");
  switch (item.eventType) {
    case "step.started":
      console.log(`[>] ${friendlyStepName(name)}`);
      break;
    case "step.completed":
      console.log(`[OK] ${friendlyStepName(name)}`);
      break;
    case "step.parked":
      if (name === "delegate-analysis") {
        console.log("[WAIT] Delegated analysis is waiting for the child agent");
      } else if (name === "await-review") {
        console.log("[WAIT] Human review boundary reached");
      } else {
        console.log(`[WAIT] ${friendlyStepName(name)}`);
      }
      break;
    case "step.failed":
      console.log(`[FAIL] ${friendlyStepName(name)}`);
      break;
    case "child.created":
      console.log("[>] Child agent created");
      break;
    case "child.started":
      console.log("[>] Child agent running");
      break;
    case "child.completed":
      console.log("[OK] Child agent completed");
      break;
    case "child.failed":
      console.log("[FAIL] Child agent failed");
      break;
    case "execution.completed":
      console.log("[OK] Execution completed");
      break;
    case "execution.failed":
      console.log("[FAIL] Execution failed");
      break;
    case "execution.cancelled":
      console.log("[CANCEL] Execution cancelled");
      break;
    case "recovery.started":
    case "recovery.resumed":
    case "recovery.completed":
      console.log(`[RECOVERY] ${item.eventType}`);
      break;
  }
}

function friendlyStepName(name: string | undefined): string {
  switch (name) {
    case "plan":
      return "Planning";
    case "delegate-analysis":
      return "Delegated analysis";
    case "await-review":
      return "Human review";
    case "final-answer":
      return "Final OpenAI answer";
    case "publish-result":
      return "Business result published";
    case undefined:
    case "":
      return "Pipeline step";
    default:
      return name;
  }
}

function readPayloadString(payload: unknown, propertyName: string): string | undefined {
  if (typeof payload !== "object" || payload === null || Array.isArray(payload)) {
    return undefined;
  }

  const value = (payload as Readonly<Record<string, unknown>>)[propertyName];
  return typeof value === "string" ? value : undefined;
}

function printSnapshot(
  sequence: number | undefined,
  snapshot: AiSdkExecutionObservation,
): void {
  const steps = snapshot.steps.map((step) => `${step.name}=${step.status}`).join(", ");
  console.log(`[snapshot #${sequence ?? "-"}] execution=${snapshot.status}; ${steps}`);
}

function isTerminal(status: AiSdkExecutionStatus): boolean {
  return status === "Completed" || status === "Failed" || status === "Cancelled";
}

async function ignoreWatchTermination(task: Promise<void>): Promise<void> {
  try {
    await task;
  } catch {
    // The main observation path owns terminal convergence; Watch is auxiliary UI.
  }
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
