#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$project_dir/.env.mainnet"
# Refresh the frontend served by the API before loading account credentials.
echo "Building frontend..."
npm --prefix "$project_dir/web" run build

if [[ -f "$env_file" ]]; then
  set -a
  # shellcheck disable=SC1090
  source "$env_file"
  set +a
fi
for setting in GRID_TRADING_CREDENTIAL_KEY; do
  if [[ -z "${!setting:-}" || "${!setting}" == *REPLACE_WITH* ]]; then
    echo "Set $setting in .env.mainnet" >&2
    exit 1
  fi
done
# This instance has its own database and port. Existing cycles resume reconciliation after a restart.
export ConnectionStrings__GridTrading="Data Source=$project_dir/data/grid-trading-mainnet.db"
cd "$project_dir"
exec dotnet run --project src/GridTrading.Api/GridTrading.Api.csproj --urls http://127.0.0.1:5051
