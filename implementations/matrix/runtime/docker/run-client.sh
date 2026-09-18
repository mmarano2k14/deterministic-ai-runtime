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

run_one dotnet
run_one typescript
run_one python

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
fi
