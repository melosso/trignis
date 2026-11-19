#!/usr/bin/env bash
# Runs the probe parity tests against local brokers. Usage: Tests/brokers/run.sh [extra dotnet test args]
set -euo pipefail
cd "$(dirname "$0")/../.."

COMPOSE=${COMPOSE:-podman compose}
$COMPOSE -f Tests/brokers/compose.yml up -d

# Emulators report ready on their health port.
for port in 5301 5302; do
  for _ in $(seq 1 90); do curl -fs "http://127.0.0.1:$port/health" >/dev/null && break; sleep 2; done
done

export TRIGNIS_BROKERS=1
dotnet test trignis.sln --filter "FullyQualifiedName~BrokerParityTests" "$@"
