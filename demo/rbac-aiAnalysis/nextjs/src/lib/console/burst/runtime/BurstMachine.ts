import { BurstConfig, BurstEvent, BurstRuntime, BurstReport } from "./BurstMachineType";
export class BurstMachine {
  static initial(defaultConfig: BurstConfig): BurstRuntime {
    return {
      state: "Idle",
      report: {
        config: BurstMachine.sanitize(defaultConfig),
        progress: { started: 0, completed: 0, inFlight: 0, total: 0 },
        counters: { ok: 0, unauthorized: 0, forbidden: 0, other: 0, errors: 0, rejected:0 },
        timing: {},
        stats: { durationsMs: [] },
      },
      stopRequested: false,
    };
  }
  static reduce(model: BurstRuntime, ev: BurstEvent): BurstRuntime {
    switch (model.state) {
      case "Idle":
        return BurstMachine.reduceIdle(model, ev);
      case "Completed":
        return BurstMachine.reduceCompleted(model, ev);
      case "Error":
        return BurstMachine.reduceError(model, ev);
      case "Running":
        return BurstMachine.reduceRunning(model, ev);
      case "Stopping":
        return BurstMachine.reduceStopping(model, ev);
    }
  }
  private static reduceIdle(model: BurstRuntime, ev: BurstEvent): BurstRuntime {
    switch (ev.type) {
      case "Configure": {
        const nextCfg = BurstMachine.sanitize(ev.config);
        return {
          ...model,
          report: model.report
            ? { ...model.report, config: nextCfg }
            : BurstMachine.newReport(nextCfg),
        };
      }
      case "Start": {
        /*
        const cfg = BurstMachine.sanitize(
          model.report?.config ?? BurstMachine.defaultConfig()
        );
        */
       const cfg = BurstMachine.sanitize(ev.config);

        return {
          state: "Running",
          stopRequested: false,
          report: BurstMachine.newReport(cfg, ev.startedAt),
        };
      }
      case "Reset": {
        const cfg = BurstMachine.sanitize(BurstMachine.defaultConfig());
        return BurstMachine.initial(cfg);
      }

      default:
        return model;
    }
  }
  private static reduceCompleted(model: BurstRuntime, ev: BurstEvent): BurstRuntime {
    switch (ev.type) {
      case "Configure": {
        const nextCfg = BurstMachine.sanitize(ev.config);
        return {
          ...model,
          report: model.report
            ? { ...model.report, config: nextCfg }
            : BurstMachine.newReport(nextCfg),
        };
      }
      case "Start": {
        /*
        const cfg = BurstMachine.sanitize(
          model.report?.config ?? BurstMachine.defaultConfig()
        );
        */
        const cfg = BurstMachine.sanitize(ev.config);

        return {
          state: "Running",
          stopRequested: false,
          report: BurstMachine.newReport(cfg, ev.startedAt),
        };
      }
      case "Reset": {
        const cfg = BurstMachine.sanitize(
          model.report?.config ?? BurstMachine.defaultConfig()
        );
        return BurstMachine.initial(cfg);
      }

      default:
        return model;
    }
  }
  private static reduceError(model: BurstRuntime, ev: BurstEvent): BurstRuntime {
    switch (ev.type) {
        case "Configure": {
            const nextCfg = BurstMachine.sanitize(ev.config);
            return {
                ...model,
                report: model.report ? { ...model.report, config: nextCfg } : BurstMachine.newReport(nextCfg),
            };
        }
        case "Start": {
            const cfg = BurstMachine.sanitize(model.report?.config ?? BurstMachine.defaultConfig());
            return {
                state: "Running",
                stopRequested: false,
                report: BurstMachine.newReport(cfg, ev.startedAt),
            };
        }
        case "Reset": {
            const cfg = BurstMachine.sanitize(model.report?.config ?? BurstMachine.defaultConfig());
            return BurstMachine.initial(cfg);
        }
        default:
            return model;
    }
  }
  private static reduceRunning(model: BurstRuntime, ev: BurstEvent): BurstRuntime {
    if (!model.report) return { ...model, state: "Error", report: null, stopRequested: false };
    switch (ev.type) {
        case "Configure":
            // ignore config changes during run (or allow, but deterministic is easier)
            return model;

        case "Stop":
            return { ...model, state: "Stopping", stopRequested: true };
        case "TickStart": {
            const r = model.report;
            const started = r.progress.started + ev.count;
            const inFlight = r.progress.inFlight + ev.count;
            return { ...model, report: { ...r, progress: { ...r.progress, started, inFlight } } };
        }
        case "TickComplete": {
            const r = model.report;
            const completed = r.progress.completed + ev.count;
            const inFlight = Math.max(0, r.progress.inFlight - ev.count);
            return { ...model, report: { ...r, progress: { ...r.progress, completed, inFlight } } };
        }
        case "ResultOk": {
            const r = model.report;
            const durationsMs = BurstMachine.pushDuration(r.stats.durationsMs, ev.durationMs);
            const liveStats = BurstMachine.computeStats(durationsMs);

            return {
              ...model,
              report: {
                ...r,
                counters: { ...r.counters, ok: r.counters.ok + 1 },
                stats: { ...r.stats, durationsMs, ...liveStats },
              },
            };
        }
        case "ResultHttp": {
            const r = model.report;
            const durationsMs = BurstMachine.pushDuration(r.stats.durationsMs, ev.durationMs);
            const liveStats = BurstMachine.computeStats(durationsMs);

            const c = { ...r.counters };
            if (ev.status === 401) c.unauthorized += 1;
            else if (ev.status === 403) c.forbidden += 1;
            else if (ev.status === 429) c.rejected += 1;
            else c.other += 1;
            return {
              ...model,
              report: {
                ...r,
                counters: c,
                stats: { ...r.stats, durationsMs, ...liveStats },
              },
            };
        }
        case "ResultError": {
            const r = model.report;
            return {
                ...model,
                report: {
                ...r,
                counters: { ...r.counters, errors: r.counters.errors + 1 },
                error: ev.message,
                },
            };
        }
        case "Finish": {
            const r = model.report;
            const finishedAt = Date.now();
            const stats = BurstMachine.computeStats(r.stats.durationsMs);
            return {
                state: "Completed",
                stopRequested: false,
                report: { ...r, timing: { ...r.timing, finishedAt }, stats: { ...r.stats, ...stats } },
            };
        }
        case "Fail": {
            const r = model.report;
            return { state: "Error", stopRequested: false, report: { ...r, error: ev.message } };
        }

        default:
        return model;
    }
  }

  private static reduceStopping(model: BurstRuntime, ev: BurstEvent): BurstRuntime {
    // allow inflight to drain; controller may still dispatch results
    if (!model.report) return { ...model, state: "Error", report: null, stopRequested: true };
    switch (ev.type) {
        case "TickComplete": {
            const r = model.report;
            const completed = r.progress.completed + ev.count;
            const inFlight = Math.max(0, r.progress.inFlight - ev.count);
            return { ...model, report: { ...r, progress: { ...r.progress, completed, inFlight } } };
        }
        case "ResultOk":
        case "ResultHttp":
        case "ResultError":
            // reuse Running logic by temporary call:
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
            return BurstMachine.reduce({ ...model, state: "Running" }, ev as any) as BurstRuntime;
        case "Finish": {
            const r = model.report;
            const finishedAt = Date.now();
            const stats = BurstMachine.computeStats(r.stats.durationsMs);
        return {
            state: "Completed",
            stopRequested: false,
            report: { ...r, timing: { ...r.timing, finishedAt }, stats: { ...r.stats, ...stats } },
        };
        }
        case "Fail": {
            const r = model.report;
            return { state: "Error", stopRequested: false, report: { ...r, error: ev.message } };
        }

        default:
        return model;
    }
  }

  // ----------------- helpers -----------------

  static sanitize(cfg: BurstConfig): BurstConfig {
    const total = Math.max(1, Math.floor(cfg.total));
    const delayMs = Math.max(0, Math.floor(cfg.delayMs));
    switch (cfg.dispatchMode) {
      case "single-burst":
        return {
          dispatchMode: "single-burst",
          total,
          delayMs,
          planKey: cfg.planKey,
        };

      case "maintained-concurrency":
        return {
          dispatchMode: "maintained-concurrency",
          total,
          delayMs,
          planKey: cfg.planKey,
          concurrency: Math.max(1, Math.floor(cfg.concurrency)),
        };
      case "wave-batches":
        return {
          dispatchMode: "wave-batches",
          total,
          delayMs,
          planKey: cfg.planKey,
          batchSize: Math.max(1, Math.floor(cfg.batchSize)),
          wavePauseMs: Math.max(0, Math.floor(cfg.wavePauseMs)),
        };
      case "wave-batches-staggered":
        return {
          dispatchMode: "wave-batches-staggered",
          total,
          delayMs,
          planKey: cfg.planKey,
          batchSize: Math.max(1, Math.floor(cfg.batchSize)),
          wavePauseMs: Math.max(0, Math.floor(cfg.wavePauseMs)),
        };
    }
  }
  static defaultConfig(): BurstConfig {
    return {
      dispatchMode: "maintained-concurrency",
      total: 500,
      concurrency: 50,
      delayMs: 10,
      planKey: "read",
    };
  }
  static newReport(cfg: BurstConfig, startedAt?: number): BurstReport {
    return {
      config: cfg,
      progress: { started: 0, completed: 0, inFlight: 0, total: cfg.total },
      counters: { ok: 0, unauthorized: 0, forbidden: 0, other: 0, errors: 0, rejected:0 },
      timing: { startedAt },
      stats: { durationsMs: [] },
      error: undefined,
    };
  }
  static pushDuration(list: number[], value: number): number[] {
    // Keep memory bounded (demo): last 50k durations
    const v = Number.isFinite(value) && value >= 0 ? value : 0;
    if (list.length >= 50_000) return [...list.slice(1), v];
    return [...list, v];
  }
  static computeStats(durations: number[]): { p50ms?: number; p95ms?: number } {
    if (!durations.length) return {};
    const sorted = [...durations].sort((a, b) => a - b);
    const p = (pct: number) => {
      const idx = Math.max(0, Math.min(sorted.length - 1, Math.ceil((pct / 100) * sorted.length) - 1));
      return sorted[idx];
    };
    return { p50ms: p(50), p95ms: p(95) };
  }
}
