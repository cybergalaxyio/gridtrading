# Hyperliquid Mainnet

Mainnet uses the `hyperliquid-mainnet` execution environment, with separate API wallet credentials, signing, endpoints and market subscriptions. You configure the strategy and click **Start** to trade.

## Setup

1. Authorize an API wallet on Hyperliquid Mainnet. Use the main account public address and the API wallet private key. Vault/subaccount routing through `VaultAddress` is not supported. Configure your leverage on the exchange; the application does not change it.
2. Set a stable `GRID_TRADING_CREDENTIAL_KEY` in the process environment or `.env.mainnet`. For a new installation, generate it with `openssl rand -base64 32`; keep an existing key unchanged.
3. Run `./run-mainnet.sh`, then open `http://127.0.0.1:5051`. The script keeps `data/grid-trading-mainnet.db`, independently of the Testnet instance on port 5050. Use one backend process per database; do not share an API wallet between running instances.
4. In **Settings → Exchange Accounts**, add a Mainnet account using its public address and approved API wallet private key. Save, then **Test and enable**. Repeat for additional accounts; no restart is required. See [Account management](ACCOUNTS.md) for credential replacement, disabling, and migration from environment files.
5. Create a strategy, choose **Hyperliquid Mainnet · LIVE**, select the account and perpetual market, then enter your parameters. New installations do not create sample strategies, and there is no preset button. Existing saved strategies remain available.
6. Save the strategy, open its dashboard, review your settings and center price, then click **Start**. Start generates a fresh preview and submits the initial orders. Saving a strategy does not place orders.

## Strategy behavior

Mainnet uses your configured grid spacing, levels, quantities, size growth, maximum quantities and basket thresholds. Set optional basket thresholds or the per-order quantity cap to zero to disable them. `MaxNetLot` remains a required strategy parameter. The shared grid engine supports up to 200 levels per side and maintains one working entry per side, advancing after fills. These engine rules apply across environments.

Exchange minimum notionals and quantity/price precision still apply. The account must have valid credentials and funds. Manual positions and orders, including on the selected symbol, are allowed. The cycle owns only fills from its database-recorded orders. Each Mainnet account can run one active strategy per symbol; strategies on different symbols may run together. Symbol aliases such as `SOL` and `SOL-USDC` share the same reservation, which follows the running cycle's frozen market even if its saved strategy is edited. Fresh quotes, acknowledged cancellations and complete position snapshots remain execution requirements.

## Pause, close and restart

**Pause Entry** cancels entries while maintaining take profits. **Exit** and **Emergency Stop** cancel only the cycle’s orders, reconcile cancellation-race fills, and offset its confirmed strategy quantity with an ordinary IOC order. Manual orders remain untouched. The cycle completes when its execution ledger is flat and all its orders are resolved; the account position need not be flat. An uncertain exit is reconciled before another exit can be sent. Partial exits retry only the confirmed remainder.

For example, manual +10 SOL and strategy −3 SOL produce an account position of +7 SOL. Closing the strategy buys 3 SOL and leaves +10 SOL. These exits can increase or reverse the account position. The database separates accounting; collateral, margin availability and liquidation remain shared. A manual trade never erases the strategy’s recorded quantity.

Your configured basket thresholds use strategy execution cashflows and the executable value of the strategy remainder, minus its fees and estimated exit costs. Funding and manual-trade PnL are excluded. The dashboard shows strategy, account, and external/unattributed quantities separately.

Existing nonterminal cycles resume reconciliation after a backend restart. Before order maintenance resumes, their saved orders, fills, fees and virtual lots must reconcile. Existing pauses and faults remain in effect. Keep the same database; manual positions are never imported into a cycle. Missing executions or unresolved submissions display `RECOVERY_REQUIRED` and block dependent actions. Exchange history is limited, so a lost database or a history gap cannot be reconstructed from the account’s net position. Starting the service does not create a new cycle. Keep the service running while it manages a cycle.

## Implementation checks

Network endpoints and signing are separated between Mainnet and Testnet. Credentials are submitted once through the local settings form and encrypted at rest; reads never return private keys or ciphertext. No transfer or withdrawal action is implemented. Tests use isolated databases and synthetic exchange responses; they do not place live orders.
