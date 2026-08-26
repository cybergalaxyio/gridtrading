# Strategy and execution architecture

## Responsibilities

```text
StrategyDefinition (StrategyType = GRID)
        |
        | default selection, overridable during preview
        v
ExecutionSelection (EnvironmentId + AccountId)
        |
        | frozen on Cycle before the first order
        v
IExecutionAdapter
  - paper-local
  - hyperliquid-testnet
```

A strategy describes how to trade. An execution environment describes the venue and network. An execution account is an identity inside that environment. A cycle is the immutable binding of one strategy version to one environment/account selection.

`TradingService` is the API application facade. It dispatches the current `GRID` type to `GridStrategyWorkflow`. The workflow owns preview, start, operator commands and cycle state. `GridOrderLifecycle` owns normalized fills, take-profits, virtual lots, moving entries and reconciliation. Adapters own only venue behavior: instrument metadata, quote/preflight, placement, cancellation, position/flattening and conversion to normalized events.

The environment registry is configuration-backed rather than persisted. It currently advertises:

| Environment ID | Venue | Network | Account source |
| --- | --- | --- | --- |
| `paper-local` | PAPER | LOCAL | built-in `acct_paper_01` |
| `hyperliquid-testnet` | HYPERLIQUID | TESTNET | credentials bootstrapped from environment variables |

The account list is intentionally plural even though each environment currently has one account.

## Preview and cycle lifecycle

1. Select a GRID strategy.
2. Use its default environment/account or supply a preview override.
3. Resolve and validate that the account belongs to the environment.
4. Load tick size, quantity step, minimum quantity/notional and fees from that adapter.
5. Build and cache the preview with the resolved execution selection.
6. Start only from that preview. The Cycle persists the same environment/account before orders are created.
7. Route REST reconciliation, WebSocket events and Paper matches to normalized fills/order updates.
8. Apply fills idempotently, create one TP and Virtual Lot per fill fragment, then maintain working entries.
9. On close, resolve the adapter from the Cycle binding, cancel orders, flatten, reconcile and mark terminal.

A running Cycle never reads the Strategy's current default environment/account. Changing a Strategy default therefore affects only future previews and Cycles.

## GRID invariants

- Only a confirmed `NEW` Entry with zero filled quantity may move with the current mid price.
- A partially filled, pending or unknown Entry stays in place until its fill/cancel state resolves.
- Every Entry fill fragment creates its own TP and Virtual Lot.
- A level is occupied while any Virtual Lot from that Entry level remains open. For example, three S7 TP fragments collectively occupy S7; no new S7 Entry is allowed until all three close, while S8 or another valid level can still be selected.
- Duplicate or late fills are keyed by normalized execution ID and cannot create duplicate TP orders.
- Paper and Hyperliquid use these same rules. Their adapters differ only in execution and market simulation details.

## Extension points

To add an exchange/network, implement `IExecutionAdapter`, register a stable environment descriptor and expose its accounts. No GRID rule should be added to the adapter.

To add a strategy type, create a separate strategy workflow and pure domain rules, then dispatch it from the application facade using persisted `StrategyType`. It must consume the same execution selection and adapter contracts rather than importing a venue client.
