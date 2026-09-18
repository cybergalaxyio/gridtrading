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

test('account API mutations use network-specific JSON routes and never put keys in URLs', async () => {
  const requests = []
  const { api } = load('../src/api.ts', {}, { fetch: async (url, init = {}) => {
    requests.push({ url, ...init })
    return { ok: true, status: 200, json: async () => ({ accountId: 'a', enabled: false }) }
  } })
  await api.createAccount('hyperliquid-mainnet', { name: 'Main', accountAddress: '0xabc', agentPrivateKey: 'secret-key' })
  await api.renameAccount('hyperliquid-mainnet', 'a', { name: 'New name' })
  await api.replaceAccountCredentials('hyperliquid-mainnet', 'a', { agentPrivateKey: 'replacement-key' })
  await api.enableAccount('hyperliquid-mainnet', 'a')
  await api.disableAccount('hyperliquid-mainnet', 'a')
  assert.deepEqual(requests.map(x => [x.method, x.url]), [
    ['POST', '/api/v1/hyperliquid-mainnet/accounts'],
    ['PATCH', '/api/v1/hyperliquid-mainnet/accounts/a'],
    ['PUT', '/api/v1/hyperliquid-mainnet/accounts/a/credentials'],
    ['POST', '/api/v1/hyperliquid-mainnet/accounts/a/test-and-enable'],
    ['POST', '/api/v1/hyperliquid-mainnet/accounts/a/disable'],
  ])
  for (const request of requests) {
    assert.equal(request.headers['Content-Type'], 'application/json')
    assert.doesNotMatch(request.url, /secret-key|replacement-key/)
    assert.doesNotThrow(() => JSON.parse(request.body))
  }
  assert.equal(JSON.parse(requests[0].body).agentPrivateKey, 'secret-key')
})

const account = { accountId: 'a', name: 'Main account', environment: 'MAINNET', accountAddress: '0xabc', agentAddress: '0xdef',
  enabled: true, signingKeyStored: true, activeCycleCount: 0, blockers: [] }
function panel(accounts = [], fail = false) {
  let cursor = 0, tree
  const states = [], calls = []
  const useState = initial => {
    const index = cursor++
    if (!(index in states)) states[index] = typeof initial === 'function' ? initial() : initial
    return [states[index], value => { states[index] = typeof value === 'function' ? value(states[index]) : value }]
  }
  const api = new Proxy({}, { get: (_, name) => async (...args) => {
    calls.push([name, ...args])
    if (fail) throw new Error('Simulated validation error')
    return { ...account, enabled: false }
  } })
  const { AccountsPanel } = load('../src/components/AccountsPanel.tsx', {
    react: { ...require('react'), useState, useRef: initial => useState(() => ({ current: initial }))[0], useEffect() {} },
    '../api': { api },
    '../context/AccountsContext': { useAccounts: () => ({ accounts, loading: false, error: '', revision: 1, refresh: async () => { calls.push(['refresh']) } }) },
    './Icon': { Icon: () => null },
    './Modal': { Modal: props => props.children },
  })
  function render() { cursor = 0; tree = AccountsPanel(); return renderToStaticMarkup(tree) }
  function nodes(predicate, node = tree) {
    if (!node || typeof node !== 'object') return []
    return [...(predicate(node) ? [node] : []), ...[node.props?.children].flat(Infinity).flatMap(child => nodes(predicate, child ?? null))]
  }
  function button(label) { return nodes(x => x.type === 'button' && [x.props.children].flat().some(child => typeof child === 'string' && child.trim() === label))[0] }
  return { calls, render, button, nodes, async settle() { await new Promise(resolve => setImmediate(resolve)); return render() } }
}

test('saving an account clears the password immediately, creates it disabled, and refreshes selectors', async () => {
  const page = panel()
  page.render(); page.button('添加账户').props.onClick(); page.render()
  const inputs = page.nodes(x => x.type === 'input')
  inputs[0].props.onChange({ target: { value: 'Test account' } })
  inputs[1].props.onChange({ target: { value: '0x123' } })
  inputs[2].props.onChange({ target: { value: 'one-time-key' } })
  assert.equal(inputs[2].props.type, 'password')
  page.render()
  page.nodes(x => x.type === 'form')[0].props.onSubmit({ preventDefault() {} })
  assert.doesNotMatch(page.render(), /one-time-key/)
  const html = await page.settle()
  assert.deepEqual(JSON.parse(JSON.stringify(page.calls[0])), ['createAccount', 'hyperliquid-testnet', { name: 'Test account', accountAddress: '0x123', agentPrivateKey: 'one-time-key' }])
  assert.ok(page.calls.some(x => x[0] === 'refresh'))
  assert.match(html, /账户已保存并禁用/)
  assert.equal(page.nodes(x => x.type === 'form').length, 0)
})

test('a failed save leaves an actionable error and requires key re-entry; dismissal clears it', async () => {
  const page = panel([], true)
  page.render(); page.button('添加账户').props.onClick(); page.render()
  page.nodes(x => x.type === 'input' && x.props.type === 'password')[0].props.onChange({ target: { value: 'do-not-retain' } })
  page.render(); page.nodes(x => x.type === 'form')[0].props.onSubmit({ preventDefault() {} })
  const html = await page.settle()
  assert.match(html, /Simulated validation error/)
  assert.doesNotMatch(html, /do-not-retain/)
  assert.equal(page.nodes(x => x.type === 'input' && x.props.type === 'password')[0].props.value, '')
  page.button('取消').props.onClick(); page.render(); page.button('添加账户').props.onClick(); page.render()
  assert.equal(page.nodes(x => x.type === 'input' && x.props.type === 'password')[0].props.value, '')
})

test('active strategies are named and disruptive actions disabled while rename remains available', () => {
  const page = panel([{ ...account, activeCycleCount: 1, blockers: [{ strategyName: 'SOL grid', reason: 'ACTIVE_CYCLE' }] }])
  assert.match(page.render(), /SOL grid/)
  assert.match(page.render(), /MAINNET · REAL FUNDS/)
  assert.equal(page.button('禁用').props.disabled, true)
  assert.equal(page.button('替换 API Wallet').props.disabled, true)
  assert.equal(page.button('重命名').props.disabled, false)
  page.button('重命名').props.onClick(); page.render()
  assert.equal(page.nodes(x => x.type === 'input' && x.props.type === 'password').length, 0)
})

test('testing a disabled account never implicitly enables it', async () => {
  const page = panel([{ ...account, enabled: false }])
  page.render(); page.button('测试连接').props.onClick(); await page.settle()
  assert.equal(page.calls[0][0], 'hyperliquidHealth')
  assert.equal(page.calls.some(x => x[0] === 'enableAccount'), false)
})
