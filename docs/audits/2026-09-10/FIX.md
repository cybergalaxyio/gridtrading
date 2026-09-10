# Amendment fix and deployment — 2026-09-10

Implemented and deployed to the local service on port 5050. The previous audit files remain immutable observations of the pre-fix state.

- Use one-item `batchModify` with matching JSON and MessagePack signing; do not assume an unexpected success envelope contains `data.statuses`.
- Commit pending TP protection with the fill/lot before exchange I/O; reconcile it independently of fill deduplication after a restart or an uncertain response.
- Reconcile actual order price/quantity and amendment OID generations. Preserve real execution quantities and prevent double-counting an IOC acknowledgement when its execution arrives.
- Ignore late updates from previous OIDs, and distinguish exchange timestamps from local save timestamps.
- Continue read-only reconciliation for nonterminal FAULT cycles. Reload Cycle under the account gate and rebuild fees from actual executions.
- Query an earlier fill window when protection is unresolved and paginate full fill pages with overlap rather than silently advancing past missing fills.

Validation: **65 API tests + 42 domain tests passed**; backend build completed with **0 warnings / 0 errors**. Real-adapter tests use synthetic HTTP responses and isolated SQLite, covering confirmed amendments, unexpected/default ACK, timeout before/after acceptance, recovery after restart, duplicate fills, partial TP execution, explicit rejection, late OID events, IOC fill accounting, read-only FAULT behavior, and the historical false-exposure scenario. Schema compatibility tests preserve pending work across repeated upgrades. No test order was sent to the exchange.

## Operator close and historical repair

During implementation, an operator closed cycle `cycle_9030f8e838b8417587f662ed9d926cb4` at **09:30:40 UTC**. The guarded repair detected the changed state and refused the original pre-close evidence. Fresh exchange data was captured in `post-close-evidence.json`: **86 local orders, 83 executions matching 83 exchange fills, no open venue orders, zero SOL position**.

The repair was first applied to a private database copy and rerun to verify it was a no-op. It was then applied to the service database with a mode-0600 SQLite backup and a `TP_LEDGER_REPAIRED` audit record. See `repair-result.json` for all eight before/after corrections and the backup path. Cycle state, execution records, and actual position were preserved.

| Closed-cycle metric | Corrected value |
|---|---:|
| Gross realised PnL, all execution cashflows | 33.3068 |
| Fees | 0.503474 |
| Funding cost | 0.233637 |
| Net realised result | 32.569689 |
| Actual / reconstructed position | 0 / 0 SOL |
| State | WAITING_FOR_OPERATOR, terminal |

The earlier 33.3194 figure was TP-only attribution before flattening. The operator sold the remaining 0.03 SOL at 100.74 against its 101.16 entry: the additional loss is 0.0126, producing final gross cashflow of 33.3068. The final flatten also added 0.001359 in fees.

At **09:44 UTC**, the restarted API reported healthy, returned these corrected PnL/position values, and served its current JS/CSS assets with HTTP 200. The three additive schema columns were present. A repeat repair preview returned no remaining changes. The cycle remains closed; deployment did not start or resume trading.

## Repair utility

`scripts/repair_audited_tp_ledger.py` is dry-run by default and validates captured venue fills, unchanged execution identity/amounts, lot conservation, eligible cycle state, and each closed TP's venue history. It only supports the uniform-entry-price lots in this audit; other cases fail closed for review. At zero net position it derives final realised PnL from all execution cashflows, including flattening. Applying changes requires `--apply`, creates a private backup, and writes an audit record. It never calls the exchange.
