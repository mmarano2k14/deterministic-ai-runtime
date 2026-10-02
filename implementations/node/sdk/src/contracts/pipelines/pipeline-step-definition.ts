import type { AiSdkJsonValue } from "../../json/json-value.js";
import type { AiSdkInvocationDefinition } from "./invocation-definition.js";
import type { AiSdkPipelineStepExecutionDefinition } from "./pipeline-step-execution-definition.js";

export interface AiSdkPipelineStepDefinition {
  readonly name: string;
  readonly stepKey: string;
  readonly executionLanguage?: string;
  readonly invocation?: AiSdkInvocationDefinition;
  readonly order: number;
  readonly dependsOn?: readonly string[];
  readonly input?: Readonly<Record<string, AiSdkJsonValue>>;
  readonly config?: Readonly<Record<string, AiSdkJsonValue>>;
  readonly execution?: AiSdkPipelineStepExecutionDefinition;
}
