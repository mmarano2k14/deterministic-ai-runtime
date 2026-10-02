from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from matrix_plan import assert_valid_plan, default_plan_path, load_plan, validate_plan


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Validate and inspect the deterministic multilanguage runtime matrix plan.")
    parser.add_argument("--plan", type=Path, default=default_plan_path(), help="Path to the matrix plan JSON file.")
    subcommands = parser.add_subparsers(dest="command", required=True)

    subcommands.add_parser("validate", help="Validate the matrix plan and fail closed on unsupported or missing coverage.")

    list_parser = subcommands.add_parser("list", help="List planned core and bound feature scenarios.")
    list_parser.add_argument("--json", action="store_true", help="Emit machine-readable JSON.")

    coverage_parser = subcommands.add_parser("coverage", help="List required coverage targets without claiming they were executed.")
    coverage_parser.add_argument("--json", action="store_true", help="Emit machine-readable JSON.")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    plan = load_plan(args.plan)

    if args.command == "validate":
        errors = validate_plan(plan)
        if errors:
            for error in errors:
                print(f"ERROR: {error}", file=sys.stderr)
            return 1
        print(f"Matrix plan '{plan['matrixId']}' is valid.")
        print(f"Core client/worker scenarios: {len(plan['coreScenarios'])}")
        print(f"Coverage targets: {len(plan['coverageTargets'])}")
        print(f"Bound feature scenarios: {len(plan['featureScenarios'])}")
        print("No live runtime scenario is executed by plan validation.")
        return 0

    assert_valid_plan(plan)

    if args.command == "list":
        scenarios = [
            {
                "id": scenario["id"],
                "kind": "core",
                "clientLanguage": scenario["clientLanguage"],
                "workerLanguage": scenario["workerLanguage"],
                "executorBound": scenario["executor"] is not None,
            }
            for scenario in plan["coreScenarios"]
        ]
        scenarios.extend(
            {
                "id": scenario["id"],
                "kind": "feature",
                "clientLanguage": scenario.get("clientLanguage"),
                "workerLanguage": scenario.get("workerLanguage"),
                "coverageTarget": scenario["coverageTarget"],
                "executorBound": scenario.get("executor") is not None,
            }
            for scenario in plan["featureScenarios"]
        )
        if args.json:
            print(json.dumps(scenarios, indent=2))
        else:
            for scenario in scenarios:
                client = scenario.get("clientLanguage") or "-"
                worker = scenario.get("workerLanguage") or "-"
                bound = "bound" if scenario["executorBound"] else "unbound"
                print(f"{scenario['id']}: client={client}, worker={worker}, executor={bound}")
        return 0

    if args.command == "coverage":
        targets = plan["coverageTargets"]
        if args.json:
            print(json.dumps(targets, indent=2))
        else:
            for target in targets:
                values = ", ".join(target["requiredValues"]) or "-"
                clients = ", ".join(target.get("requiredClientLanguages", [])) or "-"
                workers = ", ".join(target.get("requiredWorkerLanguages", [])) or "-"
                print(
                    f"{target['id']}: minimum={target['minimumExecutedScenarios']}, "
                    f"clients={clients}, workers={workers}, values={values}"
                )
        return 0

    return 2


if __name__ == "__main__":
    raise SystemExit(main())
