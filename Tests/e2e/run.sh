#!/usr/bin/env bash
# End-to-end tests: the built app against the parity databases, every route and every UI page. Usage: Tests/e2e/run.sh [extra dotnet test args]
set -euo pipefail
cd "$(dirname "$0")/../.."

COMPOSE=${COMPOSE:-podman compose}
$COMPOSE -f Tests/parity/docker-compose.yml up -d
for _ in $(seq 1 60); do
  (exec 3<>/dev/tcp/127.0.0.1/14333 && exec 3<>/dev/tcp/127.0.0.1/54329) 2>/dev/null && break
  sleep 2
done
sleep 5

export TRIGNIS_PARITY_MSSQL='Server=127.0.0.1,14333;User ID=sa;Password=Parity_Test_1433!;Encrypt=False;TrustServerCertificate=True'
export TRIGNIS_PARITY_POSTGRES='Host=127.0.0.1;Port=54329;Username=postgres;Password=parity_test;Database=postgres'
export TRIGNIS_E2E=1

dotnet build trignis.sln
dotnet test trignis.sln --no-build --filter "FullyQualifiedName~EndToEndTests" --logger "console;verbosity=detailed" "$@"
