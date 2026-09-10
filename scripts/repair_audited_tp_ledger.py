#!/usr/bin/env python3
"""Repair only the closed TP records and aggregates verified by a captured audit.

Dry-run by default. Requires an unchanged execution set, a FAULT or closed
cycle, and uniform entry prices per lot. Never sends exchange requests, changes
cycle state, modifies executions, or infers fills from order quantities.
"""
import argparse
import datetime
import hashlib
import json
import os
import sqlite3
from collections import defaultdict
from decimal import Decimal as D
from pathlib import Path


def rows(db, table, cycle):
    return [dict(r) for r in db.execute(f'SELECT * FROM "{table}" WHERE CycleId=?', (cycle,))]


def prepare(db, evidence):
    local = evidence['local']
    cid = local['cycle']['Id']
    cycle = dict(db.execute('SELECT * FROM Cycles WHERE Id=?', (cid,)).fetchone())
    closed = cycle['State'] == 'WAITING_FOR_OPERATOR' and cycle['IsTerminal']
    if not closed and not (cycle['State'] == 'FAULT' and not cycle['IsTerminal']):
        raise ValueError('Repair requires the audited cycle to remain FAULT or terminal WAITING_FOR_OPERATOR.')
    if cycle['State'] != local['cycle']['State'] or cycle['IsTerminal'] != local['cycle']['IsTerminal']:
        raise ValueError('Cycle state changed since evidence capture; take a new snapshot.')
    executions = rows(db, 'Executions', cid)
    remote = {f"hl:{f['hash']}:{f['oid']}:{f['time']}:{f['tid']}": f for f in evidence['exchange']['userFillsByTime']['data']}
    if len(executions) != len(remote) or {e['ExchangeExecutionId'] for e in executions} != remote.keys():
        raise ValueError('Execution set changed or is incomplete; capture a new audit before repair.')
    by_order = defaultdict(list)
    for e in executions:
        f = remote[e['ExchangeExecutionId']]
        if any(D(e[a]) != D(f[b]) for a, b in [('Price', 'px'), ('Quantity', 'sz'), ('Fee', 'fee')]) or (e['Side'] == 'BUY') != (f['side'] == 'B'):
            raise ValueError('Local execution differs from captured exchange execution.')
        by_order[e['OrderId']].append(e)
    net = sum((D(e['Quantity']) * (1 if e['Side'] == 'BUY' else -1) for e in executions), D(0))
    if net != D(cycle['ActualNetQuantity']) or net != D(cycle['ReconstructedNetQuantity']):
        raise ValueError('Position differs from the audited ledger; reconcile before repair.')
    lots = rows(db, 'VirtualLots', cid)
    pnl = D(0)
    for lot in lots:
        entry_fills = by_order[lot['EntryOrderId']]
        if not entry_fills or any(D(e['Price']) != D(lot['EntryFillPrice']) for e in entry_fills):
            raise ValueError('This bounded repair requires uniform entry prices; manual allocation review needed.')
        entered = sum((D(e['Quantity']) for e in entry_fills), D(0))
        exited = sum((D(e['Quantity']) for e in by_order[lot['TakeProfitOrderId']]), D(0))
        if entered != D(lot['FilledQuantity']) or entered - exited != D(lot['RemainingQuantity']):
            raise ValueError('Lot quantities do not conserve actual executions.')
        pnl += sum(((D(e['Price']) - D(lot['EntryFillPrice'])) * D(e['Quantity']) * (1 if lot['Side'] == 'BUY' else -1)
                    for e in by_order[lot['TakeProfitOrderId']]), D(0))
    changes = []
    for order in rows(db, 'Orders', cid):
        quantity = sum((D(e['Quantity']) for e in by_order[order['Id']]), D(0))
        if quantity == D(order['FilledQuantity']):
            continue
        if order['Kind'] != 'TAKE_PROFIT' or order['Status'] != 'FILLED':
            raise ValueError('Unexpected nonterminal/entry quantity mismatch; refusing automatic repair.')
        venue = [h for h in evidence['exchange']['historicalOrders']['data']
                 if str(h['order']['oid']) == order['ExchangeOrderId'] and h['status'] == 'filled']
        if not venue or D(venue[0]['order']['origSz']) != quantity:
            raise ValueError('Closed TP quantity is not confirmed by the captured venue history.')
        changes.append({'orderId': order['Id'], 'exchangeOrderId': order['ExchangeOrderId'],
                        'beforeQuantity': order['Quantity'], 'beforeFilled': order['FilledQuantity'], 'after': str(quantity)})
    if closed:
        if net != 0:
            raise ValueError('Terminal-cycle repair requires zero reconstructed position.')
        # At zero position, all execution cashflows (including the final flatten)
        # give authoritative realised PnL without mark-price or lot allocation assumptions.
        pnl = sum((D(e['Price']) * D(e['Quantity']) * (1 if e['Side'] == 'SELL' else -1) for e in executions), D(0))
    totals = {'RealisedCyclePnl': pnl, 'PaidFees': sum((D(e['Fee']) for e in executions), D(0)),
              'AccruedFunding': sum((D(f['FundingCost']) for f in rows(db, 'FundingPayments', cid)), D(0))}
    totals = {key: {'before': cycle[key], 'after': str(value)} for key, value in totals.items() if D(cycle[key]) != value}
    return {'cycleId': cid, 'orders': changes, 'totals': totals, 'statePreserved': cycle['State'], 'positionPreserved': str(net),
            'pnlBasis': 'net-flat cycle execution cashflow' if closed else 'closed TP attribution'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    evidence_bytes = args.evidence.read_bytes()
    evidence = json.loads(evidence_bytes)
    path = args.database.resolve(strict=True)
    db = sqlite3.connect(path.as_uri() + ('?mode=rw' if args.apply else '?mode=ro'), uri=True, timeout=20)
    db.row_factory = sqlite3.Row
    plan = prepare(db, evidence)
    if args.apply and (plan['orders'] or plan['totals']):
        backup = path.with_name(path.stem + '-before-tp-repair-' + datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%f') + '.db')
        fd = os.open(backup, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.close(fd)
        with sqlite3.connect(backup) as target:
            db.backup(target)
        db.execute('BEGIN IMMEDIATE')
        try:
            plan = prepare(db, evidence)  # Revalidate under the write lock.
            for change in plan['orders']:
                db.execute('UPDATE Orders SET Quantity=?, FilledQuantity=? WHERE Id=?',
                           (change['after'], change['after'], change['orderId']))
            for key, change in plan['totals'].items():
                db.execute(f'UPDATE Cycles SET "{key}"=? WHERE Id=?', (change['after'], plan['cycleId']))
            plan['evidenceSha256'] = hashlib.sha256(evidence_bytes).hexdigest()
            db.execute('INSERT INTO AuditLogs(ResourceId,Action,Actor,Detail,OccurredAt) VALUES(?,?,?,?,?)',
                       (plan['cycleId'], 'TP_LEDGER_REPAIRED', 'local-maintenance', json.dumps(plan),
                        datetime.datetime.now(datetime.timezone.utc).isoformat()))
            db.commit()
        except BaseException:
            db.rollback()
            raise
        plan['backup'] = str(backup)
        plan['applied'] = True
    else:
        plan['applied'] = False
    print(json.dumps(plan, indent=2))
    db.close()


if __name__ == '__main__':
    main()
