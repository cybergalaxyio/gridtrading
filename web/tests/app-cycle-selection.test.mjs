import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import { renderToStaticMarkup } from 'react-dom/server'

const require = createRequire(import.meta.url)
function compile(path) {
  return ts.transpileModule(readFileSync(new URL(path, import.meta.url), 'utf8'), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  }).outputText
}
const registryModule = { exports: {} }
vm.runInNewContext(compile('../src/lib/activeCycles.ts'), { module: registryModule, exports: registryModule.exports })
const source = compile('../src/App.tsx')
const cycle = (symbol, id = symbol) => ({
  cycleId: id, strategyId: id, symbol, state: 'RUNNING', stateVersion: 3, isTerminal: false,
  executionAccountId: 'account', executionEnvironmentId: 'hyperliquid-testnet',
})
const strategy = cycle => ({ strategyId: cycle.strategyId, name: `Grid ${cycle.strategyId}`, symbol: cycle.symbol, activeCycle: cycle })

function app(cycles = [cycle('ETH'), cycle('SOL')]) {
  const data = { cycles, strategies: cycles.map(strategy) }
  const hooks = [], children = new Map(), commands = []
  let cursor = 0, effects = [], html
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
    useCallback: (callback, deps) => useMemo(() => callback, deps),
    useRef: initial => useState(() => ({ current: initial }))[0],
    useEffect: (callback, deps) => {
      const index = cursor++, previous = hooks[index]
      if (!previous || deps.some((dep, i) => !Object.is(dep, previous.deps[i]))) {
        hooks[index] = { deps, cleanup: previous?.cleanup }
        effects.push(() => { hooks[index].cleanup?.(); hooks[index].cleanup = callback() })
      }
    },
  }
  const api = {
    strategies: async () => data.strategies, activeCycles: async () => data.cycles,
    command: async (...args) => { commands.push(args) },
  }
  const module = { exports: {} }
  vm.runInNewContext(source, {
    module, exports: module.exports,
    require: name => {
      if (name === 'react') return react
      if (name === './context/AccountsContext') return { AccountLabel: props => props.accountId ?? '—' }
      if (name === './api') return { api }
      if (name === './lib/activeCycles') return registryModule.exports
      if (name.startsWith('./')) {
        const exportName = name.split('/').at(-1).replace('HyperliquidDashboardPage', 'DashboardPage').replace('TestnetSettingsPage', 'SettingsPage')
        return { [exportName]: props => { children.set(exportName, props); return props.children ?? null } }
      }
      return require(name)
    },
    localStorage: { getItem: () => null },
    setInterval: () => 1, clearInterval() {}, setTimeout: () => 1, clearTimeout() {},
  })
  function render() {
    cursor = 0
    children.clear()
    html = renderToStaticMarkup(module.exports.default())
    return html
  }
  async function settle() {
    for (let pass = 0; pass < 3; pass++) {
      render()
      const pending = effects; effects = []
      pending.forEach(effect => effect())
      await new Promise(resolve => setImmediate(resolve))
    }
    return render()
  }
  function findButton(node, label) {
    if (!node || typeof node !== 'object') return null
    if (node.type === 'button' && node.props.children === label) return node
    for (const child of [node.props?.children].flat(Infinity)) {
      const found = findButton(child, label)
      if (found) return found
    }
    return null
  }
  return {
    render, settle, children, commands, data,
    select: (symbol, cycleId = null) => {
      const dashboard = children.get('DashboardPage')
      dashboard.onSelectionChange({ ...dashboard.selection, symbol, cycleId })
      render()
    },
    openEmergency: () => { children.get('Layout').onEmergency(); render() },
    confirm: () => findButton(children.get('Modal')?.children, '撤单并清零仓位'),
  }
}

test('initial selection waits for data and uses the actual execution account', async () => {
  const page = app()
  assert.match(page.render(), /正在加载/)
  assert.equal(page.children.get('DashboardPage'), undefined)
  await page.settle()
  assert.equal(page.children.get('DashboardPage').selection.accountId, 'account')
  assert.equal(page.children.get('Layout').environment, 'TESTNET')
})

test('emergency target follows the selected market and remains fixed during confirmation', async () => {
  const page = app()
  await page.settle()
  page.select('SOL-USDC')
  page.openEmergency()
  assert.match(page.render(), /Grid SOL/)
  assert.doesNotMatch(page.render(), /Grid ETH/)
  page.select('ETH')
  assert.match(page.render(), /Grid SOL/)
  page.confirm().props.onClick()
  assert.equal(page.commands[0][0], 'SOL')
  assert.equal(page.commands[0][1], 'emergency-flatten')
  await page.settle()
})

test('idle markets and unresolved duplicates never fall back to another emergency target', async () => {
  const page = app([cycle('ETH'), cycle('ETH', 'second')])
  await page.settle()
  page.openEmergency()
  assert.equal(page.children.get('Modal'), undefined)
  page.select('ATOM')
  page.openEmergency()
  assert.equal(page.children.get('Modal'), undefined)
  assert.equal(page.commands.length, 0)
  page.select('ETH', 'second')
  page.openEmergency()
  page.confirm().props.onClick()
  assert.equal(page.commands[0][0], 'second')
  await page.settle()
})

test('ending a cycle during confirmation disables the action instead of targeting its replacement', async () => {
  const page = app()
  await page.settle()
  page.openEmergency()
  page.data.cycles = [cycle('ETH', 'replacement'), cycle('SOL')]
  await page.children.get('DashboardPage').reload()
  page.render()
  assert.equal(page.confirm().props.disabled, true)
  page.confirm().props.onClick()
  assert.equal(page.commands.length, 0)
})

test('loading an edited strategy selects the running cycle frozen symbol', async () => {
  const running = { ...cycle('ATOM'), frozenConfiguration: { symbol: 'ETH' } }
  const page = app([running])
  await page.settle()
  page.children.get('Layout').onRoute('strategies')
  page.render()
  page.children.get('StrategiesPage').onLoad(page.data.strategies[0])
  page.render()
  assert.equal(page.children.get('DashboardPage').selection.symbol, 'ETH')
})
