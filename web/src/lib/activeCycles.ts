import type { Cycle } from '../types'

export type CycleSelection = {
  environmentId: string
  accountId: string
  symbol: string
  cycleId: string | null
}
export type ActiveCycleRegistry = Map<string, Cycle[]>

export function normalizeSymbol(symbol: string) {
  return symbol.trim().toUpperCase().replace(/(USDC|USDT)$/, '').replace(/[-_/]+$/, '')
}

export function cycleSymbol(cycle: Cycle) {
  return cycle.frozenConfiguration?.symbol ?? cycle.symbol ?? ''
}

export function marketKey(environmentId: string, accountId: string, symbol: string) {
  return JSON.stringify([environmentId, accountId, normalizeSymbol(symbol)])
}

export function buildActiveCycleRegistry(cycles: Cycle[]): ActiveCycleRegistry {
  const registry: ActiveCycleRegistry = new Map()
  for (const cycle of cycles) {
    if (cycle.isTerminal) continue
    const key = marketKey(cycle.executionEnvironmentId, cycle.executionAccountId, cycleSymbol(cycle))
    const group = registry.get(key) ?? []
    group.push(cycle)
    registry.set(key, group)
  }
  return registry
}

export function marketCycles(registry: ActiveCycleRegistry, selection: CycleSelection) {
  return registry.get(marketKey(selection.environmentId, selection.accountId, selection.symbol)) ?? []
}

export function selectedCycle(registry: ActiveCycleRegistry, selection: CycleSelection) {
  const cycles = marketCycles(registry, selection)
  // Legacy duplicates require an explicit choice; never silently pick the first cycle.
  return cycles.find(cycle => cycle.cycleId === selection.cycleId) ?? (cycles.length === 1 ? cycles[0] : null)
}
