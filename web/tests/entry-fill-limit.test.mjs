import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'

function loadApi() {
  const requests = []
  const module = { exports: {} }
  const source = readFileSync(new URL('../src/api.ts', import.meta.url), 'utf8')
  vm.runInNewContext(ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS },
  }).outputText, {
    module, exports: module.exports,
    fetch: async (url, init) => {
      requests.push({ url, body: JSON.parse(init.body) })
      return { ok: true, status: 200, json: async () => ({}) }
    },
  })
  return { ...module.exports, requests }
}

test('Entry Fill Limit defaults are opt-in and legacy edits retain defaults', () => {
  const { defaultConfig } = loadApi()
  const edited = { ...defaultConfig, name: 'Legacy strategy' }
  assert.equal(edited.entryFillLimitEnabled, false)
  assert.equal(edited.entryFillWindowMinutes, 60)
  assert.equal(edited.maxEntryFillsPerSide, 3)
})

test('candidate preview, create and update send the same English configuration fields', async () => {
  const { api, defaultConfig, requests } = loadApi()
  const config = { ...defaultConfig, entryFillLimitEnabled: true, entryFillWindowMinutes: 45, maxEntryFillsPerSide: 4 }
  await api.previewCandidate(config, '100')
  await api.createStrategy(config)
  await api.updateStrategy('strategy-1', config)
  const payloads = [requests[0].body.candidateConfiguration, requests[1].body, requests[2].body]
  for (const payload of payloads) {
    assert.equal(payload.entryFillLimitEnabled, true)
    assert.equal(payload.entryFillWindowMinutes, 45)
    assert.equal(payload.maxEntryFillsPerSide, 4)
  }
})

// Render the real parameter panel; its grid tab is unrelated to this check.
test('legacy frozen cycle displays disabled defaults instead of edited template settings', async () => {
  const { createRequire } = await import('node:module')
  const { createElement } = await import('react')
  const { renderToStaticMarkup } = await import('react-dom/server')
  const require = createRequire(import.meta.url)
  const module = { exports: {} }
  const source = readFileSync(new URL('../src/components/StrategyParameters.tsx', import.meta.url), 'utf8')
  vm.runInNewContext(ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  }).outputText, {
    module, exports: module.exports,
    require: name => name === './GridPreview' ? { buildGridPreview: () => [], GridPreview: () => null } : require(name),
  })
  const { defaultConfig } = loadApi()
  const strategy = {
    strategyId: 'legacy', name: 'Legacy', symbol: 'SOLUSDT', version: 2,
    defaultExecutionEnvironmentId: 'paper-local', defaultExecutionAccountId: 'acct_paper_01',
    configuration: { ...defaultConfig, entryFillLimitEnabled: true, entryFillWindowMinutes: 45, maxEntryFillsPerSide: 4 },
    activeCycle: { frozenConfiguration: {}, state: 'RUNNING' },
  }
  const html = renderToStaticMarkup(createElement(module.exports.StrategyParameters, { strategy, onClose: () => {} }))
  assert.match(html, /<dt>Enable Entry Fill Limit<\/dt><dd title="Disabled">Disabled<\/dd>/)
  assert.match(html, /<dt>Lookback Window \(min\)<\/dt><dd title="60">60<\/dd>/)
  assert.match(html, /<dt>Max Filled Entries per Side<\/dt><dd title="3">3<\/dd>/)
})

// Exercise the form handlers with local hook state; no exchange requests or DOM are needed.
test('disabling the limit hides fields and allows preview after invalid input while retaining valid settings', async () => {
  const { createRequire } = await import('node:module')
  const require = createRequire(import.meta.url)
  const source = readFileSync(new URL('../src/pages/CreateStrategyPage.tsx', import.meta.url), 'utf8')
  const compiled = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  }).outputText
  for (const [windowInput, countInput, expectedWindow, expectedCount] of [
    ['', '', 60, 3], ['0', '-1', 60, 3], ['1.5', '2147483648', 60, 3],
    ['45', '4', 45, 4], ['', '4', 60, 4],
  ]) {
    const client = loadApi()
    const state = []
    const errors = []
    let cursor = 0
    const module = { exports: {} }
    vm.runInNewContext(compiled, {
      module, exports: module.exports,
      require: name => {
        if (name === 'react') return {
          useState(initial) {
            const index = cursor++
            if (!(index in state)) state[index] = typeof initial === 'function' ? initial() : initial
            return [state[index], value => { state[index] = typeof value === 'function' ? value(state[index]) : value }]
          },
          useMemo: calculate => calculate(), useEffect: () => {},
        }
        if (name === '../api') return client
        if (name === '../components/GridPreview') return { buildGridPreview: () => [], GridPreview: () => null }
        if (name === '../components/Icon') return { Icon: () => null }
        return require(name)
      },
    })
    const configuration = Object.fromEntries(Object.entries(client.defaultConfig).map(([key, value]) => [key, value === '' ? '1' : value]))
    const props = {
      initialStrategy: { name: 'Test', strategyId: 'test', strategyType: 'GRID', symbol: 'SOLUSDT',
        defaultExecutionEnvironmentId: 'paper-local', defaultExecutionAccountId: 'acct_paper_01', configuration },
      onSaved: async () => {}, onCancel: () => {}, reportError: message => errors.push(message),
    }
    function nodes(node) {
      if (Array.isArray(node)) return node.flatMap(nodes)
      if (!node || typeof node !== 'object') return []
      return [node, ...nodes(node.props?.children)]
    }
    function render() { cursor = 0; return nodes(module.exports.CreateStrategyPage(props)) }
    let tree = render()
    tree.find(node => node.props?.className === 'steps').props.children[2].props.onClick()
    const checkbox = () => render().find(node => node.props?.['aria-controls'] === 'entry-fill-limit-fields')
    assert.equal(render().some(node => node.props?.id === 'entry-fill-limit-fields'), false)
    checkbox().props.onChange({ target: { checked: true } })
    render().find(node => node.props?.label === 'Lookback Window (min)').props.onChange(windowInput)
    render().find(node => node.props?.label === 'Max Filled Entries per Side').props.onChange(countInput)
    checkbox().props.onChange({ target: { checked: false } })
    tree = render()
    assert.equal(tree.some(node => node.props?.id === 'entry-fill-limit-fields'), false)
    tree.find(node => node.type === 'button' && node.props.children === '生成完整预览').props.onClick()
    await Promise.resolve()
    const payload = client.requests[0]?.body.candidateConfiguration
    assert.ok(payload, `Preview should be sent after disabling (${windowInput}, ${countInput}): ${errors}`)
    assert.equal(payload.entryFillLimitEnabled, false)
    assert.equal(payload.entryFillWindowMinutes, expectedWindow)
    assert.equal(payload.maxEntryFillsPerSide, expectedCount)
    assert.ok(errors.every(message => message === ''))
  }
})
