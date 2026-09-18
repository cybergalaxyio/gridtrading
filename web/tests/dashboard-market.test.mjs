import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import { renderToStaticMarkup } from 'react-dom/server'

const require = createRequire(import.meta.url)
const compiled = ts.transpileModule(readFileSync(new URL('../src/pages/HyperliquidDashboardPage.tsx', import.meta.url), 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
}).outputText
const registryModule = { exports: {} }
vm.runInNewContext(ts.transpileModule(readFileSync(new URL('../src/lib/activeCycles.ts', import.meta.url), 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
}).outputText, { module: registryModule, exports: registryModule.exports })
const { buildActiveCycleRegistry } = registryModule.exports
const environment = 'hyperliquid-testnet'
const account = 'test-account'
function strategy(symbol, state = 'RUNNING', id = symbol) {
  return {
    strategyId: id, name: id, symbol, version: 1, archived: false,
    defaultExecutionEnvironmentId: environment, defaultExecutionAccountId: account,
    configuration: { maxLevelsPerSide: 4, maxNetLot: '10', gridMode: 'TWO_WAY' },
    activeCycle: state ? {
      cycleId: `${id}-cycle`, strategyId: id, symbol, state, stateVersion: 1, isTerminal: false,
      executionEnvironmentId: environment, executionAccountId: account,
      startedAt: symbol === 'ETH' ? '2026-09-17T01:00:00Z' : '2026-09-17T02:00:00Z',
    } : null,
  }
}
const eth = strategy('ETH')
const sol = strategy('SOL', 'PAUSED')
const snapshot = {
  cycle: { ...eth.activeCycle, fixedCenterPrice: '12345', stateVersion: 2 },
  market: { mid: '12345' }, position: {}, basketPnl: { realisedCyclePnl: '9876' },
  orders: { activeEntryCount: 7, activeTakeProfitCount: 8 }, risk: {},
}

// Run the real dashboard with deterministic hook state, mocked I/O and inert timers.
// Effects and user callbacks are exercised without connecting to a trading account.
function dashboard(strategies = [eth, sol], symbol = 'ETH', loadedStrategyId = null, responses = {}) {
  let cursor = 0, effects = [], tree, html
  const hooks = [], calls = [], children = new Map()
  const useState = initial => {
    const index = cursor++
    if (!(index in hooks)) hooks[index] = typeof initial === 'function' ? initial() : initial
    return [hooks[index], value => { hooks[index] = typeof value === 'function' ? value(hooks[index]) : value }]
  }
  const useMemo = (callback, deps) => {
    const index = cursor++, previous = hooks[index]
    if (!previous || deps.some((dep, i) => !Object.is(dep, previous.deps[i]))) hooks[index] = { deps, value: callback() }
    return hooks[index].value
  }
  const react = {
    ...require('react'), useState, useMemo,
    useRef: initial => useState(() => ({ current: initial }))[0],
    useCallback: (callback, deps) => useMemo(() => callback, deps),
    useEffect: (callback, deps) => {
      const index = cursor++, previous = hooks[index]
      if (!previous || deps.some((dep, i) => !Object.is(dep, previous.deps[i]))) {
        hooks[index] = { deps, cleanup: previous?.cleanup }
        effects.push(() => { hooks[index].cleanup?.(); hooks[index].cleanup = callback() })
      }
    },
  }
  const api = new Proxy({}, { get: (_, name) => async (...args) => {
    calls.push([name, ...args])
    if (responses[name]) return responses[name](...args)
    if (name === 'executionAccounts') return [{ id: account }]
    if (name === 'hyperliquidInstruments') return { universe: [] }
    if (name === 'snapshot') return args[0] === eth.activeCycle.cycleId ? snapshot : null
    if (name === 'orders') return [{ cycleId: eth.activeCycle.cycleId, kind: 'ENTRY', status: 'NEW', price: '12345', side: 'BUY' }]
    if (['executionEnvironments', 'hyperliquidCandles', 'hyperliquidOpenOrders', 'hyperliquidOrderHistory'].includes(name)) return []
    return null
  } })
  const connection = { start: async () => {}, stop: async () => {}, invoke: async () => {}, on() {}, onreconnecting() {}, onreconnected() {}, onclose() {} }
  class HubConnectionBuilder {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    configureLogging() { return this }
    build() { return connection }
  }
  const timers = { setInterval: () => 1, clearInterval() {}, setTimeout: () => 1, clearTimeout() {} }
  const module = { exports: {} }
  vm.runInNewContext(compiled, {
    module, exports: module.exports,
    require: name => {
      if (name === 'react') return react
      if (name === '../context/AccountsContext') return { useAccounts: () => ({ revision: responses.catalog?.revision ?? 0 }) }
      if (name === '../api') return { api }
      if (name === '../lib/activeCycles') return registryModule.exports
      if (name === '@microsoft/signalr') return { HubConnectionBuilder, LogLevel: { Warning: 3 } }
      if (name.startsWith('../components/')) {
        const component = name.split('/').at(-1)
        return { [component]: props => { children.set(component, props); return null } }
      }
      return require(name)
    },
    localStorage: { getItem: key => key === 'grid.dashboardSymbol' ? symbol : null, setItem() {} },
    document: { getElementById: () => null }, window: timers, ...timers,
  })
  const errors = []
  const loaded = strategies.find(item => item.strategyId === loadedStrategyId)
  const props = { strategies, loadedStrategyId, reload: async () => {}, notify() {}, reportError: error => errors.push(error),
    cycleRegistry: buildActiveCycleRegistry(strategies.flatMap(item => item.activeCycle ? [item.activeCycle] : [])),
    selection: { environmentId: environment, accountId: account, symbol: loaded?.activeCycle?.symbol ?? loaded?.symbol ?? symbol, cycleId: null },
    onSelectionChange: value => { props.selection = value },
  }
  function render() {
    cursor = 0
    tree = module.exports.DashboardPage(props)
    html = renderToStaticMarkup(tree)
    return html
  }
  function findButton(label, node = tree) {
    if (!node || typeof node !== 'object') return undefined
    if (node.type === 'button' && node.props.children === label) return node
    for (const child of [node.props?.children].flat(Infinity)) {
      const found = findButton(label, child ?? null)
      if (found) return found
    }
  }
  return {
    render, calls, children, props, findButton,
    executionSelectors: () => tree.props.children[0].props,
    switchTo: value => { children.get('SymbolPicker').onChange(value); return render() },
    async settle() {
      for (let pass = 0; pass < 5; pass++) {
        render()
        const pending = effects; effects = []
        pending.forEach(effect => effect())
        await new Promise(resolve => setImmediate(resolve))
      }
      assert.deepEqual(errors, [])
      return render()
    },
  }
}

test('switching ETH → SOL → ATOM selects each market cycle and disables unrelated controls', async () => {
  const page = dashboard()
  await page.settle()
  assert.match(page.render(), /Cycle · RUNNING/)
  assert.match(page.render(), /12,345/)
  assert.equal(page.children.get('TradingChart').entryOrders.length, 1)

  const solHtml = page.switchTo('SOL-USDC')
  assert.match(solHtml, /Cycle · PAUSED/)
  assert.doesNotMatch(solHtml, /12,345|9,876/)
  assert.equal(page.children.get('TradingChart').entryOrders.length, 0)
  assert.equal(page.children.get('GridSuitability').strategy.strategyId, 'SOL')
  assert.ok(page.findButton('Resume Entry'))
  assert.equal(page.findButton('Pause Entry'), undefined)
  page.findButton('Sync').props.onClick()
  assert.ok(page.calls.some(call => call[0] === 'command' && call[1] === 'SOL-cycle' && call[2] === 'reconcile'))
  await page.settle()

  const atomHtml = page.switchTo('ATOM-USDC')
  assert.match(atomHtml, /Cycle · IDLE/)
  assert.match(atomHtml, /Start: —/)
  assert.match(atomHtml, /Runs: —/)
  for (const label of ['View', 'Pause Entry', 'Resume Entry', 'Sync', 'Exit']) assert.equal(page.findButton(label), undefined)
  assert.equal(page.findButton('Start').props.disabled, true)
  assert.equal(page.children.get('GridSuitability').strategy, undefined)
  await page.settle()
  assert.ok(page.calls.some(call => call[0] === 'hyperliquidAccountState' && call[2] === 'ATOM-USDC'))
  assert.match(page.render(), /Cycle · IDLE/)
  assert.match(page.switchTo('ETH-USDC'), /Cycle · RUNNING/)
})

test('a loaded ETH strategy does not override subsequent market selection', async () => {
  const page = dashboard([eth, sol], 'ATOM', 'ETH')
  await page.settle()
  assert.match(page.render(), /Cycle · RUNNING/)
  page.switchTo('SOL')
  await page.settle()
  assert.equal(page.children.get('SymbolPicker').value, 'SOL')
  assert.equal(page.children.get('GridSuitability').strategy.strategyId, 'SOL')
})

test('active strategies take precedence over idle strategies for the selected market', () => {
  const idleSol = strategy('SOL', null, 'idle-sol')
  const page = dashboard([eth, idleSol, sol], 'SOL')
  assert.match(page.render(), /Cycle · PAUSED/)
  assert.equal(page.children.get('GridSuitability').strategy.strategyId, 'SOL')
})

test('cycles on other accounts or environments cannot appear in the selected market', () => {
  const wrongAccount = strategy('ATOM', 'RUNNING', 'other-account')
  wrongAccount.activeCycle.executionAccountId = 'other-account'
  const wrongNetwork = strategy('ATOM', 'RUNNING', 'mainnet')
  wrongNetwork.activeCycle.executionEnvironmentId = 'hyperliquid-mainnet'
  const page = dashboard([eth, wrongAccount, wrongNetwork], 'ATOM')
  assert.match(page.render(), /Cycle · IDLE/)
  assert.equal(page.findButton('Exit'), undefined)
})

test('an idle saved strategy for the selected market offers Start', async () => {
  const page = dashboard([eth, sol, strategy('ATOM', null)], 'ATOM')
  await page.settle()
  assert.match(page.render(), /Cycle · IDLE/)
  assert.equal(page.findButton('Start').props.disabled, false)
  assert.equal(page.children.get('GridSuitability').strategy.strategyId, 'ATOM')
})


test('a late response from the previous cycle cannot replace the selected market data', async () => {
  let completeEth
  const pendingEth = new Promise(resolve => { completeEth = resolve })
  const page = dashboard([eth, sol], 'ETH', null, {
    snapshot: cycleId => cycleId === 'ETH-cycle' ? pendingEth : null,
  })
  await page.settle()
  page.switchTo('SOL')
  await page.settle()
  completeEth(snapshot)
  await page.settle()
  assert.match(page.render(), /Cycle · PAUSED/)
  assert.doesNotMatch(page.render(), /12,345|9,876/)
  assert.equal(page.children.get('TradingChart').entryOrders.length, 0)
})

test('an idle loaded strategy remains available when choosing another run environment', async () => {
  const page = dashboard([strategy('ATOM', null)], 'ATOM', 'ATOM')
  await page.settle()
  page.executionSelectors().onEnvironmentChange('hyperliquid-mainnet')
  await page.settle()
  assert.equal(page.executionSelectors().environmentId, 'hyperliquid-mainnet')
  assert.equal(page.children.get('GridSuitability').strategy.strategyId, 'ATOM')
  assert.equal(page.findButton('Start').props.disabled, false)
})


test('edited strategy keeps the running cycle on its frozen market', async () => {
  const edited = { ...eth, symbol: 'ATOM', activeCycle: { ...eth.activeCycle, symbol: 'ATOM', frozenConfiguration: { ...eth.configuration, symbol: 'ETH' } } }
  const page = dashboard([edited, sol], 'ETH')
  await page.settle()
  assert.match(page.render(), /Cycle · RUNNING/)
  assert.equal(page.children.get('GridSuitability').strategy.strategyId, 'ETH')
  assert.match(page.switchTo('ATOM'), /Cycle · IDLE/)
  assert.equal(page.findButton('Exit'), undefined)
  assert.equal(page.findButton('Start').props.disabled, true)
})

test('legacy duplicates require an explicit choice and block new starts', async () => {
  const duplicate = strategy('ETH', 'PAUSED', 'eth-2')
  const page = dashboard([eth, duplicate, sol])
  await page.settle()
  assert.match(page.render(), /Cycle · CONFLICT/)
  assert.match(page.render(), /eth-2-cycle/)
  assert.equal(page.findButton('Sync'), undefined)
  assert.equal(page.findButton('Start').props.disabled, true)
  page.props.onSelectionChange({ ...page.props.selection, cycleId: duplicate.activeCycle.cycleId })
  await page.settle()
  assert.match(page.render(), /Cycle · PAUSED/)
  page.findButton('Sync').props.onClick()
  assert.ok(page.calls.some(call => call[0] === 'command' && call[1] === 'eth-2-cycle'))
  assert.match(page.switchTo('ATOM'), /Cycle · IDLE/)
  assert.match(page.switchTo('ETH'), /Cycle · CONFLICT/)
})

test('a command completing after a market switch cannot refresh its previous market', async () => {
  let completeCommand
  const pendingCommand = new Promise(resolve => { completeCommand = resolve })
  const page = dashboard([eth, sol], 'ETH', null, { command: () => pendingCommand })
  await page.settle()
  page.findButton('Sync').props.onClick()
  page.switchTo('SOL')
  await page.settle()
  const ethRequests = page.calls.filter(call => call[0] === 'snapshot' && call[1] === 'ETH-cycle').length
  completeCommand(null)
  await page.settle()
  assert.equal(page.calls.filter(call => call[0] === 'snapshot' && call[1] === 'ETH-cycle').length, ethRequests)
  assert.match(page.render(), /Cycle · PAUSED/)
  assert.doesNotMatch(page.render(), /12,345|9,876/)
})

test('finished cycles leave the active registry and clear their dashboard controls', async () => {
  const page = dashboard()
  await page.settle()
  page.props.cycleRegistry = buildActiveCycleRegistry([{ ...eth.activeCycle, isTerminal: true }, sol.activeCycle])
  await page.settle()
  assert.match(page.render(), /Cycle · IDLE/)
  assert.equal(page.findButton('Exit'), undefined)
  assert.equal(page.children.get('TradingChart').entryOrders.length, 0)
})


test('account catalog changes refresh selectors without switching the current account', async () => {
  const responses = { catalog: { revision: 0 }, executionAccounts: async () => [{ id: account, displayName: 'Original' }] }
  const page = dashboard([eth, sol], 'ETH', null, responses)
  await page.settle()
  responses.executionAccounts = async () => [{ id: account, displayName: 'Renamed' }, { id: 'second', displayName: 'Second' }]
  responses.catalog.revision++
  await page.settle()
  assert.equal(page.executionSelectors().accounts.length, 2)
  assert.equal(page.executionSelectors().accounts[0].displayName, 'Renamed')
  assert.equal(page.executionSelectors().accountId, account)
})

test('late account balance responses cannot replace the newly selected account', async () => {
  let finishOld
  const pending = new Promise(resolve => { finishOld = resolve })
  const other = strategy('ETH', 'RUNNING', 'other')
  other.activeCycle.executionAccountId = 'second'
  const page = dashboard([eth, other], 'ETH', null, {
    executionAccounts: async () => [{ id: account }, { id: 'second' }],
    hyperliquidAccountState: id => id === account ? pending : { accountId: 'second', tradingEquity: '222', availableBalance: '222', accountValue: '222', withdrawable: '222', totalMarginUsed: '0', netPosition: '0', unrealizedPnl: '0' },
  })
  await page.settle()
  page.executionSelectors().onAccountChange('second')
  await page.settle()
  finishOld({ accountId: account, tradingEquity: '999999', availableBalance: '999999' })
  await page.settle()
  assert.equal(page.executionSelectors().accountId, 'second')
  assert.doesNotMatch(page.render(), /999,999|999999/)
  assert.equal(page.children.get('GridSuitability').strategy.strategyId, 'other')
})
