"use client";

import { JSX, useMemo, useState } from "react";
import { BurstScenarioFactory } from "@/lib/console/burst/scenarios/BurstScenarioFactory";
import { BurstPlanKey } from "@/lib/console/burst/runtime/BurstMachineType";
import { ScenarioInfoModal } from "./ScenarioInfoModal";
import {
  BurstScenarioDefinition,
  BurstScenarioKey,
} from "@/lib/console/burst/scenarios/BurstScenarioPresetType";

export type ScenarioPresetsPanelProps = {
  planKey?: BurstPlanKey;
  disabled?: boolean;
  onLaunch: (scenario: BurstScenarioDefinition) => void;
};

export function ScenarioPresetsPanel(
  props: ScenarioPresetsPanelProps
): JSX.Element {
  const {
    planKey = "read",
    disabled = false,
    onLaunch,
  } = props;

  const [selectedScenario, setSelectedScenario] =
    useState<BurstScenarioDefinition | null>(null);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [openKey, setOpenKey] = useState<BurstScenarioKey | null>(null);

  const scenarios = useMemo(() => {
    return BurstScenarioFactory.buildAll(planKey);
  }, [planKey]);

  const orderedKeys: BurstScenarioKey[] = [
    "single-burst",
    "maintained-concurrency",
    "wave-batches",
    "wave-batches-staggered",
  ];

  function handleInfo(key: BurstScenarioKey): void {
    setSelectedScenario(scenarios[key]);
    setIsModalOpen(true);
  }

  function handleCloseModal(): void {
    setIsModalOpen(false);
  }

  function handleLaunchFromModal(scenario: BurstScenarioDefinition): void {
    if (disabled) {
      return;
    }

    onLaunch(scenario);
    setIsModalOpen(false);
  }

  function toggleScenario(key: BurstScenarioKey): void {
    setOpenKey((current) => (current === key ? null : key));
  }

  return (
    <>
      <section className="panel">
        <h3>Scenario Presets</h3>

        <div className="scenario-list">
          {orderedKeys.map((key) => {
            const scenario = scenarios[key];
            const isOpen = openKey === key;

            return (
              <div key={scenario.key} className="scenario-card">
                <button
                  type="button"
                  className="scenario-card__toggle"
                  onClick={() => toggleScenario(key)}
                  aria-expanded={isOpen}
                >
                  <span className="scenario-card__toggle-title">
                    {scenario.title}
                  </span>
                  <span className="scenario-card__toggle-icon">
                    {isOpen ? "−" : "+"}
                  </span>
                </button>

                {isOpen && (
                  <div className="scenario-card__body">
                    <div className="scenario-card__idea">{scenario.idea}</div>

                    <div className="scenario-card__mode">
                      Mode: <code>{scenario.burstConfig.dispatchMode}</code>
                    </div>

                    <div className="scenario-card__actions">
                      <button
                        type="button"
                        className="scenario-card__launch"
                        disabled={disabled}
                        title={
                          disabled
                            ? "Burst launch is locked while AI is working."
                            : "Launch scenario"
                        }
                        onClick={() => onLaunch(scenario)}
                      >
                        Launch
                      </button>

                      <button
                        type="button"
                        className="scenario-card__info"
                        onClick={() => handleInfo(key)}
                      >
                        Info
                      </button>
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </section>

      <ScenarioInfoModal
        scenario={selectedScenario}
        isOpen={isModalOpen}
        launchDisabled={disabled}
        onClose={handleCloseModal}
        onLaunch={handleLaunchFromModal}
      />
    </>
  );
}