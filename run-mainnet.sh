#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
env_file="$project_dir/.env.mainnet"
if [[ ! -f "$env_file" ]]; then
  echo "Create .env.mainnet from .env.mainnet.example and fill in the API wallet locally." >&2
  exit 1
fi
set -a
# shellcheck disable=SC1090
source "$env_file"
set +a
for setting in GRID_TRADING_CREDENTIAL_KEY GRID_TRADING_HL_MAINNET_ACCOUNT_ADDRESS GRID_TRADING_HL_MAINNET_AGENT_PRIVATE_KEY; do
  if [[ -z "${!setting:-}" || "${!setting}" == *REPLACE_WITH* ]]; then
    echo "Set $setting in .env.mainnet" >&2
    exit 1
  fi
done
# This instance has its own database and port. Existing cycles resume reconciliation after a restart.
export ConnectionStrings__GridTrading="Data Source=$project_dir/data/grid-trading-mainnet.db"
cd "$project_dir"
exec dotnet run --project src/GridTrading.Api/GridTrading.Api.csproj --urls http://127.0.0.1:5051
