"""Offline audit of the captured cycle. Reads evidence.json; never contacts the service.

Exit 1 means the captured trading state fails an invariant, not that the script failed.
All quantities and monetary computations use Decimal. No third-party packages needed.
"""
import collections
import datetime
import hashlib
import json
from decimal import Decimal as D, ROUND_FLOOR, ROUND_CEILING
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DATA = json.loads((ROOT / 'evidence.json').read_text())
local, exchange = DATA['local'], DATA['exchange']
orders, lots, executions = local['Orders'], local['VirtualLots'], local['Executions']
cycle = local['cycle']
config = json.loads(cycle['FrozenConfigurationJson'])
plan = json.loads(cycle['FrozenPlanJson'])
history, fills = exchange['historicalOrders']['data'], exchange['userFillsByTime']['data']
by_id = {o['Id']: o for o in orders}
by_order = collections.defaultdict(list)
for execution in executions:
    by_order[execution['OrderId']].append(execution)


def timestamp(value):
    return int(datetime.datetime.fromisoformat(value).timestamp() * 1000)


def cloid(order):
    return '0x' + hashlib.sha256(order['ClientOrderId'].encode()).hexdigest()[:32]


def total(values):
    return sum(values, D(0))


checks = []

def check(name, passed, detail):
    checks.append({'check': name, 'pass': bool(passed), 'detail': detail})


# Verify the frozen plan independently of its stored level values.
plan_errors = []
tick, step = D(config['tickSize']), D(config['quantityStep'])
gap = (D(config['initialGapPoints']) or D(config['gridSpacingPoints']) / 2) * tick
for level in plan['levels']:
    index, side = level['levelIndex'], level['side']
    distance = gap + total((D(config['gridSpacingPoints']) + i * D(config['gridSpacingStepPoints'])) * tick for i in range(1, index + 1))
    price = D(config['centerPrice']) + distance * (1 if side == 'SELL' else -1)
    price = (price / tick).to_integral_value(rounding=ROUND_CEILING if side == 'SELL' else ROUND_FLOOR) * tick
    quantity = D(config['baseLotSize']) * (1 + D(config['lotSizeIncreasePercent']) / 100) ** index
    if D(config['maxTradeLot']) > 0:
        quantity = min(quantity, D(config['maxTradeLot']))
    quantity = (quantity / step).to_integral_value(rounding=ROUND_FLOOR) * step
    if price != D(level['entryPrice']) or quantity != D(level['plannedQuantity']):
        plan_errors.append((side, index))
check('frozen_grid_plan', not plan_errors, {'levels': len(plan['levels']), 'errors': plan_errors})
entries = [o for o in orders if o['Kind'] == 'ENTRY']
entry_errors = [o['Id'] for o in entries if not any(
    l['side'] == o['Side'] and l['levelIndex'] == o['GridLevel'] and
    D(l['entryPrice']) == D(o['Price']) and D(l['plannedQuantity']) == D(o['Quantity'])
    for l in plan['levels'])]
check('entry_prices_and_quantities', not entry_errors, {'orders': len(entries), 'errors': entry_errors})
check('orders_found_at_venue', all(any(h['order'].get('cloid') == cloid(o) for h in history) for o in orders), len(orders))

remote_fills = {f"hl:{f['hash']}:{f['oid']}:{f['time']}:{f['tid']}": f for f in fills}
fill_errors = []
for e in executions:
    f = remote_fills.get(e['ExchangeExecutionId'])
    if not f or any(D(e[a]) != D(f[b]) for a, b in [('Price', 'px'), ('Quantity', 'sz'), ('Fee', 'fee')]) or (e['Side'] == 'BUY') != (f['side'] == 'B'):
        fill_errors.append(e['Id'])
check('execution_identity_and_values', not fill_errors and len(remote_fills) == len(executions) == len(fills), {'count': len(executions), 'errors': fill_errors})

quantity_errors = []
for order in orders:
    executed = total(D(e['Quantity']) for e in by_order[order['Id']])
    if executed != D(order['FilledQuantity']):
        quantity_errors.append({'orderId': order['Id'], 'exchangeOrderId': order['ExchangeOrderId'], 'kind': order['Kind'], 'level': order['GridLevel'], 'localQuantity': order['Quantity'], 'localFilled': order['FilledQuantity'], 'actualFilled': executed})
check('persisted_filled_quantities', not quantity_errors, quantity_errors)

lot_errors, tp_errors, overlap = [], [], []
for lot in lots:
    entry_fills, tp_fills = by_order[lot['EntryOrderId']], by_order[lot['TakeProfitOrderId']]
    entry_qty = total(D(e['Quantity']) for e in entry_fills)
    exit_qty = total(D(e['Quantity']) for e in tp_fills)
    if entry_qty != D(lot['FilledQuantity']) or entry_qty - exit_qty != D(lot['RemainingQuantity']):
        lot_errors.append(lot['Id'])
    vwap = total(D(e['Price']) * D(e['Quantity']) for e in entry_fills) / entry_qty
    expected_tp = vwap + D(config['takeProfitPoints']) * tick * (1 if lot['Side'] == 'BUY' else -1)
    expected_tp = (expected_tp / tick).to_integral_value(rounding=ROUND_CEILING if lot['Side'] == 'BUY' else ROUND_FLOOR) * tick
    tp = by_id.get(lot['TakeProfitOrderId'])
    if D(lot['TakeProfitPrice']) != expected_tp or (tp and (D(tp['Price']) != expected_tp or tp['Side'] == lot['Side'])):
        tp_errors.append(lot['Id'])
    if any((D(e['Price']) < expected_tp if lot['Side'] == 'BUY' else D(e['Price']) > expected_tp) for e in tp_fills):
        tp_errors.append(lot['Id'])
    opened = min(timestamp(e['OccurredAt']) for e in entry_fills)
    closed = max((timestamp(e['OccurredAt']) for e in tp_fills), default=10**16) if entry_qty == exit_qty else 10**16
    overlap.extend(o['Id'] for o in entries if o['Id'] != lot['EntryOrderId'] and o['Side'] == lot['Side'] and o['GridLevel'] == lot['GridLevel'] and opened < timestamp(o['CreatedAt']) < closed)
check('lot_quantity_conservation', not lot_errors, {'lots': len(lots), 'errors': lot_errors})
check('tp_price_side_and_fill_limits', not tp_errors, {'tpOrders': sum(o['Kind'] == 'TAKE_PROFIT' for o in orders), 'errors': tp_errors})
check('no_reentry_while_lot_open', not overlap, overlap)

# Venue event replay: amendment generations use distinct OIDs and a stable CLOID.
events = collections.defaultdict(list)
for h in history:
    events[h['statusTimestamp']].append(('order', h))
for f in fills:
    events[f['time']].append(('fill', f))
active, filled = {}, collections.defaultdict(lambda: D(0))
net, max_net, max_buy, max_sell = D(0), D(0), D(0), D(0)
max_entries = {'B': 0, 'A': 0}
order_by_cloid = {cloid(o): o for o in orders}
for time, changes in sorted(events.items()):
    for kind, v in changes:
        if kind == 'fill':
            net += D(v['sz']) * (1 if v['side'] == 'B' else -1)
            filled[v['oid']] += D(v['sz'])
            max_net = max(max_net, abs(net))
    for kind, v in sorted(changes, key=lambda x: 0 if x[0] == 'order' and x[1]['status'] == 'open' else 1):
        if kind == 'order':
            if v['status'] == 'open':
                active[v['order']['oid']] = v['order']
            else:
                active.pop(v['order']['oid'], None)
    buy = total(max(D(o['origSz']) - filled[oid], D(0)) for oid, o in active.items() if o['side'] == 'B')
    sell = total(max(D(o['origSz']) - filled[oid], D(0)) for oid, o in active.items() if o['side'] == 'A')
    max_buy, max_sell = max(max_buy, net + buy), max(max_sell, sell - net)
    for side in max_entries:
        max_entries[side] = max(max_entries[side], len({o['cloid'] for o in active.values() if o['side'] == side and order_by_cloid[o['cloid']]['Kind'] == 'ENTRY'}))
check('one_working_entry_per_side', max(max_entries.values()) <= 1, max_entries)
check('worst_case_net_capacity', max(max_buy, max_sell, max_net) <= D(config['maxNetLot']), {'maxNet': max_net, 'maxBuyScenario': max_buy, 'maxSellScenario': max_sell, 'cap': config['maxNetLot']})
position = total(D(p['szi']) for p in exchange['position']['positions'])
check('final_position_reconciles', net == position == D(cycle['ActualNetQuantity']) == D(cycle['ReconstructedNetQuantity']), {'fills': net, 'venue': position, 'local': cycle['ActualNetQuantity']})

fees = total(D(e['Fee']) for e in executions)
pnl = total((D(e['Price']) - D(l['EntryFillPrice'])) * D(e['Quantity']) * (1 if l['Side'] == 'BUY' else -1) for l in lots for e in by_order[l['TakeProfitOrderId']])
check('cycle_fee_aggregate', fees == D(cycle['PaidFees']), {'recomputed': fees, 'stored': cycle['PaidFees'], 'understated': fees - D(cycle['PaidFees'])})
check('cycle_grid_profit_aggregate', pnl == D(cycle['RealisedCyclePnl']), {'recomputed': pnl, 'stored': cycle['RealisedCyclePnl'], 'understated': pnl - D(cycle['RealisedCyclePnl'])})
funding = total(D(f['FundingCost']) for f in local['FundingPayments'])
check('local_funding_aggregate', funding == D(cycle['AccruedFunding']), {'recomputed': funding, 'stored': cycle['AccruedFunding']})

fault = next(a for a in local['RiskAlerts'] if a['Severity'] == 'CRITICAL')
fault_time = timestamp(fault['CreatedAt'])
fault_parts = []
for lot in lots:
    entry_qty = total(D(e['Quantity']) for e in by_order[lot['EntryOrderId']] if timestamp(e['OccurredAt']) <= fault_time)
    exit_qty = total(D(e['Quantity']) for e in by_order[lot['TakeProfitOrderId']] if timestamp(e['OccurredAt']) <= fault_time)
    remaining = entry_qty - exit_qty
    if remaining <= 0:
        continue
    tp = by_id.get(lot['TakeProfitOrderId'])
    revisions = [h for h in history if tp and h['order'].get('cloid') == cloid(tp) and h['statusTimestamp'] <= fault_time]
    latest = max(revisions, key=lambda h: (h['statusTimestamp'], h['order']['timestamp'])) if revisions else None
    actual_protection = max(D(latest['order']['origSz']) - exit_qty, D(0)) if latest and latest['status'] == 'open' else D(0)
    local_protection = max(D(tp['Quantity']) - exit_qty, D(0)) if tp else D(0)
    fault_parts.append({'level': lot['GridLevel'], 'remaining': remaining, 'localProtectionFromStoredQuantity': local_protection, 'venueProtection': actual_protection, 'localImpliedExposure': max(remaining - local_protection, D(0)) * D(lot['TakeProfitPrice']), 'venueUnprotectedExposure': max(remaining - actual_protection, D(0)) * D(lot['TakeProfitPrice'])})
actual = total(p['venueUnprotectedExposure'] for p in fault_parts)
implied = total(p['localImpliedExposure'] for p in fault_parts)
check('fault_threshold_justified_by_venue', actual > D(config['faultExposureThresholdUsdt']), {'at': fault['CreatedAt'], 'venueUnprotected': actual, 'localImplied': implied, 'threshold': config['faultExposureThresholdUsdt'], 'components': fault_parts})
check('no_new_entries_after_fault', not any(timestamp(o['CreatedAt']) > fault_time for o in entries), {'lastEntryCreatedAt': max(o['CreatedAt'] for o in entries)})
check('fault_reconciliation_fresh_at_capture', timestamp(local['capturedAt']) - timestamp(cycle['LastReconciledAt']) <= max(60, int(config['reconcileIntervalSeconds']) * 3) * 1000, {'lastReconciledAt': cycle['LastReconciledAt'], 'capturedAt': local['capturedAt']})

summary = {'cycleId': cycle['Id'], 'capturedAt': local['capturedAt'], 'passed': sum(c['pass'] for c in checks), 'failed': sum(not c['pass'] for c in checks), 'checks': checks}
print(json.dumps(summary, ensure_ascii=False, indent=2, default=str))
raise SystemExit(1 if summary['failed'] else 0)
