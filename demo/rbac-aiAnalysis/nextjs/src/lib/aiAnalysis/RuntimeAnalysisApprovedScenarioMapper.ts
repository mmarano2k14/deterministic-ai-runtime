import type {
  BurstConfig,
  BurstPlanKey,
} from "@/lib/console/burst/runtime/BurstMachineType";
import {
  maxInFlightOptions,
  type InFlightMaxValue,
} from "@/lib/console/ConsoleType";
import type {
  RuntimeAnalysisSuggestedScenario,
} from "./RuntimeAnalysisType";

export type RuntimeAnalysisApprovedScenarioLaunchPlan = {
  burstConfig: BurstConfig;
  maxInFlight: InFlightMaxValue;
  rotationOverlapMs: string;
};

export class RuntimeAnalysisApprovedScenarioMapper {
  public static map(
    scenario: RuntimeAnalysisSuggestedScenario,
    planKey: string
  ): RuntimeAnalysisApprovedScenarioLaunchPlan {
    const approvedPlanKey = this.toPlanKey(planKey);

    switch (scenario.scenarioType) {
      case "single-burst":
        return this.wrap(
          {
            dispatchMode: "single-burst",
            total: scenario.totalRequests,
            delayMs: scenario.delayMs,
            planKey: approvedPlanKey,
          },
          scenario
        );

      case "maintained-concurrency":
        if (scenario.concurrency === null || scenario.concurrency < 1) {
          throw new Error(
            "Approved maintained-concurrency scenario is missing concurrency."
          );
        }

        return this.wrap(
          {
            dispatchMode: "maintained-concurrency",
            total: scenario.totalRequests,
            concurrency: scenario.concurrency,
            delayMs: scenario.delayMs,
            planKey: approvedPlanKey,
          },
          scenario
        );

      case "wave-batches":
      case "wave-batches-staggered":
        if (scenario.batchSize === null || scenario.batchSize < 1) {
          throw new Error(
            "Approved wave scenario is missing batch size."
          );
        }

        return this.wrap(
          {
            dispatchMode: scenario.scenarioType,
            total: scenario.totalRequests,
            batchSize: scenario.batchSize,
            wavePauseMs: scenario.wavePauseMs ?? 0,
            delayMs: scenario.delayMs,
            planKey: approvedPlanKey,
          },
          scenario
        );

      case "custom":
        throw new Error(
          "Custom AI scenarios cannot be executed by the approved scenario bridge."
        );
    }
  }

  private static wrap(
    burstConfig: BurstConfig,
    scenario: RuntimeAnalysisSuggestedScenario
  ): RuntimeAnalysisApprovedScenarioLaunchPlan {
    return {
      burstConfig,
      maxInFlight: this.toMaxInFlightValue(
        scenario.maxInFlight
      ),
      rotationOverlapMs: String(
        scenario.rotationOverlapMs
      ),
    };
  }

  private static toMaxInFlightValue(
    value: number
  ): InFlightMaxValue {
    const serializedValue = String(value);

    const option = maxInFlightOptions.find(
      (candidate) => candidate.value === serializedValue
    );

    if (!option) {
      throw new Error(
        `Approved MaxInFlight '${serializedValue}' is not supported by the console runtime.`
      );
    }

    return option.value;
  }

  private static toPlanKey(
    value: string
  ): BurstPlanKey {
    if (value === "read" || value === "refund") {
      return value;
    }

    throw new Error(
      `Unsupported approved plan key '${value}'.`
    );
  }
}
