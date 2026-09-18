# Manage multiple trading accounts

Use **Settings → Exchange Accounts** to manage Hyperliquid Mainnet and Testnet accounts in the local portal. Multiple enabled accounts can run strategies concurrently, including the same market on different accounts. Switching the displayed account does not stop its strategies.

## First-time setup

1. Configure a stable server encryption key, `GRID_TRADING_CREDENTIAL_KEY`, containing a base64-encoded 32-byte value. Generate a new one with `openssl rand -base64 32`. Keep an existing key unchanged: it also protects saved Telegram tokens. Keep a backup of the key separately from the SQLite database.
2. Start the backend normally, or use `./run-mainnet.sh` or `./run-testnet.sh`. These scripts accept the encryption key from the process environment or the existing `.env.mainnet` / `.env` file. Wallet address and private-key environment variables are optional.
3. On Hyperliquid, authorize a separate API wallet for each account and network. Configure leverage on the exchange. The portal does not authorize wallets, change leverage, transfer funds, or withdraw funds.
4. In **Settings → Exchange Accounts**, choose **Add account**, enter a name, network, main account public address, and the approved **API wallet** private key. Never enter the main wallet private key.
5. Click **Save**. The account is stored disabled. The backend derives the API wallet address and encrypts the key; the browser clears the input after submission.
6. Use **Test connection** to check authorization and balances without enabling the account. **Test and enable** checks stored credentials and agent authorization before enabling. An unfunded account may be enabled, but cannot start a strategy until the normal trading readiness checks pass.
7. Select the account and network when creating or running a strategy. Saving, testing, and enabling accounts never place orders. Trading begins only with the strategy's **Start** action.

## Changes and history

- Rename accounts at any time. Network and main account address are immutable; add a different account instead of repointing an existing account's history.
- Replace an API wallet key or disable an account only after all its cycles have finished and pending automatic restarts have been turned off. Settings lists blocking strategies. Paused, closing, and faulted cycles still count as active.
- Replacing a key disables the account until **Test and enable** succeeds again. Authorize the replacement API wallet on the exchange first.
- Disabling preserves strategies, cycles, orders, fills, and credentials. There is no permanent account deletion in this version.
- Duplicate main account addresses on the same network are rejected, even if a different API wallet is supplied. An API wallet cannot be reused across configured accounts or networks.
- Account changes are discovered by background workers on a two-second interval; no backend restart is required. Each account reconnects and reconciles independently.
- The portal supports main accounts. This version does not add new vault/subaccount setup. Existing legacy vault configuration is retained.

## Existing installations

Existing account IDs, encrypted credentials, nonces, and trading history remain intact. Environment variables can still import a missing legacy account once; they no longer overwrite or re-enable an account already in the database. After verifying the portal account, old wallet credential variables can be removed from the local environment file. Keep `GRID_TRADING_CREDENTIAL_KEY`.

The existing Mainnet script keeps its database at `data/grid-trading-mainnet.db` and port 5051; Testnet keeps its existing database and port 5050. Each portal can manage both networks. Existing databases are not merged automatically. Run one backend process per database, and do not configure the same API wallet in another running instance.

The compatibility upgrade adds account-scoped execution deduplication, preserving existing exchange execution IDs and backfilling account ownership from cycles. Back up the database and encryption key before upgrading an existing installation.

## Local security boundary

This remains a single-operator local portal without remote login. Account mutations require a loopback connection, a trusted loopback Host and Origin, and JSON requests. The same-origin portal and the existing local Vite development origins are accepted. Do not expose it through a remote reverse proxy.

Keys travel from the password field to the local backend when saved, are encrypted with AES-256-GCM, and are never returned by account reads. Neither browser storage nor account audit records contain keys or ciphertext. A missing or incorrect encryption key produces a setup/verification error; existing ciphertext is not silently replaced.
