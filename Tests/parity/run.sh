#!/usr/bin/env bash
# Runs the container-backed pipeline tests against the parity databases.
# Usage: Tests/parity/run.sh [extra dotnet test args]
set -euo pipefail
cd "$(dirname "$0")/../.."

docker compose -f Tests/parity/docker-compose.yml up -d --wait

export TRIGNIS_PARITY_MSSQL='Server=127.0.0.1,14333;User ID=sa;Password=Parity_Test_1433!;Encrypt=False;TrustServerCertificate=True'
export TRIGNIS_PARITY_POSTGRES='Host=127.0.0.1;Port=54329;Username=postgres;Password=parity_test;Database=postgres'

dotnet test trignis.sln --filter "FullyQualifiedName~PipelineParityTests" "$@"
