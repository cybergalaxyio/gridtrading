import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'

function loadApi() {
  const requests = []
  const module = { exports: {} }
  vm.runInNewContext(ts.transpileModule(readFileSync(new URL('../src/api.ts', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS },
  }).outputText, {
    module, exports: module.exports, crypto: { randomUUID: () => 'test-key' },
    fetch: async (url, init = {}) => {
      requests.push({ url, method: init.method ?? 'GET', body: init.body ? JSON.parse(init.body) : null })
      return { ok: true, status: 200, json: async () => ({ requireManualOrderConfirmation: true }) }
    },
  })
  return { api: module.exports.api, requests }
}

test('approval authorizes one exact queued order without resending editable order details', async () => {
  const { api, requests } = loadApi()
  await api.orderApprovals()
  await api.approveOrder('approval-1')
  await api.rejectOrder('approval-2')
  assert.deepEqual(requests, [
    { method: 'GET', url: '/api/v1/order-approvals', body: null },
    { method: 'POST', url: '/api/v1/order-approvals/approval-1/approve', body: null },
    { method: 'POST', url: '/api/v1/order-approvals/approval-2/reject', body: null },
  ])
})

test('settings use shared backend persistence and preserve the switch value', async () => {
  const { api, requests } = loadApi()
  assert.equal((await api.tradingControlSettings()).requireManualOrderConfirmation, true)
  await api.saveTradingControlSettings({ requireManualOrderConfirmation: false, minimumConfirmationNotional: "150.25" })
  assert.deepEqual(requests.map(x => [x.method, x.url]), [
    ['GET', '/api/v1/trading-control-settings'], ['PUT', '/api/v1/trading-control-settings'],
  ])
  assert.equal(requests[1].body.requireManualOrderConfirmation, false)
  assert.equal(requests[1].body.minimumConfirmationNotional, "150.25")
})

test('per-order buttons target the chosen approval and disable actions already applied', () => {
  const require = createRequire(import.meta.url)
  const calls = []
  const state = { busy: null, approve: id => calls.push(['approve', id]), reject: id => calls.push(['reject', id]) }
  const module = { exports: {} }
  vm.runInNewContext(ts.transpileModule(readFileSync(new URL('../src/components/OrderApprovalActions.tsx', import.meta.url), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
  }).outputText, { module, exports: module.exports, require: name => name === '../context/OrderApprovalsContext'
    ? { useOrderApprovals: () => state } : require(name) })
  const buttons = status => module.exports.OrderApprovalActions({ order: { id: 'review-2', orderId: 'order-2', status } }).props.children
  const [confirm, reject] = buttons('PENDING')
  confirm.props.onClick(); reject.props.onClick()
  assert.deepEqual(calls, [['approve', 'review-2'], ['reject', 'review-2']])
  assert.equal(buttons('APPROVED')[0].props.disabled, true)
  assert.equal(buttons('APPROVED')[1].props.disabled, false)
  assert.equal(buttons('REJECTED')[0].props.disabled, false)
  assert.equal(buttons('REJECTED')[1].props.disabled, true)
  state.busy = 'another-order'
  assert.ok(buttons('PENDING').every(button => button.props.disabled))
})
