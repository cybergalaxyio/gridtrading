# GridTrading Agent Handoff

Last updated: 2026-08-25 (UTC)

## Current outcome

The repository implements the V1 semi-automatic grid-trading terminal described under `v1/`. It supports deterministic Replay, local Paper execution, and real Hyperliquid **Testnet** trading through an API Wallet. Mainnet is intentionally unavailable.

The most recent change replaced the dashboard's fake SOL price/K-line data with Hyperliquid Testnet data whenever a Testnet strategy is selected. A live check on 2026-08-25 returned a SOL bid/ask of `103.56 / 103.69`, midpoint `103.625`, and 1-minute candles around `103–104`, matching the Hyperliquid Testnet UI at that time. These numbers are only a historical verification observation, not fixtures or expected future values.

No secret values are recorded in this document. Do not print or commit `.env`.

## What has been implemented

### Application foundation

- React, TypeScript, and Vite operator UI.
- ASP.NET Core API with SQLite/EF Core persistence and SignalR.
- Strategy templates are separated from frozen running cycles.
- Fixed-centre grid planning, increasing spacing, geometric quantity sizing, Entry/TP lifecycle, same-level re-entry, basket PnL, pause/resume, orderly close, emergency flatten, reconciliation, audit records, optimistic state versions, and idempotency support.
- Paper execution and conservative OHLC replay remain available.
- Production frontend assets are emitted directly into `src/GridTrading.Api/wwwroot` and served by the API process.

### Hyperliquid Testnet execution

- API Wallet/Agent Wallet signs actions; all Info queries use the main account's public address.
- Official EIP-712 signing and Hyperliquid MessagePack action encoding.
- Stable CLOIDs for strategy orders.
- Per-signing-address nonce persisted in SQLite and allocated as `max(current Unix milliseconds, previous + 1)`.
- Real Testnet order placement, cancel-by-CLOID, WebSocket `userFills` processing, REST fill reconciliation, ordinary non-reduce-only Limit take-profit orders, current-price entry maintenance, and manual flatten/close.
- Startup preflight rejects an unapproved agent, unfunded account, non-flat position, or pre-existing open orders.
- Testnet basket TP/SL is monitor-only; it does not automatically submit a real flatten command without separate authorization.

### Credential handling and safety

- `.env` is git-ignored and loaded by `run-testnet.sh`.
- The API Wallet private key enters only through the backend environment.
- The key is encrypted with local AES-256-GCM before storage in SQLite. `GRID_TRADING_CREDENTIAL_KEY` must stay stable across restarts so existing ciphertext can be decrypted.
- HTTP responses and the browser receive only public addresses.
- Info and Exchange endpoint validation only accepts the official HTTPS Testnet hosts.
- Mainnet/Live accounts and routes are rejected.
- Mutating HTTP requests are accepted only from loopback in V1 because remote authentication/authorization is not implemented.

### Real Testnet dashboard data

The Testnet dashboard now uses:

| UI value | Hyperliquid Info request |
|---|---|
| Chart candles | `candleSnapshot` |
| Bid, ask, midpoint | `l2Book` |
| Account value, withdrawable amount, margin used | `clearinghouseState` |
| SOL net position, entry price, unrealized PnL | `clearinghouseState.assetPositions` |

New backend endpoints:

```text
GET /api/v1/hyperliquid-testnet/market/{symbol}
GET /api/v1/hyperliquid-testnet/market/{symbol}/candles?interval=1m&limit=180
GET /api/v1/hyperliquid-testnet/accounts/{accountId}/state?symbol=SOLUSDT
```

The market-data client converts internal `SOLUSDT`/`SOLUSDC` symbols to Hyperliquid coin `SOL`. Candle timestamps are converted from milliseconds to Unix seconds because Lightweight Charts expects seconds.

On startup, `HyperliquidStrategyBootstrap` creates a non-running `Hyperliquid Testnet SOL Grid` template if a configured Testnet account exists and does not already have a strategy. Existing strategies are preserved.

Dashboard strategy selection order is:

1. Strategy with an active cycle.
2. First Testnet strategy.
3. First available strategy.

This means an active Paper cycle intentionally remains visible until it is managed or closed. With no active cycle, the configured Testnet strategy is preferred and the header displays `TESTNET`, `Hyperliquid Testnet`, and a 10-second official-data refresh label. Paper strategies still use the deterministic local `MarketState` data.

## Important files

### Hyperliquid and backend

- `src/GridTrading.Api/Services/HyperliquidMarketDataClient.cs` — Testnet candles and account-state parsing.
- `src/GridTrading.Api/Services/HyperliquidTradingClient.cs` — official Info/Exchange calls, orders, cancels, preflight, and endpoint lock.
- `src/GridTrading.Api/Services/HyperliquidAccountBootstrap.cs` — reads environment credentials and upserts the encrypted Testnet account.
- `src/GridTrading.Api/Services/HyperliquidStrategyBootstrap.cs` — creates the default Testnet strategy template.
- `src/GridTrading.Api/Services/HyperliquidNonceManager.cs` — persistent monotonic nonce allocation.
- `src/GridTrading.Api/Services/HyperliquidCycleCoordinator.cs` — Testnet placement, reconciliation, TP/re-entry, and flatten coordination.
- `src/GridTrading.Api/Exchange/HyperliquidL1Signer.cs` — EIP-712 signing.
- `src/GridTrading.Api/Exchange/HyperliquidWireCodec.cs` — action encoding and CLOID generation.
- `src/GridTrading.Api/Infrastructure/HyperliquidEndpoints.cs` — Testnet read endpoints.
- `src/GridTrading.Api/Program.cs` — service registration and primary REST surface.

### Frontend

- `web/src/pages/HyperliquidDashboardPage.tsx` — dashboard using Testnet data with Paper fallback.
- `web/src/App.tsx` — preferred-strategy/environment selection.
- `web/src/api.ts` — typed browser calls for candles, book, and account state.
- `web/src/types.ts` — `HyperliquidAccountState` and other API models.
- `web/src/components/Layout.tsx` — dynamic Paper/Testnet environment labels.

### Operations and documentation

- `run-testnet.sh` — validates and loads `.env`, then starts the API on port 5050.
- `docs/TESTNET_TRADING.md` — account setup and operator workflow.
- `README.md` — project overview, build, tests, and safety boundary.
- `.env` — local secrets; ignored by Git. Never include its contents in logs or handoff notes.

## How to run

Prerequisites: .NET 10 SDK and Node.js 24+.

For the normal Testnet run:

```bash
cd /home/azureuser/projects/GridTrading
cd web
npm install
npm run build
cd ..
./run-testnet.sh
```

Open `http://localhost:5050`. In a remote VS Code/Codespaces-style environment, use the forwarded port 5050 URL. If the page shows an old bundle, restart the backend and use a hard refresh (`Ctrl+Shift+R`). Do not open the API's generated `index.html` through a separate static Live Server.

Required `.env` variable names:

```text
GRID_TRADING_CREDENTIAL_KEY
GRID_TRADING_HL_TESTNET_ACCOUNT_ADDRESS
GRID_TRADING_HL_TESTNET_AGENT_PRIVATE_KEY
```

Optional:

```text
GRID_TRADING_HL_TESTNET_VAULT_ADDRESS
```

Do not replace `GRID_TRADING_CREDENTIAL_KEY` casually. If it changes, rows encrypted under the old key cannot be decrypted. See `docs/TESTNET_TRADING.md` for setup details.

## How to verify

Project-wide verification:

```bash
dotnet restore GridTrading.slnx
dotnet build GridTrading.slnx --no-restore
dotnet test GridTrading.slnx --no-build
cd web && npm run build
```

The last completed verification passed:

- Backend build: 0 warnings, 0 errors.
- Domain tests: 21 passed.
- API tests: 7 passed.
- Frontend TypeScript/Vite production build: passed.
- Live Testnet `l2Book`, `candleSnapshot`, and `clearinghouseState` requests: passed.

Quick read-only checks after startup:

```bash
curl -fsS http://127.0.0.1:5050/api/v1/hyperliquid-testnet/accounts
curl -fsS http://127.0.0.1:5050/api/v1/hyperliquid-testnet/market/SOLUSDT
curl -fsS 'http://127.0.0.1:5050/api/v1/hyperliquid-testnet/market/SOLUSDT/candles?interval=1m&limit=10'
```

Read the account ID from the first response before calling the state endpoint:

```bash
curl -fsS 'http://127.0.0.1:5050/api/v1/hyperliquid-testnet/accounts/ACCOUNT_ID/state?symbol=SOLUSDT'
```

These checks are read-only. Do not start a cycle merely to test connectivity unless the operator explicitly intends to place Testnet orders.

## User workflow

1. Start with `./run-testnet.sh`.
2. Open Settings and confirm the Hyperliquid Testnet account reports ready.
3. Confirm the dashboard says `TESTNET` and the displayed SOL price is close to the official Testnet UI.
4. Review the strategy parameters and current centre.
5. Click `确认预览并开启` only when real Testnet order placement is intended.
6. Monitor actual orders and fills. Hyperliquid `userFills` arrives over WebSocket; REST reconciliation still runs approximately every 10 seconds by default as recovery.
7. Use Pause to remove Entry orders while retaining protective TP orders.
8. Use Close Cycle or Emergency Stop to cancel strategy orders and submit a non-reduce-only IOC Limit sized to the observed actual position.
9. If flattening leaves any residual position, the cycle remains non-terminal and reports an error instead of claiming success.

## Known limitations and next work

- Only SOL perpetual is wired through the current V1 strategy UI; internal display still uses `SOLUSDT`, while Hyperliquid's Testnet UI displays `SOL-USDC`.
- Candle interval buttons are currently visual; the dashboard requests 1-minute candles. Wiring buttons to the `interval` parameter is a contained next task.
- Fill events use the official Testnet `userFills` WebSocket subscription. Dashboard mid-price ticks use `allMids`, then SignalR forwards only each connection's selected symbol; REST book/candle snapshots remain the initial/recovery source. Open-order tables and order-book depth are still REST-polled.
- The order table is populated from the local reconciled cycle ledger, not a standalone rendering of every account-level exchange order.
- Basket TP/SL does not auto-flatten real Testnet positions.
- Remote multi-user auth, per-user secret isolation, role permissions, and secure secret rotation are not implemented.
- The current SQLite/encrypted-key structure can support multiple accounts later, but the environment bootstrap currently manages one default Testnet account.
- Account value `0` during the last live verification meant the configured public account had no Testnet perp equity at that moment. If the official UI shows funds but the app shows zero, first confirm both are using the exact same main account public address and that funds are in the perpetual account state.
- Never weaken the official-Testnet host allowlist or Mainnet hard lock as an incidental refactor.

## Notes for the next agent

- Preserve user-owned/unrelated untracked files and the contents of `v1/`.
- Before edits, inspect `git status`; do not reset or delete unrelated work.
- Frontend builds replace hashed assets under `src/GridTrading.Api/wwwroot/assets`, so build frontend first and backend second if doing both. Running them in parallel can cause the .NET static-web-assets task to read an asset while Vite is replacing it.
- A running backend must be restarted after backend changes. Rebuilding the frontend alone does not load new API routes into an already-running process.
- Use the main account public address for all Info queries and the API Wallet private key only for signing Exchange actions.
- Keep decimal values lossless. The API serializes `decimal` values as strings by design.
- Validate any Hyperliquid schema changes against the official docs/SDK before modifying parsing or signing.
