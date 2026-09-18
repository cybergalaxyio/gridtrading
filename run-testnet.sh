#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$project_dir/.env"

# Refresh the frontend served by the API before loading account credentials.
echo "Building frontend..."
npm --prefix "$project_dir/web" run build

if [[ -f "$env_file" ]]; then
  set -a
  # shellcheck disable=SC1090
  source "$env_file"
  set +a
fi

if [[ -z "${GRID_TRADING_CREDENTIAL_KEY:-}" ]]; then
  echo "GRID_TRADING_CREDENTIAL_KEY is missing in .env" >&2
  exit 1
fi

cd "$project_dir"
exec dotnet run --project src/GridTrading.Api/GridTrading.Api.csproj --urls http://localhost:5050
