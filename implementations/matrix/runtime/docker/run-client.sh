#!/bin/sh
set -eu
LANGUAGE="$1"
MANIFEST="/matrix/state/runtime-manifest.json"
for i in $(seq 1 120); do
  [ -s "$MANIFEST" ] && break
  sleep 1
done
[ -s "$MANIFEST" ] || { echo "runtime manifest not produced" >&2; exit 1; }

run_one() {
  worker="$1"
  scenario="core-${LANGUAGE}-client-${worker}-worker"
  evidence="/matrix/evidence/${scenario}.json"
  case "$LANGUAGE" in
    dotnet)
      dotnet /app/client/Multiplexed.AI.Matrix.DotNetClient.dll --manifest "$MANIFEST" --worker "$worker" --scenario-id "$scenario" --evidence "$evidence"
      ;;
    typescript)
      node /app/implementations/matrix/clients/typescript/run.mjs --manifest "$MANIFEST" --worker "$worker" --scenario-id "$scenario" --evidence "$evidence"
      ;;
    python)
      python /app/implementations/matrix/clients/python/run.py --manifest "$MANIFEST" --worker "$worker" --scenario-id "$scenario" --evidence "$evidence"
      ;;
    *) echo "unknown client language: $LANGUAGE" >&2; exit 2 ;;
  esac
}


run_feature() {
  feature="$1"
  worker="$2"
  case "$feature" in
    publication-pinning) scenario="feature-publication-pinning-python-client-${worker}-worker" ;;
    deterministic-dependency-packaging) scenario="feature-dependency-package-python-client-${worker}-worker" ;;
    custom-policy-family) scenario="feature-custom-policy-${POLICY_FAMILY}-${worker}-worker" ;;
    nested-child-dag) scenario="feature-nested-child-dag-python-client-${worker}-worker" ;;
    *) echo "unknown feature: $feature" >&2; exit 2 ;;
  esac
  evidence="/matrix/evidence/${scenario}.json"
  policy_args=""
  if [ "$feature" = "custom-policy-family" ]; then
    policy_args="--policy-family $POLICY_FAMILY"
  fi
  # shellcheck disable=SC2086
  python /app/implementations/matrix/clients/python/feature.py \
    --manifest "$MANIFEST" \
    --feature "$feature" \
    --worker "$worker" \
    $policy_args \
    --scenario-id "$scenario" \
    --evidence "$evidence"
}

run_effect_feature() {
  effect_case="$1"
  case "$effect_case" in
    completed-local-replay) scenario="feature-mcp-effect-completed-local-replay-python-client" ;;
    uncertain-blocks-blind-resend) scenario="feature-mcp-effect-uncertain-blocks-blind-resend-python-client" ;;
    *) echo "unknown MCP effect case: $effect_case" >&2; exit 2 ;;
  esac
  evidence="/matrix/evidence/${scenario}.json"
  python /app/implementations/matrix/clients/python/feature.py     --manifest "$MANIFEST"     --feature mcp-effect-evidence     --effect-case "$effect_case"     --scenario-id "$scenario"     --evidence "$evidence"
}

run_recovery_feature() {
  recovery_case="$1"
  case "$recovery_case" in
    in-flight-resume) scenario="feature-recovery-in-flight-resume-python-client" ;;
    local-queued-redispatch) scenario="feature-recovery-local-queued-redispatch-python-client" ;;
    *) echo "unknown recovery case: $recovery_case" >&2; exit 2 ;;
  esac
  evidence="/matrix/evidence/${scenario}.json"
  python /app/implementations/matrix/clients/python/feature.py \
    --manifest "$MANIFEST" \
    --feature recovery \
    --recovery-case "$recovery_case" \
    --scenario-id "$scenario" \
    --evidence "$evidence"
}

run_journal_feature() {
  journal_case="$1"
  case "$journal_case" in
    accepted-result-replay) scenario="feature-journal-result-accepted-replay-python-client" ;;
    duplicate-delivery-convergence) scenario="feature-journal-duplicate-delivery-convergence-python-client" ;;
    *) echo "unknown journal acceptance case: $journal_case" >&2; exit 2 ;;
  esac
  evidence="/matrix/evidence/${scenario}.json"
  python /app/implementations/matrix/clients/python/feature.py \
    --manifest "$MANIFEST" \
    --feature journal-result-acceptance \
    --journal-case "$journal_case" \
    --scenario-id "$scenario" \
    --evidence "$evidence"
}

run_cancellation_feature() {
  scenario="feature-cancellation-${LANGUAGE}-client-${LANGUAGE}-worker"
  evidence="/matrix/evidence/${scenario}.json"
  case "$LANGUAGE" in
    dotnet)
      dotnet /app/client/Multiplexed.AI.Matrix.DotNetClient.dll --manifest "$MANIFEST" --feature cancellation --worker dotnet --scenario-id "$scenario" --evidence "$evidence"
      ;;
    typescript)
      node /app/implementations/matrix/clients/typescript/run.mjs --manifest "$MANIFEST" --feature cancellation --worker typescript --scenario-id "$scenario" --evidence "$evidence"
      ;;
    python)
      python /app/implementations/matrix/clients/python/run.py --manifest "$MANIFEST" --feature cancellation --worker python --scenario-id "$scenario" --evidence "$evidence"
      ;;
    *) echo "unknown cancellation client language: $LANGUAGE" >&2; exit 2 ;;
  esac
}

run_one dotnet
run_one typescript
run_one python
run_cancellation_feature

if [ "$LANGUAGE" = "python" ]; then
  run_feature publication-pinning dotnet
  run_feature publication-pinning typescript
  run_feature publication-pinning python
  run_feature deterministic-dependency-packaging dotnet
  run_feature deterministic-dependency-packaging typescript
  run_feature deterministic-dependency-packaging python

  POLICY_FAMILY=concurrency run_feature custom-policy-family python
  POLICY_FAMILY=retry run_feature custom-policy-family typescript
  POLICY_FAMILY=delegation run_feature custom-policy-family dotnet

  run_feature nested-child-dag dotnet
  run_feature nested-child-dag typescript
  run_feature nested-child-dag python

  run_effect_feature completed-local-replay
  run_effect_feature uncertain-blocks-blind-resend

  run_recovery_feature in-flight-resume
  run_recovery_feature local-queued-redispatch

  run_journal_feature accepted-result-replay
  run_journal_feature duplicate-delivery-convergence
fi
