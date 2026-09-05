import { BurstPlan, BurstPlanKey } from "../runtime/BurstMachineType";

export class BurstPlans {
  static readonly Read: BurstPlan = {
    key: "read",
    displayName: "Invoice.Read (GET)",
    makeRequest(i) {
      const id = `invoice-${(i % 10) + 1}`;
      return {
        name: "Invoice.Read",
        method: "GET",
        path: `/billing/${encodeURIComponent(id)}`,
      };
    },
  };

  static readonly Refund: BurstPlan = {
    key: "refund",
    displayName: "Invoice.Refund (POST)",
    makeRequest(i) {
      const id = `invoice-${(i % 10) + 1}`;
      const amount = 100 + (i % 5) * 50;
      return {
        name: "Invoice.Refund",
        method: "POST",
        path: `/billing/${encodeURIComponent(id)}/refund`,
        body: { amount },
      };
    },
  };

  static byKey(key: BurstPlanKey): BurstPlan {
    return key === "refund" ? BurstPlans.Refund : BurstPlans.Read;
  }
}