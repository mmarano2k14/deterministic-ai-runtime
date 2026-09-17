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

run_one dotnet
run_one typescript
run_one python
