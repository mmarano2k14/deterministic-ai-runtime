import type { ConsoleController } from "../ConsoleController";
import type { ConsoleState } from "../ConsoleType";

export interface IConsoleRuntime {
  readonly controller: ConsoleController;
  syncState(state: ConsoleState): void;
  dispose(): Promise<void>;
}