import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import { renderToStaticMarkup } from 'react-dom/server'
const require = createRequire(import.meta.url)
function load(path, imports = {}, extras = {}) {
  const module = { exports: {} }
  vm.runInNewContext(ts.transpileModule(readFileSync(new URL(path, import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX, target: ts.ScriptTarget.ES2022 },
  }).outputText, { module, exports: module.exports, Error, require: name => imports[name] ?? require(name), ...extras })
  return module.exports
}
const { liveCyclePreview } = load('../src/lib/liveCyclePreview.ts')
const config = { symbol: 'SOLUSDT', takeProfitPoints: '50', maxLevelsPerSide: 2, baseLotSize: '.2', tickSize: '.01', quantityStep: '.1',
  gridSpacingPoints: '10', gridSpacingStepPoints: '2', lotSizeIncreasePercent: '50', maxTradeLot: '.5' }
const levels = [{ side: 'BUY', levelIndex: 0, entryPrice: '102.05', plannedQuantity: '.2' },
  { side: 'BUY', levelIndex: 1, entryPrice: '101.93', plannedQuantity: '.3' }]
const cycle = { cycleId: 'cycle', state: 'RUNNING', stateVersion: 3, isTerminal: false, effectiveConfiguration: config,
  effectivePlan: { centerPrice: '102.10', levels } }

test('preview preserves shifted saved prices, extends spacing, and applies quantity growth and cap', () => {
  const original = JSON.stringify(levels)
  const preview = liveCyclePreview(levels, { ...config, maxLevelsPerSide: 4, baseLotSize: '.35' }, .01, .1)
  assert.deepEqual(Array.from(preview, x => x.price.toFixed(2)), ['102.05', '101.93', '101.79', '101.63'])
  assert.deepEqual(Array.from(preview, x => x.quantity.toFixed(1)), ['0.3', '0.5', '0.5', '0.5'])
  assert.equal(JSON.stringify(levels), original)
  assert.equal(liveCyclePreview(levels, { ...config, maxLevelsPerSide: 201 }, .01, .1).length, 0)
})

test('client sends decimal strings, version and a reusable idempotency key to the cycle endpoint', async () => {
  const calls = []
  const { api } = load('../src/api.ts', {}, { fetch: async (url, init) => {
    calls.push({ url, ...init }); return { ok: true, status: 202, json: async () => ({ status: 'COMPLETED' }) }
  } })
  await api.updateCycleParameters('cycle', { takeProfitPoints: '125.5', maxLevelsPerSide: 4, baseLotSize: '.35' }, 3, 'retry-key')
  assert.equal(calls[0].url, '/api/v1/cycles/cycle/commands/update-parameters')
  assert.equal(calls[0].headers['If-Match'], '"3"')
  assert.equal(calls[0].headers['Idempotency-Key'], 'retry-key')
  assert.equal(JSON.parse(calls[0].body).baseLotSize, '.35')
})

function editor(failFirst = false) {
  let cursor = 0, tree, failures = failFirst ? 1 : 0, ids = 0, currentCycle = cycle
  const states = [], calls = [], saved = []
  const useState = initial => {
    const i = cursor++
    if (!(i in states)) states[i] = typeof initial === 'function' ? initial() : initial
    return [states[i], value => { states[i] = typeof value === 'function' ? value(states[i]) : value }]
  }
  const api = {
    updateCycleParameters: async (...args) => { calls.push(args); if (failures-- > 0) throw new Error('Cycle changed; refresh') },
    cycle: async () => ({ ...cycle, stateVersion: 4, effectiveConfiguration: { ...config, baseLotSize: '.4' } }),
  }
  const { LiveCycleEditor } = load('../src/components/LiveCycleEditor.tsx', {
    react: { ...require('react'), useState, useRef: initial => useState(() => ({ current: initial }))[0] },
    '../api': { api }, '../lib/liveCyclePreview': { liveCyclePreview }, './GridPreview': { GridPreview: () => null },
  }, { crypto: { randomUUID: () => `key-${++ids}` } })
  function render() { cursor = 0; tree = LiveCycleEditor({ cycle: currentCycle, config, onSaved: x => saved.push(x), onCancel() {} }); return renderToStaticMarkup(tree) }
  function nodes(predicate, node = tree) {
    if (!node || typeof node !== 'object') return []
    return [...(predicate(node) ? [node] : []), ...[node.props?.children].flat(Infinity).flatMap(x => nodes(predicate, x ?? null))]
  }
  function button(text) { return nodes(x => x.type === 'button' && x.props.children === text)[0] }
  return { calls, saved, render, nodes, button, setCycle(value) { currentCycle = value }, async settle() { await new Promise(resolve => setImmediate(resolve)); return render() } }
}

test('editor blocks reductions, permits smaller future lots, and saves all three values', async () => {
  const page = editor()
  assert.match(page.render(), /已有挂单、部分成交订单和持仓保留原数量与 TP 距离/)
  let inputs = page.nodes(x => x.type === 'input')
  inputs[1].props.onChange({ target: { value: '1' } }); page.render()
  assert.equal(page.button('保存到当前 Cycle').props.disabled, true)
  inputs = page.nodes(x => x.type === 'input')
  inputs[0].props.onChange({ target: { value: '125' } })
  inputs[1].props.onChange({ target: { value: '4' } })
  inputs[2].props.onChange({ target: { value: '.15' } }); page.render()
  assert.equal(page.button('保存到当前 Cycle').props.disabled, false)
  page.button('保存到当前 Cycle').props.onClick(); await page.settle()
  assert.deepEqual(JSON.parse(JSON.stringify(page.calls[0].slice(0, 3))), ['cycle', { takeProfitPoints: '125', maxLevelsPerSide: 4, baseLotSize: '.15' }, 3])
  assert.equal(page.saved[0].stateVersion, 4)
})

test('failed save keeps the form and retry key; refresh adopts latest version and values', async () => {
  const page = editor(true); page.render()
  page.nodes(x => x.type === 'input')[2].props.onChange({ target: { value: '.3' } }); page.render()
  page.button('保存到当前 Cycle').props.onClick(); assert.match(await page.settle(), /Cycle changed; refresh/)
  page.button('保存到当前 Cycle').props.onClick(); await page.settle()
  assert.equal(page.calls[0][3], page.calls[1][3])
  const stale = editor(true); stale.render()
  stale.nodes(x => x.type === 'input')[2].props.onChange({ target: { value: '.3' } }); stale.render()
  stale.button('保存到当前 Cycle').props.onClick(); await stale.settle()
  stale.button('重新加载当前值').props.onClick(); await stale.settle()
  assert.equal(stale.nodes(x => x.type === 'input')[2].props.value, '.4')
  stale.nodes(x => x.type === 'input')[2].props.onChange({ target: { value: '.5' } }); stale.render()
  stale.button('保存到当前 Cycle').props.onClick(); await stale.settle()
  assert.equal(stale.calls[1][2], 4)
  assert.notEqual(stale.calls[0][3], stale.calls[1][3])
})

test('parameter dialog displays effective cycle values and only exposes live edits for active states', () => {
  const { StrategyParameters } = load('../src/components/StrategyParameters.tsx', {
    './GridPreview': { buildGridPreview: () => [], GridPreview: () => null },
    './LiveCycleEditor': { LiveCycleEditor: () => null },
  })
  const { defaultConfig } = load('../src/api.ts')
  const settings = { ...defaultConfig, ...config }
  const strategy = { strategyId: 'strategy', symbol: 'SOLUSDT', name: 'Grid', configuration: settings,
    activeCycle: { ...cycle, frozenConfiguration: settings, effectiveConfiguration: { ...settings, takeProfitPoints: '125', maxLevelsPerSide: 5, baseLotSize: '.4' } } }
  const render = () => renderToStaticMarkup(require('react').createElement(StrategyParameters, { strategy, onClose() {} }))
  const html = render()
  assert.match(html, /CURRENT CYCLE/)
  assert.match(html, /125 pts/)
  assert.match(html, /5 层/)
  assert.match(html, /\.4 SOL/)
  assert.match(html, /Edit current cycle/)
  strategy.activeCycle.state = 'CLOSING'
  assert.doesNotMatch(render(), /Edit current cycle/)
})


test('an open editor disables saving when the current cycle starts closing', () => {
  const page = editor(); page.render()
  page.nodes(x => x.type === 'input')[2].props.onChange({ target: { value: '.3' } }); page.render()
  assert.equal(page.button('保存到当前 Cycle').props.disabled, false)
  page.setCycle({ ...cycle, state: 'CLOSING', stateVersion: 4 }); page.render()
  assert.equal(page.button('保存到当前 Cycle').props.disabled, true)
  page.button('保存到当前 Cycle').props.onClick()
  assert.equal(page.calls.length, 0)
})
