import { BurstConfig, BurstDispatchModeKey } from "../runtime/BurstMachineType";

export type BurstScenarioKey = BurstDispatchModeKey
export type BurstScenarioRecommendedParameter = {
  label: string;
  value: string | number;
};

export type BurstScenarioDefinition = {
  key: BurstScenarioKey;
  title: string;

  maxInFlight?: string;
  rotationOverlapMs?: string;

  burstConfig: BurstConfig;

  idea: string;
  recommendedParameters: BurstScenarioRecommendedParameter[];
  whatItTests: string[];
  expectedReading: string[];
  simpleExplanation: string;
};