"use client";

import { BurstScenarioDefinition } from "@/lib/console/burst/scenarios/BurstScenarioPresetType";
import { JSX } from "react";

export type ScenarioInfoModalProps = {
  scenario: BurstScenarioDefinition | null;
  isOpen: boolean;
  launchDisabled?: boolean;
  onClose: () => void;
  onLaunch: (scenario: BurstScenarioDefinition) => void;
};

function Section(props: {
  title: string;
  children: React.ReactNode;
}): JSX.Element {
  const { title, children } = props;

  return (
    <section className="scenario-info-section">
      <div className="scenario-info-section__title">
        {title}
      </div>

      <div className="scenario-info-section__content">
        {children}
      </div>
    </section>
  );
}

export function ScenarioInfoModal(
  props: ScenarioInfoModalProps
): JSX.Element | null {
  const {
    scenario,
    isOpen,
    launchDisabled = false,
    onClose,
    onLaunch,
  } = props;

  if (!isOpen || !scenario) {
    return null;
  }

  return (
    <div
      className="scenario-info-modal-overlay"
      onClick={onClose}
    >
      <div
        className="scenario-info-modal"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="scenario-info-modal__header">
          <div className="scenario-info-modal__heading">
            <div className="scenario-info-modal__title">
              {scenario.title}
            </div>

            <div className="scenario-info-modal__subtitle">
              Scenario preset
            </div>
          </div>

          <div className="scenario-info-modal__actions">
            <button
              type="button"
              disabled={launchDisabled}
              title={
                launchDisabled
                  ? "Burst launch is locked while AI is working."
                  : "Launch scenario"
              }
              onClick={() => {
                if (!launchDisabled) {
                  onLaunch(scenario);
                }
              }}
              className="scenario-info-modal__launch"
            >
              Launch
            </button>

            <button
              type="button"
              onClick={onClose}
              className="scenario-info-modal__close"
            >
              Close
            </button>
          </div>
        </div>

        <Section title="Idea">
          <div>{scenario.idea}</div>
        </Section>

        <Section title="Recommended parameters">
          <div className="scenario-info-parameters">
            {scenario.recommendedParameters.map((parameter) => (
              <div
                key={parameter.label}
                className="scenario-info-parameters__row"
              >
                <div className="scenario-info-parameters__label">
                  {parameter.label}
                </div>
                <div>
                  <code>{String(parameter.value)}</code>
                </div>
              </div>
            ))}
          </div>
        </Section>

        <Section title="What it tests">
          <ul className="scenario-info-list">
            {scenario.whatItTests.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </Section>

        <Section title="Expected reading">
          <ul className="scenario-info-list">
            {scenario.expectedReading.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </Section>

        <Section title="Simple explanation">
          <div>{scenario.simpleExplanation}</div>
        </Section>
      </div>
    </div>
  );
}
