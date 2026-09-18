import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'

const require = createRequire(import.meta.url)
function load(relative, imports = {}, extras = {}) {
  const module = { exports: {} }
  const source = readFileSync(new URL(relative, import.meta.url), 'utf8')
  vm.runInNewContext(ts.transpileModule(source, { compilerOptions: {
    module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX,
  } }).outputText, { module, exports: module.exports, require: name => name === '../context/AccountsContext' ? { useAccounts: () => ({ revision: 0 }) } : imports[name] ?? require(name), ...extras })
  return module.exports
}
function client() {
  const requests = []
  const api = load('../src/api.ts', {}, { fetch: async (url, init) => {
    requests.push({ url, body: JSON.parse(init.body) })
    return { ok: true, status: 200, json: async () => ({ levels: [], expiresAt: '2026-09-13T12:00:00Z' }) }
  } })
  return { ...api, requests }
}
function nodes(node) {
  if (Array.isArray(node)) return node.flatMap(nodes)
  if (!node || typeof node !== 'object') return []
  return [node, ...nodes(node.props?.children)]
}
function form(overrides = {}) {
  const api = client(), state = [], errors = []
  let cursor = 0
  const page = load('../src/pages/CreateStrategyPage.tsx', {
    react: {
      useState(initial) {
        const index = cursor++
        if (!(index in state)) state[index] = typeof initial === 'function' ? initial() : initial
        return [state[index], value => { state[index] = typeof value === 'function' ? value(state[index]) : value }]
      },
      useMemo: fn => fn(), useEffect: () => {},
    },
    '../api': api,
    '../components/GridPreview': { buildGridPreview: () => [], GridPreview: () => null },
    '../components/Icon': { Icon: () => null },
  })
  const configuration = {
    ...Object.fromEntries(Object.entries(api.defaultConfig).map(([key, value]) => [key, value === '' ? '1' : value])),
    gridSpacingPoints: '25', ...overrides,
  }
  const props = {
    initialStrategy: { name: 'Test', strategyId: 'test', strategyType: 'GRID', symbol: 'SOLUSDT',
      defaultExecutionEnvironmentId: 'paper-local', defaultExecutionAccountId: 'acct_paper_01', configuration },
    onSaved: async () => {}, onCancel: () => {}, reportError: message => errors.push(message),
  }
  function render() { cursor = 0; return nodes(page.CreateStrategyPage(props)) }
  const field = label => render().find(node => node.props?.label === label)
  const mode = value => field('网格模式').props.children.props.onChange({ target: { value } })
  const step = number => render().find(node => node.props?.className === 'steps').props.children[number - 1].props.onClick()
  async function preview() {
    step(3)
    render().find(node => node.type === 'button' && node.props.children === '生成完整预览').props.onClick()
    await new Promise(resolve => setImmediate(resolve))
  }
  return { render, field, mode, step, preview, errors, ...api }
}

test('single-side selection displays two independent settings and preserves them across mode switches', () => {
  const f = form()
  assert.equal(f.render().some(node => node.props?.id === 'single-mode-move-fields'), false)
  f.mode('SELL_ONLY')
  assert.equal(f.field('移动触发距离').props.value, '25')
  assert.equal(f.field('移动最短等待时间').props.value, 30)
  f.field('移动触发距离').props.onChange('80')
  f.field('移动最短等待时间').props.onChange('120')
  f.mode('TWO_WAY')
  assert.equal(f.field('移动触发距离'), undefined)
  f.mode('BUY_ONLY')
  assert.equal(f.field('移动触发距离').props.value, '80')
  assert.equal(f.field('移动最短等待时间').props.value, 120)
  f.step(2)
  f.field('Grid Spacing').props.onChange('40')
  f.step(1)
  assert.equal(f.field('移动触发距离').props.value, '80')
})

test('when spacing is initially empty, entering it supplies the initial move distance', () => {
  const f = form({ gridSpacingPoints: '' })
  f.mode('SELL_ONLY')
  assert.equal(f.field('移动触发距离').props.value, '')
  f.step(2)
  f.field('Grid Spacing').props.onChange('50')
  f.step(1)
  assert.equal(f.field('移动触发距离').props.value, '50')
})

test('invalid single-mode values block preview and direct the user back to the settings', async () => {
  for (const [distance, seconds] of [['', '30'], ['0', '30'], ['-1', '30'], ['20', '0'], ['20', '1.5'], ['20', '2147483648']]) {
    const f = form({ gridMode: 'SELL_ONLY' })
    f.field('移动触发距离').props.onChange(distance)
    f.field('移动最短等待时间').props.onChange(seconds)
    await f.preview()
    assert.equal(f.requests.length, 0)
    assert.ok(f.errors.at(-1).includes('移动'))
    assert.ok(f.field('移动触发距离'))
  }
})

test('hidden invalid inputs do not block two-way preview', async () => {
  const f = form({ gridMode: 'SELL_ONLY' })
  f.field('移动触发距离').props.onChange('')
  f.field('移动最短等待时间').props.onChange('1.5')
  f.mode('TWO_WAY')
  await f.preview()
  assert.equal(f.requests.length, 1)
  assert.equal(f.requests[0].body.candidateConfiguration.singleModeMoveDistancePoints, null)
  assert.equal(f.requests[0].body.candidateConfiguration.singleModeMoveIntervalSeconds, 30)
})

test('preview, save, and edit carry the custom settings and preview confirmation shows them', async () => {
  const f = form({ gridMode: 'SELL_ONLY', singleModeMoveDistancePoints: '70', singleModeMoveIntervalSeconds: 90 })
  await f.preview()
  assert.match(JSON.stringify(f.render()), /移动最短等待时间/)
  const config = { ...f.defaultConfig, gridMode: 'SELL_ONLY', singleModeMoveDistancePoints: '70', singleModeMoveIntervalSeconds: 90 }
  await f.api.createStrategy(config)
  await f.api.updateStrategy('test', config)
  for (const payload of [f.requests[0].body.candidateConfiguration, f.requests[1].body, f.requests[2].body]) {
    assert.equal(payload.singleModeMoveDistancePoints, '70')
    assert.equal(payload.singleModeMoveIntervalSeconds, 90)
  }
})

test('parameter details show frozen settings and legacy fallback from the frozen spacing', () => {
  const { StrategyParameters } = load('../src/components/StrategyParameters.tsx', {
    './GridPreview': { buildGridPreview: () => [], GridPreview: () => null },
  })
  const { defaultConfig } = client()
  for (const [frozenSettings, distance, seconds] of [[{}, '25', 30], [{ singleModeMoveDistancePoints: '50', singleModeMoveIntervalSeconds: 60 }, '50', 60]]) {
    const strategy = {
      strategyId: 'test', name: 'Test', symbol: 'SOLUSDT', version: 2,
      configuration: { ...defaultConfig, gridMode: 'SELL_ONLY', gridSpacingPoints: '999', singleModeMoveDistancePoints: '999', singleModeMoveIntervalSeconds: 999 },
      activeCycle: { state: 'RUNNING', frozenConfiguration: { gridMode: 'SELL_ONLY', gridSpacingPoints: '25', ...frozenSettings } },
    }
    const html = renderToStaticMarkup(createElement(StrategyParameters, { strategy, onClose() {} }))
    assert.match(html, new RegExp(`<dt>移动触发距离</dt><dd title="${distance} pts">${distance} pts</dd>`))
    assert.match(html, new RegExp(`<dt>移动最短等待时间</dt><dd title="${seconds} 秒">${seconds} 秒</dd>`))
    strategy.activeCycle.frozenConfiguration.gridMode = 'TWO_WAY'
    const twoWay = renderToStaticMarkup(createElement(StrategyParameters, { strategy, onClose() {} }))
    assert.doesNotMatch(twoWay, /移动触发距离/)
  }
})
