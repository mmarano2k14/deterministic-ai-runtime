import {
  BurstPlanKey,
  MaintainedConcurrencyConfig,
  SingleBurstConfig,
  WaveBatchesConfig,
  WaveBatchesStaggeredConfig,
} from "@/lib/console/burst/runtime/BurstMachineType";
import { BurstScenarioDefinition, BurstScenarioKey } from "./BurstScenarioPresetType";
export class BurstScenarioFactory {
  static buildAll(
    planKey: BurstPlanKey = "read"
  ): Record<BurstScenarioKey, BurstScenarioDefinition> {
    return {
      "single-burst": this.singleBurst(planKey),
      "maintained-concurrency": this.maintainedConcurrency(planKey),
      "wave-batches": this.waveBatches(planKey),
      "wave-batches-staggered": this.waveBatchesStaggered(planKey),
    };
  }
  static singleBurst(planKey: BurstPlanKey): BurstScenarioDefinition {
    const burstConfig: SingleBurstConfig = {
      dispatchMode: "single-burst",
      planKey,
      total: 100,
      delayMs: 0,
    };
    return {
      key: "single-burst",
      title: "Single burst",
      maxInFlight: "5",
      rotationOverlapMs: "1000",
      burstConfig,
      idea:
        "All requests are sent at the same time with a bounded in-flight limit and enough rotation overlap to keep the result focused on contention rather than stale-context noise.",
      recommendedParameters: [
        { label: "Dispatch mode", value: "single-burst" },
        { label: "Total requests", value: 100 },
        { label: "Delay per request", value: 0 },
        { label: "Max In-Flight", value: 5 },
        { label: "Rotation overlap", value: 1000 },
      ],
      whatItTests: [
        "controlled burst contention",
        "immediate middleware reaction under simultaneous pressure",
        "bounded in-flight rejection without rotation-related stale-context noise",
      ],
      expectedReading: [
        "a bounded group of requests succeeds immediately",
        "excess pressure is rejected primarily with 429 responses rather than 403 responses",
        "context rotation remains valid for requests already dispatched inside the overlap window",
      ],
      simpleExplanation:
        "This mode sends all the load at once. The preset keeps contention intentionally high, but uses the normal in-flight limit and a safe rotation-overlap window so the first run demonstrates controlled 429 back-pressure instead of looking like authorization is failing because the context rotated during the burst.",
    };
  }
  static maintainedConcurrency(planKey: BurstPlanKey): BurstScenarioDefinition {
    const burstConfig: MaintainedConcurrencyConfig = {
      dispatchMode: "maintained-concurrency",
      planKey,
      total: 500,
      concurrency: 50,
      delayMs: 10,
    };
    return {
      key: "maintained-concurrency",
      title: "Maintained concurrency",
      maxInFlight: "5",
      rotationOverlapMs: "1000",
      burstConfig,
      idea:
        "The client keeps X requests in-flight continuously until reaching the total.",
      recommendedParameters: [
        { label: "Dispatch mode", value: "maintained-concurrency" },
        { label: "Total requests", value: 500 },
        { label: "Concurrency", value: 50 },
        { label: "Delay per request", value: 10 },
        { label: "Max In-Flight", value: 5 },
        { label: "Rotation overlap", value: 1000 },
      ],
      whatItTests: [
        "realistic continuous load",
        "average system behavior over time",
        "stability of p50 / p95 metrics",
        "effect of rotating context under sustained traffic",
      ],
      expectedReading: [
        "relatively stable throughput",
        "usable histogram",
        "some rejections if pressure exceeds limits",
        "smoother context timeline",
      ],
      simpleExplanation:
        "This mode simulates a real client maintaining a certain level of concurrency. It is useful for observing runtime stability, latency, and overall behavior under sustained load.",
    };
  }
  static waveBatches(planKey: BurstPlanKey): BurstScenarioDefinition {
    const burstConfig: WaveBatchesConfig = {
      dispatchMode: "wave-batches",
      planKey,
      total: 100,
      batchSize: 5,
      wavePauseMs: 300,
      delayMs: 0,
    };
    return {
      key: "wave-batches",
      title: "Wave batches",
      maxInFlight: "1",
      rotationOverlapMs: "1000",
      burstConfig,
      idea:
        "Requests are sent in fixed batches with Max In-Flight intentionally lower than the batch size. A safe rotation-overlap window keeps the result focused on deterministic concurrency rejection rather than stale-context authorization failures.",
      recommendedParameters: [
        { label: "Dispatch mode", value: "wave-batches" },
        { label: "Total requests", value: 100 },
        { label: "Batch size", value: 5 },
        { label: "Wave pause", value: 300 },
        { label: "Delay per request", value: 0 },
        { label: "Max In-Flight", value: 1 },
        { label: "Rotation overlap", value: 1000 },
      ],
      whatItTests: [
        "wave-based request behavior",
        "interaction between batch size and bounded in-flight capacity",
        "deterministic concurrency rejection through HTTP 429",
        "separation of concurrency pressure from stale-context authorization failures",
      ],
      expectedReading: [
        "a mix of successful requests and HTTP 429 concurrency rejections",
        "the exact success/rejection ratio depends on request timing and server latency",
        "rotation-related HTTP 403 responses should be absent or exceptional",
        "rejection patterns remain easy to correlate with each request wave",
      ],
      simpleExplanation:
        "This mode sends five requests at once, waits, then repeats. Max In-Flight stays at 1 on purpose so each wave creates bounded concurrency pressure. The exact success-to-429 ratio is timing-dependent because the single in-flight slot can be released and reacquired while a wave is still being processed. The 1000 ms rotation-overlap window keeps stale-context 403 responses out of the foreground so the chart remains focused on intentional concurrency back-pressure.",
    };
  }
  static waveBatchesStaggered(planKey: BurstPlanKey): BurstScenarioDefinition {
    const burstConfig: WaveBatchesStaggeredConfig = {
      dispatchMode: "wave-batches-staggered",
      planKey,
      total: 100,
      batchSize: 5,
      wavePauseMs: 300,
      delayMs: 100,
    };
    return {
      key: "wave-batches-staggered",
      title: "Wave batches (staggered)",
      maxInFlight: "5",
      rotationOverlapMs: "1000",
      burstConfig,
      idea:
        "Requests in each wave reuse one captured context key while being staggered over time; the overlap window is deliberately long enough to keep that wave key valid during the full staggered dispatch.",
      recommendedParameters: [
        { label: "Dispatch mode", value: "wave-batches-staggered" },
        { label: "Total requests", value: 100 },
        { label: "Batch size", value: 5 },
        { label: "Wave pause", value: 300 },
        { label: "Delay between requests", value: 100 },
        { label: "Max In-Flight", value: 5 },
        { label: "Rotation overlap", value: 1000 },
      ],
      whatItTests: [
        "interaction between staggered request timing and context rotation",
        "reuse of one captured context key for all requests inside a wave",
        "stable overlap behavior while requests arrive progressively rather than simultaneously",
      ],
      expectedReading: [
        "most or all requests should complete without rotation-related 403 responses",
        "context rotation remains visible while the captured wave key stays valid inside the overlap window",
        "the request and context timelines remain easy to correlate with the 100 ms stagger",
      ],
      simpleExplanation:
        "This mode introduces a delay between requests within the same wave while intentionally reusing the same context key for that wave. With five requests staggered by 100 ms, the preset uses a 1000 ms rotation-overlap window so the first run demonstrates rotation and timing clearly without turning later requests in the wave into stale-context 403 responses.",
    };
  }
  static simpleRequest(planKey: BurstPlanKey): BurstScenarioDefinition {
    const scenario = this.maintainedConcurrency(planKey);

    const burstConfig: MaintainedConcurrencyConfig = {
      dispatchMode: "maintained-concurrency",
      planKey,
      total: 1,
      concurrency: 1,
      delayMs: 10,
    };

    scenario.burstConfig = burstConfig;
    return scenario;
  }
}
