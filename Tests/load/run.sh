#!/usr/bin/env bash
# Load and transaction tests: parity databases and brokers, then LoadTests and TransactionParityTests. Usage: Tests/load/run.sh [extra dotnet test args]
set -euo pipefail
cd "$(dirname "$0")/../.."

COMPOSE=${COMPOSE:-podman compose}
$COMPOSE -f Tests/parity/docker-compose.yml up -d
$COMPOSE -f Tests/brokers/compose.yml up -d

for _ in $(seq 1 60); do
  (exec 3<>/dev/tcp/127.0.0.1/14333 && exec 3<>/dev/tcp/127.0.0.1/54329 && exec 3<>/dev/tcp/127.0.0.1/9092 && exec 3<>/dev/tcp/127.0.0.1/5672) 2>/dev/null && break
  sleep 2
done
# Emulators report ready on their health port.
for port in 5301 5302; do
  for _ in $(seq 1 90); do curl -fs "http://127.0.0.1:$port/health" >/dev/null && break; sleep 2; done
done
sleep 5

export TRIGNIS_PARITY_MSSQL='Server=127.0.0.1,14333;User ID=sa;Password=Parity_Test_1433!;Encrypt=False;TrustServerCertificate=True'
export TRIGNIS_PARITY_POSTGRES='Host=127.0.0.1;Port=54329;Username=postgres;Password=parity_test;Database=postgres'
export TRIGNIS_LOAD=1

dotnet test trignis.sln --filter "FullyQualifiedName~Stress.LoadTests|FullyQualifiedName~TransactionParityTests" --logger "console;verbosity=detailed" "$@"
