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
8. Apply fills idempotently, create one TP and Virtual Lot per Entry, atomically amend that TP for later fill fragments, then maintain working entries.
9. On close, resolve the adapter from the Cycle binding, cancel orders, flatten, reconcile and mark terminal.

A running Cycle never reads the Strategy's current default environment/account. Changing a Strategy default therefore affects only future previews and Cycles.

## GRID invariants

- Only a confirmed `NEW` Entry with zero filled quantity may move with the current mid price.
- A partially filled, pending or unknown Entry stays in place until its fill/cancel state resolves.
- The first Entry fill creates its TP and Virtual Lot. Later fragments update the same lot and atomically amend the same TP, so a below-minimum tail fragment is combined with already protected quantity.
- Once a TP starts filling, any unfilled remainder of its partially filled Entry is cancelled before the next working Entry is selected. This prevents a late tail fill from requiring a new below-minimum TP and lets the side start its next round.
- If TP protection cannot be established, the Cycle sums its currently unprotected position value in USD. At or below `FaultExposureThresholdUsdt` it emits a warning and keeps its state; only exposure above the threshold enters `FAULT` and cancels active Entries.
- `FaultExposureThresholdUsdt` defaults to 10 USD for legacy and newly created strategies; setting it to zero restores the previous any-positive-exposure FAULT behavior.
- A level is occupied while its Virtual Lot remains open; no new Entry is allowed at that level until the TP closes, while another valid level can still be selected.
- Duplicate or late fills are keyed by normalized execution ID and cannot create duplicate TP orders.
- Paper and Hyperliquid use these same rules. Adapters that can receive fragmented fills must implement atomic order amendment.

## Protective-order acknowledgement and recovery

Hyperliquid amendments use a single-item `batchModify`, matching the official Python SDK's request shape. The signed MessagePack structure and JSON payload contain the same action. Unexpected success envelopes are treated as unconfirmed, preserving the last confirmed order quantity until reconciliation.

Each entry fill commits the execution, lot quantities/target price, and `VirtualLot.ProtectionPending` together before sending a protective order. Reconciliation processes this durable intent independently of execution deduplication. An accepted amendment with a lost response is matched by CLOID and confirmed from the venue; an unchanged confirmed order can be amended again. Unknown submissions are never treated as fresh pending placements. While a TP intent is unresolved, new working entries are not created.

Order reconciliation reads price, original size, remaining size, status, and OID, and resolves amendment generations through the stable CLOID. Logical order quantity includes executions from earlier OIDs plus the current generation's original size. Filled quantity is rebuilt from executions, avoiding both clipping against stale order metadata and counting IOC acknowledgements twice. Old-generation order updates cannot roll the current OID back. Exchange event timestamps are stored separately from local save timestamps.

Nonterminal FAULT cycles continue read-only REST reconciliation. FAULT/CLOSING fill processing records executions and lot changes without submitting, amending, or cancelling orders. Reconciliation reloads Cycle state under the account gate before updating aggregates. Schema upgrades add recovery metadata without replacing existing records.

## Extension points

To add an exchange/network, implement `IExecutionAdapter` and `IOrderAmendmentAdapter`, register a stable environment descriptor and expose its accounts. No GRID rule should be added to the adapter.

To add a strategy type, create a separate strategy workflow and pure domain rules, then dispatch it from the application facade using persisted `StrategyType`. It must consume the same execution selection and adapter contracts rather than importing a venue client.
