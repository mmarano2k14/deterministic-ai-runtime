"use client";

import React, { JSX } from "react";
import { Button } from "@/components/ui/Button";
import { BurstScenarioDefinition } from "@/lib/console/burst/scenarios/BurstScenarioPresetType";
import { BurstScenarioFactory } from "@/lib/console/burst/scenarios/BurstScenarioFactory";

export type RequestPanelProps = {
  disabled: boolean;
  invoiceId: string;
  amount: number;
  onInvoiceIdChange: (v: string) => void;
  onAmountChange: (v: number) => void;
  onReadClick: (scenario: BurstScenarioDefinition) => void;
  onRefundClick: (scenario: BurstScenarioDefinition) => void;
  onClearLogClick: () => void;
};

export function RequestPanel(props: RequestPanelProps): JSX.Element {
  const {
    disabled,
    invoiceId,
    amount,
    onInvoiceIdChange,
    onAmountChange,
    onReadClick,
    onRefundClick,
  } = props;

  return (
    <section className="panel">
      <h3>Request</h3>

      {/* Inputs */}
      <div className="form-grid">
        <div>
          <label>InvoiceId</label>
          <input
            value={invoiceId}
            onChange={(e) => onInvoiceIdChange(e.target.value)}
            disabled={true}
          />
        </div>

        <div>
          <label>Amount</label>
          <input
            type="number"
            value={amount}
            onChange={(e) => onAmountChange(Number(e.target.value))}
            disabled={true}
          />
        </div>
      </div>

      {/* Actions */}
      <div className="action-row">
        <Button loading={disabled} disabled={disabled} onClick={() => onReadClick(BurstScenarioFactory.simpleRequest("read"))}>
          READ
        </Button>

        <Button loading={disabled} disabled={disabled} onClick={() => onRefundClick(BurstScenarioFactory.simpleRequest("refund"))}>
          REFUND
        </Button>
      </div>
    </section>
  );
}