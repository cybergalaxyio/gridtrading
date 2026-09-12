# Hyperliquid Mainnet

Mainnet uses the `hyperliquid-mainnet` execution environment, with separate API wallet credentials, signing, endpoints and market subscriptions. You configure the strategy and click **Start** to trade.

## Setup

1. Authorize an API wallet on Hyperliquid Mainnet. Use the main account public address and the API wallet private key. Vault/subaccount routing through `VaultAddress` is not supported. Configure your leverage on the exchange; the application does not change it.
2. Copy `.env.mainnet.example` to `.env.mainnet` and fill in the credentials locally. For a new installation, generate an encryption key with `openssl rand -base64 32`. `GRID_TRADING_CREDENTIAL_KEY` encrypts the API wallet key stored in the database. Keep an existing encryption key unchanged so saved credentials remain readable.
3. Run `./run-mainnet.sh`, then open `http://127.0.0.1:5051`. This loads `.env.mainnet` and uses `data/grid-trading-mainnet.db`, independently of the Testnet instance on port 5050. Use one backend process per database/API wallet.
4. Check the Mainnet account, API wallet approval and balances in Settings.
5. Create a strategy, choose **Hyperliquid Mainnet · LIVE**, select the account and perpetual market, then enter your parameters. New installations do not create sample strategies, and there is no preset button. Existing saved strategies remain available.
6. Save the strategy, open its dashboard, review your settings and center price, then click **Start**. Start generates a fresh preview and submits the initial orders. Saving a strategy does not place orders.

## Strategy behavior

Mainnet uses your configured grid spacing, levels, quantities, size growth, maximum quantities and basket thresholds. Set optional basket thresholds or the per-order quantity cap to zero to disable them. `MaxNetLot` remains a required strategy parameter. The shared grid engine supports up to 200 levels per side and maintains one working entry per side, advancing after fills. These engine rules apply across environments.

Exchange minimum notionals and quantity/price precision still apply. The account must have valid credentials and funds. Startup requires a flat position and no outstanding orders for the selected symbol. Each Mainnet account can run one active strategy per symbol; strategies on different symbols may run together. Symbol aliases such as `SOL` and `SOL-USDC` share the same reservation, which follows the running cycle's frozen market even if its saved strategy is edited. Fresh quotes, acknowledged cancellations and complete position snapshots remain execution requirements.

## Pause, close and restart

**Pause Entry** cancels entries while maintaining take profits. **Exit** cancels tracked cycle orders, checks for remaining orders in the selected market, and closes that market's actual position with a reduce-only IOC order. The cycle completes only after that symbol's position is confirmed flat. Unexpected external orders on the same symbol must be resolved before closing can continue. Other symbols' positions and orders do not block startup, closing or automatic restart, and are left untouched.

Your configured basket thresholds are evaluated during backend reconciliation. Existing nonterminal cycles resume reconciliation after a backend restart. Starting the service does not create a new cycle. Keep the service running while it manages a cycle.

## Implementation checks

Network endpoints and signing are separated between Mainnet and Testnet. Credentials stay in the backend and are encrypted at rest. No transfer or withdrawal action is implemented. Tests use isolated databases and synthetic exchange responses; they do not place live orders.
